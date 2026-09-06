using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// La comprobación y la emisión de <see cref="TokenSesionExtension"/>,
/// extraídas de <c>ExtensionTokenEndpoints</c> (Incremento 1, PR #484) para
/// que también las use la página Blazor "Conectar extensión"
/// (<c>/cuenta/extension</c>, Incremento 4) sin repetirlas — ambas exigen
/// exactamente lo mismo: sesión interactiva ya resuelta y una cuenta con
/// security stamp. Se queda en Infrastructure, no en Application, porque ya
/// depende directamente de <see cref="UserManager{TUser}"/> y
/// <see cref="IDataProtectionProvider"/> — el mismo criterio por el que el
/// endpoint original tampoco pasaba por Application.
/// </summary>
public static class EmisorTokenExtension
{
    public static async Task<Result<(string Token, DateTime ExpiraEnUtc)>> EmitirAsync(
        Guid usuarioId, UserManager<ApplicationUser> userManager, IDataProtectionProvider dataProtectionProvider)
    {
        var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
        if (usuario is null)
            return Result.Fallo<(string, DateTime)>(Error.Crear("Extension.UsuarioNoEncontrado", "No encontramos el usuario."));

        // Un usuario sin security stamp no puede recibir token: sin él, el
        // handler no tendría con qué revocarlo. Se rechaza en vez de
        // generarlo aquí — emitir un stamp es un efecto sobre la cuenta, y
        // esto no está para tocar cuentas.
        if (string.IsNullOrWhiteSpace(usuario.SecurityStamp))
            return Result.Fallo<(string, DateTime)>(Error.Crear(
                "Extension.CuentaAMedioActivar",
                "La cuenta no puede conectar la extensión todavía. Cambia la contraseña para completar su activación."));

        var token = TokenSesionExtension.Proteger(dataProtectionProvider, usuarioId, usuario.SecurityStamp);
        return Result.Exito((token, DateTime.UtcNow.Add(TokenSesionExtension.Vigencia)));
    }
}
