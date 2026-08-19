using System.Reflection;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Mcp;

namespace PartnerCenterBridge.Tests;

public class McpToolAnnotationTests
{
    [Theory]
    [InlineData(typeof(DiagnosticsTools), nameof(DiagnosticsTools.WhoAmI))]
    [InlineData(typeof(WorkflowTools), nameof(WorkflowTools.ListWorkflows))]
    [InlineData(typeof(WorkflowTools), nameof(WorkflowTools.DiagnoseWorkflow))]
    [InlineData(typeof(TenantTools), nameof(TenantTools.ListTenants))]
    [InlineData(typeof(DashboardTools), nameof(DashboardTools.GetDashboard))]
    [InlineData(typeof(PendingActionTools), nameof(PendingActionTools.CheckPendingAction))]
    public void Read_only_tools_are_annotated_non_destructive(Type toolType, string methodName)
    {
        var annotation = toolType.GetMethod(methodName)!
            .GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.True(annotation.ReadOnly);
        Assert.False(annotation.Destructive);
    }

    [Fact]
    public void Remediation_tool_is_annotated_destructive()
    {
        var annotation = typeof(WorkflowTools).GetMethod(nameof(WorkflowTools.RemediateWorkflow))!
            .GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.False(annotation.ReadOnly);
        Assert.True(annotation.Destructive);
    }
}
