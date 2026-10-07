using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.TenantAudits;

namespace PartnerCenterBridge.Tests.TenantAudits;

/// <summary>In-memory audit data providers: set a dataset, or set an exception to simulate "PCB cannot read this here".</summary>
internal sealed class FakeDirectoryData : IAuditDirectoryData
{
    public List<AuditUser> Users { get; } = new();
    public string? SignInUnavailable { get; set; }
    public Exception? UsersError { get; set; }
    public List<AuditSku> Skus { get; } = new();
    public Exception? SkusError { get; set; }
    public List<AuditRoleMember> Roles { get; } = new();
    public Exception? RolesError { get; set; }
    public List<AuditAuthRegistration> Registrations { get; } = new();
    public Exception? RegistrationsError { get; set; }
    public int UserReads { get; private set; }

    public Task<AuditUserSet> GetUsersAsync(AuditCheckContext ctx)
    {
        UserReads++;
        if (UsersError is not null) throw UsersError;
        return Task.FromResult(new AuditUserSet
        {
            Users = Users,
            SignInUnavailableReason = SignInUnavailable,
            SignInMissing = SignInUnavailable is null ? [] : ["Graph permission AuditLog.Read.All"]
        });
    }

    public Task<IReadOnlyList<AuditSku>> GetSubscribedSkusAsync(AuditCheckContext ctx) =>
        SkusError is not null ? throw SkusError : Task.FromResult<IReadOnlyList<AuditSku>>(Skus);
    public Task<IReadOnlyList<AuditRoleMember>> GetDirectoryRoleMembersAsync(AuditCheckContext ctx) =>
        RolesError is not null ? throw RolesError : Task.FromResult<IReadOnlyList<AuditRoleMember>>(Roles);
    public Task<IReadOnlyList<AuditAuthRegistration>> GetAuthMethodRegistrationsAsync(AuditCheckContext ctx) =>
        RegistrationsError is not null ? throw RegistrationsError : Task.FromResult<IReadOnlyList<AuditAuthRegistration>>(Registrations);
}

internal sealed class FakeDeviceData : IAuditDeviceData
{
    public List<AuditDevice> Devices { get; } = new();
    public List<AuditCompliancePolicy> Policies { get; } = new();
    public AuditDeviceManagementSettings Settings { get; set; } = new(true);
    public Exception? Error { get; set; }

    public Task<IReadOnlyList<AuditDevice>> GetManagedDevicesAsync(AuditCheckContext ctx) =>
        Error is not null ? throw Error : Task.FromResult<IReadOnlyList<AuditDevice>>(Devices);
    public Task<IReadOnlyList<AuditCompliancePolicy>> GetCompliancePoliciesAsync(AuditCheckContext ctx) =>
        Error is not null ? throw Error : Task.FromResult<IReadOnlyList<AuditCompliancePolicy>>(Policies);
    public Task<AuditDeviceManagementSettings> GetDeviceManagementSettingsAsync(AuditCheckContext ctx) => Task.FromResult(Settings);
}

internal sealed class FakeSecurityData : IAuditSecurityData
{
    public List<AuditConditionalAccessPolicy> Policies { get; } = new();
    public Dictionary<string, string> Names { get; } = new();
    public bool SecurityDefaults { get; set; }
    public Exception? CaError { get; set; }
    public AuditAuthorizationPolicy Authorization { get; set; } = new("adminsAndGuestInviters", false, false, false, [], null);
    public List<AuditPermissionGrant> Grants { get; } = new();

    public Task<AuditConditionalAccess> GetConditionalAccessAsync(AuditCheckContext ctx) =>
        CaError is not null ? throw CaError : Task.FromResult(new AuditConditionalAccess { Policies = Policies, Names = Names });
    public Task<bool> GetSecurityDefaultsEnabledAsync(AuditCheckContext ctx) => Task.FromResult(SecurityDefaults);
    public Task<AuditAuthorizationPolicy> GetAuthorizationPolicyAsync(AuditCheckContext ctx) => Task.FromResult(Authorization);
    public Task<IReadOnlyList<AuditPermissionGrant>> GetDelegatedPermissionGrantsAsync(AuditCheckContext ctx) =>
        Task.FromResult<IReadOnlyList<AuditPermissionGrant>>(Grants);
}

internal sealed class FakeMailboxData : IAuditMailboxData
{
    public string? UnavailableReason { get; set; }
    public AuditMailboxReport Report { get; set; } = new() { Mailboxes = [] };
    public Exception? Error { get; set; }

    public Task<AuditMailboxReport> GetMailboxReportAsync(AuditCheckContext ctx)
    {
        if (UnavailableReason is not null) throw new AuditUnavailableException(UnavailableReason, ["Exchange Online app-only access"]);
        return Error is not null ? throw Error : Task.FromResult(Report);
    }
}

internal static class AuditTest
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public const string E3 = "05e9a617-0261-4cee-bb44-138d3ef5d965";
    public const string FlowFree = "f30db892-07e9-47e9-837c-80727f46fd3d";

    public static Tenant Tenant() => new() { TenantId = "11111111-2222-3333-4444-555555555555", DisplayName = "Contoso Ltd" };

    public static AuditCheckContext Context(int? inactiveDays = null, int? deviceStaleDays = null)
    {
        var p = new Dictionary<string, int>();
        if (inactiveDays is { } i) p[AuditParameterKeys.InactiveDays.Key] = i;
        if (deviceStaleDays is { } s) p[AuditParameterKeys.DeviceStaleDays.Key] = s;
        return new AuditCheckContext(Tenant(), new AuditParameters(p), Now, CancellationToken.None);
    }

    /// <summary>A user; dates given as "days before Now" (null = not recorded).</summary>
    public static AuditUser User(string name, int? successDaysAgo = null, int? attemptDaysAgo = null, int createdDaysAgo = 400,
        bool enabled = true, bool guest = false, string[]? skus = null, string? externalState = null, int? stateChangedDaysAgo = null)
    {
        DateTimeOffset? Ago(int? d) => d is null ? null : Now.AddDays(-d.Value);
        return new AuditUser(
            $"id-{name}", name, $"{name.ToLowerInvariant().Replace(' ', '.')}@contoso.com", guest ? "Guest" : "Member", enabled,
            Ago(createdDaysAgo), false, externalState, Ago(stateChangedDaysAgo), skus ?? [E3],
            new AuditSignInActivity(Ago(successDaysAgo), Ago(attemptDaysAgo), null));
    }

    public static AuditSku Sku(string id, string part, int enabled = 10, int consumed = 10, int warning = 0, int suspended = 0) =>
        new(id, part, "Enabled", "User", enabled, warning, suspended, 0, consumed);

    public static AuditCheckResult Run(ITenantAuditCheck check, AuditCheckContext? ctx = null) =>
        TenantAuditEngine.RunCheckAsync(check, ctx ?? Context(), CancellationToken.None).GetAwaiter().GetResult();

    public static AuditFinding Finding(AuditCheckResult r, string key) =>
        Assert.Single(r.Findings, f => f.Id.EndsWith(":" + key, StringComparison.Ordinal));

    public static string[] Names(AuditFinding f) => f.Subjects.Select(s => s.Name).ToArray();
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
