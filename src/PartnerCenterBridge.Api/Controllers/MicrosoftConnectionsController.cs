using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController, Authorize, Route("api/microsoft-connections")]
public sealed class MicrosoftConnectionsController(DirectTenantConnection connections,
    IInstanceAccessService instanceAccess, ITenantAccessService access, BridgeDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new
    {
        available = connections.Available,
        configured = await connections.GetClientIdAsync(ct) is not null,
        canConfigure = connections.Available && await instanceAccess.HasPermissionAsync(InstancePermission.ManageSam, ct),
        connections = await connections.ListAsync(ct)
    });

    public sealed record ConnectRequest(Guid? TenantId);
    public sealed record SetupRequest(string ClientId);

    [HttpPut("setup")]
    public async Task<IActionResult> Setup(SetupRequest request, CancellationToken ct)
    {
        if (!connections.Available) return BadRequest("Microsoft sign-in setup requires a loopback-only Local Workbench.");
        try { await connections.ConfigureAsync(request.ClientId, ct); return NoContent(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost]
    public async Task<IActionResult> Connect(ConnectRequest request, CancellationToken ct)
    {
        if (!connections.Available) return BadRequest("Direct Microsoft sign-in is available in a loopback-only Local Workbench.");
        if (!await instanceAccess.HasPermissionAsync(InstancePermission.ManageTenantRegistry, ct)) return Forbid();
        if (request.TenantId is { } id)
        {
            if (!await access.HasRoleAsync(id, TenantRole.Owner, ct)) return Forbid();
            if (!await db.Tenants.AnyAsync(t => t.Id == id, ct)) return NotFound();
        }
        try { return Ok(TenantDto.From(await connections.ConnectAsync(request.TenantId, ct))); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (OperationCanceledException) { return BadRequest("Microsoft sign-in was cancelled or timed out. You can try again."); }
        catch (MsalException) { return BadRequest("Microsoft sign-in did not complete. Check the app registration, tenant consent, and sign-in policy, then try again."); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }
}
