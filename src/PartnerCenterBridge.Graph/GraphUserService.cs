using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Graph;

/// <summary>
/// Per-tenant identity operations over Graph beta. Multi-step runs (create/offboard) record each
/// step's outcome rather than aborting on the first non-fatal failure, so the operator sees
/// exactly what happened (e.g. user created but one group add failed).
/// </summary>
public class GraphUserService : IGraphUserService
{
    private readonly ITokenProvider _tokens;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GraphUserService> _log;
    private readonly string _baseUrl;

    public GraphUserService(
        ITokenProvider tokens,
        IHttpClientFactory httpFactory,
        IOptions<IntuneOptions> options,
        ILogger<GraphUserService> log)
    {
        _tokens = tokens;
        _httpFactory = httpFactory;
        _log = log;
        _baseUrl = options.Value.GraphBetaBaseUrl;
    }

    private async Task<GraphRestClient> ClientAsync(Tenant tenant, CancellationToken ct)
    {
        var token = await _tokens.GetAccessTokenAsync(tenant.TenantId, Resources.Graph, ct);
        return new GraphRestClient(_httpFactory.CreateClient("graph"), token, _baseUrl);
    }

    public async Task<ProvisioningResult> CreateUserAsync(Tenant tenant, NewHireRequest request, CancellationToken ct = default)
    {
        var result = new ProvisioningResult { UserPrincipalName = request.UserPrincipalName };
        var graph = await ClientAsync(tenant, ct);

        var password = string.IsNullOrEmpty(request.Password) ? GeneratePassword() : request.Password;
        if (string.IsNullOrEmpty(request.Password)) result.InitialPassword = password;

        // User creation is the one fatal step — everything after is best-effort and recorded.
        try
        {
            using var created = await graph.PostAsync("/users", new Dictionary<string, object?>
            {
                ["accountEnabled"] = true,
                ["displayName"] = request.DisplayName,
                ["givenName"] = request.GivenName,
                ["surname"] = request.Surname,
                ["mailNickname"] = request.MailNickname,
                ["userPrincipalName"] = request.UserPrincipalName,
                ["usageLocation"] = request.UsageLocation,
                ["jobTitle"] = request.JobTitle,
                ["department"] = request.Department,
                ["passwordProfile"] = new
                {
                    password,
                    forceChangePasswordNextSignIn = request.ForceChangePasswordNextSignIn
                }
            }, ct);
            result.UserId = created.RootElement.GetProperty("id").GetString();
            result.Steps.Add(new("Create user", true, result.UserId));
        }
        catch (Exception ex)
        {
            result.Steps.Add(new("Create user", false, ex.Message));
            return result; // cannot continue without a user
        }

        var userId = result.UserId!;

        if (request.LicenseSkuIds.Count > 0)
            await Step(result, "Assign licenses", async () =>
            {
                await graph.PostAsync($"/users/{userId}/assignLicense", new
                {
                    addLicenses = request.LicenseSkuIds.Select(s => new { skuId = s, disabledPlans = Array.Empty<string>() }).ToArray(),
                    removeLicenses = Array.Empty<string>()
                }, ct);
                return $"{request.LicenseSkuIds.Count} SKU(s)";
            });

        foreach (var groupId in request.GroupIds)
            await Step(result, $"Add to group {groupId}", async () =>
            {
                await graph.PostAsync($"/groups/{groupId}/members/$ref",
                    new Dictionary<string, object?> { ["@odata.id"] = $"{graph.BaseUrl}/directoryObjects/{userId}" }, ct);
                return "added";
            });

        if (!string.IsNullOrEmpty(request.ManagerId))
            await Step(result, "Set manager", async () =>
            {
                await graph.PutAsync($"/users/{userId}/manager/$ref",
                    new Dictionary<string, object?> { ["@odata.id"] = $"{graph.BaseUrl}/directoryObjects/{request.ManagerId}" }, ct);
                return request.ManagerId!;
            });

        return result;
    }

    public async Task<ProvisioningResult> TerminateUserAsync(Tenant tenant, TerminationRequest request, CancellationToken ct = default)
    {
        var result = new ProvisioningResult { UserId = request.UserId };
        var graph = await ClientAsync(tenant, ct);
        var userId = request.UserId;

        if (request.BlockSignIn)
            await Step(result, "Block sign-in", async () =>
            {
                await graph.PatchAsync($"/users/{userId}", new { accountEnabled = false }, ct);
                return "accountEnabled=false";
            });

        if (request.RevokeSessions)
            await Step(result, "Revoke sessions", async () =>
            {
                await graph.PostAsync($"/users/{userId}/revokeSignInSessions", new { }, ct);
                return "revoked";
            });

        if (request.RemoveLicenses)
            await Step(result, "Remove licenses", async () =>
            {
                using var doc = await graph.GetAsync($"/users/{userId}?$select=assignedLicenses", ct);
                var skus = doc.RootElement.TryGetProperty("assignedLicenses", out var al)
                    ? al.EnumerateArray().Select(l => l.GetProperty("skuId").GetString()!).ToArray()
                    : Array.Empty<string>();
                if (skus.Length == 0) return "none";
                await graph.PostAsync($"/users/{userId}/assignLicense",
                    new { addLicenses = Array.Empty<object>(), removeLicenses = skus }, ct);
                return $"{skus.Length} removed";
            });

        if (request.RemoveFromGroups)
            await Step(result, "Remove from groups", async () =>
            {
                using var doc = await graph.GetAsync($"/users/{userId}/memberOf?$select=id", ct);
                var groups = doc.RootElement.TryGetProperty("value", out var v)
                    ? v.EnumerateArray()
                        .Where(g => g.TryGetProperty("@odata.type", out var t) && t.GetString()!.EndsWith("group"))
                        .Select(g => g.GetProperty("id").GetString()!).ToArray()
                    : Array.Empty<string>();
                foreach (var gid in groups)
                {
                    try { await graph.DeleteAsync($"/groups/{gid}/members/{userId}/$ref", ct); }
                    catch (Exception ex) { _log.LogWarning(ex, "Failed removing {User} from group {Group}", userId, gid); }
                }
                return $"{groups.Length} group(s)";
            });

        return result;
    }

    public async Task<IReadOnlyList<SkuSummary>> ListSkusAsync(Tenant tenant, CancellationToken ct = default)
    {
        var graph = await ClientAsync(tenant, ct);
        using var doc = await graph.GetAsync("/subscribedSkus", ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(s => new SkuSummary(
            s.GetProperty("skuId").GetString()!,
            s.GetProperty("skuPartNumber").GetString()!,
            s.GetProperty("prepaidUnits").GetProperty("enabled").GetInt32(),
            s.GetProperty("consumedUnits").GetInt32())).ToList();
    }

    public async Task<IReadOnlyList<DirectoryObject>> ListGroupsAsync(Tenant tenant, CancellationToken ct = default)
    {
        var graph = await ClientAsync(tenant, ct);
        using var doc = await graph.GetAsync("/groups?$select=id,displayName&$top=200", ct);
        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(g => new DirectoryObject(g.GetProperty("id").GetString()!, g.GetProperty("displayName").GetString() ?? "")).ToList();
    }

    public async Task<IReadOnlyList<DirectoryObject>> ListUsersAsync(Tenant tenant, string? search = null, CancellationToken ct = default)
    {
        var graph = await ClientAsync(tenant, ct);
        var filter = string.IsNullOrWhiteSpace(search)
            ? ""
            : $"&$filter=startswith(displayName,'{Uri.EscapeDataString(search)}') or startswith(userPrincipalName,'{Uri.EscapeDataString(search)}')";
        using var doc = await graph.GetAsync($"/users?$select=id,displayName,userPrincipalName&$top=100{filter}", ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(u => new DirectoryObject(
            u.GetProperty("id").GetString()!,
            u.GetProperty("displayName").GetString() ?? "",
            u.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null)).ToList();
    }

    /// <summary>Run a best-effort step, recording success/failure without throwing.</summary>
    private static async Task Step(ProvisioningResult result, string name, Func<Task<string>> action)
    {
        try { result.Steps.Add(new(name, true, await action())); }
        catch (Exception ex) { result.Steps.Add(new(name, false, ex.Message)); }
    }

    /// <summary>Generate a 16-char password meeting Entra complexity (upper/lower/digit/symbol).</summary>
    internal static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz",
                     digits = "23456789", symbols = "!@#$%^&*-_";
        var all = upper + lower + digits + symbols;
        Span<char> buf = stackalloc char[16];
        buf[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        buf[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        buf[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        buf[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < buf.Length; i++) buf[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        // Fisher-Yates shuffle so the required-class chars aren't always in the first positions.
        for (var i = buf.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (buf[i], buf[j]) = (buf[j], buf[i]);
        }
        return new string(buf);
    }
}
