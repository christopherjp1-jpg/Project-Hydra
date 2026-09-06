using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Application.Vehiculos.Commands.EliminarVehiculo;
using CaeManager.Application.Vehiculos.Commands.EliminarVehiculos;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Vehiculos.Pages;

public partial class Vehiculos : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };

    // H2 (docs/ux-audit/02-clientes.md): paginador único en español, ver Clientes.razor.cs.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_paginacion.ItemsPerPage));

    private Task CambiarPaginaAsync(int pagina) => _paginacion.SetCurrentPageIndexAsync(pagina - 1);

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private async Task CambiarTamanoPaginaAsync(int tamano)
    {
        _paginacion.ItemsPerPage = tamano;
        await _paginacion.SetCurrentPageIndexAsync(0);
        if (_grid is not null)
            await _grid.RefreshDataAsync();
    }

    private QuickGrid<VehiculoListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private string _filtroEmpresaId = string.Empty;
    private string _filtroSubcontrataId = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<SubcontrataSelectorDto> _subcontratasDisponibles = [];

    private bool _drawerVisible;
    private string _tipoEmpleador = "empresa";

    // DDL-076: en perfil Cliente Directo con una única Empresa, el selector
    // de Empresa no aparece — se resuelve en silencio. Mismo mecanismo que
    // Trabajadores.razor.cs.
    private bool _resolverEmpresaEnSilencio;

    private string _empresaId = string.Empty;
    private string _subcontrataId = string.Empty;
    private string _nombre = string.Empty;
    private string _modelo = string.Empty;
    private string _numeroPlaca = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

    // Drawer ligero (Vehiculos TALVEG.dc.html, mismo patrón que
    // ClientePreviewDrawer/EmpresaPreviewDrawer): nombre de fila y "Ver" del
    // menú abren esto primero, no el Context Workspace directamente.
    private Guid? _previewVehiculoId;
    private bool _previewVisible;

    private void AbrirPreview(Guid id)
    {
        _previewVehiculoId = id;
        _previewVisible = true;
    }

    private Task AbrirDesdePreviewAsync((Guid Id, string Pestana) destino)
    {
        var nombre = _elementosPagina.FirstOrDefault(e => e.Id == destino.Id)?.Nombre ?? string.Empty;
        return WorkspaceService.AbrirAsync(EntidadWorkspace.Vehiculo, destino.Id, nombre, destino.Pestana);
    }

    private readonly HashSet<Guid> _seleccionados = [];

    /// <summary>
    /// Los checkboxes de fila solo se pintan con esto activo (Centro 360,
    /// PLAN-EJECUCION-UX.md § 0.9) — son ruido permanente para una acción
    /// ocasional. Apagarlo limpia la selección: dejar filas marcadas que ya
    /// no se ven dejaría la barra de acciones en lote apuntando a algo
    /// invisible.
    /// </summary>
    private bool _seleccionMultiple;

    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
            _seleccionados.Clear();
    }
    private List<VehiculoListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>
    /// Filtro de estado documental (ver ICalculoEstadoDocumentalService) — esta
    /// entidad no tiene estado propio en el modelo, se deriva de sus Documentos.
    /// </summary>
    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IValidator<CrearVehiculoCommand> ValidadorCrear { get; set; } = default!;

    private GridItemsProvider<VehiculoListaDto>? _proveedorElementos;

    protected override async Task OnInitializedAsync()
    {
        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (deLaUrl != _busqueda)
            _busqueda = deLaUrl;

        var estadoDeLaUrl = EstadoDocumentoUi.OpcionesDocumentales.Any(o => o.Valor == EstadoInicial)
            ? EstadoInicial!
            : string.Empty;
        if (estadoDeLaUrl != _estadoFiltro)
            _estadoFiltro = estadoDeLaUrl;
    }

    private async Task CambiarEstadoAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    private async ValueTask<GridItemsProviderResult<VehiculoListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<VehiculoListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;
            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            var resultado = await Mediator.Send(new ObtenerVehiculosQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                EmpresaId: Guid.TryParse(_filtroEmpresaId, out var empresaId) ? empresaId : null,
                SubcontrataId: Guid.TryParse(_filtroSubcontrataId, out var subcontrataId) ? subcontrataId : null,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage,
                OrdenarPor: ordenarPor,
                Descendente: descendente,
                EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro));

            _totalElementos = resultado.TotalElementos;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<VehiculoListaDto>(), 0);
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private async Task FiltrarPorEmpresaAsync(string valor)
    {
        _filtroEmpresaId = valor;
        _filtroSubcontrataId = string.Empty;
        await RecargarAsync();
    }

    private async Task FiltrarPorSubcontrataAsync(string valor)
    {
        _filtroSubcontrataId = valor;
        _filtroEmpresaId = string.Empty;
        await RecargarAsync();
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private async Task AbrirCrearAsync()
    {
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());

        var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _resolverEmpresaEnSilencio = perfil == PerfilVocabularioTenant.ClienteDirecto && _empresasDisponibles.Count == 1;

        // Si la lista ya está filtrada por Empresa o Subcontrata, se presupone
        // que el vehículo que se va a dar de alta es de ese mismo empleador.
        if (!string.IsNullOrWhiteSpace(_filtroSubcontrataId))
        {
            _tipoEmpleador = "subcontrata";
            _subcontrataId = _filtroSubcontrataId;
            _empresaId = string.Empty;
        }
        else if (_resolverEmpresaEnSilencio)
        {
            _tipoEmpleador = "empresa";
            _empresaId = _empresasDisponibles[0].Id.ToString();
            _subcontrataId = string.Empty;
        }
        else
        {
            _tipoEmpleador = "empresa";
            _empresaId = _filtroEmpresaId;
            _subcontrataId = string.Empty;
        }
        _nombre = string.Empty;
        _modelo = string.Empty;
        _numeroPlaca = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void SeleccionarTipoEmpresa() => CambiarTipoEmpleador("empresa");

    private void SeleccionarTipoSubcontrata() => CambiarTipoEmpleador("subcontrata");

    private void CambiarTipoEmpleador(string tipo)
    {
        _tipoEmpleador = tipo;
        _empresaId = string.Empty;
        _subcontrataId = string.Empty;
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
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            Guid? empresaId = null;
            Guid? subcontrataId = null;

            if (_tipoEmpleador == "empresa")
            {
                if (!Guid.TryParse(_empresaId, out var empresaIdValor))
                {
                    _mensajeErrorFormulario = "Selecciona una empresa.";
                    return;
                }
                empresaId = empresaIdValor;
            }
            else
            {
                if (!Guid.TryParse(_subcontrataId, out var subcontrataIdValor))
                {
                    _mensajeErrorFormulario = "Selecciona una subcontrata.";
                    return;
                }
                subcontrataId = subcontrataIdValor;
            }

            var resultado = await Mediator.Send(
                new CrearVehiculoCommand(empresaId, subcontrataId, _nombre, _modelo, _numeroPlaca));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Vehículo creado correctamente.", TonoToast.Exito);
            _drawerVisible = false;
            await RecargarAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    /// <summary>
    /// Validación inline al salir del campo (mismo patrón que Centros.razor,
    /// UX_PATTERNS.md, P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    private Task ValidarNombreAsync() => ValidarCampoAsync(nameof(CrearVehiculoCommand.Nombre));

    private Task ValidarModeloAsync() => ValidarCampoAsync(nameof(CrearVehiculoCommand.Modelo));

    private Task ValidarNumeroPlacaAsync() => ValidarCampoAsync(nameof(CrearVehiculoCommand.NumeroPlaca));

    private async Task ValidarCampoAsync(string campo)
    {
        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearVehiculoCommand(null, null, _nombre, _modelo, _numeroPlaca),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private void AbrirEliminar(Guid id, string nombre)
    {
        _idAEliminar = id;
        _nombreAEliminar = nombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarVehiculoCommand(_idAEliminar));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Vehículo eliminado correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el vehículo. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    private bool TodosSeleccionados =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _seleccionados.Contains(e.Id));

    private void AlternarSeleccionTodos(bool marcar)
    {
        if (marcar)
            foreach (var elemento in _elementosPagina) _seleccionados.Add(elemento.Id);
        else
            _seleccionados.Clear();
    }

    private void AlternarSeleccion(Guid id, bool marcado)
    {
        if (marcado) _seleccionados.Add(id);
        else _seleccionados.Remove(id);
    }

    private async Task ConfirmarEliminarLoteAsync()
    {
        _eliminandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarVehiculosCommand(_seleccionados.ToList()));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} vehículo(s) eliminado(s)."
                    : $"{dto.Eliminados} eliminado(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar los vehículos seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    private string ObtenerClaseFila(VehiculoListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

    private async Task ManejarAtajoAsync(string tecla)
    {
        if (_elementosPagina.Count == 0) return;

        switch (tecla)
        {
            case "j":
                {
                    var indiceActual = _idEnfocado is null ? -1 : _elementosPagina.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = _elementosPagina[Math.Min(indiceActual + 1, _elementosPagina.Count - 1)].Id;
                    break;
                }
            case "k":
                {
                    var indiceActual = _idEnfocado is null ? 0 : _elementosPagina.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = _elementosPagina[Math.Max(indiceActual - 1, 0)].Id;
                    break;
                }
            case "x":
                if (_idEnfocado is { } idAlternar)
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                break;
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                    AbrirPreview(idAbrir);
                break;
        }

        StateHasChanged();
    }
}
