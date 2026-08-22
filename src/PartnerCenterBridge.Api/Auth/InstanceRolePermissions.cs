using PartnerCenterBridge.Core;

namespace PartnerCenterBridge.Api.Auth;

public static class InstanceRolePermissions
{
    public const InstanceRole KnownRoles = InstanceRole.Administrator
        | InstanceRole.CatalogManager
        | InstanceRole.CredentialManager
        | InstanceRole.AutomationPolicyManager;

    public static bool IsValidAssignment(InstanceRole roles) =>
        (roles & ~KnownRoles) == 0
        && (!roles.HasFlag(InstanceRole.Administrator) || roles == InstanceRole.Administrator);

    public static bool Includes(InstanceRole roles, InstancePermission permission)
    {
        if (roles.HasFlag(InstanceRole.Administrator)) return true;
        return permission switch
        {
            InstancePermission.ManageCatalog => roles.HasFlag(InstanceRole.CatalogManager),
            InstancePermission.ManageSam => roles.HasFlag(InstanceRole.CredentialManager),
            InstancePermission.ManageMcpPolicy => roles.HasFlag(InstanceRole.AutomationPolicyManager),
            _ => false
        };
    }

    public static IReadOnlyList<InstanceRole> Expand(InstanceRole roles) =>
        Enum.GetValues<InstanceRole>()
            .Where(role => role != InstanceRole.None && roles.HasFlag(role))
            .ToList();

    public static IReadOnlyList<string> PermissionNames(InstanceRole roles) =>
        Enum.GetValues<InstancePermission>()
            .Where(permission => Includes(roles, permission))
            .Select(WireName)
            .ToList();

    public static string WireName(InstancePermission permission) => permission switch
    {
        InstancePermission.ManageRoles => "instance.roles.manage",
        InstancePermission.ManageCatalog => "instance.catalog.manage",
        InstancePermission.ManageSam => "instance.sam.manage",
        InstancePermission.ManageMcpPolicy => "instance.mcp-policy.manage",
        InstancePermission.ManageTenantRegistry => "instance.tenant-registry.manage",
        _ => throw new ArgumentOutOfRangeException(nameof(permission))
    };
}
