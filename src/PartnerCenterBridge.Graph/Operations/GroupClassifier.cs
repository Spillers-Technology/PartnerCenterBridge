using System.Net;
using System.Text.Json;

namespace PartnerCenterBridge.Graph.Operations;

/// <summary>Group/role categories shared by Access Parity, the person workspace and offboarding.</summary>
public static class MembershipCategory
{
    public const string Security = "Security";
    public const string Microsoft365 = "Microsoft365";
    public const string MailEnabledSecurity = "MailEnabledSecurity";
    public const string Distribution = "Distribution";
    public const string Dynamic = "Dynamic";
    public const string RoleAssignable = "RoleAssignable";
    public const string OnPremSynced = "OnPremSynced";
    public const string DirectoryRole = "DirectoryRole";
    public const string AlreadyMember = "AlreadyMember";
    public const string Other = "Other";
}

/// <summary>One direct <c>memberOf</c> entry, classified.</summary>
/// <param name="Modifiable">
/// True only for cloud Security and Microsoft 365 groups that are not dynamic, synced or
/// role-assignable: the memberships Graph can safely add/remove via <c>members/$ref</c>.
/// </param>
internal sealed record Membership(
    string Id, string DisplayName, string ODataType, string Category, bool IsGroup, bool Modifiable, string? Reason);

/// <summary>Reads and classifies a user's direct memberships.</summary>
internal static class GroupClassifier
{
    public const string ExchangeManagedReason = "Managed in Exchange Online; not modified by this operation";

    private const string Select =
        "id,displayName,groupTypes,securityEnabled,mailEnabled,membershipRule,isAssignableToRole,onPremisesSyncEnabled";

    /// <summary>Direct memberships (groups, directory roles, administrative units), following nextLink paging.</summary>
    public static async Task<List<Membership>> DirectMembershipsAsync(GraphRestClient graph, string userId, CancellationToken ct)
    {
        var items = await graph.GetAllAsync($"/users/{Uri.EscapeDataString(userId)}/memberOf?$select={Select}", ct);
        return items.Select(Classify).ToList();
    }

    public static Membership Classify(JsonElement e)
    {
        var id = Str(e, "id") ?? "";
        var name = Str(e, "displayName") ?? id;
        var type = Str(e, "@odata.type") ?? "";

        if (type.EndsWith("directoryRole", StringComparison.OrdinalIgnoreCase))
            return new(id, name, type, MembershipCategory.DirectoryRole, false, false,
                "Directory role; roles are never copied or changed by this operation. Assign roles deliberately.");
        // Graph omits @odata.type only on single-type collections; memberOf always carries it, but be lenient.
        var isGroup = type.Length == 0 || type.EndsWith(".group", StringComparison.OrdinalIgnoreCase);
        if (!isGroup)
            return new(id, name, type, MembershipCategory.Other, false, false,
                $"Not a group ({type.Replace("#microsoft.graph.", "")}); not compared.");

        var groupTypes = e.TryGetProperty("groupTypes", out var gt) && gt.ValueKind == JsonValueKind.Array
            ? gt.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
        var dynamic = groupTypes.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase)
                      || !string.IsNullOrWhiteSpace(Str(e, "membershipRule"));
        var unified = groupTypes.Contains("Unified", StringComparer.OrdinalIgnoreCase);
        var synced = Bool(e, "onPremisesSyncEnabled");
        var roleAssignable = Bool(e, "isAssignableToRole");
        var security = Bool(e, "securityEnabled");
        var mail = Bool(e, "mailEnabled");

        if (dynamic)
            return new(id, name, type, MembershipCategory.Dynamic, true, false,
                "Dynamic group; membership is rule-managed and cannot be changed directly.");
        if (synced)
            return new(id, name, type, MembershipCategory.OnPremSynced, true, false,
                "Synced from on-premises AD; membership must be changed there.");
        if (roleAssignable)
            return new(id, name, type, MembershipCategory.RoleAssignable, true, false,
                "Role-assignable group; membership grants directory role privileges and is not changed automatically.");
        if (unified)
            return new(id, name, type, MembershipCategory.Microsoft365, true, true, null);
        if (mail && security)
            return new(id, name, type, MembershipCategory.MailEnabledSecurity, true, false, ExchangeManagedReason);
        if (mail)
            return new(id, name, type, MembershipCategory.Distribution, true, false, ExchangeManagedReason);
        if (security)
            return new(id, name, type, MembershipCategory.Security, true, true, null);
        return new(id, name, type, MembershipCategory.Other, true, false,
            "Group is neither security- nor mail-enabled; not changed by this operation.");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Turns Graph failures into short, operator-readable reasons.</summary>
internal static class GraphErrors
{
    /// <summary>"Graph 403: Insufficient privileges to complete the operation." (message from the error body when present).</summary>
    public static string Describe(Exception ex)
    {
        if (ex is not GraphRequestException g) return ex.Message;
        return $"Graph {(int)g.Status}: {Message(g) ?? g.Status.ToString()}";
    }

    public static string? Message(GraphRequestException g)
    {
        try
        {
            using var doc = JsonDocument.Parse(g.Body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m))
                return m.GetString();
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(g.Body) ? null : (g.Body.Length > 200 ? g.Body[..200] : g.Body);
    }

    public static bool IsForbidden(Exception ex) => ex is GraphRequestException { Status: HttpStatusCode.Forbidden };
    public static bool IsNotFound(Exception ex) => ex is GraphRequestException { Status: HttpStatusCode.NotFound };

    /// <summary>Graph's 400 for adding a member that is already there.</summary>
    public static bool IsAlreadyExists(Exception ex) =>
        ex is GraphRequestException { Status: HttpStatusCode.BadRequest } g
        && g.Body.Contains("added object references already exist", StringComparison.OrdinalIgnoreCase);
}
