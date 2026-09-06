using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Api.V1;

/// <summary>
/// Proyección pública de <see cref="DocumentoListaDto"/> sin <c>ArchivoUrl</c>
/// (hallazgo del Módulo 9, auditoría 2026-08-30): ese campo es el
/// identificador opaco interno de <c>IFileStorageService</c> (p. ej.
/// "{tenantId}/{guid}.pdf"), no una URL utilizable — el único endpoint que
/// sirve el archivo (<c>/documentos/{id}/archivo</c>) exige sesión
/// interactiva o token de extensión (política
/// <c>Policies.SesionOExtension</c>), en ningún caso la clave de API, así
/// que devolverlo aquí no habilita nada y solo expone el layout interno de
/// almacenamiento a quien tenga la clave. Servir la descarga a un consumidor de <c>/api/v1</c> es una
/// decisión de producto propia (endpoint autorizado por clave, con alcance
/// y auditoría) — no se inventa aquí.
/// </summary>
public record DocumentoApiListaDto(
    Guid Id,
    AmbitoAplicacion Ambito,
    string PropietarioNombre,
    string TipoDocumentoNombre,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado,
    IReadOnlyList<AcreditacionResumenDto> Acreditaciones)
{
    public static DocumentoApiListaDto DesdeInterno(DocumentoListaDto dto) => new(
        dto.Id, dto.Ambito, dto.PropietarioNombre, dto.TipoDocumentoNombre,
        dto.FechaEmision, dto.FechaVencimiento, dto.Estado, dto.Acreditaciones);
}

/// <summary>
/// Mismo criterio que <see cref="DocumentoApiListaDto"/> — ver ese
/// comentario. Solo se retira <c>ArchivoUrl</c>; el resto de campos se deja
/// con paridad exacta respecto a <see cref="DocumentoDetalleDto"/> para no
/// mezclar en este fix una decisión de alcance de campos que no se pidió
/// (esa sí exige definir un sistema de scopes por clave de API, ver informe
/// de cierre del Módulo 9).
/// </summary>
public record DocumentoApiDetalleDto(
    Guid Id,
    AmbitoAplicacion Ambito,
    string PropietarioNombre,
    string TipoDocumentoNombre,
    bool TipoDocumentoAplicaVencimientoAutomatico,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    string? Comentarios,
    string? TipoDocumentoDescripcion,
    string? TipoDocumentoCriteriosValidacion,
    string? TipoDocumentoSeSolicitaA,
    string? TipoDocumentoObservaciones,
    Guid Version,
    PerfilDocumentoOficial TipoDocumentoPerfilDocumentoOficial,
    Guid? EmpresaId)
{
    public static DocumentoApiDetalleDto DesdeInterno(DocumentoDetalleDto dto) => new(
        dto.Id, dto.Ambito, dto.PropietarioNombre, dto.TipoDocumentoNombre,
        dto.TipoDocumentoAplicaVencimientoAutomatico, dto.FechaEmision, dto.FechaVencimiento,
        dto.Comentarios, dto.TipoDocumentoDescripcion, dto.TipoDocumentoCriteriosValidacion,
        dto.TipoDocumentoSeSolicitaA, dto.TipoDocumentoObservaciones, dto.Version,
        dto.TipoDocumentoPerfilDocumentoOficial, dto.EmpresaId);
}
