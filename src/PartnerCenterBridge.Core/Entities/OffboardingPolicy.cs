namespace PartnerCenterBridge.Core.Entities;

/// <summary>Which of the leaver's direct group memberships offboarding removes.</summary>
public enum GroupCleanupMode
{
    /// <summary>Leave every membership in place.</summary>
    None,
    /// <summary>Remove cloud Security and Microsoft 365 memberships; leave role-assignable groups for a human.</summary>
    RemoveAssignable,
    /// <summary>Remove every membership Graph can change, including role-assignable groups.</summary>
    RemoveAll
}

public enum ManagerAccessMode { None, FullAccess }

public enum DeviceWipeMode { None, Retire }

/// <summary>
/// Per-contract offboarding policy (owned JSON on <see cref="Contract"/>). The defaults reproduce
/// the pre-policy offboarding behavior exactly: block sign-in, revoke sessions, remove licenses,
/// remove from all groups; no mailbox conversion, forwarding, GAL hiding, delegation or device
/// action; no follow-up reminder.
/// </summary>
public class OffboardingPolicy
{
    public bool BlockSignIn { get; set; } = true;
    public bool RevokeSessions { get; set; } = true;
    public GroupCleanupMode GroupCleanup { get; set; } = GroupCleanupMode.RemoveAll;
    public bool ConvertMailboxToShared { get; set; }
    public bool RemoveLicenses { get; set; } = true;
    public bool HideFromGal { get; set; }
    /// <summary>Optional SMTP address to forward the (converted) mailbox to.</summary>
    public string? ForwardTo { get; set; }
    public ManagerAccessMode ManagerAccess { get; set; } = ManagerAccessMode.None;
    public DeviceWipeMode WipeDevices { get; set; } = DeviceWipeMode.None;
    /// <summary>Days after offboarding to revisit/delete the account. 0 = no follow-up. Recorded in evidence only.</summary>
    public int FollowUpDays { get; set; }

    public OffboardingPolicy Clone() => (OffboardingPolicy)MemberwiseClone();

    /// <summary>Validation errors (empty when valid). Shared by the contract API and the terminate endpoints.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (FollowUpDays is < 0 or > 3650) errors.Add("followUpDays must be between 0 and 3650.");
        if (!string.IsNullOrWhiteSpace(ForwardTo))
        {
            var ok = System.Net.Mail.MailAddress.TryCreate(ForwardTo.Trim(), out var addr)
                     && string.Equals(addr.Address, ForwardTo.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!ok) errors.Add("forwardTo must be a plain SMTP address (user@domain).");
        }
        if (!Enum.IsDefined(GroupCleanup)) errors.Add("groupCleanup must be None, RemoveAssignable or RemoveAll.");
        if (!Enum.IsDefined(ManagerAccess)) errors.Add("managerAccess must be None or FullAccess.");
        if (!Enum.IsDefined(WipeDevices)) errors.Add("wipeDevices must be None or Retire.");
        return errors;
    }
}
