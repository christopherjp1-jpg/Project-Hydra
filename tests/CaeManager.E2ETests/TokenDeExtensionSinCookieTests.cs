using System.Net.Http.Headers;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Regresión de un hallazgo real del MVP1 de extensión de navegador (ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio): ninguna
/// de las suites existentes (unitarios con fakes, integración con
/// <c>ExtensionAuthenticationHandler</c> construido a mano) ejercita el
/// pipeline HTTP real completo, así que ninguna detectó que
/// <c>TenantActual</c> devolvía el tenant correcto cuando la petición traía
/// cookie de sesión y <c>null</c> cuando solo traía el token de la extensión
/// — que es la situación real de la extensión, que nunca tiene cookie de
/// Hydra. Se vio primero a mano, contra un servidor local real, con
/// <c>fetch(..., {credentials: 'omit'})</c>.
///
/// <para>
/// Este test reproduce exactamente esa condición con <see cref="HttpClient"/>
/// puro (nunca envía cookies) y compara su resultado contra el mismo token
/// usado a través de una petición que SÍ lleva la cookie de la pestaña de
/// Playwright — si algún día <c>TenantActual</c> volviera a cachear su
/// resolución antes de tiempo, las dos respuestas divergerían otra vez (la de
/// sin-cookie a vacío) y este test lo notaría. No depende de qué acreditación
/// exacta haya sembrada -- solo de que las dos vías vean lo mismo -- porque
/// "AppCollection" es compartida con el resto de la suite E2E.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class TokenDeExtensionSinCookieTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Un_token_de_extension_sin_cookie_ve_lo_mismo_que_con_cookie()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/cuenta/extension");
        await page.GetByRole(AriaRole.Button, new() { Name = "Generar token" }).ClickAsync();

        var campoToken = page.Locator(".campo input");
        await Assertions.Expect(campoToken).Not.ToHaveValueAsync(
            string.Empty, new LocatorAssertionsToHaveValueOptions { Timeout = 15_000 });
        var token = await campoToken.InputValueAsync();

        var cookiesDeLaSesion = await contexto.CookiesAsync();
        var cabeceraCookie = string.Join("; ", cookiesDeLaSesion.Select(c => $"{c.Name}={c.Value}"));

        // Sin cookie en absoluto — la condición real de la extensión, que solo
        // conoce el token. Un HttpClient nuevo, nunca el de Playwright, para
        // no arrastrar ninguna cookie de la pestaña sin darse cuenta.
        using var clienteSinCookie = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var peticionSinCookie = new HttpRequestMessage(HttpMethod.Get, "/extension/acreditaciones-pendientes");
        peticionSinCookie.Headers.Authorization = new AuthenticationHeaderValue("Extension", token);
        var respuestaSinCookie = await clienteSinCookie.SendAsync(peticionSinCookie);
        respuestaSinCookie.EnsureSuccessStatusCode();
        var cuerpoSinCookie = await respuestaSinCookie.Content.ReadAsStringAsync();

        // Mismo token, pero con la cookie de la sesión añadida a mano —
        // reproduce lo que ya se sabía correcto (probado a mano) para tener
        // con qué comparar.
        using var clienteConCookie = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var peticionConCookie = new HttpRequestMessage(HttpMethod.Get, "/extension/acreditaciones-pendientes");
        peticionConCookie.Headers.Authorization = new AuthenticationHeaderValue("Extension", token);
        peticionConCookie.Headers.Add("Cookie", cabeceraCookie);
        var respuestaConCookie = await clienteConCookie.SendAsync(peticionConCookie);
        respuestaConCookie.EnsureSuccessStatusCode();
        var cuerpoConCookie = await respuestaConCookie.Content.ReadAsStringAsync();

        // Precondición: si la siembra de Refrielectric alguna vez deja de
        // tener acreditaciones pendientes, este test debe fallar diciendo
        // ESO, no confundirse con dos respuestas vacías "iguales" que no
        // demuestran nada.
        Assert.NotEqual("[]", cuerpoConCookie);

        // El token de extensión por sí solo debe ver exactamente la misma
        // cartera que la sesión interactiva — si TenantActual volviera a
        // cachear su resolución antes de que el esquema de extensión
        // autenticara, esta respuesta volvería a llegar vacía.
        Assert.Equal(cuerpoConCookie, cuerpoSinCookie);
    }
}
