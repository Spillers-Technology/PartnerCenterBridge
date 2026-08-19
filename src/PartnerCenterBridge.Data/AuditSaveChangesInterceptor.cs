using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Data;

/// <summary>
/// Appends an <see cref="AuditEvent"/> for every insert/update/delete of a security-relevant
/// entity, in the same <c>SaveChanges</c> batch as the change itself. This is the "can't forget
/// to log it" half of the audit story: a controller author doesn't opt in per endpoint, the
/// interceptor sees every mutation that goes through <see cref="BridgeDbContext"/> regardless of
/// which controller made it.
/// </summary>
public class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    // WorkflowRun is deliberately excluded: it's already its own richer audit record, built by
    // WorkflowsController with the full diagnose/remediate detail a generic AuditEvent can't
    // capture. SecretRecord is excluded too -- its value is encrypted ciphertext, but the fact
    // that a secret's name changed still isn't something worth a generic audit row.
    private static readonly HashSet<Type> AuditedTypes = new()
    {
        typeof(AppUser), typeof(TenantAccessGrant), typeof(Tenant),
        typeof(Contract), typeof(AppTemplate), typeof(Deployment), typeof(PasskeyCredential),
        typeof(PendingAction), typeof(McpToken)
    };

    private readonly ICurrentActor _actor;

    public AuditSaveChangesInterceptor(ICurrentActor actor) => _actor = actor;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Audit(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Audit(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    private void Audit(DbContext? context)
    {
        if (context is null) return;

        // Snapshot first: ChangeTracker.Entries() enumerates live tracker state, and we're about
        // to add new (AuditEvent) entries to that same tracker below.
        var entries = context.ChangeTracker.Entries()
            .Where(e => AuditedTypes.Contains(e.Entity.GetType())
                        && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entries)
            context.Add(BuildEvent(entry));
    }

    private AuditEvent BuildEvent(EntityEntry entry)
    {
        var eventType = entry.State switch
        {
            EntityState.Added => AuditEventType.EntityCreated,
            EntityState.Deleted => AuditEventType.EntityDeleted,
            _ => AuditEventType.EntityModified
        };

        var changedProps = entry.State == EntityState.Modified
            ? entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList()
            : new List<string>();

        Guid? tenantId = entry.Entity switch
        {
            Tenant t => t.Id,
            TenantAccessGrant g => g.TenantId,
            Deployment d => d.TenantId,
            _ => null
        };

        return new AuditEvent
        {
            EventType = eventType,
            ActorUserId = _actor.UserId,
            ActorName = _actor.Name,
            TenantId = tenantId,
            EntityType = entry.Entity.GetType().Name,
            EntityId = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "Id")?.CurrentValue?.ToString(),
            Detail = changedProps.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(changedProps) : null
        };
    }
}
