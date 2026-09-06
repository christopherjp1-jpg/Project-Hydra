namespace CaeManager.Web.Components.Pages;

/// <summary>
/// Las tres pantallas de estado del sistema que comparten una única
/// composición visual (Estados Sistema TALVEG.dc.html): tarjeta centrada,
/// sin menú lateral ni barra superior, acento de color y salida clara al
/// Dashboard. El propio mockup las cubre con un enum en vez de tres
/// markups distintos — <see cref="PaginaEstadoSistema"/> hace lo mismo.
/// </summary>
public enum TipoEstadoSistema
{
    Error,
    NoEncontrado,
    AccesoDenegado,
}
