using ApexCharts;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Web.Api.Comercial;
using CaeManager.Web.Api.Integraciones;
using CaeManager.Web.Api.V1;
using CaeManager.Web.Components;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Asignaciones;
using CaeManager.Web.Features.AsistenteIa;
using CaeManager.Web.Features.Auditoria;
using CaeManager.Web.Features.BusquedaGlobal;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Clientes;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Empresas;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Features.Facturacion;
using CaeManager.Web.Features.Incidencias;
using CaeManager.Web.Features.Integraciones.Endpoints;
using CaeManager.Web.Features.Subcontratas;
using CaeManager.Web.Features.Plataforma;
using CaeManager.Web.Features.Tenants;
using CaeManager.Web.Features.Trabajadores;
using CaeManager.Web.Reportes;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PdfSharp.Fonts;
using Sentry;
using Serilog;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

// El resolver de fuentes de PDFsharp 6 es global e independiente del ciclo de
// vida de DI — se registra una sola vez al arrancar (ver EmbeddedFontResolver).
GlobalFontSettings.FontResolver = new EmbeddedFontResolver();

var builder = WebApplication.CreateBuilder(args);

// Todo el producto es en español (ver UX_PATTERNS.md) — fechas y números se
// formatean con la cultura es-ES en toda la aplicación, no por pantalla.
var culturaEspanola = new CultureInfo("es-ES");
CultureInfo.DefaultThreadCurrentCulture = culturaEspanola;
CultureInfo.DefaultThreadCurrentUICulture = culturaEspanola;

// Logging estructurado con Serilog — sustituye al proveedor de logging por
// defecto de Microsoft.Extensions.Logging (la sección "Logging" del
// appsettings ya no se usa; los niveles ahora se leen de "Serilog", con el
// mismo mecanismo de env vars que el resto de la app, p. ej.
// Serilog__MinimumLevel__Default=Warning). Los sitios que ya hacen
// logger.LogInformation/LogWarning/LogError (IdentitySeeder,
// DatosPruebaSeeder) no necesitan cambios: solo se sustituye el proveedor,
// no la API de ILogger.
//
// La ruta del sink de archivo sigue el mismo patrón que
// DataProtection:RutaClaves / AlmacenamientoArchivos:Ruta (relativa al
// content root si no es absoluta) en vez de vivir dentro del JSON de
// Serilog, para poder fijarla con una única variable de entorno con el
// mismo estilo de clave en español que el resto de esta app.
var rutaLogs = builder.Configuration["Logging:RutaArchivo"] ?? "App_Data/logs/log-.txt";
var rutaLogsAbsoluta = Path.IsPathRooted(rutaLogs)
    ? rutaLogs
    : Path.Combine(builder.Environment.ContentRootPath, rutaLogs);

// Sink de logs en la nube (Seq): inerte mientras "Serilog:Seq:ServerUrl" no
// esté configurado — mismo patrón "funciona sin configurar, se endurece con
// una variable de entorno en producción" que Sentry, KMS o Backups. Sin él,
// los logs viven solo en el volumen del contenedor y desaparecen con él, que
// es justo lo que hace indiagnosticable un incidente (P1-10 de
// docs/business/MATURITY_REVIEW.md). Seq acepta tanto una instancia propia
// como Seq cloud; la ApiKey es opcional porque una instancia sin
// autenticación no la pide.
var urlSeq = builder.Configuration["Serilog:Seq:ServerUrl"];
var apiKeySeq = builder.Configuration["Serilog:Seq:ApiKey"];

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // Sin esto, los eventos de varias réplicas o de varios despliegues
        // se mezclan sin poder separarse una vez en la nube.
        .Enrich.WithProperty("Aplicacion", "CaeManager")
        .Enrich.WithProperty("Entorno", context.HostingEnvironment.EnvironmentName)
        .WriteTo.Console()
        .WriteTo.File(rutaLogsAbsoluta, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31);

    if (!string.IsNullOrWhiteSpace(urlSeq))
        loggerConfiguration.WriteTo.Seq(urlSeq, apiKey: apiKeySeq);
});

// Error tracking con Sentry — si "Sentry:Dsn" no está configurado (hoy, en
// todos los entornos: no hay cuenta de Sentry provisionada todavía), la SDK
// queda inerte por diseño propio: no envía nada, no lanza, no bloquea el
// arranque. IMPORTANTE: hay que pasar explícitamente "" (no null) para que
// quede inerte — un Dsn null hace que Sentry.SentrySdk.InitHub lance
// ArgumentNullException en el arranque en vez de desactivarse en silencio
// (comprobado en local, no es el comportamiento que sugiere la documentación
// a primera vista). El middleware de Sentry se registra internamente vía
// IStartupFilter y envuelve TODO el pipeline HTTP, incluido
// app.UseExceptionHandler("/Error", ...) más abajo — captura la excepción
// real para reportarla y la deja seguir su curso normal hacia la página de
// error genérica ya existente (ver ARCHITECTURE.md, "Excepciones reservadas
// para errores verdaderamente inesperados").
builder.WebHost.UseSentry(options =>
{
    options.Dsn = builder.Configuration["Sentry:Dsn"] ?? string.Empty;
    options.Environment = builder.Environment.EnvironmentName;
    options.SendDefaultPii = false;

    // D-2: CaptureFailedRequests viene a true por defecto en Sentry.AspNetCore
    // (FailedRequestTargets = [".*"], FailedRequestStatusCodes = 500-599),
    // así que captura TODA petición HTTP saliente que responda 5xx — incluida
    // la que hace el propio exportador OTLP contra Seq (más abajo) cuando Seq
    // está caído. El exportador no tiene backoff propio, así que un Seq caído
    // sostenido generaba un evento de Sentry por cada POST fallido a
    // /ingest/otlp/v1/* — 6.362 en 6 días fue el issue DOTNET-7: el propio
    // recolector de errores queda instrumentado por su compañero de
    // instrumentación. Recuperar Seq no es competencia de este filtro (exige
    // el VPS); lo que sí es responsabilidad de este código es que esa caída
    // no ahogue el resto de Sentry. Ver CortacircuitoEventosSeqCaido para el
    // porqué de "acotado a Seq" y "throttle, no silencio total".
    if (!string.IsNullOrWhiteSpace(urlSeq))
    {
        var cortacircuitoSeq = new CortacircuitoEventosSeqCaido(urlSeq);
        options.SetBeforeSend((sentryEvent, _) => cortacircuitoSeq.DebeDescartar(sentryEvent) ? null : sentryEvent);
    }
});

// OpenTelemetry (Horizonte 2.3 del plan macro): trazas de MediatR (un span
// por Command/Query, ver LoggingBehavior/Observabilidad), HTTP entrante y
// saliente (incluidas las llamadas de AsistenteIa a Anthropic/Gemini/Mistral
// OCR) y EF Core, más las 4 métricas que pide el plan (latencia por comando,
// profundidad de cola de IA, circuitos activos, documentos
// procesados/hora — ver Observabilidad.cs y ProcesadorAnalisisDocumentoHostedService)
// — todo exportado por OTLP al mismo Seq que ya recibe los logs
// correlacionados (WriteTo.Seq más arriba). Seq habla OTLP nativamente desde
// 2024.1 (trazas y métricas, endpoints /ingest/otlp/v1/*), así que esto
// reutiliza el backend ya desplegado (docker-compose.produccion.yml) en vez
// de sumar Jaeger/Prometheus/Grafana para un despliegue de un solo operador
// — la misma razón por la que el plan lo sugiere como primera opción.
// Reutiliza las dos variables del sink de logs de arriba: el mismo
// "Serilog:Seq:ApiKey" también autentica el ingest de OTLP (cabecera
// X-Seq-ApiKey, ver la documentación de Seq), y el mismo principio "inerte
// por defecto" — sin "Serilog:Seq:ServerUrl" no se registra ningún pipeline
// de OpenTelemetry, ni exportador ni instrumentación. El
// ActivitySource/Meter de Observabilidad siguen existiendo igualmente
// (LoggingBehavior y el resto los usan sin condición), pero sin listener
// StartActivity devuelve null y las métricas no tienen a quién exportar: el
// coste es marginal, no una llamada de red que reintentar en bucle.
if (!string.IsNullOrWhiteSpace(urlSeq))
{
    var otlpTracesUrl = new Uri($"{urlSeq.TrimEnd('/')}/ingest/otlp/v1/traces");
    var otlpMetricsUrl = new Uri($"{urlSeq.TrimEnd('/')}/ingest/otlp/v1/metrics");

    void ConfigurarExportadorSeq(OtlpExporterOptions opciones, Uri endpoint)
    {
        opciones.Endpoint = endpoint;
        opciones.Protocol = OtlpExportProtocol.HttpProtobuf;
        if (!string.IsNullOrWhiteSpace(apiKeySeq))
            opciones.Headers = $"X-Seq-ApiKey={apiKeySeq}";
    }

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(recurso => recurso.AddService(
            "CaeManager", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(tracing => tracing
            // MediatR: ver Observabilidad.ActivitySource / LoggingBehavior.
            .AddSource(Observabilidad.NombreOrigen)
            // HTTP entrante (páginas Razor, endpoints, API v1) y saliente
            // (Anthropic/Gemini/Mistral OCR, Microsoft Graph...).
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // EF Core: la traza real de cada SELECT/INSERT/UPDATE — lo que
            // permite ver si una latencia alta de comando viene de la cola
            // de PuertaAccesoDatos o de la consulta misma.
            .AddEntityFrameworkCoreInstrumentation()
            .AddOtlpExporter(opciones => ConfigurarExportadorSeq(opciones, otlpTracesUrl)))
        .WithMetrics(metrics => metrics
            .AddMeter(Observabilidad.NombreOrigen)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opciones => ConfigurarExportadorSeq(opciones, otlpMetricsUrl)));
}

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
// Identidad de AUDITORIA, separada de la de autorizacion (ADR-011 § 8.5): hoy
// resuelven al mismo usuario, pero solo la primera sera simulable el dia que
// exista la impersonacion.
builder.Services.AddScoped<IActorAuditoria, ActorAuditoriaDesdeSesion>();
builder.Services.AddScoped<IClienteActivoSeleccionado, CaeManager.Web.Services.ClienteActivoSeleccionado>();
builder.Services.AddScoped<CaeManager.Application.Tenants.IVistaVocabularioPreviewService, CaeManager.Web.Services.VistaVocabularioPreviewCookie>();
builder.Services.AddScoped<ITenantActual, CaeManager.Web.Services.TenantActual>();
// Scoped: cachea por circuito si la sesión es de soporte, para que registrar
// una interacción no cueste una consulta (ver TrazaSoporteService).
builder.Services.AddScoped<CaeManager.Web.Services.TrazaSoporteService>();
builder.Services.AddScoped<CaeManager.Web.Services.ActividadUsuarioService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<BusquedaGlobalService>();
builder.Services.AddScoped<AsistenteIaService>();
builder.Services.AddScoped<CaeManager.Web.Components.Workspace.ContextWorkspaceService>();
builder.Services.AddApexCharts();

builder.Services.AddCascadingAuthenticationState();
var authenticationBuilder = builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    });
authenticationBuilder.AddIdentityCookies();

// API pública (P3-29, docs/business/MATURITY_REVIEW.md) — todavía no
// anunciada/publicada, pero completa: esquema propio para no heredar el
// FallbackPolicy de cookie (ver policy "ApiPublica" más abajo). El tenant se
// resuelve del claim que rellena el propio handler a partir de la clave, no
// de un parámetro suelto — ver docs/MULTITENANCY.md § 8.
authenticationBuilder.AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationSchemeOptions.NombreEsquema, options => { });

// Extensión de navegador (MVP1 de integración con plataformas CAE). Esquema
// propio por el mismo motivo que el de arriba, pero con una diferencia de
// fondo: este NO fabrica claims ni fija un rol: reconstruye el principal del
// usuario con TenantClaimsPrincipalFactory, la misma fábrica del login por
// cookie. Así la extensión ve exactamente la cartera de quien la usa, no todo
// el tenant — ver ExtensionAuthenticationHandler.
authenticationBuilder.AddScheme<ExtensionAuthenticationSchemeOptions, ExtensionAuthenticationHandler>(
    ExtensionAuthenticationSchemeOptions.NombreEsquema, options => { });

// Login corporativo vía Microsoft Entra ID (SSO), opcional — ver
// AzureAdOptions y RestriccionLoginLocalClaimsTransformation. Sin las tres
// variables configuradas, este proveedor externo ni se registra: el login
// local sigue siendo el único camino y se comporta exactamente igual que
// hoy (mismo principio "inerte por defecto" que Sentry/Backups/Anthropic).
var azureAd = builder.Configuration.GetSection(AzureAdOptions.SeccionConfiguracion).Get<AzureAdOptions>() ?? new AzureAdOptions();
if (azureAd.EstaConfigurado)
{
    authenticationBuilder.AddOpenIdConnect(IdentityEndpointsExtensions.EsquemaMicrosoft, "Microsoft (empresa)", options =>
    {
        options.Authority = $"{azureAd.Instance}{azureAd.TenantId}/v2.0";
        options.ClientId = azureAd.ClientId;
        options.ClientSecret = azureAd.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.CallbackPath = "/signin-microsoft";
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
    });
}

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/cuenta/iniciar-sesion";
    options.AccessDeniedPath = "/acceso-denegado";

    // D-3 (Sentry DOTNET-8, confirmado contra el evento real, no solo el
    // informe): el crash no nace en MainLayout/Blazor — Error.razor ya no
    // depende de ese layout (ver Error.razor) — sino AQUÍ, en
    // AuthenticationMiddleware, antes de que Blazor decida nada.
    // SecurityStampValidator revalida el security stamp del usuario contra
    // la base periódicamente en cada petición autenticada; si la base está
    // caída, agota los 6 reintentos de ConfiguracionDeContexto y lanza
    // RetryLimitExceededException justo en la petición a /Error — la propia
    // pantalla que debería informar del fallo. /Error no muestra nada que
    // dependa del rol ni de si el stamp sigue siendo válido, así que saltarse
    // la revalidación ahí no cede ninguna autorización real.
    OmitirRevalidacionDeStampEnRuta.Configurar(options, "/Error");
});

builder.Services.AddAuthorization(options =>
{
    // Toda página/endpoint requiere sesión iniciada salvo que declare [AllowAnonymous]
    // (como Login) — ver ARCHITECTURE.md, "Autenticación y autorización".
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Solo el esquema ApiKey — sin esto, el FallbackPolicy (que no fija
    // esquema) aceptaría también la cookie de Identity, y un usuario con
    // sesión de navegador podría llamar a /api/v1 sin clave.
    options.AddPolicy("ApiPublica", policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationSchemeOptions.NombreEsquema)
        .RequireAuthenticatedUser());

    // Lo que sirve tanto a una pestaña de Hydra como a la extensión: la
    // descarga del PDF de un Documento. Ambos esquemas explícitos porque el
    // FallbackPolicy no fija ninguno y aceptaría solo el de por defecto.
    //
    // Extension va PRIMERO a propósito. Cuando una política nombra varios
    // esquemas, el middleware autentica con todos y FUSIONA las identidades;
    // si una petición trajera a la vez cabecera y cookie, el orden decide cuál
    // gana en FindFirst. Que gane la que el llamante presentó explícitamente
    // es lo predecible.
    options.AddPolicy(Policies.SesionOExtension, policy => policy
        .AddAuthenticationSchemes(
            ExtensionAuthenticationSchemeOptions.NombreEsquema,
            IdentityConstants.ApplicationScheme)
        .RequireAuthenticatedUser());

    // DEC-36 (REC-099): "Administrador del Tenant propietario, mediante
    // permiso específico" — el rol Administrador es necesario pero no
    // suficiente para consultar RegistroAccesoDocumentoSensible. El permiso
    // viaja como claim de sesión (ver TenantClaimsPrincipalFactory), no como
    // consulta a base en cada petición.
    options.AddPolicy(
        CaeManager.Infrastructure.Identity.Policies.ConsultarAccesoDocumentosSensibles,
        policy => policy
            .RequireRole(CaeManager.Infrastructure.Identity.Roles.Administrador)
            .RequireClaim(CaeManager.Infrastructure.Identity.TenantClaimsPrincipalFactory.TipoClaimPermisoConsultarAccesoDocumentosSensibles));
});

// Rate limiting de la API pública, por tenant (no por IP — varias
// integraciones del mismo cliente comparten cupo, coherente con "por tenant"
// del ítem P3-29). Configurable sin recompilar, mismo patrón que
// RetencionDatosOptions.
var rateLimitPorMinuto = builder.Configuration.GetValue("ApiPublica:RateLimitPorMinuto", 300);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ApiPublica", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value ?? "sin-tenant",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPorMinuto,
            Window = TimeSpan.FromMinutes(1),
        }));
});

// Los enums viajan por JSON como CADENA, no como número. Sin esto salían
// como el ordinal implícito de su posición en la declaración, de modo que
// insertar un valor en medio de EstadoDocumento habría cambiado en silencio
// el significado de todo lo ya entregado a los consumidores de la API. Peor:
// el mismo número significaba cosas distintas según el enum — 0 era SinCaducidad
// en EstadoDocumento y Vigente en EstadoCentro, así que quien tratara ambos
// como "estado" leía lo peor como lo mejor.
//
// El contrato era además asimétrico: los filtros de entrada (?estado=Vencido)
// siempre se han parseado por NOMBRE, así que el cliente enviaba una cadena y
// recibía un número.
//
// Afecta solo a la API pública: es la única superficie que devuelve JSON
// (verificado — el resto de endpoints devuelven archivos, redirecciones o
// códigos de estado, y ningún JavaScript del cliente consume endpoints).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>();
});

// Rate limiting por IP sobre los POST de autenticación en /cuenta/* (login
// local, callback de Microsoft, verificación 2FA) — junto con el lockout de
// Identity, cierra el hallazgo P0-2 de docs/business/MATURITY_REVIEW.md
// (fuerza bruta sin fricción): el lockout protege cada cuenta concreta, este
// límite frena el barrido de muchas cuentas distintas desde una misma IP.
// Solo se limitan los POST — un GET a /cuenta/iniciar-sesion es simplemente
// cargar la página, y limitarlo también castigaba tráfico legítimo: un
// runner de CI (o un usuario real detrás de un NAT/proxy compartido) hace
// varias cargas de página desde la misma IP y se quedaba sin poder ni ver el
// formulario de login (regresión real, encontrada en los E2E de este mismo
// cambio — devolvía 429 antes de que existiera nada que enviar). El resto de
// la aplicación no se limita: es Blazor Server con sesión iniciada, el
// tráfico útil viaja por el circuito SignalR, no por peticiones HTTP
// repetidas. Limitador en memoria: suficiente mientras el techo sea 1
// réplica (autodocumentado en ARCHITECTURE.md); con multi-réplica habría que
// moverlo a un almacén compartido, igual que el resto de estado de proceso.
// Techos configurables con los mismos valores de siempre por defecto: la
// suite E2E hace logins reales en serie (cada login del Administrador son
// DOS POST anónimos: credenciales + código 2FA) y supera los 10/min desde
// 127.0.0.1 — exactamente el patrón de fuerza bruta que este límite corta,
// solo que aquí es tráfico legítimo de test. El fixture E2E sube el techo
// por variable de entorno (ver WebAppFixture); en producción no hay ninguna
// configuración y aplican los valores de siempre.
var limiteCuentaAnonimo = builder.Configuration.GetValue("RateLimiting:Cuenta:LimiteAnonimo", 10);
var limiteCuentaAutenticado = builder.Configuration.GetValue("RateLimiting:Cuenta:LimiteAutenticado", 60);

builder.Services.AddRateLimiter(opciones =>
{
    opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opciones.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(contexto =>
    {
        var esPostDeCuenta = HttpMethods.IsPost(contexto.Request.Method)
            && contexto.Request.Path.StartsWithSegments("/cuenta");

        if (!esPostDeCuenta)
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("sin-limite");

        // Con sesión iniciada (cambio de Delegated Workspace vía
        // /cuenta/cliente-activo, cerrar sesión) el margen es holgado — el
        // objetivo son los anónimos que martillean el login.
        var limite = contexto.User.Identity?.IsAuthenticated == true ? limiteCuentaAutenticado : limiteCuentaAnonimo;

        // La IP real ya está resuelta: UseForwardedHeaders corre al principio
        // del pipeline (ver más abajo) y el middleware de rate limiting actúa
        // después, por petición.
        var ip = contexto.Connection.RemoteIpAddress?.ToString() ?? "desconocida";

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            $"{limite}:{ip}",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = limite,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

// CircuitOptions explícitas (ADR-008 en Project-Hydra-Negocio, Horizonte 2.1
// del plan macro) — hasta esta medición la app corría con los defaults de
// ASP.NET Core sin examinarlos. El VPS de producción (Hetzner CX22: 2 vCPU,
// ~3.7 GB, ~2.8 GB disponibles, Postgres compartiendo la misma máquina) no
// tiene margen de RAM que sobre; los defaults de .NET 10 están pensados para
// un despliegue genérico, no para este presupuesto concreto.
//
// Medido con tools/CargaCircuitos (harness de Playwright, no incluido en
// CaeManager.slnx — ver ese proyecto): hasta 160 circuitos concurrentes
// reales (login + interacción sostenida sobre /documentos) sin errores, con
// ~1.8 MB de RAM marginal por circuito — la memoria no es el recurso que se
// agota a esta escala, muy por debajo de los "10 usuarios concurrentes
// iniciales, con crecimiento moderado" de ARCHITECTURE.md. El ajuste de abajo
// no reacciona a un problema medido; es gestión preventiva de un presupuesto
// de RAM ajustado:
// PersistedCircuitInMemoryMaxRetained (novedad de .NET 10, estado persistido
// de componentes para reconexión) viene en 1000 por defecto — a ~1.8 MB por
// circuito eso es un peor caso de más de 1.5 GB solo de circuitos persistidos,
// más de la mitad del presupuesto disponible del VPS. Se acota al mismo orden
// de magnitud que el techo medido, con margen. DisconnectedCircuitMaxRetained
// se reduce a la mitad por el mismo motivo (circuitos desconectados
// acumulados por usuarios que cierran el portátil sin salir). El resto de
// valores se deja en su default documentado — no hay evidencia de esta
// medición que justifique tocarlos.
//
// Configurables (mismo patrón que RateLimiting:Cuenta:* más arriba) para
// poder ajustarlos en producción sin recompilar si la telemetría real (una
// vez haya observabilidad, ver RUNBOOK-HORIZONTE-0.md § 0.3) apunta a otro
// número.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(opcionesCircuito =>
    {
        opcionesCircuito.DisconnectedCircuitMaxRetained =
            builder.Configuration.GetValue("Circuit:DisconnectedCircuitMaxRetained", 50);
        opcionesCircuito.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(
            builder.Configuration.GetValue("Circuit:DisconnectedCircuitRetentionMinutes", 3));
        opcionesCircuito.PersistedCircuitInMemoryMaxRetained =
            builder.Configuration.GetValue("Circuit:PersistedCircuitInMemoryMaxRetained", 100);
        opcionesCircuito.MaxBufferedUnacknowledgedRenderBatches =
            builder.Configuration.GetValue("Circuit:MaxBufferedUnacknowledgedRenderBatches", 10);
    });

// "Circuitos activos" (Horizonte 2.3, ver Observabilidad.CircuitosActivos):
// singleton porque no guarda estado por circuito, solo cuenta — compone con
// las CircuitOptions de arriba, es la señal real de cuándo ese ajuste deja
// de bastar (umbral de la multi-réplica, ADR-008 § 2.1).
builder.Services.AddSingleton<CircuitHandler, CaeManager.Web.Services.MetricasCircuitHandler>();

// Revalidación de lectura dentro de un circuito ya abierto (hallazgo del
// Módulo 9, auditoría 2026-08-30) — Scoped a propósito, a diferencia del
// handler de arriba: necesita los servicios scoped del propio circuito (ver
// CaeManager.Web.Services.RevalidacionCircuitoActivoHandler).
builder.Services.AddScoped<CircuitHandler, CaeManager.Web.Services.RevalidacionCircuitoActivoHandler>();

// Health check real (P0-5 de docs/business/MATURITY_REVIEW.md): /salud
// respondía "ok" incondicional — con PostgreSQL caído seguía dando 200 y
// cualquier uptime check externo veía un servicio sano que no podía servir
// ni el login. Ahora ejecuta un SELECT 1 contra la base de datos: 200
// "Healthy" solo si el proceso vive Y la BD responde. Sigue siendo anónimo
// y barato a propósito (es lo que sondea el healthcheck de Docker Compose y
// el uptime check externo — ver deploy/local/docker-compose.produccion.yml).
builder.Services.AddHealthChecks()
    .AddNpgSql(
        sp => builder.Configuration.GetConnectionString("CaeManagerDb")
            ?? throw new InvalidOperationException("Falta el connection string CaeManagerDb."),
        name: "postgresql");

// El nombre comercial se resuelve una sola vez, antes de que nada lo pinte.
// Sin configuración se queda en el histórico; ver CaeManager.Application.Common.Marca.
CaeManager.Application.Common.Marca.Configurar(builder.Configuration["Marca:Nombre"]);

var app = builder.Build();

// Modo dedicado para un paso de "pre-deploy" (ver DEPLOY.md § 4 — P2 #22 de
// docs/business/MATURITY_REVIEW.md, una de las tres cosas que desbloquean
// multi-réplica): aplica las migraciones pendientes y termina, sin levantar
// Kestrel ni sembrar datos. Así el esquema se cierra una única vez, antes de
// que arranque ninguna réplica del proceso web — no N réplicas compitiendo
// por aplicar DDL a la vez en cada redeploy/reinicio. No está wireado a
// ningún paso del pipeline de deploy actual (deploy/local, .github/workflows/
// deploy.yml aplican las migraciones en el arranque normal, ver
// Migraciones:AlArrancar más abajo) — queda disponible para cuando el
// despliegue pase a multi-réplica y haga falta un pre-deploy explícito.
if (args.Contains("--migrate-only"))
{
    using var scopeMigracion = app.Services.CreateScope();
    await MigrarBaseDeDatosAsync(app.Configuration, scopeMigracion.ServiceProvider);
    return;
}

// Modo administrativo explícito para retirar por completo un tenant de demo
// (ver RetiradaTenantDemoService, motivado por el incidente de siembra
// parcial del 2026-08-28): tenant, usuarios y toda fila tenant-scoped, fuera
// del arranque normal — nunca corre sola, exige el TenantId exacto como
// argumento y termina el proceso sin levantar Kestrel, mismo patrón que
// --migrate-only. RetiradaTenantDemoService se niega a tocar cualquier
// tenant que no esté en su allowlist de nombres de demo conocidos, empezando
// por el de plataforma — ese rechazo es la garantía real, no esta capa de
// entrada.
//
// Identidad de BOOTSTRAP (FabricaContextoDeBootstrap), no el contexto
// inyectado: igual que AsignacionesOperativasBackfillSeeder, la retirada es
// cross-tenant por naturaleza — tiene que ver y borrar los dos catálogos
// globales de asignación operativa (ver RetiradaTenantDemoService) tanto por
// la posición de propietario como por la de operador, y su política RLS
// (posicion_en_la_asignacion) solo deja ver el lado operador bajo la
// coordenada de tenant de origen del usuario autenticado — una coordenada que
// no existe en un proceso administrativo sin sesión. Bajo el rol restringido,
// un tenant que retira siendo operador de la cartera de otro (p. ej. ArcoSPA,
// la Consultora) dejaría esas filas huérfanas sin que ningún error lo
// avisara — RLS no falla, solo no muestra. Medido: la primera versión de este
// dispatch usaba el contexto inyectado y ese hueco pasó sin detectarse hasta
// ejercitar la retirada de la Consultora de verdad.
//
// VALIDA PRIMERO, ELEVA DESPUÉS — con el contexto inyectado (rol
// restringido), NO con el de bootstrap. Este comando corre con identidad de
// propietario de base de datos y bypassa RLS por diseño: mientras la
// allowlist no haya confirmado que el TenantId es retirable, el proceso no
// tiene por qué tener en la mano una conexión capaz de tocar cualquier fila
// de cualquier tenant. RetiradaTenantDemoService.ValidarTenantRetirableAsync
// exige el contexto normal y devuelve el Tenant ya validado — la firma de
// RetirarAsync exige ESE Tenant, no un Guid suelto, así que no hay forma de
// llegar a FabricaContextoDeBootstrap.Crear() sin haber validado antes.
if (args.Contains("--retirar-tenant-demo"))
{
    var indiceArgumento = Array.IndexOf(args, "--retirar-tenant-demo");
    if (indiceArgumento < 0 || indiceArgumento + 1 >= args.Length ||
        !Guid.TryParse(args[indiceArgumento + 1], out var tenantIdARetirar))
    {
        Console.Error.WriteLine("Uso: --retirar-tenant-demo <TenantId guid> — falta el argumento o no es un Guid válido.");
        Environment.ExitCode = 1;
        return;
    }

    using var scopeRetirada = app.Services.CreateScope();
    var loggerRetirada = scopeRetirada.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        // Paso 1 — identidad NO privilegiada.
        var dbContextNoPrivilegiado = scopeRetirada.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var tenantValidado = await RetiradaTenantDemoService.ValidarTenantRetirableAsync(dbContextNoPrivilegiado, tenantIdARetirar);

        // Paso 2 — solo aquí, con el tenant ya validado, se eleva.
        await using var dbContextRetirada = scopeRetirada.ServiceProvider
            .GetRequiredService<CaeManager.Infrastructure.Persistence.FabricaContextoDeBootstrap>()
            .Crear();

        var resultado = await RetiradaTenantDemoService.RetirarAsync(dbContextRetirada, tenantValidado, loggerRetirada);
        Console.WriteLine(
            $"Retirado: '{resultado.NombreTenant}' ({resultado.TenantId}) — " +
            $"{resultado.FilasBorradas} filas tenant-scoped, {resultado.UsuariosBorrados} usuarios.");
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Retirada rechazada: {ex.Message}");
        Environment.ExitCode = 1;
    }

    return;
}

// Detrás de un proxy inverso (Caddy, ver deploy/local/Caddyfile y DEPLOY.md),
// Kestrel solo ve tráfico HTTP interno; sin esto,
// UseHttpsRedirection/UseHsts no reconocen la petición original como HTTPS
// y pueden entrar en bucle de redirección. KnownProxies/KnownNetworks se
// dejan vacíos a propósito: el proxy de entrada cambia según dónde se
// despliegue, y este es un único servicio detrás de un solo proxy de borde,
// no una red interna con saltos que haya que enumerar.
var opcionesForwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// KnownProxies/KnownIPNetworks traen loopback por defecto: un inicializador
// `= { }` no los vacía, solo no añade nada más. Sin este Clear() explícito,
// el middleware descarta X-Forwarded-Proto porque Caddy no habla desde
// loopback (es un contenedor propio en la red "edge", ver docker-compose.
// produccion.yml), y la app cree que la petición es HTTP (genera
// Location: http:// en redirects, lo que rompe el login vía CSP form-action).
opcionesForwardedHeaders.KnownProxies.Clear();
opcionesForwardedHeaders.KnownIPNetworks.Clear();
app.UseForwardedHeaders(opcionesForwardedHeaders);

using (var scope = app.Services.CreateScope())
{
    // Migraciones__AlArrancar=false, el día que un pre-deploy (--migrate-only
    // de más arriba) se adopte de verdad en el pipeline — hasta entonces, por
    // defecto (true), el arranque normal las aplica igual que siempre: con el
    // pre-deploy sin adoptar, es la única vía que las ejecuta. Con las dos
    // activas a la vez no hay riesgo de una sola réplica (las migraciones ya
    // aplicadas no se repiten), pero si se escalase a varias réplicas
    // simultáneas sí volvería la carrera que migrate-only existe para evitar
    // — de ahí el apagador explícito en vez de dejarlo siempre encendido.
    if (app.Configuration.GetValue("Migraciones:AlArrancar", defaultValue: true))
    {
        await MigrarBaseDeDatosAsync(app.Configuration, scope.ServiceProvider);
    }

    var dbContext = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    var userStore = scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Si alguien declaró la conexión de runtime, que la demuestre. La cadena
    // configurada solo prueba que existe una cadena: apuntarla al rol
    // propietario deja RLS igual de decorativa que no configurarla, y en
    // silencio. Se comprueba después de migrar, porque la propiedad se observa
    // sobre las tablas con RLS ya creadas, y sobre la conexión del contexto
    // inyectado, que es la que usará el tráfico.
    //
    // Y si NO está declarada, aquí solo se llega habiendo pasado por la puerta
    // de ResolverCadenaDeTrafico: o esto es Development, o alguien puso
    // Rls:PermitirIdentidadAdministrativaInsegura. En los dos casos el tráfico
    // corre con el rol propietario, al que PostgreSQL no somete a RLS, y eso
    // tiene que decirse en CADA arranque y no solo en un comentario que nadie
    // vuelve a mirar — es lo que aportaba VerificacionRolRuntimeHostedService
    // (Módulo 8), retirado al fusionar porque comprobaba lo mismo que la línea
    // de arriba y su aviso quedaba inalcanzable detrás de ella.
    if (!string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("CaeManagerDbRuntime")))
    {
        await CaeManager.Infrastructure.Persistence.VerificacionIdentidadDeRuntime
            .ExigirIdentidadSometidaARlsAsync(dbContext);
    }
    else
    {
        logger.LogWarning(
            "[AVISO] Sin ConnectionStrings:CaeManagerDbRuntime, el tráfico conecta con el rol propietario " +
            "de las tablas, al que PostgreSQL no somete a RLS ni con FORCE ROW LEVEL SECURITY. El " +
            "aislamiento por tenant descansa hoy solo en el filtro global de EF Core, que no cubre SQL " +
            "crudo, IgnoreQueryFilters ni las tablas de Identity. Entorno: {Entorno}. Ver " +
            "deploy/bootstrap/roles-de-cluster.sql para aprovisionar cae_app_runtime.",
            app.Environment.EnvironmentName);
    }

    // Identidad ADMINISTRATIVA para los dos seeders que no son trafico de
    // aplicacion: IdentitySeeder escribe estado de sistema sin identidad de
    // usuario, y el backfill de asignaciones es cross-tenant por diseno.
    // Ninguno de los dos puede ejecutarse bajo un rol sometido a RLS por-tenant
    // — ver FabricaContextoDeBootstrap, que explica los dos fallos concretos.
    // Los demas seeders siguen con el contexto inyectado a proposito: operan
    // dentro de un AmbitoTenantExplicito, que es lo que las politicas piden.
    await using var dbContextBootstrap = scope.ServiceProvider
        .GetRequiredService<CaeManager.Infrastructure.Persistence.FabricaContextoDeBootstrap>()
        .Crear();

    // Sin sesión de usuario en el arranque no hay tenant que resolver por
    // claim — la siembra del Administrador inicial se ejecuta explícitamente
    // como tenant #1 (ver AmbitoTenantExplicito, docs/MULTITENANCY.md § 8.4).
    using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
    {
        await IdentitySeeder.SeedAsync(userManager, roleManager, userStore, logger, app.Configuration, app.Environment, dbContextBootstrap);
    }

    // Los datos de prueba de CAE ya no se siembran en el tenant #1: en el
    // escenario de demo de ADR-004-delegacion-consultoras-cae.md, el tenant
    // #1 juega el papel de Consultora (sin datos operativos propios, § 5.1)
    // — DelegacionDemoSeeder los siembra en un tenant Cliente Delegante
    // nuevo y establece su propio AmbitoTenantExplicito internamente.
    await DelegacionDemoSeeder.SeedAsync(dbContext, userManager, userStore, app.Configuration, app.Environment, logger);

    // Segundo tenant, exclusivamente para verificación E2E multi-tenant con
    // navegador real (ver PLAN-MIGRACION-MULTITENANT.md § 6) — inerte salvo
    // que SegundoTenant:Activo esté configurado explícitamente.
    await SegundoTenantSeeder.SeedAsync(dbContext, userManager, userStore, app.Configuration, app.Environment, logger);

    // Despues de TODOS los sembradores, no dentro de ninguno: la verificacion
    // IA se reconcilia sobre los tenants de demo que ya existen, no solo sobre
    // los que se acaban de crear. Colgarla de un camino de siembra dejo cinco
    // de seis tenants sin encender en produccion (ver el metodo).
    await DatosPruebaSeeder.ReconciliarVerificacionIaEnTenantsDeDemoAsync(
        dbContext, app.Configuration, logger);

    // Nivel 0 (DEC-33, REC-035): sin esto, el Nivel 1 que la reconciliación
    // de arriba acaba de encender no basta — la instrucción documentada de
    // tratamiento IA es el gate que se comprueba primero, y sin ella ningún
    // tenant de demo llega a ejercitar IA de verdad. Deliberadamente solo el
    // tenant #1 (ver el método): el segundo tenant y el Cliente Delegante de
    // demo quedan sin instrucción, como control negativo vivo.
    await DatosPruebaSeeder.SembrarInstruccionTratamientoIaTenantPrincipalAsync(
        dbContext, app.Configuration, logger);

    // Al final a propósito: aprovisiona la delegación de soporte —apagada—
    // de todo tenant que exista, incluidos los que acaben de sembrarse.
    // Idempotente, así que cubre también los tenants creados en arranques
    // anteriores. Aprovisionar no concede acceso: abrirlo exige motivo y
    // ventana (ver DelegacionesSoporteSeeder).
    await DelegacionesSoporteSeeder.SeedAsync(dbContext, app.Configuration, logger);

    // Después de todo lo anterior: traslada el reparto de responsabilidad
    // operativa (delegaciones comerciales y ejecutivos de cliente) a las tablas
    // de asignación, incluyendo los tenants que se acaben de sembrar. Es
    // idempotente y reconciliador, así que se ejecuta en cada arranque hasta
    // que la doble escritura quede establecida (F1 del plan de migración).
    await AsignacionesOperativasBackfillSeeder.SeedAsync(dbContextBootstrap, logger);
}

// Registrado antes del manejo de excepciones para envolverlo por completo:
// una petición que termina en 500 vía UseExceptionHandler se sigue
// registrando aquí con su código de estado final, no como si hubiera ido bien.
//
// EnrichDiagnosticContext corre al cerrar la petición, cuando el tenant ya
// está resuelto — por eso los servicios se piden a ctx.RequestServices y no
// por constructor: ITenantActual es scoped y este callback lo comparte un
// middleware singleton. Es lo que hace que la traza HTTP (descargas de
// documentos, exports, endpoints de identidad) salga correlacionada con el
// tenant igual que la de MediatR, que se correlaciona en LoggingBehavior.
app.UseSerilogRequestLogging(opciones =>
{
    opciones.EnrichDiagnosticContext = (contextoDiagnostico, contextoHttp) =>
    {
        var tenantActual = contextoHttp.RequestServices.GetService<ITenantActual>();
        if (tenantActual?.TenantId is { } tenantId)
            contextoDiagnostico.Set("TenantId", tenantId);

        var usuarioId = contextoHttp.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(usuarioId))
            contextoDiagnostico.Set("UsuarioId", usuarioId);
    };
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Antes de todo lo que produce respuesta (páginas, endpoints y estáticos):
// las cabeceras han de ir en cualquier respuesta, incluidas las de error.
app.UseCabecerasSeguridad();

app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(culturaEspanola.Name)
    .AddSupportedCultures(culturaEspanola.Name)
    .AddSupportedUICultures(culturaEspanola.Name));

app.UseAuthentication();

// Inmediatamente después de UseAuthentication y ANTES de UseAuthorization: una
// sesión privilegiada de plataforma no lleva rol de negocio, y las puertas
// [Authorize(Roles = …)] preguntan al principal, no a CurrentUserService (ver
// SesionPrivilegiadaSinRolDeNegocioMiddleware).
app.UseSesionPrivilegiadaSinRolDeNegocio();

// Inmediatamente después del anterior y ANTES de UseAuthorization: bajo un
// workspace delegado (plano 2) el claim de rol es el del tenant de ORIGEN, y
// las puertas [Authorize(Roles = …)] lo creerían. Aquí se sustituye por el rol
// efectivo de la cartera de ese workspace (ver RolEfectivoDelWorkspaceMiddleware).
app.UseRolEfectivoDelWorkspace();

// Una cuenta a medio activar (contraseña temporal sin cambiar, o Administrador
// sin 2FA) no alcanza nada fuera de /cuenta/. Antes esas dos obligaciones solo
// las imponía MainLayout, que es una pantalla: con la cookie ya emitida se
// llegaba a cualquier endpoint autenticado sin renderizar ningún layout — entre
// ellos la descarga de PDFs (ver CuentaAMedioActivarSinAccesoMiddleware).
app.UseCuentaAMedioActivarSinAcceso();

// Tras UseAuthentication (el límite distingue anónimo/autenticado) y antes
// de que ningún endpoint procese la petición.
app.UseRateLimiter();

app.UseAuthorization();
app.UseAntiforgery();

// Después de UseAuthentication (hace falta el usuario resuelto) y antes de
// los endpoints y componentes. No antes de que nada resuelva el tenant:
// UseRolEfectivoDelWorkspace ya lo hizo más arriba, y entre los dos median
// UseCuentaAMedioActivarSinAcceso, UseRateLimiter, UseAuthorization y
// UseAntiforgery, que pueden cortar la petición sin pasar por aquí (REC-189,
// ver el porqué y por qué no es un agujero de autorización en el doc-comment
// de UseRevalidacionClienteActivo).
app.UseRevalidacionClienteActivo();

// Los archivos estáticos (JS/CSS) no son sensibles y nunca deben exigir
// sesión iniciada — dejarlos detrás de la FallbackPolicy generaba una
// carrera real: en una navegación fresca, blazor.web.js y nuestros propios
// módulos JS a veces se pedían antes de que la cookie de auth completara su
// ida y vuelta, y un import() dinámico fallido no se reintenta solo.
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/salud").AllowAnonymous();
app.MapIdentityEndpoints();
app.MapClientesEndpoints();
app.MapAsignacionesEndpoints();
app.MapEmpresasEndpoints();
app.MapCentrosEndpoints();
app.MapIncidenciasEndpoints();
app.MapFacturacionEndpoints();
app.MapDocumentosEndpoints();
app.MapTrabajadoresEndpoints();
app.MapFirmasGuardadasEndpoints();
app.MapRequisitosDocumentalesEndpoints();
app.MapComunicacionesEndpoints();
app.MapSubcontratasEndpoints();
app.MapReportesEndpoints();
app.MapAuditoriaEndpoints();
app.MapClienteActivoEndpoints();
// Entrada por sesión privilegiada (plano 3, B1). Aparte del anterior a
// propósito: aquél acumula ramas de autorización en un OR y éste no debe
// añadirse a ese OR — ver SesionSoporteEndpoints.
app.MapSesionSoporteEndpoints();
app.MapExtensionTokenEndpoints();
app.MapAcreditacionesPendientesEndpoints();
app.MapMarcarAcreditacionSubidaEndpoints();
app.MapVistaVocabularioPreviewEndpoints();
app.MapConectarMicrosoft365Endpoints();
app.MapWebhookMicrosoft365Endpoints();
app.MapWebhookWhatsAppEndpoints();
app.MapWebhookStripeEndpoints();

// API pública v1 (P3-29) — solo lectura, no publicada todavía. Un único
// grupo con la política de auth/rate-limit aplicada una vez, en vez de por
// endpoint: ningún MapXxxApiEndpoints necesita saber que existen.
var apiV1 = app.MapGroup("/api/v1")
    .RequireAuthorization("ApiPublica")
    .RequireRateLimiting("ApiPublica");
apiV1.MapClientesApiEndpoints();
apiV1.MapCentrosApiEndpoints();
apiV1.MapTrabajadoresApiEndpoints();
apiV1.MapDocumentosApiEndpoints();
apiV1.MapAsignacionesApiEndpoints();

// El documento OpenAPI no expone datos, solo forma — no requiere auth
// (misma práctica habitual que /swagger.json en una API privada).
app.MapOpenApi("/api/v1/openapi.json").AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Las migraciones (DDL: CreateTable, y desde HabilitarRlsPostgres además
// ENABLE ROW LEVEL SECURITY / CREATE POLICY) exigen el rol propietario de
// las tablas — el rol de runtime que RUNBOOK-RLS.md provisiona para
// ConnectionStrings:CaeManagerDbRuntime no tiene privilegios de DDL a
// propósito (es justo lo que hace que RLS lo restrinja de verdad). Por eso
// las migraciones se aplican con una instancia propia apuntando siempre a
// CaeManagerDb (el rol propietario), sin pasar por el DbContext inyectado —
// que desde que se configura CaeManagerDbRuntime usa ese rol restringido
// para todo lo demás (ver TenantRlsConnectionInterceptor). Mientras
// CaeManagerDbRuntime no esté configurado (todos los entornos hoy) ambas
// cadenas son la misma y esto es equivalente a conectar una sola vez.
static async Task MigrarBaseDeDatosAsync(IConfiguration configuration, IServiceProvider servicios)
{
    var cadenaMigraciones = configuration.GetConnectionString("CaeManagerDb");
    var opcionesMigraciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
        .UseNpgsql(cadenaMigraciones, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
        .Options;
    await using var dbContextMigraciones = new CaeManagerDbContext(
        opcionesMigraciones,
        servicios.GetRequiredService<IDataProtectionProvider>(),
        new TenantActualAmbiental());
    await dbContextMigraciones.Database.MigrateAsync();
}

namespace CaeManager.Web.Services
{
    /// <summary>
    /// D-2: filtro de <c>SentryOptions.SetBeforeSend</c> que acota la captura
    /// automática de peticiones HTTP fallidas de Sentry.AspNetCore (ver
    /// Program.cs, junto a <c>UseSentry</c>) para que un Seq caído no ahogue
    /// el resto de Sentry con un evento por cada exportación OTLP fallida.
    ///
    /// Acotado a propósito: solo mira la URL de destino, y solo actúa si
    /// coincide con la de ingesta de Seq — un 5xx real contra cualquier otro
    /// destino (Anthropic, Gemini, Microsoft Graph, Stripe, WhatsApp...)
    /// sigue generando su evento normal, sin pasar por este filtro.
    ///
    /// No es un descarte total: dentro de la ventana de enfriamiento se deja
    /// pasar el primer fallo (visibilidad inmediata de que Seq cayó) y luego,
    /// como mucho uno cada <see cref="VentanaEnfriamiento"/> mientras se
    /// mantenga caído — una serie de eventos espaciados en Sentry, no un
    /// flood ni un silencio total. Estado en memoria de proceso (no
    /// persistido): un reinicio del proceso reinicia la ventana, lo cual es
    /// aceptable porque el propio reinicio ya es una señal visible en Sentry
    /// por otras vías (arranque, releases).
    ///
    /// Estado de instancia, no estático a propósito: un único
    /// <c>Program.cs</c> crea una sola instancia (cerrada en el lambda de
    /// <c>SetBeforeSend</c>) para toda la vida del proceso, pero cada test
    /// puede construir la suya sin pisar el reloj de los demás.
    /// </summary>
    public sealed class CortacircuitoEventosSeqCaido(string urlSeq)
    {
        internal static readonly TimeSpan VentanaEnfriamiento = TimeSpan.FromMinutes(30);

        private long _ultimoPermitidoTicks = DateTime.MinValue.Ticks;

        public bool DebeDescartar(SentryEvent evento) => DebeDescartarEn(evento, DateTime.UtcNow);

        /// <summary>
        /// Pública solo para poder testear la ventana de enfriamiento con
        /// tiempos explícitos, sin esperas reales ni <c>InternalsVisibleTo</c>
        /// en <c>CaeManager.Web</c> (mismo patrón que otros métodos puros del
        /// proyecto, ver <c>WebhookWhatsAppEndpoints.EsFirmaValida</c>).
        /// </summary>
        public bool DebeDescartarEn(SentryEvent evento, DateTime ahora)
        {
            if (!EsDestinoSeq(evento.Request?.Url))
                return false; // No es Seq: nunca se toca — control positivo del resto de integraciones.

            var ahoraTicks = ahora.Ticks;
            var ultimo = Interlocked.Read(ref _ultimoPermitidoTicks);
            if (ahoraTicks - ultimo < VentanaEnfriamiento.Ticks)
                return true; // Dentro de la ventana: ya se avisó recientemente, descarta.

            // Fuera de ventana (o primera vez): esta llamada "gana" el turno de
            // avisar si consigue mover el reloj; si otra llamada concurrente ya
            // lo movió primero, esta se descarta en vez de duplicar el aviso.
            return Interlocked.CompareExchange(ref _ultimoPermitidoTicks, ahoraTicks, ultimo) != ultimo;
        }

        /// <summary>
        /// Comparación ESTRUCTURADA (esquema + host + puerto), no un
        /// <c>string.Contains</c> sobre la URL cruda — revisión adversaria:
        /// un <c>Contains</c> da falsos positivos (<c>https://seq.talveg.es.ejemplo.com/…</c>
        /// contendría la URL de Seq como substring) y falsos negativos por
        /// normalización (puerto explícito, escapes). <c>Uri.Compare</c> con
        /// <c>UriComponents.SchemeAndServer</c> evita ambos: compara "es el
        /// mismo servidor", no "el texto se parece".
        /// </summary>
        private bool EsDestinoSeq(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(urlSeq))
                return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uriEvento))
                return false;
            if (!Uri.TryCreate(urlSeq, UriKind.Absolute, out var uriSeq))
                return false;

            return Uri.Compare(
                uriEvento, uriSeq, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0;
        }
    }

    /// <summary>
    /// D-3 (Sentry DOTNET-8): envuelve <c>CookieAuthenticationOptions.Events.OnValidatePrincipal</c>
    /// para que una ruta concreta (pensada para <c>/Error</c>) no dispare la
    /// revalidación del security stamp contra la base — sin ella,
    /// <c>SecurityStampValidator</c> (cableado por <c>AddIdentityCookies()</c>,
    /// ver Program.cs) puede agotar los 6 reintentos de
    /// <c>ConfiguracionDeContexto</c> con la base caída justo en la petición
    /// que debería informar del fallo.
    ///
    /// Comprobado en aislamiento contra el framework compartido real (no de
    /// memoria): en el momento en que <c>ConfigureApplicationCookie</c>
    /// ejecuta su delegado, <c>options.Events.OnValidatePrincipal</c> YA es
    /// <c>SecurityStampValidator.ValidatePrincipalAsync</c> — <c>Configure</c>
    /// aplica los delegados en el orden en que se registraron, y
    /// <c>AddIdentityCookies()</c> se registra antes. Este método conserva esa
    /// referencia y solo la salta para la ruta indicada; cualquier otra ruta
    /// sigue revalidando exactamente igual que hoy.
    ///
    /// Por qué es seguro saltárselo en <c>/Error</c>: cuando SÍ revalida con
    /// éxito, <c>SecurityStampValidator</c> puede volver a firmar al usuario
    /// con un <c>ClaimsPrincipal</c> fresco (roles y demás claims
    /// actualizados desde su última emisión) — es la garantía de que un
    /// cambio de rol o una contraseña cambiada se reflejan sin esperar a que
    /// expire la cookie. <c>Error.razor</c> no usa <c>HttpContext.User</c>
    /// para nada (ni roles, ni tenant, ni ninguna acción — solo el
    /// <c>TraceIdentifier</c> de la petición) y no tiene <c>[Authorize]</c>
    /// propio (lleva <c>[AllowAnonymous]</c>, la excepción explícita al
    /// <c>FallbackPolicy</c> que exige sesión en el resto del sitio), así que
    /// no hay ninguna decisión de autorización en esa página que dependa de
    /// una revalidación fresca. Comparación de ruta EXACTA (no por prefijo)
    /// para que el salto no se cuele a ninguna otra ruta que empiece igual.
    /// </summary>
    public static class OmitirRevalidacionDeStampEnRuta
    {
        public static void Configurar(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions options, string ruta)
        {
            var revalidarOriginal = options.Events.OnValidatePrincipal;
            var rutaExacta = new Microsoft.AspNetCore.Http.PathString(ruta);
            options.Events.OnValidatePrincipal = contexto =>
                contexto.Request.Path.Equals(rutaExacta, StringComparison.OrdinalIgnoreCase)
                    ? Task.CompletedTask
                    : revalidarOriginal(contexto);
        }
    }
}
