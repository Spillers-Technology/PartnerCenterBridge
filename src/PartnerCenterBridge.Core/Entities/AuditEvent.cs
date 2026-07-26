namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// Append-only record of a security-relevant event: auth (register/login/logout), tenant-access
/// sharing, or a mutation to a sensitive entity. Unlike <see cref="WorkflowRun"/> (which a
/// controller builds up explicitly because it captures rich diagnose/remediate detail), most
/// <see cref="AuditEvent"/> rows are appended automatically by
/// <c>AuditSaveChangesInterceptor</c> so logging a mutation is not something a controller author
/// has to remember to do. Never updated or deleted after insert.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public AuditEventType EventType { get; set; }

    /// <summary>Id of the acting <see cref="AppUser"/>, when the actor is a local account. Null for OIDC/dev-auth actors (see <see cref="ActorName"/>).</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Display name/claim of whoever performed the action, whatever the auth mode. "anonymous" if unauthenticated.</summary>
    public string ActorName { get; set; } = "anonymous";

    /// <summary>Tenant the event concerns, when applicable (tenant-access changes, tenant-scoped entity mutations).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>CLR type name of the entity that changed, for EntityCreated/Modified/Deleted events.</summary>
    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    /// <summary>Free-form JSON detail (e.g. changed property names for a modification, granted role for an access change).</summary>
    public string? Detail { get; set; }

    public bool Success { get; set; } = true;
}
