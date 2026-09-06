using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Incremento 2 del MVP1 de extensión de navegador (ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio):
/// <see cref="ObtenerAcreditacionesPorProveedorQuery"/> no tenía ningún test
/// antes de este incremento — se cubre aquí tanto su comportamiento ya
/// existente (agrupación, alcance) como los dos campos nuevos que la
/// extensión necesita (<see cref="AcreditacionDrillDownDto.CanalGestionDocumentalId"/>,
/// <see cref="AcreditacionDrillDownDto.TrabajadorDni"/>).
/// </summary>
public class ObtenerAcreditacionesPorProveedorQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Incluye_el_canal_y_el_dni_del_trabajador_para_una_acreditacion_pendiente()
    {
        Guid trabajadorId, tipoDocumentoId, canalId, proveedorId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Extensión S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Extensión S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Extensión");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canalPlataforma = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", proveedor.Id, "https://plataforma.test", "usuario", "clave");
            contexto.CanalesGestionDocumental.Add(canalPlataforma);

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Nora", "Vidal", "22334455Y");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
            var tipoDocumento = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            trabajadorId = trabajador.Id;
            tipoDocumentoId = tipoDocumento.Id;
            canalId = canalPlataforma.Id;
            proveedorId = proveedor.Id;
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new CrearDocumentoCommandHandler(
                new DocumentoRepository(contexto), contexto, contexto, contexto, contexto, contexto,
                contexto, new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
                new DerivarCanalesAplicablesDocumentoService(contexto, contexto, contexto),
                new AcreditacionDocumentoPlataformaRepository(contexto), new PublisherFalso());

            var resultado = await handler.Handle(
                new CrearDocumentoCommand(
                    TrabajadorId: trabajadorId, ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                    TipoDocumentoId: tipoDocumentoId, FechaEmision: new DateOnly(2026, 1, 1),
                    FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var consulta = CrearContexto();
        var handlerQuery = new ObtenerAcreditacionesPorProveedorQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta, new AlcanceDatosServiceFalso());

        var proveedores = await handlerQuery.Handle(new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None);

        var proveedorResultado = proveedores.Should().ContainSingle().Subject;
        proveedorResultado.ProveedorPlataformaCaeId.Should().Be(proveedorId);

        var acreditacion = proveedorResultado.Clientes.Should().ContainSingle().Subject
            .Documentos.Should().ContainSingle().Subject;

        acreditacion.CanalGestionDocumentalId.Should().Be(canalId);
        acreditacion.TrabajadorDni.Should().Be("22334455Y");
        acreditacion.TrabajadorId.Should().Be(trabajadorId);
    }

    [Fact]
    public async Task No_devuelve_nada_fuera_de_la_cartera_del_gestor()
    {
        Guid centroId, proveedorId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Fuera De Cartera S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Fuera De Cartera S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Fuera De Cartera");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canalPlataforma = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", proveedor.Id, "https://plataforma.test", "usuario", "clave");
            contexto.CanalesGestionDocumental.Add(canalPlataforma);

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Iker", "Soto", "99887766P");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
            var tipoDocumento = new TipoDocumento("Formación", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var handlerCrear = new CrearDocumentoCommandHandler(
                new DocumentoRepository(contexto), contexto, contexto, contexto, contexto, contexto,
                contexto, new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
                new DerivarCanalesAplicablesDocumentoService(contexto, contexto, contexto),
                new AcreditacionDocumentoPlataformaRepository(contexto), new PublisherFalso());

            await handlerCrear.Handle(
                new CrearDocumentoCommand(
                    TrabajadorId: trabajador.Id, ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                    TipoDocumentoId: tipoDocumento.Id, FechaEmision: new DateOnly(2026, 1, 1),
                    FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
                CancellationToken.None);

            centroId = centro.Id;
            proveedorId = proveedor.Id;
        }

        // Un GestorCae cuya cartera no incluye este Centro: alcance vacío, no
        // null — el mismo contrato que usa la implementación real para "no
        // tiene nada asignado todavía", nunca "sin restricción".
        await using var consulta = CrearContexto();
        var handlerQuery = new ObtenerAcreditacionesPorProveedorQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta,
            new AlcanceDatosServiceFalso(centroIds: []));

        var proveedores = await handlerQuery.Handle(new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None);

        proveedores.Should().BeEmpty();
        _ = centroId;
        _ = proveedorId;
    }

    private sealed class ColaAnalisisDocumentoFalsa : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo)
        {
        }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
