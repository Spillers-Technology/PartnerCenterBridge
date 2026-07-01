using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProvisioningController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly IGraphUserService _users;

    public ProvisioningController(BridgeDbContext db, IGraphUserService users)
    {
        _db = db;
        _users = users;
    }

    /// <summary>Create a new-hire user in the selected tenant, applying licenses/groups/manager.</summary>
    [HttpPost("hire")]
    public async Task<ActionResult<ProvisioningResult>> Hire(HireApiRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([req.TenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        return Ok(await _users.CreateUserAsync(tenant, req.Hire, ct));
    }

    /// <summary>Offboard a user: block sign-in, revoke sessions, strip licenses/groups per options.</summary>
    [HttpPost("terminate")]
    public async Task<ActionResult<ProvisioningResult>> Terminate(TerminateApiRequest req, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([req.TenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        return Ok(await _users.TerminateUserAsync(tenant, req.Termination, ct));
    }
}
