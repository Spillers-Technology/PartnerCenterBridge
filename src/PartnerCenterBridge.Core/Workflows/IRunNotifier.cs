using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Workflows;

/// <summary>
/// Pushes a completed <see cref="WorkflowRun"/> to an external channel (e.g. a Teams incoming
/// webhook). Implementations are best-effort and must never throw into the request pipeline.
/// </summary>
public interface IRunNotifier
{
    Task NotifyAsync(WorkflowRun run, CancellationToken ct = default);
}
