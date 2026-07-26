using System.Text.Json.Nodes;
using PartnerCenterBridge.Api.GitSync;
using PartnerCenterBridge.Core.ConfigSnapshots;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Orchestration;

/// <summary>
/// Runs every registered <see cref="Core.Abstractions.IConfigSection"/> against one tenant and
/// persists the result as a <see cref="ConfigSnapshotRun"/> -- the "take a backup" action behind
/// the config-snapshots UI. A section failing to capture doesn't abort the run; it's recorded on
/// that section and the rest still complete, same spirit as <c>DeploymentOrchestrator</c>.
/// </summary>
public class ConfigSnapshotService
{
    private readonly BridgeDbContext _db;
    private readonly ConfigSectionCatalog _catalog;
    private readonly GitSyncService _gitSync;
    private readonly ILogger<ConfigSnapshotService> _log;

    public ConfigSnapshotService(BridgeDbContext db, ConfigSectionCatalog catalog, GitSyncService gitSync, ILogger<ConfigSnapshotService> log)
    {
        _db = db;
        _catalog = catalog;
        _gitSync = gitSync;
        _log = log;
    }

    public async Task<ConfigSnapshotRun> CaptureAsync(Tenant tenant, string operatorName, CancellationToken ct)
    {
        var run = new ConfigSnapshotRun { TenantId = tenant.Id, Operator = operatorName };
        var allSucceeded = true;

        foreach (var section in _catalog.All)
        {
            var row = new ConfigSnapshotSection { Run = run, RunId = run.Id, SectionId = section.Id, SectionName = section.Name };
            try
            {
                var json = await section.CaptureAsync(tenant, ct);
                row.ContentJson = json;
                row.ItemCount = (JsonNode.Parse(json) as JsonArray)?.Count ?? 0;
            }
            catch (Exception ex)
            {
                row.Error = ex.Message;
                allSucceeded = false;
                _log.LogWarning(ex, "Config section {Section} failed to capture for tenant {Tenant}.", section.Id, tenant.DisplayName);
            }
            run.Sections.Add(row);
        }

        run.CompletedAt = DateTimeOffset.UtcNow;
        run.Succeeded = allSucceeded;

        if (_gitSync.Enabled)
            run.GitCommitSha = await _gitSync.CommitSnapshotAsync(tenant, run, run.Sections.ToList(), ct);

        _db.ConfigSnapshotRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return run;
    }
}
