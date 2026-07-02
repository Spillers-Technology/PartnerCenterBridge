using PartnerCenterBridge.Core.Abstractions;

namespace PartnerCenterBridge.Graph.Workflows;

/// <summary>Runs a remediation step, recording success/failure without aborting the remaining steps.</summary>
internal static class WorkflowSteps
{
    public static async Task RunAsync(List<ProvisioningStep> steps, string name, Func<Task<string?>> action)
    {
        try { steps.Add(new ProvisioningStep(name, true, await action())); }
        catch (Exception ex) { steps.Add(new ProvisioningStep(name, false, ex.Message)); }
    }
}
