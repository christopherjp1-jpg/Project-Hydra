using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Incremento 3 del MVP1 de extensión de navegador (ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio):
/// <see cref="MarcarAcreditacionSubidaCommandHandler"/> no tenía ningún test
/// (solo lo ejercitaba manualmente el botón "Marcar subido" de
/// PlataformaTab.razor) antes de exponerlo también a la extensión vía
/// <c>MarcarAcreditacionSubidaEndpoints</c>.
/// </summary>
public class MarcarAcreditacionSubidaCommandHandlerTests
{
    [Fact]
    public async Task Marca_la_acreditacion_como_subida()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid());
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, documentoRepositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Falla_cuando_la_acreditacion_no_existe()
    {
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, new DocumentoRepositorioFalso(), new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_con_el_mismo_codigo_cuando_el_documento_es_de_un_cliente_fuera_de_la_cartera()
    {
        // Mismo código que "no existe" (Acreditacion.NoEncontrada) a
        // propósito, para no filtrar por enumeración si el AcreditacionId es
        // real pero ajeno a la cartera de quien pide — mismo criterio que
        // EliminarDocumentoCommandHandlerTests.
        var clienteAjeno = Guid.NewGuid();
        var documento = Documento.DeCliente(clienteAjeno, Guid.NewGuid(), new DateOnly(2026, 1, 1), null);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, Guid.NewGuid());
        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);
        var handler = new MarcarAcreditacionSubidaCommandHandler(
            acreditacionRepositorio, documentoRepositorio, alcance, new ProyectosQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new MarcarAcreditacionSubidaCommand(acreditacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
