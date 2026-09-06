using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Services;

/// <summary>
/// Resuelve el tenant de la sesión Blazor desde el claim <c>tenant_id</c>
/// (ver <see cref="TenantClaimsPrincipalFactory"/> y docs/MULTITENANCY.md
/// § 8). Mismo patrón que <see cref="CurrentUserService"/>: dentro de un
/// circuito de Blazor, <c>AuthenticationStateProvider</c> ya trae el
/// <c>ClaimsPrincipal</c> correcto. Fuera de uno — endpoints minimal API
/// como <c>GET /documentos/{id}/archivo</c>, que no tienen circuito pero sí
/// <c>HttpContext.User</c> ya autenticado por la cookie de Identity — hace
/// falta el fallback a <see cref="IHttpContextAccessor"/>. Antes de asumir
/// "sin tenant" en cualquiera de los dos casos, consulta primero
/// <see cref="AmbitoTenantExplicito"/> — así la siembra al arrancar
/// (Program.cs) y futuros jobs de fondo pueden operar sin sesión sin que el
/// interceptor los rechace. Solo si no hay ámbito explícito ni usuario
/// autenticado por ninguna de las dos vías se resuelve a <c>null</c> — el
/// filtro global interpreta eso como "sin tenant, sin datos" (fallo cerrado).
///
/// El claim firmado es la base, y solo <b>sobre</b> él se aplica el
/// Delegated Workspace elegido en la sesión (<see cref="IClienteActivoSeleccionado"/>
/// — ADR-004 § 6, cuarto modo de la Tenant Resolution Strategy). Vacío para
/// cualquier usuario que no sea Operador Delegado de nadie, así que el
/// comportamiento no cambia para el caso de hoy.
///
/// El orden importa y es deliberado: primero se exige un claim firmado de
/// sesión, y solo si existe se consulta la selección. Así la selección puede
/// <i>cambiar</i> el tenant de un usuario ya autenticado, pero nunca
/// <i>crear</i> un contexto de tenant donde no había ninguno — una petición
/// sin sesión válida resuelve a null por mucho que traiga cookie (fallo
/// cerrado). La selección aporta además su propia garantía: viaja en un token
/// protegido y ligado al usuario, no en un GUID en claro que cualquiera
/// pudiera escribir a mano (ver <see cref="ClienteActivoSeleccionado"/> y el
/// hallazgo C-1 de INFORME-AUDITORIA-TECNICA.md).
///
/// <see cref="ITenantActual.TenantId"/> es síncrono porque EF Core necesita
/// evaluarlo dentro de un <c>HasQueryFilter</c>; tanto
/// <c>AuthenticationStateProvider.GetAuthenticationStateAsync()</c> en
/// Blazor Server como leer <c>IHttpContextAccessor.HttpContext.User</c> son
/// operaciones sin I/O real (el <c>ClaimsPrincipal</c> ya está materializado
/// en memoria en ambos casos).
///
/// <para>
/// <b>No se cachea el resultado — se resuelve en cada lectura.</b> Antes se
/// resolvía una única vez por instancia (scoped) bajo el supuesto de que
/// "sin I/O real" implicaba "sin motivo para cambiar dentro de la misma
/// petición". Ese supuesto era falso para <c>ExtensionAuthenticationHandler</c>
/// (MVP1 de extensión de navegador, ver ARQUITECTURA-INTEGRACIONES.md § 14 en
/// el repositorio de negocio): a diferencia de la cookie de Identity, ese
/// esquema llama a <c>UserManager.FindByIdAsync</c> en cada petición para
/// comprobar el security stamp, y esa consulta pasa por
/// <c>CaeManagerDbContext</c> — cuyo filtro global evalúa
/// <see cref="TenantId"/> DURANTE la propia autenticación, antes de que
/// <c>HttpContext.User</c> reflejara la identidad de la extensión. La primera
/// lectura (sin tenant, porque la petición aún no estaba autenticada) quedaba
/// cacheada para el resto de la petición, y ya no se volvía a calcular cuando
/// el endpoint corría después con el usuario ya autenticado de verdad — toda
/// petición autenticada solo por token de extensión (nunca por cookie, que es
/// el caso real de la extensión) veía su cartera vacía sin ningún error
/// visible. Verificado contra un servidor real: con cookie funcionaba
/// (enmascarando el defecto en cualquier prueba manual hecha desde un
/// navegador con sesión iniciada), sin cookie no.
/// </para>
/// </summary>
public class TenantActual(
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor,
    IClienteActivoSeleccionado clienteActivoSeleccionado) : ITenantActual
{
    public Guid? TenantId
    {
        get
        {
            if (AmbitoTenantExplicito.TenantIdActual is { } tenantIdExplicito)
                return tenantIdExplicito;

            var tenantId = ResolverAsync().GetAwaiter().GetResult();
            if (tenantId is null)
                return null;

            return clienteActivoSeleccionado.TenantIdSeleccionado ?? tenantId;
        }
    }

    private async Task<Guid?> ResolverAsync()
    {
        var desdeCircuito = await ResolverDesdeCircuitoAsync();
        if (desdeCircuito is { } tenantId)
            return tenantId;

        var usuarioHttp = httpContextAccessor.HttpContext?.User;
        if (usuarioHttp?.Identity?.IsAuthenticated != true)
            return null;

        var valorClaimHttp = usuarioHttp.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value;
        return Guid.TryParse(valorClaimHttp, out var tenantIdHttp) ? tenantIdHttp : null;
    }

    private async Task<Guid?> ResolverDesdeCircuitoAsync()
    {
        try
        {
            var estado = await authenticationStateProvider.GetAuthenticationStateAsync();
            if (estado.User.Identity?.IsAuthenticated != true)
                return null;

            var valorClaim = estado.User.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value;
            return Guid.TryParse(valorClaim, out var tenantId) ? tenantId : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
