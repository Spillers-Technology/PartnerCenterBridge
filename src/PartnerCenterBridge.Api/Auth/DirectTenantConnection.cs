using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Api.Auth;

public sealed record DirectConnectionState(string AccountId, string Username, string Cache, bool ReconnectRequired = false, string? Claims = null);
public sealed record DirectConnectionInfo(Guid Id, string TenantId, string DisplayName, string Username, bool ReconnectRequired);

/// <summary>One encrypted MSAL cache per local operator and directory. Never shares an admin identity.</summary>
public sealed class DirectTenantConnection(BridgeDbContext db, IDataProtectionProvider protection,
    IConfiguration config, HostingInfo hosting, ITenantAccessService access, SamTokenService sam,
    IInstanceAccessService instanceAccess) : ITokenProvider
{
    // This feature is local-only (one process owns the SQLite data directory). Serialize cache rotation
    // and onboarding so concurrent requests cannot overwrite a newer refresh token or duplicate a tenant.
    internal static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly SemaphoreSlim SignInGate = new(1, 1);
    private readonly IDataProtector protector = protection.CreateProtector("PartnerCenterBridge.DirectTenant.v1");
    public bool Available => hosting.Local is { IsLoopbackOnly: true } && access.CurrentUserId is not null;
    private const string ClientIdKey = "direct-client-id";
    public async Task<string?> GetClientIdAsync(CancellationToken ct)
    {
        var saved = await db.Secrets.AsNoTracking().SingleOrDefaultAsync(s => s.Name == ClientIdKey, ct);
        var value = saved is null ? config["MicrosoftSignIn:ClientId"]
            ?? typeof(DirectTenantConnection).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "PcbMicrosoftClientId")?.Value
            : protector.Unprotect(saved.ProtectedValue);
        return Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString() : null;
    }

    public async Task ConfigureAsync(string clientId, CancellationToken ct)
    {
        if (!Available || !await instanceAccess.HasPermissionAsync(InstancePermission.ManageSam, ct))
            throw new UnauthorizedAccessException();
        if (!Guid.TryParse(clientId, out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Enter the Application (client) ID from your Microsoft app registration.");
        if (!await SignInGate.WaitAsync(0, ct))
            throw new InvalidOperationException("Finish the current Microsoft sign-in before changing setup.");
        try
        {
            await Gate.WaitAsync(ct);
            try
            {
                var current = await GetClientIdAsync(ct);
                if (current != id.ToString() && await db.Secrets.AnyAsync(s => s.Name.StartsWith("direct:"), ct))
                    throw new InvalidOperationException("The application ID cannot be changed while Microsoft account connections exist.");
                var record = await db.Secrets.SingleOrDefaultAsync(s => s.Name == ClientIdKey, ct);
                if (record is null) db.Secrets.Add(record = new SecretRecord { Name = ClientIdKey, ProtectedValue = "" });
                record.ProtectedValue = protector.Protect(id.ToString());
                record.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            finally { Gate.Release(); }
        }
        finally { SignInGate.Release(); }
    }
    private string Key(string tenantId) => $"direct:{access.CurrentUserId}:{(Guid.TryParse(tenantId, out var id) ? id.ToString() : tenantId)}";
    private static string Marker(string tenantId) => $"direct-mode:{tenantId}";
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    private sealed class Session(IPublicClientApplication app, string? cache)
    {
        public IPublicClientApplication App { get; } = app;
        public string? Cache { get; set; } = cache;
    }

    private async Task<Session> CreateAppAsync(string tenantId, DirectConnectionState? state, CancellationToken ct)
    {
        var clientId = await GetClientIdAsync(ct);
        if (!Available || clientId is null) throw new InvalidOperationException("Complete Microsoft sign-in setup in Settings > Microsoft connections first.");
        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithRedirectUri("http://localhost").Build();
        var session = new Session(app, state?.Cache);
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            if (session.Cache is not null) args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(session.Cache));
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged) session.Cache = Convert.ToBase64String(args.TokenCache.SerializeMsalV3());
        });
        return session;
    }

    private DirectConnectionState Read(SecretRecord record) => JsonSerializer.Deserialize<DirectConnectionState>(protector.Unprotect(record.ProtectedValue))!;
    private async Task SaveAsync(string tenantId, DirectConnectionState state, CancellationToken ct)
    {
        var key = Key(tenantId);
        var record = await db.Secrets.SingleOrDefaultAsync(s => s.Name == key, ct);
        if (record is null) db.Secrets.Add(record = new SecretRecord { Name = key, ProtectedValue = "" });
        record.ProtectedValue = protector.Protect(JsonSerializer.Serialize(state));
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DirectConnectionInfo>> ListAsync(CancellationToken ct)
    {
        if (!Available) return [];
        var allowed = await access.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        var tenants = await db.Tenants.AsNoTracking().Where(t => allowed == null || allowed.Contains(t.Id)).ToListAsync(ct);
        var result = new List<DirectConnectionInfo>();
        foreach (var tenant in tenants)
        {
            var key = Key(tenant.TenantId);
            var record = await db.Secrets.AsNoTracking().SingleOrDefaultAsync(s => s.Name == key, ct);
            if (record is null) continue;
            var state = Read(record);
            result.Add(new(tenant.Id, tenant.TenantId, tenant.DisplayName, state.Username, state.ReconnectRequired));
        }
        return result;
    }

    public async Task<Tenant> ConnectAsync(Guid? existingId, CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException("Microsoft sign-in requires a loopback-only Local Workbench and a local operator session.");
        if (!await SignInGate.WaitAsync(0, ct)) throw new InvalidOperationException("Another Microsoft sign-in is in progress. Finish it before starting another.");
        var ownsCacheGate = false;
        try
        {
            Tenant? existing = existingId is null ? null : await db.Tenants.SingleAsync(t => t.Id == existingId, ct);
            var authority = existing?.TenantId ?? "organizations";
            SecretRecord? previous = existing is null ? null : await db.Secrets.SingleOrDefaultAsync(s => s.Name == Key(existing.TenantId), ct);
            var state = previous is null ? null : Read(previous);
            var session = await CreateAppAsync(authority, state, ct);
            var app = session.App;
            var request = app.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false);
            if (state is null) request = request.WithPrompt(Prompt.SelectAccount);
            else request = request.WithLoginHint(state.Username);
            if (state?.Claims is { } claims) request = request.WithClaims(claims);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await request.ExecuteAsync(timeout.Token);
            if (!Guid.TryParse(result.TenantId, out var directoryId)) throw new InvalidOperationException("Microsoft did not return an organization tenant.");
            var tenantId = directoryId.ToString();
            if (existing is not null && !string.Equals(existing.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sign in to the selected tenant to reconnect it.");
            if (state is not null && state.AccountId != result.Account.HomeAccountId.Identifier)
                throw new InvalidOperationException("Reconnect with the original Microsoft account.");
            using var http = new HttpClient();
            using var message = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/organization?$select=id,displayName,verifiedDomains");
            message.Headers.Authorization = new("Bearer", result.AccessToken);
            using var response = await http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft sign-in succeeded, but organization lookup failed (HTTP {(int)response.StatusCode}). Check Organization.Read.All delegated permission, admin consent and tenant policy.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var org = json.RootElement.GetProperty("value").EnumerateArray().Single(o => o.GetProperty("id").GetString() == tenantId);
            // Do not block token renewal in other tenants while the operator completes MFA.
            await Gate.WaitAsync(ct);
            ownsCacheGate = true;
            if (!await instanceAccess.HasPermissionAsync(InstancePermission.ManageTenantRegistry, ct))
                throw new UnauthorizedAccessException("Your tenant registry permission has been revoked.");
            var tenant = existing ?? await db.Tenants.SingleOrDefaultAsync(t => t.TenantId.ToLower() == tenantId, ct);
            // Recheck after the browser round trip: Microsoft sign-in never bypasses workbench grants.
            if (tenant is not null && !await access.HasRoleAsync(tenant.Id, TenantRole.Owner, ct))
                throw new UnauthorizedAccessException("Only this tenant's workbench Owner can connect an admin account.");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (tenant is null)
            {
                tenant = new Tenant { TenantId = tenantId, DisplayName = org.GetProperty("displayName").GetString() ?? tenantId };
                db.Tenants.Add(tenant);
                db.TenantAccessGrants.Add(new TenantAccessGrant { TenantId = tenant.Id, UserId = access.CurrentUserId!.Value, Role = TenantRole.Owner });
                db.Secrets.Add(new SecretRecord { Name = Marker(tenantId), ProtectedValue = protector.Protect("direct") });
            }
            foreach (var domain in org.GetProperty("verifiedDomains").EnumerateArray())
            {
                if (domain.GetProperty("isDefault").GetBoolean()) tenant.DefaultDomain = domain.GetProperty("name").GetString();
                if (domain.GetProperty("isInitial").GetBoolean())
                {
                    tenant.ExchangeOrganization = domain.GetProperty("name").GetString();
                    tenant.ExchangeOrganizationVerifiedAt = DateTimeOffset.UtcNow;
                }
            }
            tenant.LastSeenAt = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent
            {
                EventType = previous is null ? AuditEventType.EntityCreated : AuditEventType.EntityModified,
                ActorUserId = access.CurrentUserId,
                TenantId = tenant.Id,
                EntityType = "MicrosoftTenantConnection",
                EntityId = tenant.Id.ToString(),
                Detail = "Microsoft Graph admin account connected; token cache stored encrypted"
            });
            await SaveAsync(tenantId, new(result.Account.HomeAccountId.Identifier, result.Account.Username,
                session.Cache ?? throw new InvalidOperationException("Microsoft did not return a persistent token cache.")), ct);
            await transaction.CommitAsync(ct);
            return tenant;
        }
        finally
        {
            if (ownsCacheGate) Gate.Release();
            SignInGate.Release();
        }
    }

    public async Task<Func<CancellationToken, Task<string>>> CreateTokenSourceAsync(string tenantId, string resource, CancellationToken ct = default)
    {
        var initial = await GetAccessTokenAsync(tenantId, resource, ct);
        var key = Key(Guid.TryParse(tenantId, out var id) ? id.ToString() : tenantId);
        await Gate.WaitAsync(ct);
        try
        {
            if (Available && resource == Resources.Graph && await db.Secrets.AsNoTracking().AnyAsync(s => s.Name == key, ct))
                return token => GetAccessTokenAsync(tenantId, resource, token);
        }
        finally { Gate.Release(); }
        return _ => Task.FromResult(initial);
    }

    public async Task<string> GetAccessTokenAsync(string tenantId, string resource, CancellationToken ct = default)
    {
        // Partner Center always uses the partner SAM identity, even if the same directory is connected directly.
        if (resource != Resources.Graph) return await sam.GetAccessTokenAsync(tenantId, resource, ct);
        tenantId = Guid.TryParse(tenantId, out var id) ? id.ToString() : tenantId;
        await Gate.WaitAsync(ct);
        try
        {
            var key = Key(tenantId);
            var record = Available ? await db.Secrets.AsNoTracking().SingleOrDefaultAsync(s => s.Name == key, ct) : null;
            if (record is null)
            {
                if (await db.Secrets.AnyAsync(s => s.Name == Marker(tenantId), ct))
                    throw new InvalidOperationException("Connect your Microsoft admin account for this tenant in Tenants before running this operation.");
            }
            else
            {
                var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.TenantId.ToLower() == tenantId, ct);
                if (tenant is null || !await access.HasRoleAsync(tenant.Id, TenantRole.Viewer, ct))
                    throw new UnauthorizedAccessException("Your workbench access to this tenant has been revoked.");
                var state = Read(record);
                var session = await CreateAppAsync(tenantId, state, ct);
                var app = session.App;
                try
                {
                    var account = await app.GetAccountAsync(state.AccountId);
                    var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
                    if (!string.Equals(result.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Microsoft returned a token for a different tenant.");
                    if (state.Cache != session.Cache || state.ReconnectRequired || state.Claims is not null)
                        await SaveAsync(tenantId, state with { Cache = session.Cache!, ReconnectRequired = false, Claims = null }, ct);
                    return result.AccessToken;
                }
                catch (MsalUiRequiredException ex)
                {
                    if (!state.ReconnectRequired || state.Claims != ex.Claims)
                        await SaveAsync(tenantId, state with { ReconnectRequired = true, Claims = ex.Claims }, ct);
                    throw new InvalidOperationException($"Microsoft sign-in required for {state.Username}. Open Tenants and choose Reconnect.");
                }
            }
        }
        finally { Gate.Release(); }
        return await sam.GetAccessTokenAsync(tenantId, resource, ct);
    }
}
