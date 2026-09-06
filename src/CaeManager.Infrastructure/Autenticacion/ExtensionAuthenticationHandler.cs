using System.Security.Claims;
using System.Text.Encodings.Web;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Autenticacion;

public class ExtensionAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
    public const string NombreEsquema = "Extension";
}

/// <summary>
/// Autentica a la extensión de navegador con el header
/// <c>Authorization: Extension {token}</c> (ver <see cref="TokenSesionExtension"/>).
///
/// <para>
/// <b>La diferencia que importa frente a <see cref="ApiKeyAuthenticationHandler"/>.</b>
/// Aquel fabrica los claims a mano y fija siempre <c>Roles.Consulta</c>, que
/// en <c>IAlcanceDatosService</c> significa <b>acceso total al tenant sin
/// restricción de cartera</b>. Eso es correcto para una clave de servidor a
/// servidor y sería inaceptable aquí: el token de extensión vive en el
/// portátil de un gestor, y un GestorCae debe ver por la extensión
/// exactamente su cartera, ni un documento más.
/// </para>
///
/// <para>
/// Por eso este handler <b>no construye claims</b>: delega en
/// <see cref="IUserClaimsPrincipalFactory{TUser}"/>, que en este proyecto es
/// <see cref="TenantClaimsPrincipalFactory"/> — el mismo que usa el login por
/// cookie. El principal resultante es, claim por claim, el de una sesión
/// interactiva de ese usuario: su rol real, su <c>tenant_id</c>, y los
/// permisos específicos. Así el aislamiento multi-tenant y el alcance de
/// cartera se aplican sin que este fichero sepa nada de ninguno de los dos, y
/// no hay un segundo sitio donde decidir lo mismo que pueda dejar de
/// coincidir con el primero.
/// </para>
///
/// <para>
/// <b>Tres motivos de rechazo que la cookie no comprueba en cada petición</b>
/// y aquí sí, deliberadamente, porque este credencial vive fuera del
/// navegador y de sus protecciones:
/// </para>
/// <list type="number">
///   <item>Security stamp distinto del de la base — la vía de revocación.</item>
///   <item>Cuenta bloqueada (<c>LockoutEnd</c> vigente). Identity no toca el
///   security stamp al bloquear, así que sin esto un bloqueo no surtiría
///   efecto hasta que el token caducara.</item>
///   <item>Cuenta a medio activar (contraseña temporal sin cambiar, o
///   Administrador sin 2FA). La UI lo hace cumplir en cada navegación con
///   <c>CuentaAMedioActivarSinAccesoMiddleware</c>; sin esta comprobación la
///   extensión sería la puerta que se salta esa exigencia.</item>
/// </list>
/// </summary>
public class ExtensionAuthenticationHandler(
    IOptionsMonitor<ExtensionAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDataProtectionProvider dataProtectionProvider,
    UserManager<ApplicationUser> userManager,
    IUserClaimsPrincipalFactory<ApplicationUser> fabricaPrincipal)
    : AuthenticationHandler<ExtensionAuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string EsquemaCabecera = "Extension";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var valorCabecera))
            return AuthenticateResult.NoResult();

        var cabecera = valorCabecera.ToString();
        if (!cabecera.StartsWith($"{EsquemaCabecera} ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = cabecera[(EsquemaCabecera.Length + 1)..].Trim();
        if (token.Length == 0)
            return AuthenticateResult.Fail("Falta el token.");

        var carga = TokenSesionExtension.Leer(dataProtectionProvider, token);
        if (carga is null)
            return AuthenticateResult.Fail("Token inválido o caducado.");

        var usuario = await userManager.FindByIdAsync(carga.UsuarioId.ToString());
        if (usuario is null)
            return AuthenticateResult.Fail("Token inválido o caducado.");

        // Comparación ordinaria, no de tiempo fijo, y a propósito: el stamp
        // viaja DENTRO de la carga útil que Data Protection ya autentica, así
        // que no se puede variar sin romper el MAC. No hay nada que un
        // atacante pueda ir tanteando aquí.
        if (!string.Equals(usuario.SecurityStamp, carga.SecurityStamp, StringComparison.Ordinal))
            return AuthenticateResult.Fail("Token inválido o caducado.");

        if (await userManager.IsLockedOutAsync(usuario))
            return AuthenticateResult.Fail("La cuenta está bloqueada.");

        var principal = await fabricaPrincipal.CreateAsync(usuario);

        if (principal.HasClaim(TenantClaimsPrincipalFactory.TipoClaimRequiereActivacion, "true"))
            return AuthenticateResult.Fail("La cuenta está a medio activar.");

        if (principal.Identity is not ClaimsIdentity identidadOriginal)
            return AuthenticateResult.Fail("No se pudo construir la identidad.");

        // La fábrica marca la identidad con el esquema de cookie. Se rehace
        // con el esquema de este handler para que nada aguas abajo crea que
        // esta petición trae una sesión interactiva. NameClaimType y
        // RoleClaimType se copian de la original en vez de releerlos de
        // IdentityOptions: si algún día divergen, IsInRole dejaría de
        // funcionar en silencio, que es la peor forma de romperse.
        var identidad = new ClaimsIdentity(
            identidadOriginal.Claims, Scheme.Name,
            identidadOriginal.NameClaimType, identidadOriginal.RoleClaimType);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identidad), Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
