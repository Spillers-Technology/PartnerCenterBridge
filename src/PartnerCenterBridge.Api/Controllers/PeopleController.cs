using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

public record PersonWorkspaceDto(
    Guid TenantId,
    string UserId,
    PersonSection<PersonProfile> Profile,
    PersonSection<IReadOnlyList<PersonLicense>> Licenses,
    PersonSection<IReadOnlyList<PersonGroup>> Groups,
    PersonSection<IReadOnlyList<string>> AuthMethods,
    PersonSection<MailboxInfo> Mailbox,
    PersonSection<IReadOnlyList<PersonDevice>> Devices,
    PersonSection<IReadOnlyList<WorkflowRunDto>> RecentRuns);

/// <summary>
/// The person workspace: everything PCB can read about one user, in independent sections. A
/// section PCB cannot read reports Unavailable with the reason instead of failing the page.
/// Viewer grant required; nothing here writes.
/// </summary>
[ApiController]
[Route("api/tenants/{tenantId:guid}/people")]
[Authorize]
public class PeopleController : ControllerBase
{
    private const int MaxConcurrency = 3;

    private readonly BridgeDbContext _db;
    private readonly IServiceScopeFactory _scopes;
    private readonly ITenantAccessService _access;

    public PeopleController(BridgeDbContext db, IServiceScopeFactory scopes, ITenantAccessService access)
    {
        _db = db;
        _scopes = scopes;
        _access = access;
    }

    [HttpGet("{userId}")]
    public async Task<ActionResult<PersonWorkspaceDto>> Get(Guid tenantId, string userId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound("Tenant not found.");
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest("userId is required.");

        // Profile first: it resolves the object id + UPN the mailbox lookup and run history need.
        // Every Graph call runs in its own DI scope -- the token path touches the scoped DbContext
        // (SAM token store), which must not be shared across concurrent calls (see SearchController).
        var profile = await InScope((IPersonDirectoryReader r) => r.GetProfileAsync(tenant, userId, ct));
        var objectId = profile.Data?.Id ?? userId;
        var upn = profile.Data?.Upn;

        var gate = new SemaphoreSlim(MaxConcurrency);
        async Task<T> Bounded<T>(Func<Task<T>> f)
        {
            await gate.WaitAsync(ct);
            try { return await f(); }
            finally { gate.Release(); }
        }

        var licenses = Bounded(() => InScope((IPersonDirectoryReader r) => r.GetLicensesAsync(tenant, objectId, ct)));
        var groups = Bounded(() => InScope((IPersonDirectoryReader r) => r.GetGroupsAsync(tenant, objectId, ct)));
        var auth = Bounded(() => InScope((IPersonDirectoryReader r) => r.GetAuthMethodsAsync(tenant, objectId, ct)));
        var devices = Bounded(() => InScope((IPersonDirectoryReader r) => r.GetDevicesAsync(tenant, objectId, ct)));
        var mailbox = Bounded(() => MailboxAsync(tenant, upn ?? userId, ct));
        await Task.WhenAll(licenses, groups, auth, devices, mailbox);

        return Ok(new PersonWorkspaceDto(
            tenantId, objectId, profile,
            licenses.Result, groups.Result, auth.Result, mailbox.Result, devices.Result,
            await RecentRunsAsync(tenantId, userId, objectId, upn, ct)));
    }

    private async Task<T> InScope<TService, T>(Func<TService, Task<T>> call) where TService : notnull
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task<PersonSection<MailboxInfo>> MailboxAsync(Tenant tenant, string identity, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var capability = scope.ServiceProvider.GetRequiredService<IExchangeCapability>().Check();
        if (!capability.Available)
            return PersonSection<MailboxInfo>.Unavailable(capability.MissingDependency ?? "Exchange Online is not configured.");
        try
        {
            var exchange = scope.ServiceProvider.GetRequiredService<IExchangeOnlineService>();
            var mailbox = await exchange.GetMailboxAsync(tenant, identity, ct);
            return mailbox is null
                // The EXO script cannot distinguish "no mailbox" from a failed lookup; say so.
                ? PersonSection<MailboxInfo>.Ok(null!, "Exchange Online returned no mailbox for this user (no mailbox, or the lookup failed).")
                : PersonSection<MailboxInfo>.Ok(mailbox);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PersonSection<MailboxInfo>.Error(ex.Message);
        }
    }

    private async Task<PersonSection<IReadOnlyList<WorkflowRunDto>>> RecentRunsAsync(
        Guid tenantId, string userId, string objectId, string? upn, CancellationToken ct)
    {
        try
        {
            var keys = new[] { userId, objectId, upn }
                .Select(WorkflowEvidenceAdapter.NormalizeTargetId)
                .Where(k => k is not null).Select(k => k!).Distinct().ToList();
            var runs = await _db.WorkflowRuns.AsNoTracking().Include(r => r.Tenant)
                .Where(r => r.TenantId == tenantId && r.TargetId != null && keys.Contains(r.TargetId))
                .OrderByDescending(r => r.StartedAt)
                .Take(10)
                .ToListAsync(ct);
            return PersonSection<IReadOnlyList<WorkflowRunDto>>.Ok(runs.Select(WorkflowRunDto.From).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PersonSection<IReadOnlyList<WorkflowRunDto>>.Error(ex.Message);
        }
    }
}
