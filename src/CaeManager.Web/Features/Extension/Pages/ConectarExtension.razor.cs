using System.Security.Claims;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Extension.Pages;

/// <summary>
/// Incremento 4 del MVP1 de integración con plataformas CAE externas vía
/// extensión de navegador: cierra el hueco que #484 dejó explícito ("sin UI
/// todavía, la página de Conectar extensión no puede cerrarse hasta que
/// exista la extensión que reciba el token"). Genera el mismo token que
/// consume <c>ExtensionTokenEndpoints</c>, vía <see cref="EmisorTokenExtension"/>
/// -- llamado aquí directamente, en el circuito interactivo, en vez de por
/// HTTP con antiforgery de formulario: mismo criterio que ya usa
/// <c>ClavesApi.razor</c> para generar sus secretos.
/// </summary>
public partial class ConectarExtension : ComponentBase
{
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private IDataProtectionProvider DataProtectionProvider { get; set; } = default!;

    private bool _generando;
    private string? _token;
    private DateTime? _expiraEnUtc;
    private string? _error;

    private async Task GenerarAsync()
    {
        _generando = true;
        _error = null;
        StateHasChanged();

        try
        {
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var valorClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(valorClaim, out var usuarioId))
            {
                _error = "No pudimos identificar tu sesión. Vuelve a iniciar sesión.";
                return;
            }

            var resultado = await EmisorTokenExtension.EmitirAsync(usuarioId, UserManager, DataProtectionProvider);
            if (resultado.EsFallido)
            {
                _error = resultado.Error.Mensaje;
                return;
            }

            (_token, _expiraEnUtc) = resultado.Valor;
        }
        finally
        {
            _generando = false;
        }
    }
}
