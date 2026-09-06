using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Autenticacion;

/// <summary>
/// Incremento 4 del MVP1 de extensión de navegador (ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio):
/// <see cref="EmisorTokenExtension"/> extrae la lógica de emisión de
/// <c>ExtensionTokenEndpoints</c> (PR #484) para reutilizarla también desde
/// la página Blazor "Conectar extensión" — ninguna de las dos tenía test
/// hasta ahora. Mismo patrón de <c>AutenticacionDeExtensionTests</c>: Identity
/// real contra Postgres, porque la comprobación del security stamp solo tiene
/// sentido con el store real (<see cref="UserManager{TUser}.CreateAsync(TUser)"/>
/// siempre asigna uno — para probar su ausencia hay que quitarlo por debajo,
/// directo en la fila).
/// </summary>
public class EmisorTokenExtensionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private ServiceProvider _servicios = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton(_protector);
        servicios.AddSingleton<ITenantActual>(new TenantActualFijo());
        servicios.AddScoped<PuertaAccesoDatos>();

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL")));

        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>();

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        var tenant = new Tenant("Tenant de prueba");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        _tenant = tenant.Id;

        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var rol in Roles.Todos)
            await roleManager.CreateAsync(new IdentityRole<Guid>(rol));
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Emite_un_token_que_TokenSesionExtension_puede_leer_de_vuelta()
    {
        var usuario = await CrearUsuarioAsync("conectar@x.test", Roles.GestorCae);

        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var resultado = await EmisorTokenExtension.EmitirAsync(usuario.Id, userManager, _protector);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ExpiraEnUtc.Should().BeCloseTo(DateTime.UtcNow.Add(TokenSesionExtension.Vigencia), TimeSpan.FromSeconds(5));

        var cargaUtil = TokenSesionExtension.Leer(_protector, resultado.Valor.Token);
        cargaUtil.Should().NotBeNull("el token que emite la página debe ser el mismo que consume ExtensionAuthenticationHandler");
        cargaUtil!.UsuarioId.Should().Be(usuario.Id);
        cargaUtil.SecurityStamp.Should().Be(usuario.SecurityStamp);
    }

    [Fact]
    public async Task Falla_cuando_el_usuario_no_existe()
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var resultado = await EmisorTokenExtension.EmitirAsync(Guid.NewGuid(), userManager, _protector);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Extension.UsuarioNoEncontrado");
    }

    [Fact]
    public async Task Falla_cuando_la_cuenta_no_tiene_security_stamp()
    {
        var usuario = await CrearUsuarioAsync("sin-stamp@x.test", Roles.GestorCae);

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        // UserManager.CreateAsync siempre asigna un security stamp cuando el
        // store lo soporta: la única forma de reproducir el caso real (cuenta
        // sin stamp) es quitarlo por debajo, directo en la fila.
        var fila = await contexto.Users.SingleAsync(u => u.Id == usuario.Id);
        fila.SecurityStamp = null;
        await contexto.SaveChangesAsync();

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var resultado = await EmisorTokenExtension.EmitirAsync(usuario.Id, userManager, _protector);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Extension.CuentaAMedioActivar");
    }

    private async Task<ApplicationUser> CrearUsuarioAsync(string email, string rol)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            TenantId = _tenant,
            TwoFactorEnabled = true,
        };

        (await userManager.CreateAsync(usuario)).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();

        return (await userManager.FindByIdAsync(usuario.Id.ToString()))!;
    }

    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
