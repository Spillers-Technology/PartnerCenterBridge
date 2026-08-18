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
    private readonly ITenantAccessService _access;

    public DiagnosticsTools(ITenantAccessService access) => _access = access;

    [McpServerTool, Description("Returns the identity this MCP server resolved for the caller of this tool call.")]
    public string WhoAmI() =>
        _access.CurrentUserId is { } id
            ? $"userId={id}, isSystemAdmin={_access.IsSystemAdmin}"
            : "no local user id resolved (OIDC/dev-auth caller, or auth context did not reach this tool call)";
}
