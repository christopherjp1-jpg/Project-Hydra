using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Token opaco y de vida corta con el que la extensión de navegador se
/// autentica contra Hydra (MVP1 de integración con plataformas CAE).
///
/// <para>
/// <b>Por qué Data Protection y no un JWT.</b> Es el mecanismo que este
/// repositorio ya usa para exactamente esta forma de problema —
/// <c>ClienteActivoSeleccionado.Proteger</c> emite así el token de contexto
/// de workspace, con <see cref="ITimeLimitedDataProtector"/> y el mismo
/// formato de carga útil separada por <c>|</c>. Añadir una dependencia de
/// JWT traería un segundo sistema de claves que rotar, revocar y auditar
/// para no ganar nada: el token no lo consume ningún tercero, solo lo
/// devuelve quien lo recibió.
/// </para>
///
/// <para>
/// <b>La carga útil lleva el security stamp, y eso es la revocación.</b> El
/// token es sin estado (no hay tabla, no hay migración), así que no se puede
/// borrar una fila para invalidarlo. Lo que sí se puede es cambiar el
/// security stamp del usuario —<c>UserManager.UpdateSecurityStampAsync</c>,
/// que es lo que ya ocurre al cambiar la contraseña— y entonces todo token
/// emitido antes deja de valer al instante, igual que las cookies de ese
/// usuario. Es el mismo mecanismo de invalidación que Identity ya tiene, no
/// uno nuevo; ver <c>ExtensionAuthenticationHandler</c>, que es quien lo
/// comprueba en cada petición.
/// </para>
///
/// <para>
/// La vigencia es corta a propósito: el token vive en el almacenamiento de
/// una extensión, en la máquina del gestor, fuera del tarro de cookies del
/// navegador y de sus protecciones. Ocho horas cubren una jornada completa
/// sin renovar, y acotan a una jornada la ventana de un portátil perdido.
/// No hay refresco: al caducar se vuelve a pasar por Hydra, que es donde ya
/// hay sesión iniciada.
/// </para>
/// </summary>
public static class TokenSesionExtension
{
    /// <summary>
    /// Nombre del protector. <b>No renombrar</b>: cambiarlo invalida de golpe
    /// todos los tokens vivos (mismo criterio que los protectores de
    /// credenciales de <c>CaeManagerDbContext.OnModelCreating</c>).
    /// </summary>
    private const string PropositoProtector = "CaeManager.Extension.Sesion.v1";

    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(8);

    /// <summary>
    /// Emite el token. Solo debe llamarlo el endpoint de emisión, y solo tras
    /// comprobar que quien lo pide es el propio <paramref name="usuarioId"/>
    /// con sesión interactiva viva.
    /// </summary>
    /// <param name="securityStamp">
    /// El security stamp del usuario <b>en el momento de emitir</b>. Quien
    /// consume el token lo compara con el de la base: si no coinciden, el
    /// token es de antes de una invalidación y no vale.
    /// </param>
    public static string Proteger(IDataProtectionProvider dataProtectionProvider, Guid usuarioId, string securityStamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);

        return CrearProtector(dataProtectionProvider).Protect($"{usuarioId:N}|{securityStamp}", Vigencia);
    }

    /// <summary>
    /// Devuelve la carga útil, o <c>null</c> si el token está caducado,
    /// manipulado, cifrado con una clave que ya no existe o simplemente mal
    /// formado. Los cuatro casos se tratan igual a propósito: quien llama no
    /// debe poder distinguirlos, y ninguno concede acceso.
    /// </summary>
    public static CargaUtilTokenExtension? Leer(IDataProtectionProvider dataProtectionProvider, string token)
    {
        string desprotegido;
        try
        {
            desprotegido = CrearProtector(dataProtectionProvider).Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Es también el camino de la caducidad: ITimeLimitedDataProtector
            // señala un token vencido con esta misma excepción.
            return null;
        }

        return ParsearCargaUtil(desprotegido);
    }

    /// <summary>
    /// La mitad de <see cref="Leer"/> que no es criptografía: interpretar la
    /// carga útil ya descifrada. Pública para poder probar cada rama de
    /// rechazo sin fabricar tokens —el propósito del protector es privado a
    /// propósito— y porque aceptar una carga malformada aquí sería conceder
    /// acceso, no un error de formato.
    /// </summary>
    public static CargaUtilTokenExtension? ParsearCargaUtil(string cargaUtil)
    {
        // El security stamp de Identity es Base32 (sin '|'), así que dos
        // partes exactas es la única forma válida.
        var partes = cargaUtil.Split('|');
        if (partes.Length != 2)
            return null;

        if (!Guid.TryParseExact(partes[0], "N", out var usuarioId) || usuarioId == Guid.Empty)
            return null;

        if (string.IsNullOrWhiteSpace(partes[1]))
            return null;

        return new CargaUtilTokenExtension(usuarioId, partes[1]);
    }

    private static ITimeLimitedDataProtector CrearProtector(IDataProtectionProvider dataProtectionProvider) =>
        dataProtectionProvider.CreateProtector(PropositoProtector).ToTimeLimitedDataProtector();
}

/// <summary>Contenido verificado de un token de extensión — ver <see cref="TokenSesionExtension"/>.</summary>
public record CargaUtilTokenExtension(Guid UsuarioId, string SecurityStamp);
