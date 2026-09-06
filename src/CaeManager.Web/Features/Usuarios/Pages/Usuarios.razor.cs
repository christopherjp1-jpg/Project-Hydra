using System.Globalization;
using System.Security.Claims;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Empresas.Queries.BuscarEmpresaPorCif;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Usuarios.Pages;

/// <summary>
/// <paramref name="EsOperadorDelegado"/> no es un dato nuevo: sale del mismo
/// diccionario que ya decide qué rol se muestra (ver CargarAsync). Se expone
/// aparte porque la lista necesita distinguir visualmente "este rol es el de
/// una asignación de otra organización" de "este es su rol propio" — sin él,
/// las dos filas se ven idénticas y el rol delegado parece nativo.
/// </summary>
public record UsuarioListaDto(
    Guid Id, string Email, string NombreCompleto, string Rol, bool Activo, bool EsOperadorDelegado,
    AlcanceUsuarioDto Alcance);

/// <summary>
/// Qué alcanza una cuenta, ya resuelto a texto. Es presentación y por eso vive
/// aquí y no en el directorio: la misma cartera se lee distinto según el rol
/// —un Coordinador CAE alcanza lo de sus gestores, un usuario de portal solo su
/// propia empresa— y quien mira la lista necesita la respuesta, no los datos
/// para calcularla.
/// </summary>
/// <param name="EsAviso">
/// Alcance cero. No es un error de configuración necesariamente —una cuenta
/// recién creada todavía no tiene cartera— pero sí la explicación de por qué
/// esa persona abre el producto y no ve nada, que sin esta columna no está
/// escrita en ningún sitio.
/// </param>
public record AlcanceUsuarioDto(string Texto, bool EsAviso, string Explicacion);

public record CoordinadorDto(Guid Id, string NombreCompleto, string Email);

public partial class Usuarios : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IEmailService EmailService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<Usuarios> Logger { get; set; } = default!;

    private int _tamanoPagina = 20;

    private IReadOnlyList<UsuarioListaDto> _usuarios = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private Guid? _usuarioActualId;

    /// <summary>
    /// El actor que edita, no el rol que se le asigna al usuario editado —
    /// esta página también la abre DireccionCae (mismo nivel de "ve todo el
    /// negocio"), pero DEC-36 (REC-099) dice específicamente "concedido
    /// explícitamente por otro Administrador". Sin esta distinción, un
    /// DireccionCae podría convertirse en Administrador con el permiso ya
    /// concedido en el mismo guardado.
    /// </summary>
    private bool _usuarioActualEsAdministrador;

    private int _pagina = 1;

    private string _busqueda = string.Empty;
    private string _rolFiltro = string.Empty;
    private string _activacionFiltro = string.Empty;

    private static readonly IReadOnlyList<OpcionEstado> OpcionesRol =
        Roles.Todos.Select(rol => new OpcionEstado(rol, Roles.NombreVisible(rol))).ToList();

    private const string ActivacionActivos = "activos";
    private const string ActivacionDesactivados = "desactivados";

    private static readonly IReadOnlyList<OpcionEstado> OpcionesActivacion =
    [
        new(ActivacionActivos, "Activos"),
        new(ActivacionDesactivados, "Desactivados")
    ];

    /// <summary>
    /// El filtrado es en memoria a propósito: <see cref="CargarAsync"/> ya trae
    /// la lista entera de usuarios del tenant —son decenas, no miles— y la
    /// paginación también es de cliente. Llevarlo a consulta obligaría a
    /// rehacer una carga que no pasa por MediatR sino por UserManager, y no
    /// ganaría nada.
    ///
    /// <para>
    /// El rol que se compara es el de <see cref="UsuarioListaDto"/>, que para
    /// un Operador Delegado es el de su asignación aquí y no el de su
    /// organización de origen — filtrar por "Gestor CAE" tiene que devolver a
    /// quien opera como tal en esta organización, que es lo que la fila dice.
    /// </para>
    /// </summary>
    private IReadOnlyList<UsuarioListaDto> UsuariosFiltrados
    {
        get
        {
            IEnumerable<UsuarioListaDto> filtrados = _usuarios;

            if (!string.IsNullOrWhiteSpace(_busqueda))
            {
                var termino = _busqueda.Trim();
                filtrados = filtrados.Where(u => Contiene(u.NombreCompleto, termino) || Contiene(u.Email, termino));
            }

            if (!string.IsNullOrWhiteSpace(_rolFiltro))
                filtrados = filtrados.Where(u => u.Rol == _rolFiltro);

            filtrados = _activacionFiltro switch
            {
                ActivacionActivos => filtrados.Where(u => u.Activo),
                ActivacionDesactivados => filtrados.Where(u => !u.Activo),
                _ => filtrados
            };

            return filtrados.ToList();
        }
    }

    /// <summary>
    /// Ignora mayúsculas <b>y</b> acentos: quien teclea "martinez" espera
    /// encontrar a "Martínez". Con <c>OrdinalIgnoreCase</c> no lo encontraría y
    /// el fallo sería mudo — la lista sale vacía y parece que esa persona no
    /// existe, que es exactamente el error que lleva a crearla dos veces.
    ///
    /// <para>
    /// Público solo para poder probarlo: montar la página entera en bUnit
    /// exigiría registrar UserManager, MediatR, Identity y el directorio de
    /// tenant para comprobar una función pura de dos cadenas.
    /// </para>
    /// </summary>
    public static bool Contiene(string texto, string termino) =>
        !string.IsNullOrEmpty(texto)
        && CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            texto, termino, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(UsuariosFiltrados.Count / (double)_tamanoPagina));
    private IReadOnlyList<UsuarioListaDto> UsuariosDePagina => UsuariosFiltrados.Skip((_pagina - 1) * _tamanoPagina).Take(_tamanoPagina).ToList();

    // Todo cambio de filtro vuelve a la página 1: si no, filtrar estando en la
    // página 3 deja la lista en una página que ya no existe y se ve vacía.
    private Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        _pagina = 1;
        return Task.CompletedTask;
    }

    private Task FiltrarPorRolAsync(string valor)
    {
        _rolFiltro = valor;
        _pagina = 1;
        return Task.CompletedTask;
    }

    private Task FiltrarPorActivacionAsync(string valor)
    {
        _activacionFiltro = valor;
        _pagina = 1;
        return Task.CompletedTask;
    }

    private Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _rolFiltro = string.Empty;
        _activacionFiltro = string.Empty;
        _pagina = 1;
        return Task.CompletedTask;
    }

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return Task.CompletedTask;
    }

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        _pagina = 1;
        return Task.CompletedTask;
    }

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _email = string.Empty;
    private string _nombreCompleto = string.Empty;
    /// <summary>
    /// Debe coincidir con la vigencia configurada para
    /// DataProtectionTokenProviderOptions en AddInfrastructure: el correo se
    /// lo promete al usuario, y prometer una caducidad distinta de la real es
    /// peor que no prometer ninguna.
    /// </summary>
    private const int MinutosCaducidadActivacion = 60;

    /// <summary>
    /// El enlace del alta recién hecha, para que quien la hizo pueda
    /// entregarlo por otra vía si el correo no llega. Se limpia al cerrar o
    /// reabrir el formulario: no tiene por qué sobrevivir a la operación.
    /// </summary>
    private string? _enlaceActivacion;
    private string _rol = Roles.Consulta;

    /// <summary>
    /// DEC-36 (REC-099): «permiso específico», no el rol Administrador a
    /// secas — solo se conserva al guardar si <see cref="_rol"/> sigue siendo
    /// Administrador (ver EditarUsuarioAsync/CrearUsuarioAsync), así que
    /// cambiar el rol de alguien lo retira automáticamente.
    /// </summary>
    private bool _permisoConsultarAccesoDocumentosSensibles;

    private bool _guardando;
    private string? _mensajeErrorFormulario;

    private IReadOnlyList<CoordinadorDto> _coordinadoresDisponibles = [];
    private string _coordinadorUsuarioId = string.Empty;

    private string _clienteCif = string.Empty;
    private EmpresaPorCifDto? _clienteEncontrado;
    private bool _buscandoCliente;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var idClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            _usuarioActualId = Guid.TryParse(idClaim, out var id) ? id : null;
            _usuarioActualEsAdministrador = estadoAutenticacion.User.IsInRole(Roles.Administrador);

            var usuarios = new List<UsuarioListaDto>();
            // Acotado al tenant activo: UserManager.Users no filtra nada
            // (AspNetUsers es la única tabla sin filtro global) y esta pantalla
            // listaba los usuarios de todas las organizaciones con nombre y
            // correo. Ver DirectorioUsuariosTenant.
            // Por la puerta: UserManager no pasa por MediatR y esta carga corre
            // en paralelo con los componentes del layout (ver PuertaAccesoDatos).
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                // Para un Operador Delegado se muestra el rol de la asignación
                // (Consulta/GestorCae/CoordinadorCae), no su rol de origen: un
                // operador de soporte es Administrador en el tenant de
                // plataforma, pero CurrentUserService.ObtenerRolActualAsync ya
                // lo acota al operar aquí — mostrar "Administrador" contradice
                // esa restricción y alarma sin motivo a quien lo ve.
                var rolesDelegados = await DirectorioUsuarios.ObtenerRolesDeOperadoresDelegadosAsync();

                // Dos consultas para toda la página, no una por fila: las
                // carteras vigentes del tenant, y los usuarios visibles (de
                // donde sale también qué gestores cuelga de cada coordinador,
                // sin volver a la base).
                var carteras = await DirectorioUsuarios.ObtenerCarterasVigentesAsync();
                var visibles = await DirectorioUsuarios.ObtenerVisiblesAsync();

                var gestoresPorCoordinador = visibles
                    .Where(u => u.CoordinadorUsuarioId is not null)
                    .ToLookup(u => u.CoordinadorUsuarioId!.Value, u => u.Id);

                foreach (var usuario in visibles)
                {
                    var activo = usuario.LockoutEnd is null || usuario.LockoutEnd < DateTimeOffset.UtcNow;
                    string rol;
                    var esOperadorDelegado = rolesDelegados.TryGetValue(usuario.Id, out var rolDelegado);
                    if (esOperadorDelegado)
                        rol = rolDelegado!;
                    else
                        rol = (await UserManager.GetRolesAsync(usuario)).FirstOrDefault() ?? "—";

                    usuarios.Add(new UsuarioListaDto(
                        usuario.Id, usuario.Email ?? string.Empty, usuario.NombreCompleto, rol, activo, esOperadorDelegado,
                        CalcularAlcance(usuario, rol, carteras, gestoresPorCoordinador)));
                }
            });

            _usuarios = usuarios;
            _pagina = 1;
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    /// <summary>
    /// El rol que entra aquí es el <b>efectivo en esta organización</b>: para
    /// un Operador Delegado, el de su asignación aquí y no el de su tenant de
    /// origen. Es el mismo que se pinta en la columna Rol, y tiene que serlo —
    /// decir "Administrador" en una columna y calcular el alcance con otra
    /// cosa sería peor que no decir nada.
    /// </summary>
    private static AlcanceUsuarioDto CalcularAlcance(
        ApplicationUser usuario,
        string rol,
        IReadOnlyDictionary<Guid, CarteraDeUsuario> carteras,
        ILookup<Guid, Guid> gestoresPorCoordinador)
    {
        // El mismo predicado que usa AlcanceDatosService para decidirlo de
        // verdad, no una copia: si allí un rol pasa a ver todo y aquí no, la
        // columna diría "sin cartera" de alguien que ve toda la organización.
        if (Roles.AlcanzaTodaLaOrganizacion(rol))
            return new("Todos los clientes", false,
                "Su rol alcanza toda la organización; no depende de ninguna Asignación de Cartera.");

        if (rol == Roles.Cliente)
            return usuario.ClienteId is not null
                ? new("1 cliente", false,
                    "Usuario de portal: solo ve la documentación relacionada con la empresa a la que está vinculado.")
                : new("Sin empresa vinculada", true,
                    "Un usuario de portal sin empresa vinculada no ve nada. Se vincula por CIF al editar la cuenta.");

        if (rol == Roles.CoordinadorCae)
        {
            // Un Coordinador CAE no tiene cartera propia: alcanza la unión de
            // las de los Gestores CAE que tiene asignados (ver
            // AlcanceDatosService.ObtenerClienteIdsParaCoordinadorAsync).
            // Mirar la suya sería mirar donde nunca hay nada.
            var gestores = gestoresPorCoordinador[usuario.Id].ToList();
            if (gestores.Count == 0)
                return new("Sin gestores asignados", true,
                    "Un Coordinador CAE alcanza lo que alcanzan los Gestores CAE que tiene asignados. Sin ninguno, no ve nada.");

            return DesdeCarteras(
                gestores.Where(carteras.ContainsKey).Select(id => carteras[id]).ToList(),
                explicacion: $"A través de {DescribirCantidad(gestores.Count, "Gestor CAE", "Gestores CAE")} que tiene asignados.",
                explicacionSinAlcance: "Sus Gestores CAE no tienen ninguna cartera vigente, así que tampoco él alcanza nada.");
        }

        if (rol == Roles.GestorCae)
            return DesdeCarteras(
                carteras.TryGetValue(usuario.Id, out var propia) ? [propia] : [],
                explicacion: "Por sus Asignaciones de Cartera vigentes en esta organización.",
                explicacionSinAlcance: "Sin Asignación de Cartera vigente no ve ningún cliente, y toda lista le sale vacía.");

        return new("—", false, "Esta cuenta todavía no tiene rol, así que no alcanza nada.");
    }

    private static AlcanceUsuarioDto DesdeCarteras(
        IReadOnlyList<CarteraDeUsuario> carteras, string explicacion, string explicacionSinAlcance)
    {
        // Universal es "toda la operación de este tenant", no "todos los
        // tenants" — el ámbito nunca se lee fuera de su propietario.
        if (carteras.Any(c => c.EsUniversal))
            return new("Toda la operación", false, explicacion);

        var clientes = carteras.SelectMany(c => c.ClienteIds).Distinct().Count();

        return clientes == 0
            ? new("Sin cartera", true, explicacionSinAlcance)
            : new(DescribirCantidad(clientes, "cliente", "clientes"), false, explicacion);
    }

    private static string DescribirCantidad(int cantidad, string singular, string plural) =>
        $"{cantidad} {(cantidad == 1 ? singular : plural)}";

    private async Task CambiarRolAsync(string valor)
    {
        _rol = valor;

        if (_rol == Roles.GestorCae)
            await CargarCoordinadoresAsync();
    }

    private async Task CargarCoordinadoresAsync()
    {
        var coordinadores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.CoordinadorCae);
        _coordinadoresDisponibles = coordinadores
            .Select(u => new CoordinadorDto(u.Id, u.NombreCompleto, u.Email ?? string.Empty))
            .ToList();
    }

    private async Task BuscarClientePorCifAsync(string valor)
    {
        _clienteCif = valor;
        _clienteEncontrado = null;

        if (string.IsNullOrWhiteSpace(valor)) return;

        _buscandoCliente = true;
        StateHasChanged();

        try
        {
            _clienteEncontrado = await Mediator.Send(new BuscarEmpresaPorCifQuery(valor));
        }
        finally
        {
            _buscandoCliente = false;
        }
    }

    private void AbrirCrear()
    {
        _editandoId = null;
        _email = string.Empty;
        _nombreCompleto = string.Empty;
        _enlaceActivacion = null;
        _rol = Roles.Consulta;
        _coordinadorUsuarioId = string.Empty;
        _clienteCif = string.Empty;
        _clienteEncontrado = null;
        _permisoConsultarAccesoDocumentosSensibles = false;
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var usuario = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.FindByIdAsync(id.ToString()));
        if (usuario is null)
        {
            ToastService.Mostrar("No encontramos este usuario.", TonoToast.Error);
            await CargarAsync();
            return;
        }

        var roles = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.GetRolesAsync(usuario));

        _editandoId = usuario.Id;
        _email = usuario.Email ?? string.Empty;
        _nombreCompleto = usuario.NombreCompleto;
        _enlaceActivacion = null;
        _rol = roles.FirstOrDefault() ?? Roles.Consulta;
        _coordinadorUsuarioId = usuario.CoordinadorUsuarioId?.ToString() ?? string.Empty;
        _clienteCif = string.Empty;
        _clienteEncontrado = null;
        _permisoConsultarAccesoDocumentosSensibles = usuario.PermisoConsultarAccesoDocumentosSensibles;

        if (_rol == Roles.GestorCae)
            await CargarCoordinadoresAsync();

        if (_rol == Roles.Cliente && usuario.ClienteId is not null)
        {
            var cliente = await Mediator.Send(new ObtenerClientePorIdQuery(usuario.ClienteId.Value));
            if (cliente is not null)
            {
                _clienteCif = cliente.Cif;
                _clienteEncontrado = new EmpresaPorCifDto(cliente.Id, cliente.RazonSocial, cliente.Cif);
            }
        }

        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        StateHasChanged();

        try
        {
            if (_rol == Roles.Cliente && _clienteEncontrado is null)
            {
                _mensajeErrorFormulario = "Busca y confirma el CIF del cliente a vincular antes de guardar.";
                return;
            }

            if (_editandoId is null)
                await CrearUsuarioAsync();
            else
                await EditarUsuarioAsync(_editandoId.Value);
        }
        finally
        {
            _guardando = false;
        }
    }

    private async Task CrearUsuarioAsync()
    {
        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_nombreCompleto))
        {
            // Sin "contraseña": el formulario ya no la pide — la cuenta nace sin
            // ninguna y el usuario la establece desde su enlace de activación.
            _mensajeErrorFormulario = "Correo y nombre son obligatorios.";
            return;
        }

        // ApplicationUser no lo sella el interceptor de tenant (no extiende
        // EntidadConTenant, ver CaeManagerDbContext), así que hay que
        // asignarlo aquí: sin esto el usuario nacía con TenantId vacío pese a
        // que el propio ApplicationUser documenta que "todo usuario nuevo debe
        // crearse con un TenantId explícito", y al iniciar sesión su claim de
        // tenant no correspondía a ninguna organización.
        if (TenantActual.TenantId is not { } tenantId)
        {
            _mensajeErrorFormulario = "No pudimos determinar tu organización. Vuelve a iniciar sesión.";
            return;
        }

        var usuario = new ApplicationUser
        {
            UserName = _email,
            Email = _email,
            NombreCompleto = _nombreCompleto,
            EmailConfirmed = true,
            TenantId = tenantId,
            CoordinadorUsuarioId = _rol == Roles.GestorCae && Guid.TryParse(_coordinadorUsuarioId, out var coordId) ? coordId : null,
            ClienteId = _rol == Roles.Cliente ? _clienteEncontrado?.Id : null,
            // Servidor, no solo UI (Codex, HO-099-01): DireccionCae también
            // abre esta página, y sin esta comprobación podría crear un
            // Administrador con el permiso ya concedido en el mismo alta.
            PermisoConsultarAccesoDocumentosSensibles =
                _usuarioActualEsAdministrador && _rol == Roles.Administrador && _permisoConsultarAccesoDocumentosSensibles,
            // Nadie más que el propio usuario llega a conocer su contraseña:
            // la cuenta nace SIN ninguna y él la establece desde el enlace de
            // activación. Por eso DebeCambiarContrasena queda en false — ya no
            // hay una contraseña ajena que haya que obligar a sustituir, que
            // era todo el sentido de esa marca.
            DebeCambiarContrasena = false
        };

        // Sin contraseña: CreateAsync(usuario) a secas. Antes se creaba con la
        // que escribía el Administrador y se le enviaba EN CLARO en el cuerpo
        // del correo — una credencial válida, sin caducidad propia, que queda
        // en dos buzones para siempre y que además el Administrador conocía.
        var resultado = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.CreateAsync(usuario));
        if (!resultado.Succeeded)
        {
            _mensajeErrorFormulario = string.Join(" ", resultado.Errors.Select(e => e.Description));
            return;
        }

        await PuertaAccesoDatos.EjecutarAsync(() => UserManager.AddToRoleAsync(usuario, _rol));

        _enlaceActivacion = await GenerarEnlaceActivacionAsync(usuario);

        ToastService.Mostrar("Usuario creado correctamente.", TonoToast.Exito);
        await EnviarCorreoActivacionAsync(usuario.Id, _email, _nombreCompleto, _enlaceActivacion);
        _drawerVisible = false;
        await CargarAsync();
    }

    /// <summary>
    /// Token de un solo uso de Identity —el mismo <c>DataProtectorTokenProvider</c>
    /// que usa "olvidé mi contraseña"— sobre la página que ya sabe consumirlo.
    /// No se inventa un mecanismo nuevo: el que hay ya es de un solo uso,
    /// caduca, y está probado.
    /// </summary>
    private async Task<string> GenerarEnlaceActivacionAsync(ApplicationUser usuario)
    {
        var token = await PuertaAccesoDatos.EjecutarAsync(
            () => UserManager.GeneratePasswordResetTokenAsync(usuario));
        var tokenCodificado = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));

        return NavigationManager
            .ToAbsoluteUri($"/cuenta/restablecer-contrasena?userId={usuario.Id}&code={tokenCodificado}")
            .ToString();
    }

    /// <summary>
    /// El correo lleva un ENLACE de activación, nunca una contraseña.
    ///
    /// <para>
    /// Antes viajaba en el cuerpo la contraseña temporal que acababa de
    /// escribir el Administrador: una credencial válida, sin caducidad propia,
    /// que quedaba almacenada en dos buzones indefinidamente y que además una
    /// segunda persona conocía. El correo no es un canal para credenciales —
    /// se reenvía, se archiva, se sincroniza y sobrevive a la cuenta.
    /// </para>
    ///
    /// <para>
    /// El enlace es de un solo uso y caduca (<c>DataProtectorTokenProvider</c>,
    /// con la vigencia configurada en <c>AddInfrastructure</c>), así que un
    /// correo viejo no sirve para entrar. Y si nunca llega, el Administrador
    /// tiene el mismo enlace en pantalla para entregarlo por otra vía, en vez
    /// de tener que dar de alta a la persona otra vez.
    /// </para>
    ///
    /// <para>
    /// Best-effort (Issue #2): un fallo de envío no deshace el alta, que ya se
    /// guardó — y ahora además no deja al usuario sin salida, porque el enlace
    /// sigue visible para quien acaba de crearlo.
    /// </para>
    /// </summary>
    private async Task EnviarCorreoActivacionAsync(Guid usuarioId, string email, string nombreCompleto, string enlaceActivacion)
    {
        var cuerpo = $"""
            <p>Hola {System.Net.WebUtility.HtmlEncode(nombreCompleto)},</p>
            <p>Se ha creado tu acceso a {Marca.Nombre}. Para entrar, establece tu contraseña:</p>
            <p><a href="{System.Net.WebUtility.HtmlEncode(enlaceActivacion)}">Establecer mi contraseña</a></p>
            <p>El enlace caduca en {MinutosCaducidadActivacion} minutos y solo puede usarse una vez. Si caduca, pide a quien te dio de alta que te envíe uno nuevo.</p>
            """;

        var resultado = await EmailService.EnviarAsync(email, $"Activa tu acceso a {Marca.Nombre}", cuerpo);
        if (resultado.EsFallido)
            Logger.LogWarning("No se pudo enviar el correo de activación a {UsuarioId}.", usuarioId);
    }

    private async Task EditarUsuarioAsync(Guid id)
    {
        if (string.IsNullOrWhiteSpace(_nombreCompleto))
        {
            _mensajeErrorFormulario = "El nombre es obligatorio.";
            return;
        }

        var actualizado = await PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await UserManager.FindByIdAsync(id.ToString());
            if (usuario is null) return false;

            usuario.NombreCompleto = _nombreCompleto;
            usuario.CoordinadorUsuarioId = _rol == Roles.GestorCae && Guid.TryParse(_coordinadorUsuarioId, out var coordId) ? coordId : null;
            usuario.ClienteId = _rol == Roles.Cliente ? _clienteEncontrado?.Id : null;

            // Solo un Administrador puede tocar este permiso (Codex,
            // HO-099-01): un DireccionCae editando otros campos de la misma
            // cuenta no debe poder cambiarlo en ninguna dirección, ni
            // concederlo ni revocarlo — el valor existente en base se
            // conserva tal cual si quien edita no es Administrador.
            if (_usuarioActualEsAdministrador)
                usuario.PermisoConsultarAccesoDocumentosSensibles = _rol == Roles.Administrador && _permisoConsultarAccesoDocumentosSensibles;

            await UserManager.UpdateAsync(usuario);

            var rolesActuales = await UserManager.GetRolesAsync(usuario);
            if (!rolesActuales.Contains(_rol))
            {
                await UserManager.RemoveFromRolesAsync(usuario, rolesActuales);
                await UserManager.AddToRoleAsync(usuario, _rol);
            }

            return true;
        });

        if (!actualizado)
        {
            _mensajeErrorFormulario = "No encontramos este usuario.";
            return;
        }

        ToastService.Mostrar("Usuario actualizado correctamente.", TonoToast.Exito);
        _drawerVisible = false;
        await CargarAsync();
    }

    private async Task CambiarActivacionAsync(UsuarioListaDto usuarioLista)
    {
        if (usuarioLista.Id == _usuarioActualId)
        {
            ToastService.Mostrar("No puedes desactivar tu propia cuenta.", TonoToast.Error);
            return;
        }

        var encontrado = await PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await UserManager.FindByIdAsync(usuarioLista.Id.ToString());
            if (usuario is null) return false;

            usuario.LockoutEnabled = true;
            usuario.LockoutEnd = usuarioLista.Activo ? DateTimeOffset.MaxValue : null;
            await UserManager.UpdateAsync(usuario);
            return true;
        });
        if (!encontrado) return;

        ToastService.Mostrar(
            usuarioLista.Activo ? "Usuario desactivado." : "Usuario reactivado.",
            TonoToast.Exito);

        await CargarAsync();
    }
}
