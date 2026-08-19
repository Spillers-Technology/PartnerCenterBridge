using System.ComponentModel;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

[McpServerToolType]
public class TenantTools
{
    private readonly TenantsController _tenants;

    public TenantTools(BridgeDbContext db, ITenantAccessService access) => _tenants = new TenantsController(db, access);

    [McpServerTool, Description("Lists customer tenants the caller has access to, with GDAP delegation status.")]
    public async Task<IReadOnlyList<TenantDto>> ListTenants(CancellationToken ct) => await _tenants.List(ct);
}
