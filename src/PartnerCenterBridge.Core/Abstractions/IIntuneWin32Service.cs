using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>Progress callback surface for the multi-step Win32 upload state machine.</summary>
public interface IDeploymentProgress
{
    void Report(DeploymentStatus status, string? detail = null);
}

/// <summary>
/// Orchestrates the full Graph beta Win32 LOB upload flow (create app -> content version ->
/// file -> Azure Blob upload -> commit -> set committed version -> assign) so callers can just
/// ask to deploy a template to a tenant. The encrypted payload comes from the reader.
/// </summary>
public interface IIntuneWin32Service
{
    /// <summary>
    /// Create or update the app in <paramref name="tenant"/> to match <paramref name="template"/>
    /// and (re)upload its current content. Returns the updated deployment record.
    /// </summary>
    Task<Deployment> DeployAsync(
        Tenant tenant,
        AppTemplate template,
        Stream intuneWinPackage,
        Deployment? existing = null,
        IDeploymentProgress? progress = null,
        CancellationToken ct = default);
}
