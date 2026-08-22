namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// Singleton row used to serialize first-user bootstrap and instance-role changes. Its revision is
/// deliberately operational rather than user-facing; <see cref="AppUser.AuthorizationVersion"/>
/// is the optimistic concurrency token exposed by the role editor.
/// </summary>
public class InstanceAuthorizationState
{
    public int Id { get; set; } = 1;
    public long Revision { get; set; } = 1;
}
