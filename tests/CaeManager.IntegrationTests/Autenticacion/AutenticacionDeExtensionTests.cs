using System.Security.Claims;
using System.Text.Encodings.Web;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Autenticacion;

/// <summary>
/// <c>ExtensionAuthenticationHandler</c> contra Identity de verdad.
///
/// <para>
/// <b>Qué protege esto y por qué no vale probarlo más arriba.</b> El handler
/// existe precisamente para NO parecerse a
/// <c>ApiKeyAuthenticationHandler</c>, que fija <c>Roles.Consulta</c> y por
/// tanto concede acceso total al tenant sin restricción de cartera. Si alguien
/// "simplificara" este handler copiando aquel, el token de un GestorCae
/// pasaría a ver todos los documentos del tenant desde el portátil donde vive.
/// Ningún test de Application o de Web observa eso: el rol se decide aquí, al
/// construir el principal, y solo un <c>UserManager</c> real con roles reales
/// puede distinguir "el rol del usuario" de "un rol fijo que se le parece".
/// </para>
///
/// <para>
/// Las tres vías de rechazo —stamp, bloqueo y activación pendiente— se prueban
/// una a una porque las tres fallan hacia el lado caro: sin ellas el token
/// sigue autenticando y no hay ningún síntoma visible.
/// </para>
/// </summary>
public class AutenticacionDeExtensionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private ServiceProvider _servicios = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton(_protector);
        servicios.AddSingleton<ITenantActual>(new TenantActualFijo());
        servicios.AddScoped<PuertaAccesoDatos>();

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL")));

        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        // La factory REAL, la misma que registra Program.cs — mismo criterio
        // que ClaimDeActivacionDeCuentaTests: sustituirla sería probar el test.
        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>();

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        var tenant = new Tenant("Tenant de prueba");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        _tenant = tenant.Id;

        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var rol in Roles.Todos)
            await roleManager.CreateAsync(new IdentityRole<Guid>(rol));
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_token_recien_emitido_trae_el_rol_y_el_tenant_reales_del_usuario()
    {
        var usuario = await CrearUsuarioAsync("gestor@x.test", Roles.GestorCae);
        var resultado = await AutenticarAsync(Emitir(usuario));

        resultado.Succeeded.Should().BeTrue();

        // El corazón del incremento: GestorCae, NO Consulta. Con Consulta,
        // IAlcanceDatosService.TieneAccesoTotalAsync devuelve true y la
        // extensión vería el tenant entero en vez de la cartera del gestor.
        resultado.Principal!.IsInRole(Roles.GestorCae).Should().BeTrue();
        resultado.Principal.IsInRole(Roles.Consulta).Should().BeFalse();

        resultado.Principal.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)!.Value
            .Should().Be(_tenant.ToString());
        resultado.Principal.FindFirst(ClaimTypes.NameIdentifier)!.Value
            .Should().Be(usuario.Id.ToString(), "ICurrentUserService resuelve el usuario por este claim");
    }

    [Fact]
    public async Task La_identidad_no_se_hace_pasar_por_una_sesion_de_cookie()
    {
        var usuario = await CrearUsuarioAsync("marca@x.test", Roles.CoordinadorCae);
        var resultado = await AutenticarAsync(Emitir(usuario));

        var identidad = (ClaimsIdentity)resultado.Principal!.Identity!;
        identidad.AuthenticationType.Should().Be(ExtensionAuthenticationSchemeOptions.NombreEsquema);
        identidad.AuthenticationType.Should().NotBe(IdentityConstants.ApplicationScheme);

        // Y el RoleClaimType sobrevive a rehacer la identidad. Si se perdiera,
        // IsInRole devolvería false para todo el mundo y la extensión se
        // quedaría sin autorización EN SILENCIO, que es peor que romperse.
        identidad.RoleClaimType.Should().Be(ClaimTypes.Role);
        resultado.Principal.IsInRole(Roles.CoordinadorCae).Should().BeTrue();
    }

    [Fact]
    public async Task Invalidar_el_security_stamp_revoca_los_tokens_ya_emitidos()
    {
        var usuario = await CrearUsuarioAsync("revocado@x.test", Roles.GestorCae);
        var token = Emitir(usuario);

        (await AutenticarAsync(token)).Succeeded.Should().BeTrue("el token vale antes de revocar");

        using (var ambito = _servicios.CreateScope())
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var recargado = await userManager.FindByIdAsync(usuario.Id.ToString());
            (await userManager.UpdateSecurityStampAsync(recargado!)).Succeeded.Should().BeTrue();
        }

        (await AutenticarAsync(token)).Succeeded.Should().BeFalse(
            "cambiar el stamp es la única vía de revocación de un token sin estado");
    }

    [Fact]
    public async Task Una_cuenta_bloqueada_no_autentica()
    {
        var usuario = await CrearUsuarioAsync("bloqueado@x.test", Roles.GestorCae);
        var token = Emitir(usuario);

        using (var ambito = _servicios.CreateScope())
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var recargado = await userManager.FindByIdAsync(usuario.Id.ToString());
            await userManager.SetLockoutEnabledAsync(recargado!, true);
            await userManager.SetLockoutEndDateAsync(recargado!, DateTimeOffset.UtcNow.AddHours(1));
        }

        // Identity NO toca el security stamp al bloquear: sin la comprobación
        // explícita del handler, el bloqueo no surtiría efecto hasta que el
        // token caducara solo.
        (await AutenticarAsync(token)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Una_cuenta_a_medio_activar_no_autentica()
    {
        var usuario = await CrearUsuarioAsync(
            "temporal@x.test", Roles.GestorCae, debeCambiarContrasena: true);

        (await AutenticarAsync(Emitir(usuario))).Succeeded.Should().BeFalse(
            "la extensión no puede ser la puerta que se salta lo que la UI exige en cada navegación");
    }

    [Fact]
    public async Task Un_token_manipulado_no_autentica()
    {
        var usuario = await CrearUsuarioAsync("manipulado@x.test", Roles.GestorCae);
        var token = Emitir(usuario);

        var alterado = token[..^2] + (token.EndsWith("aa", StringComparison.Ordinal) ? "bb" : "aa");

        (await AutenticarAsync(alterado)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task El_token_de_otro_proposito_no_sirve()
    {
        var usuario = await CrearUsuarioAsync("proposito@x.test", Roles.GestorCae);

        // Mismo contenido, protector distinto: si el handler no fijara el
        // propósito, cualquier token de otra parte del sistema valdría aquí.
        var ajeno = _protector.CreateProtector("CaeManager.OtroProposito.v1")
            .ToTimeLimitedDataProtector()
            .Protect($"{usuario.Id:N}|{usuario.SecurityStamp}", TimeSpan.FromHours(1));

        (await AutenticarAsync(ajeno)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_cabecera_el_handler_se_aparta()
    {
        // NoResult, no Fail: la política nombra también el esquema de cookie, y
        // un Fail aquí tumbaría las peticiones legítimas del navegador.
        var resultado = await AutenticarAsync(token: null);

        resultado.None.Should().BeTrue();
        resultado.Failure.Should().BeNull();
    }

    private async Task<ApplicationUser> CrearUsuarioAsync(
        string email, string rol, bool debeCambiarContrasena = false)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            TenantId = _tenant,
            DebeCambiarContrasena = debeCambiarContrasena,
            TwoFactorEnabled = true,
        };

        (await userManager.CreateAsync(usuario)).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();

        return (await userManager.FindByIdAsync(usuario.Id.ToString()))!;
    }

    private string Emitir(ApplicationUser usuario) =>
        TokenSesionExtension.Proteger(_protector, usuario.Id, usuario.SecurityStamp!);

    private async Task<AuthenticateResult> AutenticarAsync(string? token)
    {
        using var ambito = _servicios.CreateScope();
        var proveedor = ambito.ServiceProvider;

        var handler = new ExtensionAuthenticationHandler(
            new MonitorFijo(),
            proveedor.GetRequiredService<ILoggerFactory>(),
            UrlEncoder.Default,
            _protector,
            proveedor.GetRequiredService<UserManager<ApplicationUser>>(),
            proveedor.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>());

        var contexto = new DefaultHttpContext { RequestServices = proveedor };
        if (token is not null)
            contexto.Request.Headers.Authorization = $"Extension {token}";

        await handler.InitializeAsync(
            new AuthenticationScheme(
                ExtensionAuthenticationSchemeOptions.NombreEsquema,
                displayName: null,
                typeof(ExtensionAuthenticationHandler)),
            contexto);

        return await handler.AuthenticateAsync();
    }

    private sealed class MonitorFijo : IOptionsMonitor<ExtensionAuthenticationSchemeOptions>
    {
        public ExtensionAuthenticationSchemeOptions CurrentValue { get; } = new();

        public ExtensionAuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ExtensionAuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
