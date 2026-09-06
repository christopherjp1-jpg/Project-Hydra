using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Módulo 9 (auditoría 2026-08-30): <c>/api/v1/documentos</c> reutilizaba el
/// DTO interno tal cual, incluyendo <c>ArchivoUrl</c> — el identificador
/// opaco de <c>IFileStorageService</c>, no una URL utilizable por quien solo
/// tiene una clave de API (el endpoint de descarga exige sesión interactiva
/// o token de extensión, nunca la clave de API). Estos casos prueban que la proyección pública nunca puede
/// filtrarlo, ni siquiera si alguien vuelve a añadir el campo al DTO interno
/// sin fijarse en el mapeo.
/// </summary>
public class DocumentoApiDtosTests
{
    [Fact]
    public void La_proyeccion_de_detalle_no_expone_ArchivoUrl_como_propiedad()
    {
        typeof(DocumentoApiDetalleDto).GetProperty("ArchivoUrl").Should().BeNull();
    }

    [Fact]
    public void La_proyeccion_de_lista_no_expone_ArchivoUrl_como_propiedad()
    {
        typeof(DocumentoApiListaDto).GetProperty("ArchivoUrl").Should().BeNull();
    }

    [Fact]
    public void DesdeInterno_de_detalle_conserva_el_resto_de_campos()
    {
        var interno = new DocumentoDetalleDto(
            Id: Guid.NewGuid(),
            Ambito: AmbitoAplicacion.Trabajador,
            PropietarioNombre: "Juana Pérez",
            TipoDocumentoNombre: "Reconocimiento médico",
            TipoDocumentoAplicaVencimientoAutomatico: true,
            FechaEmision: new DateOnly(2026, 1, 10),
            FechaVencimiento: new DateOnly(2027, 1, 10),
            ArchivoUrl: "3f3e.../a1b2c3.pdf",
            Comentarios: "Comentario de prueba",
            TipoDocumentoDescripcion: "Descripción",
            TipoDocumentoCriteriosValidacion: "Criterios",
            TipoDocumentoSeSolicitaA: "Mutua",
            TipoDocumentoObservaciones: "Observaciones",
            Version: Guid.NewGuid(),
            TipoDocumentoPerfilDocumentoOficial: PerfilDocumentoOficial.Ninguno,
            EmpresaId: null);

        var publico = DocumentoApiDetalleDto.DesdeInterno(interno);

        publico.Id.Should().Be(interno.Id);
        publico.PropietarioNombre.Should().Be(interno.PropietarioNombre);
        publico.TipoDocumentoNombre.Should().Be(interno.TipoDocumentoNombre);
        publico.FechaEmision.Should().Be(interno.FechaEmision);
        publico.Version.Should().Be(interno.Version);
    }

    [Fact]
    public void DesdeInterno_de_lista_conserva_el_resto_de_campos()
    {
        var interno = new DocumentoListaDto(
            Id: Guid.NewGuid(),
            Ambito: AmbitoAplicacion.Cliente,
            PropietarioNombre: "Empresa Ejemplo SL",
            TipoDocumentoNombre: "Seguro de responsabilidad civil",
            FechaEmision: new DateOnly(2026, 3, 1),
            FechaVencimiento: null,
            Estado: EstadoDocumento.Vigente,
            ArchivoUrl: "3f3e.../otro.pdf",
            Acreditaciones: []);

        var publico = DocumentoApiListaDto.DesdeInterno(interno);

        publico.Id.Should().Be(interno.Id);
        publico.PropietarioNombre.Should().Be(interno.PropietarioNombre);
        publico.Estado.Should().Be(interno.Estado);
    }
}
