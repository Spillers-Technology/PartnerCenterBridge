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

    public record SystemStatusDto(string Profile, string Version, string AuthMode, bool NeedsFirstUser);

    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<SystemStatusDto> Status(CancellationToken ct)
    {
        Response.Headers[PortPreflight.InstanceHeader] = PortPreflight.InstanceHeaderValue;
        var needsFirstUser = _authMode.IsLocal && !await _db.AppUsers.AnyAsync(ct);
        return new SystemStatusDto(_hosting.Profile, _hosting.Version, _authMode.Mode, needsFirstUser);
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
}
