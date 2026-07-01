using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Core.Abstractions;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/admin/sam")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly ISamTokenStore _store;

    public AdminController(ISamTokenStore store) => _store = store;

    /// <summary>Whether the Secure Application Model has been bootstrapped (a refresh token is stored).</summary>
    [HttpGet("status")]
    public async Task<object> Status(CancellationToken ct) =>
        new { bootstrapped = await _store.GetRefreshTokenAsync(ct) is not null };

    /// <summary>
    /// Manually seed the SAM refresh token (e.g. one captured out-of-band). Prefer the interactive
    /// <c>bootstrap-sam</c> CLI flow; this exists for paste-in and rotation-recovery scenarios.
    /// </summary>
    [HttpPost("seed")]
    public async Task<IActionResult> Seed([FromBody] SeedRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RefreshToken)) return BadRequest("refreshToken is required.");
        await _store.SaveRefreshTokenAsync(req.RefreshToken, ct);
        return NoContent();
    }

    public record SeedRequest(string RefreshToken);
}
