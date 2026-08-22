using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

public record PendingActionStatusDto(Guid Id, PendingActionStatus Status, string? ExecutionError);

[McpServerToolType]
public class PendingActionTools
{
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;
    private readonly PendingActionService _pending;

    public PendingActionTools(
        BridgeDbContext db, ITenantAccessService access, PendingActionService pending)
    {
        _db = db;
        _access = access;
        _pending = pending;
    }

    [McpServerTool(ReadOnly = true, Destructive = false), Description(
        "Returns a staged pending action's current status and any execution error.")]
    public async Task<PendingActionStatusDto> CheckPendingAction(Guid id, CancellationToken ct)
    {
        var query = _db.PendingActions.AsNoTracking().Where(action => action.Id == id);
        var allowed = await _access.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        if (allowed is not null)
            query = query.Where(action => allowed.Contains(action.TenantId));
        if (!await query.AnyAsync(ct))
            throw new InvalidOperationException("Pending action not found.");

        var action = await _pending.GetAsync(id, ct)
            ?? throw new InvalidOperationException("Pending action not found.");
        return new PendingActionStatusDto(action.Id, action.Status, action.ExecutionError);
    }
}
