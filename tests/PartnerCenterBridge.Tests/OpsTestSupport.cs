using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Tests;

/// <summary>Shared doubles for the ops-workbench (operations, evidence, person, offboarding) tests.</summary>
internal static class OpsTest
{
    public static OperationRunRecorder Recorder(TestDb db) =>
        new(db.Context, new NullRunNotifier(), new FakeCurrentActor());

    public static T WithHttp<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    public static async Task<Tenant> AddTenantAsync(TestDb db, string name = "Contoso")
    {
        var tenant = new Tenant { TenantId = name.ToLowerInvariant() + "-tid", DisplayName = name };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        return tenant;
    }
}

internal sealed class NullRunNotifier : IRunNotifier
{
    public List<WorkflowRun> Notified { get; } = new();
    public Task NotifyAsync(WorkflowRun run, CancellationToken ct = default)
    {
        Notified.Add(run);
        return Task.CompletedTask;
    }
}

/// <summary>Grants exactly one role (or none) on every tenant, like a Local-mode caller with a single grant.</summary>
internal sealed class RoleAccess(TenantRole? role) : ITenantAccessService
{
    public Guid? CurrentUserId { get; } = Guid.NewGuid();
    public int Checks { get; private set; }
    public Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct)
    {
        Checks++;
        return Task.FromResult(role is not null && role.Value >= minimum);
    }
    public Task<IReadOnlyList<Guid>?> GetAuthorizedTenantIdsAsync(TenantRole minimum, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>?>(role is not null && role.Value >= minimum ? null : Array.Empty<Guid>());
}

internal sealed class FixedExchangeCapability(bool available, string? missing = null) : IExchangeCapability
{
    public ExchangeCapabilityStatus Check() => new(available, available ? null : missing ?? "Exchange Online is not configured: Exchange:AppId (app-only Exchange app registration) is not set.");
}

/// <summary>Scriptable Exchange double: records conversions and returns a configurable mailbox on re-read.</summary>
internal sealed class ScriptedExchange : IExchangeOnlineService
{
    public List<string> Calls { get; } = new();
    public ExoResult ConvertResult { get; set; } = new()
    {
        Steps = { new("Connect", true, "contoso"), new("Convert to shared", true, "user") }
    };
    public MailboxInfo? MailboxAfter { get; set; }

    public Task<MailboxInfo?> GetMailboxAsync(Tenant tenant, string identity, CancellationToken ct = default)
    {
        Calls.Add($"get:{identity}");
        return Task.FromResult(MailboxAfter);
    }

    public Task<ExoResult> ConvertToSharedAsync(Tenant tenant, string identity, string? forwardingSmtpAddress, bool deliverToMailboxAndForward, CancellationToken ct = default)
    {
        Calls.Add($"convert:{identity}:{forwardingSmtpAddress}");
        return Task.FromResult(ConvertResult);
    }

    public Task<IReadOnlyList<MailboxInfo>> ListSharedMailboxesAsync(Tenant tenant, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ArchiveState?> GetArchiveStateAsync(Tenant tenant, string identity, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ArchiveRemediationResult> RemediateArchiveAsync(Tenant tenant, string identity, ArchiveRemediationOptions options, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ArchiveRemediationResult> NudgeArchiveAsync(Tenant tenant, string identity, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class CountingOffboardingService : IOffboardingService
{
    public int Calls { get; private set; }
    public Task<OperationPlan> PlanAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new OperationPlan { OperationId = "offboarding", Target = new("user", userId, userId) });
    }
    public Task<OperationEvidence> ApplyAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new OperationEvidence { OperationId = "offboarding", Outcome = Outcome.NoChangeNeeded, Target = new("user", userId, userId) });
    }
}

/// <summary>A planned operation double returning canned plan/evidence and counting calls.</summary>
internal sealed class FakePlannedOperation : IPlannedOperation
{
    public string Id => "access-parity";
    public string Name => "Access parity";
    public string Description => "fake";
    public string Category => "Identity";
    public IReadOnlyList<WorkflowInput> Inputs => [new("sourceUserId", "s"), new("targetUserId", "t")];
    public int PlanCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public IReadOnlyCollection<string>? LastSelection { get; private set; }

    public OperationEvidence Evidence { get; set; } = new()
    {
        OperationId = "access-parity",
        OperationName = "Access parity",
        Target = new("user", "TGT-ID", "Target User"),
        Outcome = Outcome.Succeeded,
        Changes = { new ChangeResult { PlanItemId = "group:g1", Action = "AddMember", ObjectName = "Finance", Attempted = true, Succeeded = true } },
        Verification = { new VerificationCheck("Membership: Finance", true, "present") },
        TicketNotes = "Compared memberships. Added 1 missing eligible group membership."
    };

    public Task<OperationPlan> PlanAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        PlanCalls++;
        return Task.FromResult(new OperationPlan
        {
            OperationId = Id, OperationName = Name, TenantId = tenant.Id.ToString(),
            Target = new("user", "TGT-ID", "Target User"),
            Items = { new PlanItem { Id = "group:g1", Action = "AddMember", ObjectName = "Finance", Eligible = true, Category = "Security" } }
        });
    }

    public Task<OperationEvidence> ApplyAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, IReadOnlyCollection<string> selectedItemIds, CancellationToken ct = default)
    {
        ApplyCalls++;
        LastSelection = selectedItemIds;
        return Task.FromResult(Evidence);
    }

    public Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default) =>
        Task.FromResult(new DiagnosisResult());
    public Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default) =>
        Task.FromResult(new WorkflowRunResult());
}
