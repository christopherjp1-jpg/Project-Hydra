using CaeManager.Infrastructure.Identity;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="Roles.AlcanzaTodaLaOrganizacion"/> lo consultan dos sitios con
/// preguntas simétricas: <c>AlcanceDatosService</c> lo usa para decidir de
/// verdad si el usuario que mira ve todo —y con ello restringe datos—, y la
/// columna "Alcance" de /usuarios lo usa para decir qué alcanzan los demás.
/// Añadir o quitar un rol de esa lista cambia las dos cosas a la vez, así que
/// el conjunto se fija aquí.
///
/// <para>
/// Vive en esta suite y no en una de Infrastructure porque no hay proyecto de
/// tests de Infrastructure, y esta es la que referencia
/// <c>Infrastructure.Identity</c> sin necesitar base de datos: es una función
/// pura de una cadena.
/// </para>
/// </summary>
public class RolesQueAlcanzanTodaLaOrganizacionTests
{
    [Theory]
    // Consulta entra aunque sea de solo lectura: alcance y autoridad son ejes
    // distintos — ve toda la organización, no puede escribir en ella.
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.Consulta)]
    public void Alcanzan_toda_la_organizacion_sin_cartera(string rol) =>
        Roles.AlcanzaTodaLaOrganizacion(rol).Should().BeTrue();

    [Theory]
    // Los dos roles de cartera y el usuario de portal: su alcance sale de una
    // Asignación de Cartera o de su empresa vinculada, nunca del rol.
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Cliente)]
    // Sin rol resuelto, fallo cerrado: una sesión privilegiada devuelve null
    // aquí a propósito (no es miembro del workspace que visita) y su acceso se
    // decide antes, por capacidad — ver AlcanceDatosService.TieneAccesoTotalAsync.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RolInventado")]
    public void No_alcanzan_nada_por_el_rol(string? rol) =>
        Roles.AlcanzaTodaLaOrganizacion(rol).Should().BeFalse();
}
