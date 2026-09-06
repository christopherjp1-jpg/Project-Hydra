using CaeManager.Web.Features.Usuarios.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El buscador de la lista de usuarios filtra en memoria sobre nombre y correo.
/// Si la comparación fuese ordinal, teclear "martinez" no encontraría a
/// "Martínez" y la lista saldría vacía sin decir por qué — el gestor concluye
/// que esa persona no está dada de alta y la crea otra vez. El fallo es mudo,
/// así que se fija aquí.
/// </summary>
public class BusquedaUsuariosIgnoraAcentosTests
{
    [Theory]
    [InlineData("Ana Martínez", "martinez")]   // el término sin tilde encuentra el texto con tilde
    [InlineData("Ana Martinez", "martínez")]   // y al revés: quien sí la escribe tampoco se queda fuera
    [InlineData("Ana Martínez", "MARTÍNEZ")]   // mayúsculas
    [InlineData("Iñaki Núñez", "inaki nunez")] // eñe y varias tildes en el mismo término
    [InlineData("a.martinez@talveg.es", "MARTINEZ")]
    public void Encuentra_ignorando_mayusculas_y_acentos(string texto, string termino) =>
        Usuarios.Contiene(texto, termino).Should().BeTrue();

    [Theory]
    [InlineData("Ana Martínez", "lopez")]
    [InlineData("", "ana")]
    public void No_encuentra_lo_que_no_esta(string texto, string termino) =>
        Usuarios.Contiene(texto, termino).Should().BeFalse();
}
