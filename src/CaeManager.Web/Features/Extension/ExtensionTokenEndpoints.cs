using System.Security.Claims;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Emite el token con el que la extensión de navegador se autentica después
/// (ver <see cref="TokenSesionExtension"/> y <c>ExtensionAuthenticationHandler</c>).
///
/// <para>
/// <b>El emisor exige sesión interactiva; el consumidor, no.</b> Es el mismo
/// reparto que ya usa <c>SesionSoporteEndpoints</c>: se paga una vez el
/// precio de estar dentro de Hydra con cookie válida, y a cambio se obtiene
/// un credencial acotado que sirve fuera. Sin este endpoint no hay forma
/// legítima de que la extensión obtenga un token.
/// </para>
///
/// <para>
/// <b>POST con formulario, no GET ni JSON.</b> Mismo motivo que
/// <c>/cuenta/soporte/abrir</c> y <c>/cuenta/cliente-activo</c>: emite un
/// credencial, así que no puede viajar en una URL que alguien haga seguir a
/// un usuario, y al aceptar formulario <c>UseAntiforgery</c> valida el token
/// sin que haya que acordarse de hacerlo.
/// </para>
///
/// <para>
/// <b>Una sesión privilegiada o un workspace delegado no se heredan.</b> El
/// token solo nombra al usuario; el tenant lo resuelve después
/// <see cref="TenantClaimsPrincipalFactory"/> desde <c>user.TenantId</c>, que
/// es el tenant PROPIO. El tenant visitado vive en la cookie de contexto, que
/// la extensión no tiene. El token es por tanto siempre igual o más estrecho
/// que la sesión que lo emitió, nunca más ancho.
/// </para>
/// </summary>
public static class ExtensionTokenEndpoints
{
    public static IEndpointRouteBuilder MapExtensionTokenEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/cuenta/extension/token", async (
            // Se acepta el formulario entero aunque no se lea ningún campo:
            // es la presencia de un parámetro de formulario lo que hace que
            // UseAntiforgery valide esta petición. Un endpoint sin él pasaría
            // sin validar y nadie se enteraría hasta que fuera tarde.
            IFormCollection formulario,
            ClaimsPrincipal usuarioActual,
            UserManager<ApplicationUser> userManager,
            IDataProtectionProvider dataProtectionProvider) =>
        {
            var valorClaim = usuarioActual.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(valorClaim, out var usuarioId))
                return Results.Unauthorized();

            var resultado = await EmisorTokenExtension.EmitirAsync(usuarioId, userManager, dataProtectionProvider);
            if (resultado.EsFallido)
                return resultado.Error.Codigo == "Extension.UsuarioNoEncontrado"
                    ? Results.Unauthorized()
                    : Results.Problem(resultado.Error.Mensaje, statusCode: StatusCodes.Status409Conflict);

            var (token, expiraEnUtc) = resultado.Valor;
            return Results.Ok(new TokenExtensionRespuesta(token, expiraEnUtc));
        });

        return endpoints;
    }
}

/// <param name="ExpiraEnUtc">
/// Informativo, para que la extensión sepa cuándo volver a pedirlo sin tener
/// que provocar un 401 primero. No es la fuente de verdad: la caducidad real
/// la impone el propio token y la comprueba el servidor.
/// </param>
public record TokenExtensionRespuesta(string Token, DateTime ExpiraEnUtc);
