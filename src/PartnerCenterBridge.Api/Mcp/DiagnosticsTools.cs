using System.ComponentModel;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;

namespace PartnerCenterBridge.Api.Mcp;

/// <summary>
/// Not a real operator tool -- exists to make the MCP transport's auth wiring independently
/// verifiable (does the caller's identity actually reach a tool call?) rather than only provable
/// as a side effect of exercising some other tool.
/// </summary>
[McpServerToolType]
public class DiagnosticsTools
{
    private readonly IInstanceAccessService _access;

    public DiagnosticsTools(IInstanceAccessService access) => _access = access;

    [McpServerTool(ReadOnly = true, Destructive = false), Description("Returns the identity this MCP server resolved for the caller of this tool call.")]
    public async Task<string> WhoAmI(CancellationToken ct)
    {
        if (_access.CurrentUserId is not { } id)
            return "no local user id resolved (OIDC/dev-auth caller, or auth context did not reach this tool call)";
        var roles = InstanceRolePermissions.Expand(await _access.GetRolesAsync(ct));
        return $"userId={id}, instanceRoles={string.Join(',', roles)}";
    }
}
