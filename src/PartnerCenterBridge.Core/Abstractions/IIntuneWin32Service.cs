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
    /// <param name="checkpoint">
    /// Called with the deployment whenever it reaches a state worth persisting before the next
    /// remote write: a non-terminal status before each stage, and the new Intune app id as soon as
    /// the create call returns. Lets the caller save as it goes, so a crash mid-deploy leaves a
    /// visibly in-progress record (and a reusable app id) rather than a stale one. It does not close
    /// the window between a remote write and the save that records it.
    /// </param>
    Task<Deployment> DeployAsync(
        Tenant tenant,
        AppTemplate template,
        Stream intuneWinPackage,
        Deployment? existing = null,
        IDeploymentProgress? progress = null,
        Func<Deployment, CancellationToken, Task>? checkpoint = null,
        CancellationToken ct = default);
}
