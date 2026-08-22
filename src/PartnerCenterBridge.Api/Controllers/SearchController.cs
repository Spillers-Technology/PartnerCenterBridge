using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

public record GlobalUserHit(Guid TenantId, string TenantName, string Id, string DisplayName, string? UserPrincipalName);
public record TenantSearchError(Guid TenantId, string TenantName, string Message);
public record GlobalSearchResult(IReadOnlyList<GlobalUserHit> Hits, IReadOnlyList<TenantSearchError> Errors, int TenantsSearched);

/// <summary>
/// Cross-tenant people search: fans one query out to every Active tenant so the helpdesk can
/// start from "a user has a problem" instead of "which tenant was that again". Per-tenant
/// failures are reported alongside the hits rather than failing the whole search.
/// </summary>
[ApiController]
[Route("api/search")]
[Authorize]
public class SearchController : ControllerBase
{
    private const int MaxConcurrency = 4;

    private readonly BridgeDbContext _db;
    private readonly IServiceScopeFactory _scopes;
    private readonly ITenantAccessService _access;

    public SearchController(BridgeDbContext db, IServiceScopeFactory scopes, ITenantAccessService access)
    {
        _db = db;
        _scopes = scopes;
        _access = access;
    }

    [HttpGet("users")]
    public async Task<ActionResult<GlobalSearchResult>> Users([FromQuery] string? q, CancellationToken ct)
    {
        var query = (q ?? "").Trim();
        if (query.Length < 3) return BadRequest("Search needs at least 3 characters.");

        var allowed = await _access.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        var tenantQuery = _db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .AsQueryable();
        if (allowed is not null)
            tenantQuery = tenantQuery.Where(tenant => allowed.Contains(tenant.Id));
        var tenants = await tenantQuery.OrderBy(t => t.DisplayName).ToListAsync(ct);

        var hits = new List<GlobalUserHit>();
        var errors = new List<TenantSearchError>();
        // Each parallel lookup gets its own DI scope: the Graph token path touches the scoped
        // DbContext (SAM token store), which must not be shared across concurrent calls.
        var gate = new SemaphoreSlim(MaxConcurrency);
        await Task.WhenAll(tenants.Select(async tenant =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var users = scope.ServiceProvider.GetRequiredService<IGraphUserService>();
                var found = await users.ListUsersAsync(tenant, query, ct);
                lock (hits)
                    hits.AddRange(found.Select(u => new GlobalUserHit(
                        tenant.Id, tenant.DisplayName, u.Id, u.DisplayName, u.UserPrincipalName)));
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(new(tenant.Id, tenant.DisplayName, ex.Message));
            }
            finally { gate.Release(); }
        }));

        return Ok(new GlobalSearchResult(
            hits.OrderBy(h => h.TenantName).ThenBy(h => h.DisplayName).ToList(),
            errors.OrderBy(e => e.TenantName).ToList(),
            tenants.Count));
    }
}
