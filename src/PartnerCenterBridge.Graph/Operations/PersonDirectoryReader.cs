using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Graph.Operations;

/// <summary>
/// Read-only Graph lookups for the person workspace. Every section catches its own failures: a
/// 403 becomes <see cref="SectionStatus.Unavailable"/> naming the permission PCB lacks, anything
/// else becomes <see cref="SectionStatus.Error"/>. Nothing here writes.
/// </summary>
public class PersonDirectoryReader : IPersonDirectoryReader
{
    private readonly TenantGraphRest _graph;

    public PersonDirectoryReader(ITokenProvider tokens, IHttpClientFactory httpFactory, IOptions<IntuneOptions> options)
        => _graph = new TenantGraphRest(tokens, httpFactory, options);

    private static string U(string userId) => Uri.EscapeDataString(userId);

    public Task<PersonSection<PersonProfile>> GetProfileAsync(Tenant tenant, string userId, CancellationToken ct = default) =>
        SectionAsync(tenant, "User.Read.All", async graph =>
        {
            const string baseSelect = "id,displayName,userPrincipalName,mail,accountEnabled,jobTitle,department,onPremisesSyncEnabled,createdDateTime";
            JsonElement user;
            string? lastSignIn = null;
            try
            {
                // signInActivity needs AuditLog.Read.All (and Entra ID P1); fall back without it.
                using var doc = await graph.GetAsync($"/users/{U(userId)}?$select={baseSelect},signInActivity", ct);
                user = doc.RootElement.Clone();
                if (user.TryGetProperty("signInActivity", out var sia) && sia.ValueKind == JsonValueKind.Object)
                    lastSignIn = Str(sia, "lastSignInDateTime");
            }
            catch (GraphRequestException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                using var doc = await graph.GetAsync($"/users/{U(userId)}?$select={baseSelect}", ct);
                user = doc.RootElement.Clone();
            }
            return new PersonProfile(
                Str(user, "id") ?? userId,
                Str(user, "displayName") ?? "",
                Str(user, "userPrincipalName"),
                Str(user, "mail"),
                Bool(user, "accountEnabled"),
                Str(user, "jobTitle"),
                Str(user, "department"),
                Bool(user, "onPremisesSyncEnabled"),
                Str(user, "createdDateTime"),
                lastSignIn);
        }, ct);

    public Task<PersonSection<IReadOnlyList<PersonLicense>>> GetLicensesAsync(Tenant tenant, string userId, CancellationToken ct = default) =>
        SectionAsync<IReadOnlyList<PersonLicense>>(tenant, "User.Read.All", async graph =>
            (await graph.GetAllAsync($"/users/{U(userId)}/licenseDetails?$select=skuId,skuPartNumber", ct))
                .Select(l => new PersonLicense(Str(l, "skuPartNumber") ?? "", Str(l, "skuId") ?? ""))
                .OrderBy(l => l.SkuPartNumber, StringComparer.OrdinalIgnoreCase)
                .ToList(), ct);

    public Task<PersonSection<IReadOnlyList<PersonGroup>>> GetGroupsAsync(Tenant tenant, string userId, CancellationToken ct = default) =>
        SectionAsync<IReadOnlyList<PersonGroup>>(tenant, "GroupMember.Read.All", async graph =>
            (await GroupClassifier.DirectMembershipsAsync(graph, userId, ct))
                .Select(m => new PersonGroup(m.Id, m.DisplayName, m.Category))
                .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(), ct);

    public Task<PersonSection<IReadOnlyList<string>>> GetAuthMethodsAsync(Tenant tenant, string userId, CancellationToken ct = default) =>
        SectionAsync<IReadOnlyList<string>>(tenant, "UserAuthenticationMethod.Read.All", async graph =>
            (await graph.GetAllAsync($"/users/{U(userId)}/authentication/methods", ct))
                .Select(m => (Str(m, "@odata.type") ?? "unknown")
                    .Replace("#microsoft.graph.", "")
                    .Replace("AuthenticationMethod", ""))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList(), ct);

    public Task<PersonSection<IReadOnlyList<PersonDevice>>> GetDevicesAsync(Tenant tenant, string userId, CancellationToken ct = default) =>
        SectionAsync<IReadOnlyList<PersonDevice>>(tenant, "DeviceManagementManagedDevices.Read.All", async graph =>
            (await graph.GetAllAsync(
                $"/users/{U(userId)}/managedDevices?$select=id,deviceName,operatingSystem,osVersion,complianceState,lastSyncDateTime,managementAgent", ct))
                .Select(d => new PersonDevice(
                    Str(d, "id") ?? "", Str(d, "deviceName"), Str(d, "operatingSystem"), Str(d, "osVersion"),
                    Str(d, "complianceState"), Str(d, "lastSyncDateTime"), Str(d, "managementAgent")))
                .OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList(), ct);

    private async Task<PersonSection<T>> SectionAsync<T>(
        Tenant tenant, string permission, Func<GraphRestClient, Task<T>> fetch, CancellationToken ct)
    {
        try
        {
            var graph = await _graph.CreateAsync(tenant, ct);
            return PersonSection<T>.Ok(await fetch(graph));
        }
        catch (GraphRequestException ex) when (ex.Status == HttpStatusCode.Forbidden)
        {
            return PersonSection<T>.Unavailable(
                $"The PCB app registration (or its GDAP role assignment) lacks {permission} in this tenant (Graph 403).");
        }
        catch (GraphRequestException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return PersonSection<T>.Error($"Not found (Graph 404): {GraphErrors.Message(ex) ?? "the user or resource does not exist"}");
        }
        catch (GraphRequestException ex) when (ex.Status == HttpStatusCode.BadRequest && permission.StartsWith("DeviceManagement", StringComparison.Ordinal))
        {
            // Tenants without Intune answer device queries with 400 "not applicable to target tenant".
            return PersonSection<T>.Unavailable($"Intune device data is not available in this tenant: {GraphErrors.Message(ex)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PersonSection<T>.Error(GraphErrors.Describe(ex));
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
