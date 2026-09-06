using System.Security.Cryptography;
using ClosedXML.Excel;
using Microsoft.Playwright;
using PdfSharp.Pdf;

namespace CaeManager.E2ETests;

/// <summary>
/// Datos y utilidades compartidas por los tests E2E — credenciales de los
/// usuarios sembrados por IdentitySeeder/DatosPruebaSeeder (ver esas clases
/// en CaeManager.Infrastructure.Identity / Persistence.Seed) y helpers de
/// Playwright para los patrones repetidos de login/drawer que ya se
/// verificaban a mano con verificar_roles.js.
/// </summary>
public static class Ayudas
{
    public const string EmailAdministrador = "admin@caemanager.local";
    public const string ContrasenaAdministrador = "CaeManager#2026";

    /// <summary>
    /// Misma clave que IdentitySeeder.ClaveTotpAdministradorInicial (ver esa
    /// clase en CaeManager.Infrastructure.Identity) — duplicada aquí en vez
    /// de referenciada porque este proyecto de test no referencia
    /// Infrastructure (mismo criterio que NombreClienteDelegadoDemo); si
    /// cambia allí, este test debe actualizarse también. El Administrador
    /// inicial nace con 2FA activo (P1-13 de docs/business/MATURITY_REVIEW.md),
    /// así que IniciarSesionAsync tiene que poder calcular el código TOTP.
    /// </summary>
    public const string ClaveTotpAdministrador = "JBSWY3DPEHPK3PXP";

    public const string ContrasenaUsuariosPrueba = "Prueba#2026";

    /// <summary>
    /// Nombre del tenant Cliente Delegante que DelegacionDemoSeeder siembra
    /// para el Administrador inicial (ver esa clase en
    /// CaeManager.Infrastructure.Persistence.Seed) — duplicado aquí en vez de
    /// referenciado porque este proyecto de test no referencia Infrastructure
    /// (mismo criterio que EmailAdministradorSegundoTenant); si cambia allí,
    /// este test debe actualizarse también.
    /// </summary>
    public const string NombreClienteDelegadoDemo = "Laboratorios Dexter S.L. (Cliente Delegante demo)";

    /// <summary>Segundo Cliente Delegante de demo, sin datos de usuario propios — ver DelegacionDemoSeeder.NombreTenantClienteDemo2.</summary>
    public const string NombreClienteDelegadoDemo2 = "Transportes Planet Express S.A. (Cliente Delegante demo 2)";

    /// <summary>
    /// Nombre del tenant de origen del Administrador inicial (la Consultora,
    /// ADR-004 § 5.1) — mismo criterio de duplicación que
    /// NombreClienteDelegadoDemo: TenantSeedData vive en Infrastructure, que
    /// este proyecto de test no referencia.
    /// </summary>
    public const string NombreTenantOrigenPorDefecto = "Organización principal";

    /// <summary>
    /// La Consultora de la demo (ArcoSPA, no TALVEG — decisión del
    /// propietario del 2026-08-14: la cuenta de plataforma no debe operar
    /// ningún Delegated Workspace) y su Administrador propio, con el mismo
    /// 2FA fijo que el resto de la siembra (ver DelegacionDemoSeeder).
    /// </summary>
    public const string NombreTenantConsultora = "ArcoSPA Prevención S.L. (Consultora demo)";
    public const string EmailAdministradorConsultora = "admin.arcospa@caemanager.local";

    /// <summary>
    /// Operador Delegado de ArcoSPA con rol Consulta (ver
    /// DelegacionDemoSeeder.SembrarOperadoresConsultoraAsync), delegado sobre
    /// <see cref="NombreClienteDelegadoDemo"/> (Dexter) — el mismo workspace que
    /// el Administrador inicial, pero con otro rol. A diferencia de ese
    /// Administrador (rol GestorCae dentro del workspace delegado, HO-136-05:
    /// cero AsignacionCartera, alcance cero por diseño, ver AlcanceRolesTests),
    /// Consulta tiene <c>TieneAccesoTotalAsync</c> sin depender de cartera
    /// (AlcanceDatosService), así que SÍ ve las Empresas sembradas al entrar a
    /// ese workspace. Usarlo cuando el test necesite comprobar contenido
    /// visible tras un cambio de workspace sin que el resultado dependa de si
    /// alguien le asignó cartera.
    /// </summary>
    public const string EmailOperadorConsultaConsultora = "prueba.operador.consulta1@caemanager.local";

    /// <summary>Primer Cliente Delegante de la demo — la referencia de "empresa final" (ver DelegacionDemoSeeder.NombreTenantRefrielectric).</summary>
    public const string NombreTenantRefrielectric = "Refrielectric S.L. (Cliente Delegante demo)";

    /// <summary>
    /// GestorCae NATIVO del tenant Refrielectric (sembrado aparte de
    /// DelegacionDemoSeeder — no es un Operador Delegado, ver el comentario de
    /// <see cref="EmailOperadorConsultaConsultora"/> sobre la diferencia), con
    /// cartera real (AsignacionCartera) y acreditaciones de plataforma de
    /// demo ya sembradas. Mismo criterio de duplicación que el resto de
    /// constantes de esta clase: el nombre exacto vive en el seeder de
    /// Infrastructure, que este proyecto no referencia.
    /// </summary>
    public const string EmailGestorRefrielectric = "refri.gestorcae1@caemanager.local";

    public static string EmailPrueba(string rolEnMinusculas, int numero) =>
        $"prueba.{rolEnMinusculas}{numero}@caemanager.local";

    /// <summary>
    /// Cambia el "Cliente activo" (ver SelectorClienteActivo.razor) al
    /// Cliente Delegante indicado por nombre, usando el &lt;select&gt; real de
    /// la interfaz.
    ///
    /// Antes esto navegaba a mano al endpoint, saltándose el selector: el
    /// cambio lo disparaba un @onchange de Blazor que exigía tener el circuito
    /// ya interactivo (ida y vuelta por SignalR) y resultó intermitente — a
    /// veces el evento no llegaba a dispararse desde Playwright y el cliente
    /// activo no cambiaba sin dar ningún error visible. Desde el arreglo de
    /// M-8 el selector es un &lt;form&gt; HTML que hace POST, así que el envío
    /// lo hace el navegador sin depender de SignalR y se puede ejercitar la
    /// interfaz de verdad — que además es lo que hace el usuario.
    ///
    /// HO-136-05: <c>SelectOptionAsync</c> marca el &lt;option&gt; como
    /// seleccionado en el DOM del cliente antes de que el formulario llegue a
    /// enviarse — eso es obra de Playwright, no del servidor. Un caller que
    /// comprobara solo <c>option:checked</c> justo después daría por hecho el
    /// cambio de workspace aunque el POST nunca hubiera llegado a
    /// <c>/cuenta/cliente-activo</c> (o el servidor lo hubiera rechazado con
    /// 401/403): esa comprobación no distingue "el cliente marcó la opción"
    /// de "el servidor aplicó el cambio". La única prueba de que el servidor
    /// autorizó y escribió/borró la cookie es su propia respuesta HTTP.
    ///
    /// Medido por mutación (HO-136-05): un 3xx por sí solo NO basta. Bajo
    /// cookie authentication (<c>ConfigureApplicationCookie</c> en Program.cs)
    /// <c>Results.Forbid()</c>/<c>Results.Unauthorized()</c> no llegan al
    /// navegador como 401/403 — el middleware los convierte en un 302 hacia
    /// <c>LoginPath</c>/<c>AccessDeniedPath</c>, que sigue siendo un 3xx.
    /// Forzar el endpoint a denegar siempre (mutación de prueba) lo confirmó:
    /// el status seguía en rango 3xx y este assert no lo detectaba — el fallo
    /// solo aparecía dos pasos más tarde, en el <c>&lt;select&gt;</c>, con un
    /// mensaje que no apuntaba a la causa real. Por eso además de un 3xx se
    /// exige que el redirect NO aterrice en ninguna de esas dos rutas de
    /// autenticación/autorización.
    /// </summary>
    public static async Task CambiarClienteActivoAsync(IPage page, string baseUrl, string nombreCliente)
    {
        var opcion = page.Locator(".selector-cliente-activo option", new PageLocatorOptions { HasText = nombreCliente });
        var tenantId = await opcion.GetAttributeAsync("value");

        var respuestaCambio = await page.RunAndWaitForResponseAsync(
            () => page.SelectOptionAsync(".selector-cliente-activo", new SelectOptionValue { Value = tenantId }),
            respuesta => respuesta.Url.Contains("/cuenta/cliente-activo"));

        Assert.True(
            respuestaCambio.Status is >= 300 and < 400,
            $"POST a /cuenta/cliente-activo devolvió {respuestaCambio.Status} (se esperaba una redirección 3xx) " +
            $"al intentar cambiar a «{nombreCliente}» — el servidor no aplicó el cambio de workspace, así que " +
            "cualquier comprobación posterior sobre el <select> del cliente estaría midiendo una marca sin efecto.");

        var destinoRedirect = respuestaCambio.Headers.GetValueOrDefault("location") ?? string.Empty;
        Assert.False(
            destinoRedirect.Contains("acceso-denegado") || destinoRedirect.Contains("iniciar-sesion"),
            $"POST a /cuenta/cliente-activo redirigió a «{destinoRedirect}» al intentar cambiar a «{nombreCliente}» " +
            "— eso es LoginPath/AccessDeniedPath (ConfigureApplicationCookie en Program.cs), no un cambio de " +
            "workspace aplicado: Results.Forbid()/Unauthorized() llegan al navegador como un 3xx hacia esa ruta, " +
            "no como 401/403, así que el status por sí solo no bastaba para confirmar que el servidor autorizó el " +
            "cambio.");

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Descarta el modal de notificaciones pendientes (ver
    /// Features/Notificaciones/NotificacionesPopup.razor, montado en
    /// MainLayout) si aparece — se dispara en el primer render de cada
    /// circuito nuevo (recarga real de página) mientras el usuario tenga
    /// notificaciones sin leer, y bloquea toda interacción con la página
    /// (<c>CerrarAlHacerClicFuera="false"</c>) hasta que se descarta. Los
    /// usuarios <c>prueba.&lt;rol&gt;</c> de DatosPruebaSeeder arrancan con una
    /// notificación sin leer a propósito ("la campana no debe arrancar
    /// vacía") — sin este paso, cualquier test que inicie sesión con esos
    /// usuarios y luego interactúe con la página se bloquea contra el modal.
    /// No-op si no hay ninguna pendiente.
    /// </summary>
    public static async Task DescartarNotificacionesPendientesAsync(IPage page)
    {
        // Como mucho unas pocas notificaciones sembradas por usuario — el
        // límite evita un bucle infinito si el modal nunca llega a cerrarse.
        for (var intentos = 0; intentos < 8; intentos++)
        {
            var botonOmitir = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Omitir" });
            if (await botonOmitir.CountAsync() == 0) return;

            try
            {
                // Timeout corto y locator fresco en cada vuelta: justo tras el
                // login la página puede estar a mitad de la transición de
                // prerenderizado estático a circuito interactivo, y el DOM del
                // modal se sustituye entero en ese momento — un clic que cae
                // justo ahí ve el elemento "detached" y hay que reintentarlo
                // contra el nuevo DOM, no contra la misma referencia.
                await botonOmitir.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
            }

            await page.WaitForTimeoutAsync(300);
        }
    }

    /// <summary>
    /// Abre el desplegable "⋯" de <c>MenuAcciones.razor</c> a partir de su
    /// botón disparador y devuelve el panel ya abierto, listo para localizar
    /// dentro de él la acción que toque.
    ///
    /// El flake crónico del job E2E (siempre "Timeout 30000ms exceeded"
    /// esperando algo dentro de <c>.menu-acciones-panel</c>, cambiando de test
    /// entre ejecuciones del mismo commit) sale de que abrir el menú depende
    /// de un <c>@onclick</c> server-side: MenuAcciones vive en páginas
    /// <c>@rendermode InteractiveServer</c>, que se prerenderizan estáticas
    /// primero. El botón está en el DOM —visible, habilitado y perfectamente
    /// clicable— desde ese prerenderizado, pero su controlador no existe hasta
    /// que el componente se vuelve interactivo por el circuito; y un re-render
    /// simultáneo (QuickGrid reconstruyendo la fila tras refiltrar) puede
    /// invalidar el id del controlador de un clic ya en vuelo. En ambos casos
    /// el clic se pierde EN SILENCIO: Playwright lo da por entregado, el panel
    /// nunca llega a abrirse, y el fallo aparece 30s después en la espera
    /// siguiente. Por eso el arreglo no es subir el timeout — el clic no llega
    /// tarde, no llega.
    ///
    /// La única señal fiable de que el clic sí llegó al circuito es el
    /// <c>aria-expanded</c> del propio disparador, que Blazor renderiza desde
    /// <c>_abierto</c>. Y como <c>Alternar()</c> es un interruptor, reintentar
    /// a ciegas es peor que no reintentar: un segundo clic sobre un menú que
    /// SÍ había abierto lo vuelve a cerrar, y la espera posterior se come los
    /// 30s enteros contra un panel cerrado. De ahí las dos reglas de este
    /// helper: solo se clica tras confirmar que el menú sigue cerrado, y el
    /// método es idempotente (si ya está abierto, no lo toca).
    ///
    /// <paramref name="disparador"/> debe resolver a un único botón
    /// <c>.menu-acciones-disparador</c>; el panel se busca dentro del mismo
    /// <c>.menu-acciones</c> que ese botón, no en toda la página, así que
    /// nunca se confunde con el de otra fila.
    /// </summary>
    public static async Task<ILocator> AbrirMenuAccionesAsync(ILocator disparador)
    {
        var panel = disparador.Locator("xpath=..").Locator(".menu-acciones-panel");

        // El disparador tiene que existir y ser clicable antes de contar
        // intentos: si todavía no está en pantalla, el problema es otro y
        // debe reportarse como tal, no gastarse los reintentos.
        await disparador.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        for (var intento = 1; intento <= IntentosAbrirMenuAcciones; intento++)
        {
            if (await MenuAccionesSigueCerradoAsync(disparador))
                await disparador.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

            // 5s es enorme para una ida y vuelta de SignalR contra la app en
            // el mismo runner — si aria-expanded no ha cambiado en ese
            // tiempo, el clic no llegó al circuito y hay que repetirlo.
            if (!await EsperarMenuAccionesAbiertoAsync(disparador, TimeSpan.FromSeconds(5)))
                continue;

            // A partir de aquí el servidor ya sabe que el menú está abierto,
            // así que el panel es cuestión de que Blazor termine de parchear
            // el DOM — esperarlo (y no volver a clicar) es lo correcto.
            await panel.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            return panel;
        }

        throw new TimeoutException(
            $"El menú \"⋯\" (MenuAcciones.razor) no llegó a abrirse tras {IntentosAbrirMenuAcciones} clics: " +
            "aria-expanded se quedó en \"false\" cada vez, así que ningún clic llegó al circuito de Blazor " +
            "(componente aún no interactivo tras el prerenderizado, o controlador @onclick invalidado por un " +
            "re-render simultáneo).");
    }

    private const int IntentosAbrirMenuAcciones = 4;

    private const int IntentosSeleccionarPestana = 4;

    /// <summary>
    /// Selecciona una pestaña de <c>Pestanas.razor</c> confirmando que el clic
    /// llegó de verdad al circuito, y devuelve su locator ya activo.
    ///
    /// Mismo modo de fallo que <see cref="AbrirMenuAccionesAsync"/>, y por la
    /// misma razón: el botón <c>role="tab"</c> lleva un <c>@onclick</c>
    /// server-side (<c>ActivarAsync</c>), y las páginas que montan este
    /// componente son <c>@rendermode InteractiveServer</c>, o sea que se
    /// prerenderizan estáticas primero. Desde ese prerenderizado el botón está
    /// en el DOM —visible, habilitado y clicable— pero su controlador no existe
    /// hasta que el componente se vuelve interactivo por el circuito. Un clic
    /// que cae en esa ventana se pierde EN SILENCIO: Playwright lo da por
    /// entregado, la pestaña nunca se activa, y el fallo aparece 30 s después
    /// en la espera siguiente.
    ///
    /// Medido en CI (run 33649091982, intento 1, sobre 01aa56ba):
    /// <c>PestanaUrlDurableTests…carga_en_frio</c> falló con
    /// "Timeout 30000ms exceeded" y el registro de Playwright
    /// "waiting for navigation to …/documentos?Pestana=revision-ia until Load"
    /// en el <c>WaitForURLAsync</c> posterior al clic — es decir, el clic no
    /// llegó tarde: no llegó. El mismo test pasó en 2 s en el intento 2. Por
    /// eso el arreglo no es subir el timeout ni esperar más tiempo: es esperar
    /// una <b>señal</b> de que el clic sí llegó, y repetirlo si no llegó.
    ///
    /// La señal es <c>aria-selected</c>, que <c>Pestanas.razor</c> renderiza
    /// desde <c>PestanaActiva</c> — estado del servidor, no del navegador. Y al
    /// contrario que el menú "⋯", aquí reintentar a ciegas es seguro:
    /// <c>ActivarAsync</c> es idempotente (<c>id == PestanaActiva</c> no hace
    /// nada), no un interruptor, así que un segundo clic sobre una pestaña ya
    /// activa no la desactiva. De ahí que este helper no necesite la
    /// comprobación previa de "sigue cerrado" que sí necesita aquel.
    ///
    /// <b>Cómo reproducir el fallo a voluntad</b> (la carrera no se manifiesta
    /// en una máquina de desarrollo, que arranca el circuito antes de que dé
    /// tiempo a clicar — "pasa en local" no refuta un rojo de CI): retrasar el
    /// script que arranca el circuito y navegar sin esperarlo,
    /// <c>page.RouteAsync("**/blazor.web*.js", …Task.Delay(6000)…)</c> más
    /// <c>GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Commit })</c>.
    /// Con este helper el test pasa (reintenta hasta que el circuito responde);
    /// sustituyéndolo por un solo <c>ClickAsync</c> falla con exactamente el
    /// mismo texto que en CI — "waiting for navigation to … until Load" en
    /// <c>TaskHelper.WithTimeout</c>.
    /// </summary>
    public static async Task<ILocator> SeleccionarPestanaAsync(IPage page, ILocator pestana, string nombreParaElError)
    {
        await pestana.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        for (var intento = 1; intento <= IntentosSeleccionarPestana; intento++)
        {
            if (await pestana.GetAttributeAsync("aria-selected") == "true")
                return pestana;

            await pestana.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

            // 5 s es enorme para una ida y vuelta de SignalR contra la app en el
            // mismo runner — si aria-selected no ha cambiado en ese tiempo, el
            // clic no llegó al circuito y hay que repetirlo.
            if (await EsperarPestanaActivaAsync(pestana, TimeSpan.FromSeconds(5)))
                return pestana;
        }

        throw new TimeoutException(
            $"La pestaña \"{nombreParaElError}\" (Pestanas.razor) no llegó a activarse tras " +
            $"{IntentosSeleccionarPestana} clics: aria-selected se quedó en \"false\" cada vez, así que ningún " +
            "clic llegó al circuito de Blazor (componente aún no interactivo tras el prerenderizado). " +
            $"URL en ese momento: {page.Url}");
    }

    private static async Task<bool> EsperarPestanaActivaAsync(ILocator pestana, TimeSpan limite)
    {
        var vencimiento = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < vencimiento)
        {
            if (await pestana.GetAttributeAsync("aria-selected") == "true")
                return true;

            await Task.Delay(100);
        }

        return false;
    }


    /// <summary>
    /// Confirma que el menú sigue cerrado antes de (re)clicar. Lee
    /// <c>aria-expanded</c> dos veces separadas por un margen holgado frente a
    /// una ida y vuelta de SignalR: así un clic anterior que estuviera llegando
    /// justo en ese instante se ve aquí, en vez de que el clic nuevo lo anule
    /// cerrando un menú recién abierto.
    /// </summary>
    private static async Task<bool> MenuAccionesSigueCerradoAsync(ILocator disparador)
    {
        if (await disparador.GetAttributeAsync("aria-expanded") == "true")
            return false;

        await Task.Delay(250);
        return await disparador.GetAttributeAsync("aria-expanded") != "true";
    }

    private static async Task<bool> EsperarMenuAccionesAbiertoAsync(ILocator disparador, TimeSpan limite)
    {
        var vencimiento = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < vencimiento)
        {
            if (await disparador.GetAttributeAsync("aria-expanded") == "true")
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// Selecciona una fila de la bandeja unificada de <c>/comunicaciones</c>
    /// (<c>FilaConversacion.razor</c>) confirmando que el clic llegó de verdad
    /// al circuito de Blazor antes de devolver el control.
    ///
    /// Mismo modo de fallo que <see cref="AbrirMenuAccionesAsync"/> y por el
    /// mismo motivo: la fila es un <c>&lt;li&gt;</c> con <c>@onclick</c> dentro
    /// de una página <c>@rendermode InteractiveServer</c>, así que está en el
    /// DOM —visible, clicable— desde el prerenderizado estático, antes de que
    /// su controlador exista del lado servidor; y un re-render simultáneo (la
    /// carga del detalle del hilo anterior, o el refresco en tiempo real de
    /// <c>AlRecibirMensajeAsync</c>) puede invalidar el id del controlador de
    /// un clic ya en vuelo. En los dos casos el clic se pierde EN SILENCIO:
    /// Playwright lo da por entregado, la selección nunca ocurre, y el fallo
    /// aparece 30 s después en la espera siguiente — que en
    /// <c>DeepLinksTests</c> era un <c>WaitForURLAsync</c> y se comía el
    /// timeout entero sin decir por qué (job 99116263442, "Tests E2E
    /// (Playwright)" de main <c>eed3a648</c>: 33 s de test para 3 s de
    /// trabajo real). Por eso el arreglo no es subir el timeout — el clic no
    /// llega tarde, no llega.
    ///
    /// La señal fiable de que sí llegó es la clase <c>bandeja-fila-activa</c>,
    /// que Blazor renderiza desde <c>_conversacionSeleccionadaId</c> (ver
    /// Bandeja.razor): estado del servidor, no del navegador. A diferencia del
    /// menú "⋯", seleccionar no es un interruptor sino una asignación, así que
    /// reintentar es idempotente y no puede deshacer un clic que sí había
    /// llegado; aun así solo se reclica si la fila sigue sin estar activa.
    ///
    /// <paramref name="fila"/> debe resolver a un único <c>.bandeja-fila</c>.
    /// </summary>
    public static async Task SeleccionarFilaBandejaAsync(ILocator fila)
    {
        await fila.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        for (var intento = 1; intento <= IntentosSeleccionarFilaBandeja; intento++)
        {
            if (!await FilaBandejaActivaAsync(fila))
                await fila.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

            // Mismo margen que AbrirMenuAccionesAsync: 5 s es enorme para una
            // ida y vuelta de SignalR contra la app en el mismo runner, así
            // que si la clase no ha aparecido el clic no llegó al circuito.
            if (await EsperarFilaBandejaActivaAsync(fila, TimeSpan.FromSeconds(5)))
                return;
        }

        throw new TimeoutException(
            $"La fila de la bandeja no llegó a seleccionarse tras {IntentosSeleccionarFilaBandeja} clics: " +
            "nunca apareció la clase \"bandeja-fila-activa\" que Blazor renderiza desde " +
            "_conversacionSeleccionadaId (ver Bandeja.razor), así que ningún clic llegó al circuito " +
            "(componente aún no interactivo tras el prerenderizado, o controlador @onclick invalidado por " +
            "un re-render simultáneo).");
    }

    private const int IntentosSeleccionarFilaBandeja = 4;

    /// <summary>
    /// <c>bandeja-fila-activa</c> no es prefijo ni sufijo de ninguna otra clase
    /// de la fila (<c>bandeja-fila</c>, <c>bandeja-fila-esperando</c>), así que
    /// buscarla como subcadena del atributo no puede dar un falso positivo.
    /// </summary>
    private static async Task<bool> FilaBandejaActivaAsync(ILocator fila) =>
        (await fila.GetAttributeAsync("class"))?.Contains("bandeja-fila-activa") == true;

    private static async Task<bool> EsperarFilaBandejaActivaAsync(ILocator fila, TimeSpan limite)
    {
        var vencimiento = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < vencimiento)
        {
            if (await FilaBandejaActivaAsync(fila))
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    public static async Task IniciarSesionAsync(IPage page, string baseUrl, string email, string password)
    {
        await page.GotoAsync($"{baseUrl}/cuenta/iniciar-sesion");
        await page.FillAsync("#email", email);
        await page.FillAsync("#password", password);
        await page.ClickAsync("button[type=\"submit\"]");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Solo el Administrador inicial tiene 2FA activo hoy (P1-13 de
        // docs/business/MATURITY_REVIEW.md) — el resto de cuentas de prueba
        // pasan de largo por esta rama y siguen directas al dashboard.
        if (page.Url.Contains("/cuenta/verificar-2fa"))
        {
            await page.FillAsync("#codigo", GenerarCodigoTotp(ClaveTotpAdministrador));
            await page.ClickAsync("button[type=\"submit\"]");
        }

        await page.Locator(".nav-principal").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// TOTP de 6 dígitos (RFC 6238, HMAC-SHA1, paso de 30s) — el mismo
    /// algoritmo que <c>UserManager.VerifyTwoFactorTokenAsync</c> valida del
    /// lado servidor vía <c>AuthenticatorTokenProvider</c>. Sin paquete
    /// nuevo: es la única forma de que este proyecto de test calcule el
    /// código del Administrador inicial (ver ClaveTotpAdministrador) sin
    /// acceso a base de datos ni a Infrastructure.
    /// </summary>
    public static string GenerarCodigoTotp(string claveBase32, DateTimeOffset? momento = null)
    {
        var clave = DescodificarBase32(claveBase32);
        var contador = (long)(momento ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;

        var contadorBytes = BitConverter.GetBytes(contador);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(contadorBytes);

        using var hmac = new HMACSHA1(clave);
        var hash = hmac.ComputeHash(contadorBytes);

        var desplazamiento = hash[^1] & 0x0F;
        var codigoBinario =
            ((hash[desplazamiento] & 0x7F) << 24) |
            ((hash[desplazamiento + 1] & 0xFF) << 16) |
            ((hash[desplazamiento + 2] & 0xFF) << 8) |
            (hash[desplazamiento + 3] & 0xFF);

        return (codigoBinario % 1_000_000).ToString("D6");
    }

    private static byte[] DescodificarBase32(string base32)
    {
        const string alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bitsEnBuffer = 0;

        foreach (var caracter in base32.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alfabeto.IndexOf(caracter);
            bitsEnBuffer += 5;
            if (bitsEnBuffer < 8) continue;

            bitsEnBuffer -= 8;
            bytes.Add((byte)((buffer >> bitsEnBuffer) & 0xFF));
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// page.GotoAsync hace una navegación real de navegador (no un
    /// enrutado del lado cliente de Blazor) — cada llamada tira abajo el
    /// circuit de SignalR y lo reconecta desde cero. Si se interactúa
    /// (clic, fill…) justo después de GotoAsync sin esperar a que el
    /// circuit reconecte, el elemento ya está en el DOM (prerenderizado)
    /// pero su @onclick todavía no está cableado del lado servidor, así
    /// que el clic no hace nada — un timeout posterior en la siguiente
    /// espera, no un error inmediato. Esperar a "networkidle" (sin
    /// conexiones activas ~500ms, lo que cubre el handshake del
    /// WebSocket) es el mismo patrón ya usado con éxito en las
    /// verificaciones manuales previas de este proyecto.
    /// </summary>
    public static async Task NavegarYEsperarAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Resuelve el campo "Empresa" del drawer de alta de Trabajador
    /// (Trabajadores.razor), que renderiza de dos formas mutuamente
    /// excluyentes según el estado del tenant en ese instante: un
    /// combobox real cuando hay más de una Empresa, o un CampoInfo de
    /// solo lectura cuando DDL-076 (perfil Cliente Directo + una única
    /// Empresa) resuelve "en silencio" — ver _resolverEmpresaEnSilencio
    /// en Trabajadores.razor.cs. Cuál de los dos aparece depende del
    /// número de Empresas ya creadas por OTROS tests que comparten el
    /// mismo tenant en "AppCollection", así que no es fijo por test.
    ///
    /// Comprobar comboEmpresa.CountAsync() inmediatamente después de abrir
    /// el drawer es una carrera real (visto en CI): Blazor todavía no ha
    /// terminado de decidir/renderizar cuál de las dos ramas le toca, así
    /// que un CountAsync() prematuro puede leer "0" aunque el combobox
    /// esté a punto de aparecer, y el resto del test acaba esperando el
    /// campo equivocado. Se espera primero a que cualquiera de los dos
    /// esté realmente visible, y solo entonces se decide la rama.
    /// </summary>
    public static async Task SeleccionarEmpresaEnDrawerTrabajadorAsync(ILocator drawer, string razonSocialEmpresa)
    {
        var comboEmpresa = drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" });
        var infoEmpresa = drawer.Locator(".campo-info-valor", new LocatorLocatorOptions { HasText = razonSocialEmpresa });

        await comboEmpresa.Or(infoEmpresa).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        if (await comboEmpresa.CountAsync() > 0)
        {
            await comboEmpresa.SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
        }
        else
        {
            await infoEmpresa.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        }
    }

    /// <summary>
    /// Genera un PDF de una página válido con PDFsharp — la misma librería
    /// que usa ConversorArchivosPdf en producción para combinar/leer los
    /// archivos subidos — para que el flujo de subida real (import vía
    /// PdfReader.Open) tenga un archivo que de verdad pueda parsear, en vez
    /// de unos bytes con cabecera "%PDF" pero sin estructura real. Página en
    /// blanco a propósito, sin texto: dibujar texto requiere un
    /// IFontResolver (ver EmbeddedFontResolver de CaeManager.Web, registrado
    /// en su propio Program.cs) que este proceso de test, al no arrancar esa
    /// app in-process, nunca tiene configurado — una página vacía sigue
    /// siendo un PDF perfectamente válido y parseable, y es lo único que
    /// hace falta para probar el flujo de subida.
    /// </summary>
    public static string GenerarPdfDePruebaEnDisco(string nombreArchivo = "documento-prueba.pdf")
    {
        using var documento = new PdfDocument();
        documento.AddPage();

        var ruta = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{nombreArchivo}");
        documento.Save(ruta);
        return ruta;
    }

    /// <summary>Mismo algoritmo que DatosPruebaSeeder.GenerarCifValido (ver ese archivo) — CIF sintético válido, letra 'B'.</summary>
    public static string GenerarCifValido(int numero)
    {
        var digitos = numero.ToString("D7");
        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoControl = residuo == 0 ? 0 : 10 - residuo;
        return $"B{digitos}{digitoControl}";
    }

    /// <summary>Mismo algoritmo que DatosPruebaSeeder.GenerarDniValido — DNI sintético con dígito de control válido.</summary>
    public static string GenerarDniValido(int numero)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    /// <summary>
    /// Vuelve inválido un CIF generado por <see cref="GenerarCifValido"/> sin
    /// tocar su formato (letra + 7 dígitos + dígito de control) — solo
    /// cambia el dígito de control por uno distinto, así que
    /// ValidadorIdentificacion.Analizar lo sigue reconociendo como
    /// TipoIdentificacion.NifEmpresa pero con EsValido=false. Para los tests
    /// de importación que deliberadamente prueban la fila "CIF no válido".
    /// </summary>
    public static string InvalidarCif(string cifValido)
    {
        var ultimoDigito = cifValido[^1];
        var sustituto = ultimoDigito == '0' ? '1' : '0';
        return cifValido[..^1] + sustituto;
    }

    /// <summary>
    /// Guarda un libro ClosedXML ya construido por el test en un archivo
    /// temporal — mismo patrón que GenerarPdfDePruebaEnDisco (SetInputFilesAsync
    /// necesita una ruta real en disco). ClosedXML es la misma librería que
    /// ya usan ClosedXmlPlantillaClientesService/ClosedXmlPlantillaCombinadaService/
    /// ClosedXmlPlantillaDocumentosService/ClosedXmlImportacionParser en
    /// producción para generar y leer estos mismos formatos — cada test
    /// construye el libro con las columnas exactas que ese parser espera
    /// (documentadas en cada uno de esos archivos), no una plantilla
    /// genérica de conveniencia.
    /// </summary>
    public static string GuardarLibroDePruebaEnDisco(XLWorkbook libro, string nombreArchivo)
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{nombreArchivo}");
        libro.SaveAs(ruta);
        return ruta;
    }

    /// <summary>
    /// Lee el número mostrado por una TarjetaMetrica de las pantallas de
    /// importación ("Clientes nuevos", "Documentos creados"…) — cada
    /// etiqueta es única dentro de la pantalla, así que HasText sobre
    /// ".tarjeta-metrica" no ambigua entre tarjetas.
    /// </summary>
    public static async Task<string> LeerMetricaAsync(IPage page, string etiqueta)
    {
        var tarjeta = page.Locator(".tarjeta-metrica", new PageLocatorOptions { HasText = etiqueta });
        return (await tarjeta.Locator(".tarjeta-metrica-valor").InnerTextAsync()).Trim();
    }
}
