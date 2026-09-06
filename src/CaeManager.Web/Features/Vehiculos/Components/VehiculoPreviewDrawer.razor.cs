using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Vehiculos.Components;

public partial class VehiculoPreviewDrawer : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;

    [Parameter] public Guid? VehiculoId { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public EventCallback<(Guid Id, string Pestana)> OnOperar { get; set; }

    private string _pestanaActiva = "informacion";
    private Guid? _idCargado;

    private VehiculoDetalleDto? _detalle;
    private bool _cargando;

    // Reabrir sobre un Vehículo distinto (fila B tras fila A sin cerrar el
    // drawer) debe recargar todo desde cero — por eso se compara VehiculoId
    // aquí en vez de solo mirar Visible. Documentación/Historial no
    // necesitan estado propio: PestanaDocumentacion/PestanaHistorial ya
    // recargan solas cuando cambia su parámetro de propietario.
    protected override Task OnParametersSetAsync()
    {
        if (Visible && VehiculoId is { } id && _idCargado != id)
        {
            _idCargado = id;
            _pestanaActiva = "informacion";
            _detalle = null;
            return CargarInformacionAsync(id);
        }

        if (!Visible)
            _idCargado = null;

        return Task.CompletedTask;
    }

    private Task CambiarPestanaAsync(string pestana)
    {
        _pestanaActiva = pestana;
        return Task.CompletedTask;
    }

    private async Task CargarInformacionAsync(Guid vehiculoId)
    {
        _cargando = true;
        StateHasChanged();

        _detalle = await Mediator.Send(new ObtenerVehiculoPorIdQuery(vehiculoId));
        _cargando = false;
        StateHasChanged();
    }

    private Task Cerrar() => VisibleChanged.InvokeAsync(false);

    private async Task Operar(string pestana)
    {
        if (VehiculoId is not { } id) return;
        await VisibleChanged.InvokeAsync(false);
        await OnOperar.InvokeAsync((id, pestana));
    }
}
