using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PartnerCenterBridge.Api.Orchestration;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Persistence behavior of the deploy fan-out (docs/specs/win32-package-lifecycle.md §8.1, D2):
/// deployments are saved per tenant and at each checkpoint, not once after the whole loop. Every
/// assertion reads through a fresh context, so it sees only what was actually committed.
/// </summary>
public class DeploymentOrchestratorTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<(AppTemplate Template, Tenant A, Tenant B)> SeedAsync()
    {
        var template = new AppTemplate
        {
            DisplayName = "7-Zip", InstallCommandLine = "i", UninstallCommandLine = "u", ContentVersion = 2,
            Content = new Win32ContentInfo
            {
                FileName = "IntunePackage.intunewin", EncryptionKey = "k", MacKey = "m", InitializationVector = "iv",
                Mac = "mac", ProfileIdentifier = "ProfileVersion1", FileDigest = "d", FileDigestAlgorithm = "SHA256",
                StagedPayloadRef = "pkg",
            },
        };
        var a = new Tenant { TenantId = "a.onmicrosoft.com", DisplayName = "A" };
        var b = new Tenant { TenantId = "b.onmicrosoft.com", DisplayName = "B" };
        _db.Context.AppTemplates.Add(template);
        _db.Context.Tenants.AddRange(a, b);
        await _db.Context.SaveChangesAsync();
        return (template, a, b);
    }

    private DeploymentOrchestrator Orchestrator(IIntuneWin32Service intune) =>
        new(_db.CreateContext(), intune, new EmptyPackageStore(), NullLogger<DeploymentOrchestrator>.Instance);

    private async Task<Deployment?> CommittedAsync(Guid tenantId)
    {
        await using var fresh = _db.CreateContext();
        return await fresh.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenantId);
    }

    [Fact]
    public async Task A_new_deployment_and_its_app_id_are_committed_while_the_deploy_is_still_running()
    {
        var (template, a, _) = await SeedAsync();
        Deployment? duringUpload = null;
        var intune = new ScriptedIntune(async (tenant, deployment, checkpoint, ct) =>
        {
            deployment.Status = DeploymentStatus.Pending;
            await checkpoint!(deployment, ct);
            deployment.IntuneAppId = "app-a";
            await checkpoint(deployment, ct);
            deployment.Status = DeploymentStatus.Uploading;
            await checkpoint(deployment, ct);
            duringUpload = await CommittedAsync(tenant.Id);
            deployment.Status = DeploymentStatus.Succeeded;
        });

        await Orchestrator(intune).DeployAsync(template.Id, new[] { a.Id });

        Assert.NotNull(duringUpload);
        Assert.Equal("app-a", duringUpload!.IntuneAppId);
        Assert.Equal(DeploymentStatus.Uploading, duringUpload.Status);
        Assert.Equal(DeploymentStatus.Succeeded, (await CommittedAsync(a.Id))!.Status);
    }

    [Fact]
    public async Task A_crash_mid_fan_out_keeps_finished_tenants_and_leaves_the_current_one_visibly_in_progress()
    {
        var (template, a, b) = await SeedAsync();
        var processed = new List<Guid>();
        var intune = new ScriptedIntune(async (tenant, deployment, checkpoint, ct) =>
        {
            processed.Add(tenant.Id);
            deployment.IntuneAppId = $"app-{tenant.DisplayName}";
            deployment.Status = DeploymentStatus.Uploading;
            await checkpoint!(deployment, ct);
            if (processed.Count == 2)
                throw new OperationCanceledException("host stopping"); // stands in for a process crash
            deployment.Status = DeploymentStatus.Succeeded;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Orchestrator(intune).DeployAsync(template.Id, new[] { a.Id, b.Id }));

        var finished = await CommittedAsync(processed[0]);
        var crashed = await CommittedAsync(processed[1]);
        Assert.Equal(DeploymentStatus.Succeeded, finished!.Status);
        // Not a stale Succeeded, not missing: in progress, with the app id a retry will reuse.
        Assert.Equal(DeploymentStatus.Uploading, crashed!.Status);
        Assert.StartsWith("app-", crashed.IntuneAppId);
    }

    [Fact]
    public async Task An_existing_deployment_is_passed_to_the_service_and_updated_in_place()
    {
        var (template, a, _) = await SeedAsync();
        var existingId = Guid.NewGuid();
        _db.Context.Deployments.Add(new Deployment
        {
            Id = existingId, AppTemplateId = template.Id, TenantId = a.Id, IntuneAppId = "app-a",
            Status = DeploymentStatus.Succeeded, LastSyncedAt = DateTimeOffset.UtcNow, DeployedTemplateVersion = 1,
        });
        await _db.Context.SaveChangesAsync();
        Deployment? received = null;
        var intune = new ScriptedIntune((_, deployment, _, _) =>
        {
            received = deployment;
            deployment.DeployedTemplateVersion = 2;
            deployment.Status = DeploymentStatus.Succeeded;
            return Task.CompletedTask;
        });

        await Orchestrator(intune).DeployAsync(template.Id, new[] { a.Id });

        Assert.Equal(existingId, received!.Id);
        Assert.Equal("app-a", received.IntuneAppId);
        await using var fresh = _db.CreateContext();
        var row = await fresh.Deployments.AsNoTracking().SingleAsync();
        Assert.Equal(existingId, row.Id);
        Assert.Equal(2, row.DeployedTemplateVersion);
    }

    private delegate Task Script(
        Tenant tenant, Deployment deployment, Func<Deployment, CancellationToken, Task>? checkpoint, CancellationToken ct);

    /// <summary>Stands in for the Graph flow; the script mutates the deployment the way the real service does.</summary>
    private sealed class ScriptedIntune(Script script) : IIntuneWin32Service
    {
        public async Task<Deployment> DeployAsync(
            Tenant tenant, AppTemplate template, Stream intuneWinPackage, Deployment? existing = null,
            IDeploymentProgress? progress = null, Func<Deployment, CancellationToken, Task>? checkpoint = null,
            CancellationToken ct = default)
        {
            var deployment = existing ?? throw new InvalidOperationException("orchestrator must pass a tracked deployment");
            await script(tenant, deployment, checkpoint, ct);
            return deployment;
        }
    }

    private sealed class EmptyPackageStore : IPackageStore
    {
        public Task<string> SaveAsync(Stream package, string suggestedName, CancellationToken ct = default) =>
            Task.FromResult("pkg");

        public Task<Stream> OpenAsync(string reference, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }
}
