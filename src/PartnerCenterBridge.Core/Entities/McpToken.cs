namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A long-lived personal access token for headless/scripted MCP clients that don't want an
/// interactive OAuth flow. Revocation is real (unlike a normal login JWT, which is stateless and
/// unrevocable by design) because losing an MCP client's token is a realistic incident to recover
/// from -- the JWT this mints carries this row's Id as its "jti" claim, checked on every request
/// (see Program.cs's OnTokenValidated).
/// </summary>
public class McpToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
