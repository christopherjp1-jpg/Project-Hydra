using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Pages;

/// <summary>
/// Centro 360 (Centro 360 TALVEG, Parte XVI PROMPT 13) — la "segunda pantalla"
/// a la que se llega al pedir ver un centro por completo, ya sea desde el
/// enlace "Ver Centro 360" de la vista previa recortada de /centros
/// (AcordeonAsignacionesCentro.SoloIncidencias) o directamente por URL.
///
/// Deliberadamente NO reimplementa edición de Información, Requisitos del
/// Centro, Vehículos, Agenda ni Historial: eso ya vive, completo, en
/// CentroWorkspacePanel (el panel lateral que abre "Detalles" en /centros).
/// Esta página compone lo que ese panel no ofrece de un vistazo — cabecera
/// de cumplimiento, próxima visita y la lista de trabajadores COMPLETA (a
/// diferencia de la vista previa) — y remite al panel para operar.
/// </summary>
public partial class CentroDetalle : ComponentBase
{
    [Parameter] public Guid CentroId { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private CentroDetalleDto? _detalle;
    private CentroListaDto? _resumen;
    private IReadOnlyList<VisitaResumenDto> _visitas = [];
    private IReadOnlyList<CanalGestionResumenDto> _canales = [];
    private int _documentosPorReclamar;
    private bool _cargando = true;
    private bool _error;
    private bool _cargandoCanales = true;

    private IReadOnlyList<IncidenciaCentroDto> IncidenciasEmpresaHeader =>
        _resumen is null
            ? []
            : _resumen.Recuentos.Vencidas.Concat(_resumen.Recuentos.Proximas).Where(i => i.Ambito == AmbitoCausa.Empresa).ToList();

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        new[] { new BreadcrumbElemento("Centros"), new BreadcrumbElemento(_detalle?.Nombre ?? "…") };

    protected override async Task OnParametersSetAsync() => await CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;

        try
        {
            _detalle = await Mediator.Send(new ObtenerCentroPorIdQuery(CentroId));
            if (_detalle is null)
            {
                _error = true;
                return;
            }

            var pagina = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: null, ClienteId: null, Pagina: 1, TamanoPagina: 1, CentroId: CentroId));
            _resumen = pagina.Elementos.FirstOrDefault();

            var visitasPorCentro = await Mediator.Send(new ObtenerProximaVisitaPorCentroQuery([CentroId]));
            _visitas = visitasPorCentro.GetValueOrDefault(CentroId) ?? [];

            // Cuántos documentos hay pendientes de reclamar en ESTE centro.
            // Decide si el botón "Reclamar documentación" se pinta y con qué
            // recuento: antes aparecía siempre, también cuando no había nada
            // que reclamar, y remitía al panel para que el gestor descubriera
            // allí que no había objeto. Mismo criterio de "no tumbar la
            // página" que CentroWorkspacePanel: si esto falla, el botón
            // simplemente no aparece — es contexto, no el Centro.
            try
            {
                var lotes = await Mediator.Send(new ObtenerLoteReclamacionQuery(CentroId: CentroId));
                _documentosPorReclamar = lotes.FirstOrDefault()?.Documentos.Count ?? 0;
            }
            catch (Exception)
            {
                _documentosPorReclamar = 0;
            }

            // RendererInfo.IsInteractive: mismo motivo que TrabajadorDetalle
            // (ver ese commit) — OnParametersSetAsync también corre durante
            // el prerenderizado estático de una carga en frío, y un "_ = "
            // sin await ahí deja esta tarea en vuelo cuando ASP.NET Core ya
            // liberó el scope de DI al terminar esa fase, tirando el
            // DbContext a mitad de consulta (reportado en producción vía
            // Sentry: ObjectDisposedException sobre IServiceProvider,
            // mismo patrón exacto de pila que causó la carga en frío
            // colgada de Trabajador 360).
            if (RendererInfo.IsInteractive)
                _ = CargarCanalesAsync();
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    /// <summary>Aparte del resto: "Canales de gestión" es contexto de la barra lateral, no debe bloquear el render de la cabecera ni del cuerpo si tarda o falla.</summary>
    private async Task CargarCanalesAsync()
    {
        _cargandoCanales = true;
        try
        {
            _canales = await Mediator.Send(new ObtenerCanalesGestionDeCentroQuery(CentroId));
        }
        catch (Exception)
        {
            _canales = [];
        }
        finally
        {
            _cargandoCanales = false;
            StateHasChanged();
        }
    }

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/centros");
    }

    private void AbrirInformacion() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, CentroId, _detalle?.Nombre ?? string.Empty, "informacion");

    private void AbrirPlataforma() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, CentroId, _detalle?.Nombre ?? string.Empty, "plataforma");

    private void AbrirCliente() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, _detalle!.ClienteId, _detalle.ClienteRazonSocial, "informacion");

    private void AbrirEmpresa() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, _detalle!.EmpresaId, _detalle.EmpresaRazonSocial, "informacion");

    private void ProgramarVisita() =>
        NavigationManager.NavigateTo($"/visitas?centroId={CentroId}");

    /// <summary>
    /// "Generar informe del centro" del mockup no describe un informe nuevo —
    /// /reportes ya genera Vigencia documental/Incidencias/Asignaciones con
    /// alcance por Centro (GenerarInformeVigenciaQuery/GenerarInformeAsignacionesQuery,
    /// ambas con CentroId). Es un deep-link que preselecciona cliente y centro,
    /// mismo patrón que "Desglosar por cliente" de Inicio.
    /// </summary>
    private void GenerarInformeDelCentro() =>
        NavigationManager.NavigateTo($"/reportes?clienteId={_detalle!.ClienteId}&centroId={CentroId}");

    private static string DescribirRecuento(IReadOnlyList<IncidenciaCentroDto> incidencias, string calificativo)
    {
        var deEmpresa = incidencias.Count(i => i.Ambito == AmbitoCausa.Empresa);
        var deTrabajadores = incidencias.Count - deEmpresa;
        var cabeza = incidencias.Count == 1
            ? $"1 documento {calificativo}"
            : $"{incidencias.Count} documentos {(calificativo.EndsWith('o') ? calificativo + "s" : calificativo)}";

        var partes = new List<string>();
        if (deEmpresa > 0) partes.Add(deEmpresa == 1 ? "1 de empresa" : $"{deEmpresa} de empresa");
        if (deTrabajadores > 0) partes.Add(deTrabajadores == 1 ? "1 de trabajadores" : $"{deTrabajadores} de trabajadores");

        return partes.Count == 0 ? cabeza : $"{cabeza} — {string.Join(", ", partes)}";
    }
}
