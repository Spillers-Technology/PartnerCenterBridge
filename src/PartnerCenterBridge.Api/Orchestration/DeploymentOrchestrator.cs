using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Orchestration;

/// <summary>
/// Fans a template deploy out across many tenants and persists each <see cref="Deployment"/>
/// record. This is the "apply to many / push update to all" surface behind the deploy wizard.
/// </summary>
public class DeploymentOrchestrator
{
    private readonly BridgeDbContext _db;
    private readonly IIntuneWin32Service _intune;
    private readonly IPackageStore _packages;
    private readonly ILogger<DeploymentOrchestrator> _log;

    public DeploymentOrchestrator(
        BridgeDbContext db,
        IIntuneWin32Service intune,
        IPackageStore packages,
        ILogger<DeploymentOrchestrator> log)
    {
        _db = db;
        _intune = intune;
        _packages = packages;
        _log = log;
    }

    /// <summary>
    /// Deploy <paramref name="templateId"/> to each tenant in <paramref name="tenantIds"/>, reusing
    /// existing deployment records so an update pushes a new content version to apps that exist.
    /// Runs sequentially per tenant to keep Graph throttling and progress reporting simple.
    /// </summary>
    public async Task<IReadOnlyList<Deployment>> DeployAsync(
        Guid templateId, IReadOnlyCollection<Guid> tenantIds, CancellationToken ct = default)
    {
        var template = await _db.AppTemplates
            .Include(t => t.DetectionRules)
            .Include(t => t.Assignments)
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new KeyNotFoundException($"App template {templateId} not found.");

        if (template.Content is null)
            throw new InvalidOperationException("Template has no uploaded .intunewin package to deploy.");

        var tenants = await _db.Tenants.Where(t => tenantIds.Contains(t.Id)).ToListAsync(ct);
        var results = new List<Deployment>();

        foreach (var tenant in tenants)
        {
            var existing = await _db.Deployments
                .FirstOrDefaultAsync(d => d.TenantId == tenant.Id && d.AppTemplateId == template.Id, ct);

            // Each deploy needs its own package stream (the reader consumes it twice).
            await using var package = await _packages.OpenAsync(template.Content.StagedPayloadRef!, ct);

            var deployment = await _intune.DeployAsync(tenant, template, package, existing, progress: null, ct);
            if (existing is null)
                _db.Deployments.Add(deployment);

            results.Add(deployment);
        }

        await _db.SaveChangesAsync(ct);
        return results;
    }
}

/// <summary>
/// Stores uploaded .intunewin packages by reference so deploys can stream them per tenant. The
/// local filesystem implementation is fine for a single instance; swap for MinIO/S3 later.
/// </summary>
public interface IPackageStore
{
    Task<string> SaveAsync(Stream package, string suggestedName, CancellationToken ct = default);
    Task<Stream> OpenAsync(string reference, CancellationToken ct = default);
}

public class FilePackageStore : IPackageStore
{
    private readonly string _root;

    public FilePackageStore(IConfiguration config)
    {
        _root = config["Packages:Path"] ?? Path.Combine(AppContext.BaseDirectory, "packages");
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream package, string suggestedName, CancellationToken ct = default)
    {
        var reference = $"{Guid.NewGuid():N}-{Path.GetFileName(suggestedName)}";
        var path = Path.Combine(_root, reference);
        await using var fs = File.Create(path);
        await package.CopyToAsync(fs, ct);
        return reference;
    }

    public Task<Stream> OpenAsync(string reference, CancellationToken ct = default)
    {
        var path = Path.Combine(_root, reference);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }
}
