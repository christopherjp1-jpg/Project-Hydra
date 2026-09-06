using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;

/// <summary>
/// Cierra el hallazgo P-04 de la auditoría de producto 2026-08-16 en su
/// forma completa: <see cref="Dashboard.Queries.ObtenerPendientePorPlataformaQuery"/>
/// ya dice "Nalanda: 14 pendientes de subir · 3 rechazados" en Inicio, pero
/// no hay ningún sitio donde ver CUÁLES son esos 14 documentos ni marcarlos
/// — esta query es el drill-down real: Proveedor → Cliente (del Centro del
/// canal) → Documento, con el último motivo de rechazo si lo hay. Mismo
/// alcance por Centro que ObtenerPendientePorPlataformaQuery (la unidad real
/// de "dónde hay que subir esto" es el CanalGestionDocumental).
///
/// Segundo consumidor (Incremento 2 del MVP1 de extensión de navegador, ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio):
/// <c>AcreditacionesPendientesEndpoints</c> expone esta misma query sin
/// duplicar su alcance por cartera ni su agrupación por proveedor — solo
/// necesitó dos campos más en <see cref="AcreditacionDrillDownDto"/> que la
/// UI de drill-down no pedía (<see cref="AcreditacionDrillDownDto.CanalGestionDocumentalId"/>
/// para el Incremento 3, y <see cref="AcreditacionDrillDownDto.TrabajadorDni"/>
/// para el emparejamiento de identidad de la propia extensión).
/// </summary>
public record ObtenerAcreditacionesPorProveedorQuery : IRequest<IReadOnlyList<ProveedorAcreditacionesDto>>;

public record ProveedorAcreditacionesDto(
    Guid ProveedorPlataformaCaeId, string ProveedorNombre, string ProveedorCodigo,
    IReadOnlyList<ClienteAcreditacionesDto> Clientes);

public record ClienteAcreditacionesDto(Guid ClienteId, string ClienteNombre, IReadOnlyList<AcreditacionDrillDownDto> Documentos);

public record AcreditacionDrillDownDto(
    Guid AcreditacionId, Guid DocumentoId, string PropietarioNombre, string TipoDocumentoNombre,
    EstadoAcreditacion Estado, string? UltimoMotivoRechazo,
    Guid? TrabajadorId = null, Guid? EmpresaId = null, Guid? CentroId = null, Guid? TipoDocumentoId = null,
    Guid? CanalGestionDocumentalId = null, string? TrabajadorDni = null);

public class ObtenerAcreditacionesPorProveedorQueryHandler(
    IDocumentosQueryContext documentosContext, ICentrosQueryContext centrosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext,
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAcreditacionesPorProveedorQuery, IReadOnlyList<ProveedorAcreditacionesDto>>
{
    public async Task<IReadOnlyList<ProveedorAcreditacionesDto>> Handle(
        ObtenerAcreditacionesPorProveedorQuery request, CancellationToken cancellationToken)
    {
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var canalesQuery = centrosContext.CanalesGestionDocumental
            .Where(c => c.Tipo == TipoCanalGestion.Plataforma);
        if (centroIdsVisibles is not null)
            canalesQuery = canalesQuery.Where(c => centroIdsVisibles.Contains(c.CentroId));

        var filas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where acreditacion.Estado == EstadoAcreditacion.PendienteDeSubir || acreditacion.Estado == EstadoAcreditacion.Rechazada
            join canal in canalesQuery on acreditacion.CanalGestionDocumentalId equals canal.Id
            join centro in centrosContext.Centros on canal.CentroId equals centro.Id
            join documento in documentosContext.Documentos on acreditacion.DocumentoId equals documento.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                acreditacion.Id,
                acreditacion.DocumentoId,
                acreditacion.Estado,
                ProveedorId = canal.ProveedorPlataformaCaeId,
                CanalGestionDocumentalId = canal.Id,
                CentroId = centro.Id,
                centro.ClienteId,
                documento.TrabajadorId,
                documento.EmpresaId,
                documento.TipoDocumentoId,
                TipoDocumentoNombre = tipoDocumento.Nombre
            })
            .ToListAsync(cancellationToken);

        if (filas.Count == 0) return [];

        var proveedorIds = filas.Where(f => f.ProveedorId is not null).Select(f => f.ProveedorId!.Value).Distinct().ToList();
        var proveedores = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => (p.Nombre, p.Codigo), cancellationToken);

        // Centro.ClienteId ya apunta a Empresas (F3): el "Cliente" dueño del Centro
        // se resuelve contra Empresas, no contra la tabla Clientes congelada.
        var clienteIds = filas.Select(f => f.ClienteId).Distinct().ToList();
        var clientes = await empresasContext.Empresas
            .Where(e => clienteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var trabajadorIds = filas.Where(f => f.TrabajadorId is not null).Select(f => f.TrabajadorId!.Value).Distinct().ToList();
        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => (Nombre: t.Nombre + " " + t.Apellidos, t.Dni), cancellationToken);

        var empresaIds = filas.Where(f => f.EmpresaId is not null).Select(f => f.EmpresaId!.Value).Distinct().ToList();
        var empresas = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var acreditacionIds = filas.Select(f => f.Id).ToList();
        var motivosPorAcreditacion = await documentosContext.RechazosAcreditacionDocumentoPlataforma
            .Where(r => acreditacionIds.Contains(r.AcreditacionId))
            .OrderByDescending(r => r.FechaUtc)
            .GroupBy(r => r.AcreditacionId)
            .Select(g => new { AcreditacionId = g.Key, Motivo = g.First().MotivoLiteral })
            .ToDictionaryAsync(x => x.AcreditacionId, x => x.Motivo, cancellationToken);

        string PropietarioNombre(Guid? trabajadorId, Guid? empresaId) =>
            trabajadorId is { } tId && trabajadores.TryGetValue(tId, out var trabajador) ? trabajador.Nombre
            : empresaId is { } eId && empresas.TryGetValue(eId, out var nombreEmpresa) ? nombreEmpresa
            : "—";

        // Solo un Documento de Trabajador tiene NIF/NIE que emparejar — el de
        // Empresa no lo necesita (ARQUITECTURA-INTEGRACIONES.md § 14.5 del
        // repositorio de negocio: la extensión empareja por NIF, nunca por
        // nombre, para no subir el documento de un trabajador a la ficha de
        // otro).
        string? TrabajadorDni(Guid? trabajadorId) =>
            trabajadorId is { } tId && trabajadores.TryGetValue(tId, out var trabajador) ? trabajador.Dni : null;

        return filas
            .Where(f => f.ProveedorId is not null)
            .GroupBy(f => f.ProveedorId!.Value)
            .Select(porProveedor =>
            {
                var (nombreProveedor, codigoProveedor) = proveedores.GetValueOrDefault(porProveedor.Key, ("Plataforma", ""));
                var porCliente = porProveedor
                    .GroupBy(f => f.ClienteId)
                    .Select(g => new ClienteAcreditacionesDto(
                        g.Key,
                        clientes.GetValueOrDefault(g.Key, "—"),
                        g.Select(f => new AcreditacionDrillDownDto(
                                f.Id, f.DocumentoId, PropietarioNombre(f.TrabajadorId, f.EmpresaId), f.TipoDocumentoNombre,
                                f.Estado, motivosPorAcreditacion.GetValueOrDefault(f.Id),
                                f.TrabajadorId, f.EmpresaId, f.CentroId, f.TipoDocumentoId,
                                f.CanalGestionDocumentalId, TrabajadorDni(f.TrabajadorId)))
                            .OrderBy(d => d.PropietarioNombre)
                            .ToList()))
                    .OrderBy(c => c.ClienteNombre)
                    .ToList();

                return new ProveedorAcreditacionesDto(porProveedor.Key, nombreProveedor, codigoProveedor, porCliente);
            })
            .OrderByDescending(p => p.Clientes.Sum(c => c.Documentos.Count(d => d.Estado == EstadoAcreditacion.PendienteDeSubir)))
            .ToList();
    }
}
