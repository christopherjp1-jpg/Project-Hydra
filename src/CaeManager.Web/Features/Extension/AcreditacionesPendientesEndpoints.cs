using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Infrastructure.Identity;
using MediatR;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Incremento 2 del MVP1 de integración con plataformas CAE externas vía
/// extensión de navegador (ver ARQUITECTURA-INTEGRACIONES.md § 14 en el
/// repositorio de negocio, y <see cref="ExtensionTokenEndpoints"/> para el
/// Incremento 1).
///
/// <para>
/// No es una query nueva: reexpone <see cref="ObtenerAcreditacionesPorProveedorQuery"/>
/// — ya construida para el drill-down de <c>PlataformaTab.razor</c> y para la
/// Bandeja del gestor — sin duplicar su alcance por cartera
/// (<c>IAlcanceDatosService</c>) ni su agrupación Proveedor → Cliente →
/// Documento. Solo hizo falta extender <c>AcreditacionDrillDownDto</c> con dos
/// campos que esos dos consumidores no pedían: el canal de gestión documental
/// (para el Incremento 3, que marcará la acreditación como subida) y el NIF
/// del trabajador (para que la extensión empareje identidad sin fiarse del
/// nombre).
/// </para>
/// </summary>
public static class AcreditacionesPendientesEndpoints
{
    public static IEndpointRouteBuilder MapAcreditacionesPendientesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/extension/acreditaciones-pendientes", async (
            IMediator mediator, CancellationToken cancellationToken) =>
        {
            var proveedores = await mediator.Send(new ObtenerAcreditacionesPorProveedorQuery(), cancellationToken);
            return Results.Ok(proveedores);
        })
        // Misma política que /documentos/{id}/archivo (PR #484): sesión
        // interactiva o token de extensión, nunca clave de API de tenant. No
        // relaja nada — el alcance por cartera lo sigue imponiendo
        // IAlcanceDatosService dentro de la query con el rol real de quien
        // pide, que para la extensión es el del usuario que emitió el token.
        .RequireAuthorization(Policies.SesionOExtension);

        return endpoints;
    }
}
