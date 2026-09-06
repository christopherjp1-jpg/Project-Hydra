using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// <see cref="DirectorioUsuariosTenant.ObtenerCarterasVigentesAsync"/> alimenta
/// la columna "Alcance" de /usuarios, y para eso lee los dos catálogos de
/// asignación — que están <b>fuera del filtro global de tenant</b> a propósito
/// (ADR-011 § 2.7). Ahí no hay red: si el <c>PropietarioTenantId</c> se cayera
/// de la consulta, la pantalla contaría también las carteras que ese mismo
/// usuario tiene sobre OTROS tenants, y de un recuento se infiere para cuántos
/// clientes ajenos trabaja una consultora. Nada rojo se pondría por ello: la
/// columna seguiría mostrando un número, solo que el equivocado.
///
/// <para>
/// Por eso la prueba es de integración y no de unidad: la propiedad que
/// interesa es la que impone la CONSULTA sobre datos reales de dos tenants,
/// no la aritmética de contar ids.
/// </para>
/// </summary>
public class CarterasVigentesDelDirectorioAcotadasAlTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    /// <summary>El tenant que se mira: el propietario de los datos.</summary>
    private readonly Guid _tenantMirado = Guid.NewGuid();

    /// <summary>Otro propietario cualquiera — el que no debe colarse.</summary>
    private readonly Guid _tenantAjeno = Guid.NewGuid();

    /// <summary>La consultora que opera para los dos.</summary>
    private readonly Guid _tenantOperador = Guid.NewGuid();

    /// <summary>Una consultora distinta, para la cartera mal formada.</summary>
    private readonly Guid _tenantOtroOperador = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantMirado);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Solo_cuenta_las_carteras_vigentes_sobre_el_tenant_que_se_mira()
    {
        var ahora = DateTime.UtcNow;
        var desde = ahora.AddDays(-30);

        var gestorDeLaConsultora = Guid.NewGuid();
        var gestorPropio = Guid.NewGuid();
        var reciénCreado = Guid.NewGuid();

        Guid clienteMirado, clienteAjeno, clientePropio, clienteDeCarteraCerrada, clienteDePosicionAjena;

        // --- Clientes de cada propietario. El ámbito de una cartera apunta al
        // cliente por una FK compuesta (PropietarioTenantId, ClienteId), así
        // que cada uno tiene que existir en SU tenant.
        await using (var contexto = CrearContexto(_tenantMirado))
        {
            var uno = Empresa.CrearComoCliente("Cliente Mirado S.A.", "B12345674", false, null, null);
            var dos = Empresa.CrearComoCliente("Cliente Propio S.A.", "B10380186", false, null, null);
            var tres = Empresa.CrearComoCliente("Cliente De Cartera Cerrada S.A.", "B10380194", false, null, null);
            var cuatro = Empresa.CrearComoCliente("Cliente De Posicion Ajena S.A.", "B10380210", false, null, null);
            contexto.Empresas.AddRange(uno, dos, tres, cuatro);

            contexto.Users.AddRange(
                CrearUsuario(gestorDeLaConsultora, _tenantOperador, "gestor.consultora"),
                CrearUsuario(gestorPropio, _tenantMirado, "gestor.propio"),
                CrearUsuario(reciénCreado, _tenantMirado, "recien.creado"));

            await contexto.SaveChangesAsync();
            clienteMirado = uno.Id; clientePropio = dos.Id;
            clienteDeCarteraCerrada = tres.Id; clienteDePosicionAjena = cuatro.Id;
        }

        await using (var contexto = CrearContexto(_tenantAjeno))
        {
            var ajeno = Empresa.CrearComoCliente("Cliente Ajeno S.A.", "B10380202", false, null, null);
            contexto.Empresas.Add(ajeno);
            await contexto.SaveChangesAsync();
            clienteAjeno = ajeno.Id;
        }

        // --- Asignaciones. Las dos tablas viven fuera del filtro de tenant, así
        // que da igual con qué contexto se escriban.
        await using (var contexto = CrearContexto(_tenantMirado))
        {
            var operacionSobreElMirado = AsignacionOperacion.Externa(
                _tenantMirado, _tenantOperador, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
            var operacionSobreElAjeno = AsignacionOperacion.Externa(
                _tenantAjeno, _tenantOperador, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
            var operacionInterna = AsignacionOperacion.Raiz(_tenantMirado, ServicioCae.Outbound, desde, ahora);
            // Propietario correcto, operador que NO es el tenant de origen del
            // usuario al que se le va a colgar la cartera: la "cartera mal
            // formada" contra la que existe la segunda mitad del filtro.
            //
            // Ámbito acotado y no universal, y no por gusto: la base impone un
            // índice único parcial (IX_AsignacionesOperacion_DelegacionTotalVigente)
            // que prohíbe dos delegaciones TOTALES vigentes sobre el mismo
            // (propietario, servicio) — repartir entre dos operadores exige
            // ámbitos explícitos. Con universal, este fixture ni siquiera
            // llegaba a guardarse.
            var operacionDeOtroOperador = AsignacionOperacion.Externa(
                _tenantMirado, _tenantOtroOperador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(clienteDePosicionAjena), desde, null, ahora);
            contexto.AsignacionesOperacion.AddRange(
                operacionSobreElMirado, operacionSobreElAjeno, operacionInterna, operacionDeOtroOperador);
            await contexto.SaveChangesAsync();

            var carteraCerrada = AsignacionCartera.Interna(
                operacionInterna, gestorPropio, AmbitoAsignacion.DeRelacionCliente(clienteDeCarteraCerrada),
                desde, null, ahora);
            carteraCerrada.Cerrar(MotivoCierreAsignacion.Revocada, ahora);

            contexto.AsignacionesCartera.AddRange(
                // La que sí cuenta para el usuario de la consultora.
                AsignacionCartera.Externa(
                    operacionSobreElMirado, gestorDeLaConsultora, "GestorCae",
                    AmbitoAsignacion.DeRelacionCliente(clienteMirado), desde, null, ahora),
                // La misma persona, mismo servicio, OTRO propietario: no es
                // asunto de quien mira /usuarios en el tenant mirado.
                AsignacionCartera.Externa(
                    operacionSobreElAjeno, gestorDeLaConsultora, "GestorCae",
                    AmbitoAsignacion.DeRelacionCliente(clienteAjeno), desde, null, ahora),
                // Gestión interna, el camino normal de una cuenta de la casa.
                AsignacionCartera.Interna(
                    operacionInterna, gestorPropio, AmbitoAsignacion.DeRelacionCliente(clientePropio),
                    desde, null, ahora),
                // Mismo propietario, pero bajo una operación que opera OTRA
                // consultora: el usuario no ocupa esa posición.
                AsignacionCartera.Externa(
                    operacionDeOtroOperador, gestorDeLaConsultora, "GestorCae",
                    AmbitoAsignacion.DeRelacionCliente(clienteDePosicionAjena), desde, null, ahora),
                carteraCerrada);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(_tenantMirado);
        var carteras = await CrearDirectorio(lectura, _tenantMirado).ObtenerCarterasVigentesAsync();

        carteras[gestorDeLaConsultora].ClienteIds.Should().BeEquivalentTo([clienteMirado],
            "solo cuenta la cartera que cumple las DOS mitades del filtro de posición: propietario = el tenant " +
            "que se mira (contar la del otro propietario revelaría para cuántos clientes ajenos trabaja esa " +
            "consultora) y operador = el tenant de origen del usuario (la de {0} cuelga de una operación que " +
            "opera otra consultora, así que ese usuario no ocupa esa posición)", clienteDePosicionAjena);
        carteras[gestorDeLaConsultora].EsUniversal.Should().BeFalse();

        carteras[gestorPropio].ClienteIds.Should().BeEquivalentTo([clientePropio],
            "la cartera cerrada no concede nada, aunque su fila siga en la tabla (las asignaciones son append-only)");

        carteras.Should().NotContainKey(reciénCreado,
            "ausencia significa alcance cero — es lo que la columna pinta como «Sin cartera»");
    }

    /// <summary>
    /// El ámbito universal de una cartera es "toda la operación de ESTE
    /// propietario", nunca todos los tenants. Se comprueba aparte porque es la
    /// rama que la columna traduce a "Toda la operación", y confundirla con
    /// "todo" sería el peor texto posible en una pantalla de accesos.
    /// </summary>
    [Fact]
    public async Task El_ambito_universal_se_reconoce_y_no_arrastra_ids()
    {
        var ahora = DateTime.UtcNow;
        var desde = ahora.AddDays(-1);
        var operadorDelegadoTotal = Guid.NewGuid();

        await using (var contexto = CrearContexto(_tenantMirado))
        {
            contexto.Users.Add(CrearUsuario(operadorDelegadoTotal, _tenantOperador, "delegado.total"));
            await contexto.SaveChangesAsync();

            var operacion = AsignacionOperacion.Externa(
                _tenantMirado, _tenantOperador, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
            contexto.AsignacionesOperacion.Add(operacion);
            await contexto.SaveChangesAsync();

            contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, operadorDelegadoTotal, "GestorCae", AmbitoAsignacion.Universal, desde, null, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(_tenantMirado);
        var carteras = await CrearDirectorio(lectura, _tenantMirado).ObtenerCarterasVigentesAsync();

        carteras[operadorDelegadoTotal].EsUniversal.Should().BeTrue();
        carteras[operadorDelegadoTotal].ClienteIds.Should().BeEmpty();
    }

    /// <summary>
    /// <c>userManager</c> y <c>dbContext</c> van a null a propósito:
    /// <c>ObtenerCarterasVigentesAsync</c> no los toca, y montar un
    /// <c>UserManager</c> real para leer dos tablas de asignación sería fixture
    /// que no prueba nada. Si algún día los tocara, este test estalla con un
    /// NRE — un fallo ruidoso, no un verde silencioso.
    /// </summary>
    private static DirectorioUsuariosTenant CrearDirectorio(CaeManagerDbContext contexto, Guid tenantId) =>
        new(null!, null!, new TenantActualAmbiental { TenantId = tenantId }, new PuertaAccesoDatos(), contexto);

    private static ApplicationUser CrearUsuario(Guid id, Guid tenantId, string alias) => new()
    {
        Id = id,
        TenantId = tenantId,
        UserName = $"{alias}@caemanager.local",
        NormalizedUserName = $"{alias}@CAEMANAGER.LOCAL".ToUpperInvariant(),
        Email = $"{alias}@caemanager.local",
        NormalizedEmail = $"{alias}@CAEMANAGER.LOCAL".ToUpperInvariant(),
        NombreCompleto = alias,
        EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString(),
        ConcurrencyStamp = Guid.NewGuid().ToString()
    };

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
