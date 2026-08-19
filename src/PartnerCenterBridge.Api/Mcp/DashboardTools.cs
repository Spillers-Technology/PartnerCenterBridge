using System.ComponentModel;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

[McpServerToolType]
public class DashboardTools
{
    private readonly DashboardController _dashboard;

    public DashboardTools(BridgeDbContext db) => _dashboard = new DashboardController(db);

    [McpServerTool, Description("Landing-page stats and the operator triage list: failed deployments, tenants missing GDAP delegation, and recent failed workflow runs.")]
    public async Task<DashboardDto> GetDashboard(CancellationToken ct) => await _dashboard.Get(ct);
}
