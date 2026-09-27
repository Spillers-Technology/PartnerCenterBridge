using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Diagnostics;
using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// First-run and health surface. <c>status</c> is anonymous and carries nothing sensitive (the SPA
/// uses it to decide between sign-in and create-first-account); <c>diagnostics</c> needs a signed-in
/// user and only an instance Administrator sees the check details -- everyone else gets the
/// capability flags the UI needs to hide features that cannot work.
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly HostingInfo _hosting;
    private readonly AuthModeInfo _authMode;
    private readonly BridgeDbContext _db;

    public SystemController(HostingInfo hosting, AuthModeInfo authMode, BridgeDbContext db)
    {
        _hosting = hosting;
        _authMode = authMode;
        _db = db;
    }

    /// <param name="Accountless">The Local Workbench is used without an account (launch-link sign-in only).</param>
    /// <param name="CanSkipAccount">First run may offer "use without an account" (Local profile, loopback only, no user yet).</param>
    /// <param name="WindowsUser">The Windows user the workbench runs as -- only when one of the two flags above is true.</param>
    /// <param name="SetupTicketRequired">First run of a Local Workbench: creating the first account (or choosing no account) needs the one-time setup link the exe opens.</param>
    public record SystemStatusDto(string Profile, string Version, string AuthMode, bool NeedsFirstUser,
        bool Accountless = false, bool CanSkipAccount = false, string? WindowsUser = null, bool SetupTicketRequired = false);

    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<SystemStatusDto> Status([FromServices] WorkbenchOwnerService owner, CancellationToken ct)
    {
        var needsFirstUser = _authMode.IsLocal && !await _db.AppUsers.AnyAsync(ct);
        // Never in the Server profile: there, owner.Local is null.
        var localOwnerPlane = _authMode.IsLocal && owner.Local is not null;
        var accountless = localOwnerPlane && await WorkbenchOwnerService.IsAccountlessAsync(_db, ct);
        var canSkip = localOwnerPlane && needsFirstUser && owner.UnavailableReason is null;
        return new SystemStatusDto(_hosting.Profile, _hosting.Version, _authMode.Mode, needsFirstUser,
            accountless, canSkip, accountless || canSkip ? Environment.UserName : null,
            localOwnerPlane && needsFirstUser);
    }

    [HttpGet("diagnostics")]
    [Authorize]
    public async Task<SystemDiagnosticsReport> Diagnostics(
        [FromServices] ISystemDiagnostics diagnostics, [FromServices] IInstanceAccessService access, CancellationToken ct)
    {
        var report = await diagnostics.RunAsync(ct);
        var roles = await access.GetRolesAsync(ct);
        return (roles & InstanceRole.Administrator) != 0
            ? report
            : report with { Checks = Array.Empty<SystemCheck>() };
    }

    public sealed record DependencyDecisionDto(bool Declined);

    [HttpPut("dependencies/{id}/decision")]
    [Authorize]
    public async Task<IActionResult> DependencyDecision(string id, [FromBody] DependencyDecisionDto decision,
        [FromServices] IInstanceAccessService access, [FromServices] IDependencySetupService setup, CancellationToken ct)
    {
        if (_hosting.Local is null || !DependencyIds.IsInstallable(id)) return NotFound();
        if ((await access.GetRolesAsync(ct) & InstanceRole.Administrator) == 0) return Forbid();
        await setup.SetDeclinedAsync(id, decision.Declined, ct);
        return NoContent();
    }

    [HttpPost("dependencies/{id}/install")]
    [Authorize]
    public async Task<ActionResult<DependencyInstallResult>> InstallDependency(string id,
        [FromServices] IInstanceAccessService access, [FromServices] IDependencySetupService setup, CancellationToken ct)
    {
        if (_hosting.Local is null || !DependencyIds.IsInstallable(id)) return NotFound();
        if ((await access.GetRolesAsync(ct) & InstanceRole.Administrator) == 0) return Forbid();
        return await setup.InstallAsync(id, ct);
    }
}
