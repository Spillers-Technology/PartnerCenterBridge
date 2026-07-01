using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>Per-tenant directory lookups that populate the provisioning UI's pickers.</summary>
[ApiController]
[Route("api/directory/{tenantId:guid}")]
[Authorize]
public class DirectoryController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly IGraphUserService _users;

    public DirectoryController(BridgeDbContext db, IGraphUserService users)
    {
        _db = db;
        _users = users;
    }

    [HttpGet("skus")]
    public Task<ActionResult<IReadOnlyList<SkuSummary>>> Skus(Guid tenantId, CancellationToken ct) =>
        Resolve(tenantId, ct, t => _users.ListSkusAsync(t, ct));

    [HttpGet("groups")]
    public Task<ActionResult<IReadOnlyList<DirectoryObject>>> Groups(Guid tenantId, CancellationToken ct) =>
        Resolve(tenantId, ct, t => _users.ListGroupsAsync(t, ct));

    [HttpGet("users")]
    public Task<ActionResult<IReadOnlyList<DirectoryObject>>> Users(Guid tenantId, [FromQuery] string? search, CancellationToken ct) =>
        Resolve(tenantId, ct, t => _users.ListUsersAsync(t, search, ct));

    private async Task<ActionResult<T>> Resolve<T>(Guid tenantId, CancellationToken ct, Func<Core.Entities.Tenant, Task<T>> op)
    {
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        try { return Ok(await op(tenant)); }
        catch (Exception ex) { return StatusCode(502, ex.Message); }
    }
}
