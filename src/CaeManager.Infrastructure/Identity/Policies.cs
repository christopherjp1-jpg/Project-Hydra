namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Políticas de autorización que exigen algo distinto de un rol — a
/// diferencia de <see cref="Roles"/>, que solo nombra roles para
/// <c>RequireRole</c>. Se registran todas en <c>Program.cs</c>.
/// </summary>
public static class Policies
{
    /// <summary>
    /// DEC-36 (REC-099) pide «Administrador del Tenant propietario, mediante
    /// permiso específico», no el rol Administrador a secas — el claim lo
    /// pone <see cref="TenantClaimsPrincipalFactory"/>.
    /// </summary>
    public const string ConsultarAccesoDocumentosSensibles = "ConsultarAccesoDocumentosSensibles";

    /// <summary>
    /// Vale tanto una sesión interactiva (cookie) como el token de la
    /// extensión de navegador — para lo que ambas superficies necesitan
    /// servir, hoy la descarga del PDF de un Documento. No es una política
    /// «más permisiva»: cada esquema sigue trayendo el rol y el tenant reales
    /// de su usuario, y el alcance de cartera se aplica después igual en los
    /// dos casos.
    /// </summary>
    public const string SesionOExtension = "SesionOExtension";
}
