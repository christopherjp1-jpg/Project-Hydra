using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Capa 2 de la política de lectura de los catálogos globales de asignación
/// operativa (plano 2 del ADR-011, endurecimiento E1 del plan de migración).
///
/// <c>AsignacionesOperacion</c> y <c>AsignacionesCartera</c> están
/// <b>deliberadamente fuera del filtro global de tenant</b>: una asignación
/// cruza fronteras por naturaleza (el operador puede ser otro tenant), así que
/// no puede llevar <c>TenantId</c>. Pero estar fuera del filtro no las hace
/// legibles sin restricción: cada fila revela quién opera para quién y sobre
/// qué ámbito, que es metadata empresarial.
///
/// Toda consulta debe acotarse a la posición del llamante — propietario por
/// <c>PropietarioTenantId</c>, operador por <c>OperadorTenantId</c> = su tenant
/// de ORIGEN — y eso no se puede comprobar leyendo texto. Lo que sí se puede es
/// mantener corta y explícita la lista de sitios donde se toca: si el acceso
/// vive en seis archivos revisados, revisar la política es leer seis archivos.
/// Un handler nuevo que consulte estos DbSets sin filtro de posición hace
/// fallar este test y obliga a justificarlo.
///
/// Mismo mecanismo de ratchet por texto que
/// <see cref="ProhibicionSqlCrudoYFiltrosIgnoradosTests"/>, y por el mismo
/// motivo: son accesos a propiedad, no dependencias de tipo, así que la
/// reflexión sobre el ensamblado no los ve sin desensamblar cada método.
/// </summary>
public class AccesoRestringidoACatalogosDeAsignacionTests
{
    /// <summary>
    /// Cubre los dos planos que viven fuera del filtro de tenant: el de
    /// operación (asignaciones) y el de privilegio de plataforma (concesiones y
    /// sesiones). Las filas del segundo son, si cabe, más sensibles: dicen qué
    /// usuario de TALVEG puede abrir los datos de qué cliente y hasta cuándo.
    ///
    /// <para>
    /// Los nombres de los <c>DbSet</c> no son la única forma de llegar a esas
    /// tablas, y la primera versión de este patrón solo vigilaba esos. Un
    /// <c>Set&lt;AsignacionOperacion&gt;()</c> —el tipo de entidad, en
    /// singular— devuelve el catálogo global entero, de todos los tenants, sin
    /// filtro de posición, y pasaba en verde. Demostrado por mutación.
    /// </para>
    ///
    /// <para>
    /// Se vigila la <b>forma de acceso</b> y no el nombre del tipo a secas, y
    /// es deliberado: los cinco tipos se mencionan en sitios que no son
    /// accesos —<c>TipoViaAcceso.SesionPrivilegiada</c> es un valor de enum, no
    /// una consulta—, y meterlos pelados obligaría a justificar seis ficheros
    /// que no tocan ninguna tabla. Una lista de excepciones llena de entradas
    /// que no son accesos deja de servir para revisar la política, que es justo
    /// para lo que existe.
    /// </para>
    ///
    /// <para>
    /// <b>Límite conocido</b>: un alias de <c>using</c> sobre el tipo permitiría
    /// escribir <c>Set&lt;AO&gt;()</c>. Se cubre vigilando también la línea del
    /// alias, que sí tiene que nombrar el tipo completo.
    /// </para>
    /// </summary>
    private static readonly Regex PatronAcceso = new(
        @"\bAsignacionesOperacion\b|\bAsignacionesCartera\b"
        + @"|\bConcesionesPrivilegio\b|\bSesionesPrivilegiadas\b|\bTenantsAlcanzadosPorConcesion\b"
        + @"|\bSet<\s*(?:[\w.]*\.)?" + NombresDeEntidad + @"\s*>"
        + @"|^\s*using\s+\w+\s*=\s*[\w.]*" + NombresDeEntidad + @"\s*;",
        RegexOptions.Compiled);

    /// <summary>Los tipos de entidad de los dos planos, para las formas de acceso que no usan el DbSet.</summary>
    private const string NombresDeEntidad =
        @"(?:AsignacionOperacion|AsignacionCartera|ConcesionPrivilegio|SesionPrivilegiada|TenantAlcanzadoPorConcesion)";

    /// <summary>
    /// Los puntos autorizados, con el papel que cumple cada uno. Añadir uno
    /// nuevo es una decisión de diseño que se revisa en el mismo commit, no un
    /// descuido que se cuela.
    /// </summary>
    private static readonly HashSet<string> ArchivosAutorizados =
    [
        // Autoridad de AdminPlataforma (A3): decide si el usuario puede ejercer
        // la capacidad sobre un tenant o globalmente. Consulta acotada al propio
        // usuario —UsuarioPlataformaId == el de la sesión—, el mismo predicado
        // que la política RLS del plano de privilegio, así que no introduce una
        // segunda definición de "sus concesiones".
        "src/CaeManager.Infrastructure/Plataforma/AutorizacionAdminPlataformaPorConcesion.cs",

        // Matriz de auto-concesión (A2): consulta si el usuario tiene una
        // concesión AdminPlataforma VIGENTE para decidir si puede darse
        // SoporteLectura. Consulta acotada al propio usuario —el filtro de
        // posición es UsuarioPlataformaId== el de la sesión—, que es
        // exactamente lo que este ratchet exige, y además coincide con el
        // predicado de la política RLS del plano de privilegio.
        "src/CaeManager.Infrastructure/Plataforma/AutorizacionAutoConcesionPorMatriz.cs",

        // El contrato de consulta (expone el DbSet) y el unico escritor. El
        // contrato de ESCRITURA no esta aqui: declara firmas sobre los tipos de
        // entidad, no toca ninguna tabla. Era otra entrada muerta, destapada por
        // la guarda por igualdad.
        "src/CaeManager.Application/Operaciones/IOperacionesQueryContext.cs",
        "src/CaeManager.Infrastructure/Operaciones/AsignacionesOperativasWriter.cs",

        // Job de expiración de vigencias: catálogo global por naturaleza, sin
        // posición de llamante (no hay sesión en un job de fondo).
        "src/CaeManager.Infrastructure/Operaciones/ExpiracionAsignacionesHostedService.cs",

        // Backfill de F1: recorre todos los tenants una vez, al arrancar.
        "src/CaeManager.Infrastructure/Persistence/Seed/AsignacionesOperativasBackfillSeeder.cs",

        // Registro de los DbSet y de las interfaces de consulta.
        "src/CaeManager.Infrastructure/Persistence/CaeManagerDbContext.cs",

        // Autorización fina del usuario que MIRA: qué ve él. Filtra por
        // propietario (el tenant activo) y por operador de origen (el del claim
        // de su sesión).
        "src/CaeManager.Infrastructure/Autorizacion/AlcanceDatosService.cs",

        // La misma pregunta, hecha sobre OTROS: qué alcanza cada cuenta del
        // tenant, para poder pintarlo en /usuarios. Aplica las dos mitades del
        // filtro de posición, con la única diferencia que impone la pregunta —
        // el operador no puede salir del claim de sesión, que es el de quien
        // mira, así que sale del tenant de ORIGEN de cada usuario listado
        // (OperadorTenantId == usuario.TenantId, join contra AspNetUsers). El
        // propietario sí es el tenant activo, y quien llama ocupa esa posición:
        // la pantalla es [Authorize(Administrador, DireccionCae)]. No expone
        // ninguna fila de otro propietario ni de una posición ajena; ni
        // siquiera devuelve las asignaciones, solo un recuento por usuario.
        "src/CaeManager.Infrastructure/Autorizacion/DirectorioUsuariosTenant.cs",

        // Configuraciones EF de las dos tablas.
        "src/CaeManager.Infrastructure/Persistence/Configurations/AsignacionOperacionConfiguration.cs",
        "src/CaeManager.Infrastructure/Persistence/Configurations/AsignacionCarteraConfiguration.cs",

        // Configuraciones EF de las tres tablas del plano de privilegio de
        // plataforma. Mismo motivo que las de asignación: definen la tabla, no
        // la consultan.
        "src/CaeManager.Infrastructure/Persistence/Configurations/ConcesionPrivilegioConfiguration.cs",
        "src/CaeManager.Infrastructure/Persistence/Configurations/SesionPrivilegiadaConfiguration.cs",
        "src/CaeManager.Infrastructure/Persistence/Configurations/TenantAlcanzadoPorConcesionConfiguration.cs",

        // Selección de workspace: filtra por PropietarioTenantId = el tenant
        // que se quiere abrir Y por OperadorTenantId = tenant de origen del
        // usuario, exige cartera vigente y excluye la raíz.
        "src/CaeManager.Web/Features/Tenants/ClienteActivoEndpoints.cs",

        // Rol efectivo dentro del workspace: acotado a la operación que el
        // token identifica y al propietario que ese mismo token declara.
        "src/CaeManager.Web/Services/CurrentUserService.cs",

        // Revalidación por petición: comprueba la coherencia token↔operación y
        // exige cartera vigente del usuario.
        "src/CaeManager.Web/Services/RevalidacionClienteActivoMiddleware.cs",

        // Contrato de lectura del plano 3. "No existe ni debe existir un
        // listar sesiones" habla de un catálogo navegable —una pantalla que
        // enumere "todas las mías"—, y eso sigue sin existir. Los dos
        // consumidores de abajo son búsquedas puntuales por un Id que YA se
        // conoce de antemano, nunca una enumeración: la resolución de la
        // sesión que el token de la petición en curso nombra
        // (SesionPrivilegiadaActual), y desde H-2 (plan de sesiones nocturnas
        // 2026-09-02, DEC-2) el detalle de la sesión que
        // /cuenta/soporte/salir trae en su propia redirección de salida, para
        // mostrar tenant/motivo/TTL en la pantalla de cierre. Ninguno de los
        // dos añade una forma de descubrir un Id que no se tuviera ya: el
        // segundo lo recibe del primero, nunca lo busca.
        "src/CaeManager.Application/Plataforma/IPlataformaQueryContext.cs",
        "src/CaeManager.Infrastructure/Plataforma/SesionPrivilegiadaActual.cs",
        "src/CaeManager.Application/Plataforma/Queries/ObtenerSesionPrivilegiadaPorId/ObtenerSesionPrivilegiadaPorIdQuery.cs",

        // NOTA: aqui vivia InfrastructureServiceCollectionExtensions.cs, justificado
        // como "registro del contrato en el contenedor". Nunca caso con el patron:
        // registra IOperacionesQueryContext e IPlataformaQueryContext por el nombre de
        // la interfaz, sin nombrar ninguna tabla. Era una entrada muerta desde el
        // origen, y una entrada muerta afirma una revision que no corresponde con el
        // codigo. La destapo la guarda por igualdad, no una lectura a ojo.

        // Ceremonia de apertura y cierre de sesiones privilegiadas (F2b-6), y
        // el único escritor del plano 3.
        //
        // Desde F2b-5 estos tres accesos tienen una acotación que los de
        // asignación no tienen: RLS con FORCE sobre las tres tablas, con la
        // política privilegio_del_usuario. Aunque un comando pidiera una
        // concesión ajena por Id, la base no la devuelve — la política solo
        // entrega las filas que nombran a app.usuario_id. Por eso ninguno de
        // los tres lleva un "y además comprueba que es tuya" a mano: sería una
        // segunda regla que mantener sincronizada con la primera.
        "src/CaeManager.Application/Plataforma/Commands/AbrirSesionPrivilegiada/AbrirSesionPrivilegiadaCommand.cs",
        "src/CaeManager.Application/Plataforma/Commands/CerrarSesionPrivilegiada/CerrarSesionPrivilegiadaCommand.cs",
        "src/CaeManager.Infrastructure/Plataforma/PlataformaWriter.cs",

        // Retirada de tenant de demo (incidente del 2026-08-28): limpia
        // SesionesPrivilegiadas/TenantsAlcanzadosPorConcesion que pudieran
        // nombrar al tenant retirado (p. ej. como TenantObjetivoId de una
        // sesión de soporte abierta sobre él en algún momento), acotado a mano
        // por TenantId == el tenant ya validado contra la allowlist de demo —
        // el mismo filtro de posición que exige esta política, aplicado al
        // tenant en vez de al usuario.
        "src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs",
    ];

    [Fact]
    public void Solo_los_puntos_autorizados_tocan_los_catalogos_de_asignacion()
    {
        var raiz = RaizDelRepositorio();
        var carpetas = new[]
        {
            "src/CaeManager.Application", "src/CaeManager.Infrastructure", "src/CaeManager.Web"
        };

        var infractores = new List<string>();

        foreach (var carpeta in carpetas)
        {
            var directorio = Path.Combine(raiz, carpeta.Replace('/', Path.DirectorySeparatorChar));

            // Los .razor entran igual que los code-behind: un bloque @code es C#.
            // obj/ y bin/ quedan fuera para no contar el C# que el compilador genera
            // a partir de cada .razor como si fuera un acceso más.
            var archivos = Directory
                .EnumerateFiles(directorio, "*", SearchOption.AllDirectories)
                .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

            foreach (var archivo in archivos)
            {
                var rutaRelativa = Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');
                if (ArchivosAutorizados.Contains(rutaRelativa)) continue;

                if (File.ReadLines(archivo).Any(linea => PatronAcceso.IsMatch(linea)))
                    infractores.Add(rutaRelativa);
            }
        }

        string.Join("\n", infractores.OrderBy(x => x)).Should().BeEmpty(
            "los catálogos de asignación están fuera del filtro global de tenant, así que cada consulta debe " +
            "acotarse a mano a la posición del llamante (propietario por PropietarioTenantId, operador por " +
            "OperadorTenantId = tenant de ORIGEN, ver ADR-011 § 2.7 y el endurecimiento E1 del plan) — si el " +
            "acceso listado está justificado, añádelo a ArchivosAutorizados en este mismo commit explicando qué " +
            "filtro de posición aplica");
    }

    /// <summary>
    /// Guarda del propio test: cada archivo de la lista tiene que seguir
    /// <b>observándose</b>, no un número cualquiera por encima de un umbral.
    ///
    /// <para>
    /// Antes exigía "más de cinco de los autorizados casan". Con dos docenas en
    /// la lista, eso deja pasar que la mayoría se quedaran obsoletos —carpeta
    /// movida, tipo renombrado, línea reformateada— sin que nada lo dijera, y
    /// cada entrada muerta es un fichero que dejó de estar vigilado mientras
    /// la lista sigue afirmando que se revisó.
    /// </para>
    /// </summary>
    [Fact]
    public void Cada_acceso_autorizado_sigue_observandose()
    {
        var raiz = RaizDelRepositorio();

        var desaparecidos = ArchivosAutorizados
            .Where(ruta =>
            {
                var archivo = Path.Combine(raiz, ruta.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(archivo) || !File.ReadLines(archivo).Any(l => PatronAcceso.IsMatch(l));
            })
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, desaparecidos).Should().BeEmpty(
            "un autorizado que ya no casa con el patrón es una entrada muerta: o el escaneo dejó de mirar donde " +
            "cree que mira, o el acceso se retiró sin actualizar la lista; en ambos casos la lista afirma una " +
            "revisión que ya no corresponde con el código");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
