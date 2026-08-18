# MCP Server Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up a working, end-to-end slice of MCP server support — transport, the
`McpApprovalMode`/`PendingAction` HITL gate, a PAT auth path, and enough tools (2 read-only + 1
mutating) to prove the whole approval loop against a real MCP client.

**Architecture:** `ModelContextProtocol.AspNetCore` mounted at `/mcp` inside the existing
`PartnerCenterBridge.Api` Kestrel pipeline, reusing the existing JWT bearer auth and
`ITenantAccessService` checks unchanged. Mutating tools stage a `PendingAction` instead of
executing when a tenant is in the (default) `Queue` approval mode; a new SPA "Approvals" tab
approves them, which invokes the same service call a direct controller action would.

**Tech Stack:** .NET 8, EF Core (Postgres in prod, Sqlite in tests), `ModelContextProtocol.AspNetCore`,
React/Vite/TS.

**Spec:** `docs/superpowers/specs/2026-08-18-mcp-server-design.md`

## Global Constraints

- Keep every C# string literal ASCII-only — non-ASCII characters in compiled string literals have
  previously shipped as mojibake on this project's toolchain (comments are unaffected).
- Follow this repo's existing patterns exactly rather than introducing new ones: controllers use
  `[ApiController]`/`[Authorize]`/constructor injection with inline DTO records; services are
  plain classes registered `AddScoped`; entities are plain POCOs configured in
  `BridgeDbContext.OnModelCreating`; migrations are generated via
  `dotnet ef migrations add <Name> --project src/PartnerCenterBridge.Data --startup-project src/PartnerCenterBridge.Api`,
  never hand-written.
- `IsSystemAdmin` must never gain the ability to act on a tenant's data or bypass a
  `TenantAccessGrant` check — every new admin-gated endpoint in this plan is a platform
  safety-policy control, not tenant operational access (see spec's RBAC Extension section).
- No AI attribution in any commit.
- This plan covers the vertical-slice foundation only: full tool parity across the remaining
  controllers, Local-mode OAuth (OpenIddict), and the `docs/mcp.html` documentation page are
  separate follow-up plans per the spec's suggested phasing — do not expand scope here.

---

## File Structure

**Core** (`src/PartnerCenterBridge.Core/`)
- `Enums.cs` (modify) — add `McpApprovalMode`, `PendingActionStatus`
- `Entities/PendingAction.cs` (create)
- `Entities/McpToken.cs` (create)
- `Entities/Tenant.cs` (modify) — add `McpApprovalMode` property
- `Entities/AppUser.cs` (modify) — add `McpTokens` collection

**Data** (`src/PartnerCenterBridge.Data/`)
- `BridgeDbContext.cs` (modify) — new `DbSet`s + `OnModelCreating` entries
- `AuditSaveChangesInterceptor.cs` (modify) — add `PendingAction`, `McpToken` to `AuditedTypes`
- a generated EF migration (via CLI, not hand-written)

**Api** (`src/PartnerCenterBridge.Api/`)
- `Auth/ITenantAccessService.cs` (modify) — fix stale XML doc
- `Auth/LocalTokenService.cs` (modify) — add `IssueMcpToken`
- `Controllers/AdminController.cs` (modify) — add `PATCH api/admin/tenants/{id}/mcp-mode`
- `Controllers/McpTokensController.cs` (create)
- `Controllers/PendingActionsController.cs` (create)
- `Services/PendingActionService.cs` (create)
- `Services/IPendingActionExecutor.cs` (create)
- `Mcp/DiagnosticsTools.cs` (create) — the auth-context spike tool
- `Mcp/DashboardTools.cs` (create)
- `Mcp/TenantTools.cs` (create)
- `Mcp/WorkflowTools.cs` (create)
- `Mcp/WorkflowRemediateExecutor.cs` (create)
- `Program.cs` (modify) — MCP wiring, new DI registrations, JWT revocation check
- `PartnerCenterBridge.Api.csproj` (modify) — `ModelContextProtocol.AspNetCore` package

**Web** (`web/src/`)
- `types.ts` (modify)
- `api.ts` (modify)
- `components/Approvals.tsx` (create)
- `components/Security.tsx` (modify) — MCP token section
- `App.tsx` (modify) — tab wiring

**Tests** (`tests/PartnerCenterBridge.Tests/`)
- `TestDb.cs` (create) — shared in-memory Sqlite `BridgeDbContext` helper
- `PendingActionServiceTests.cs` (create)
- `McpTokenTests.cs` (create)
- `PendingActionsControllerTests.cs` (create)
- `AdminControllerMcpModeTests.cs` (create)
- `WorkflowRemediateExecutorTests.cs` (create)

---

### Task 1: MCP transport spike — verify auth context reaches a tool call

This de-risks the whole plan first: every later task assumes `ITenantAccessService` works
correctly inside an MCP tool call, the same as it does inside a controller action. There's a known
GitHub issue against this SDK where `HttpContext` was empty under the Streamable HTTP transport in
some versions — confirm it live against the version we actually install before building anything
on top of the assumption.

**Files:**
- Modify: `src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj`
- Modify: `src/PartnerCenterBridge.Api/Program.cs`
- Create: `src/PartnerCenterBridge.Api/Mcp/DiagnosticsTools.cs`

**Interfaces:**
- Produces: `[McpServerToolType] DiagnosticsTools.WhoAmI()` — every later `Mcp/*Tools.cs` class
  follows this exact shape (constructor-injects `ITenantAccessService` and whichever service it
  needs, one `[McpServerTool]` method per tool).

- [ ] **Step 1: Add the MCP SDK package**

Run: `dotnet add src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj package ModelContextProtocol.AspNetCore`

- [ ] **Step 2: Wire the MCP server into `Program.cs`**

Add near the other `builder.Services.Add...` calls (after the `ITenantAccessService` registration,
around line 92):

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();
```

Add near `app.MapControllers()` (around line 201), **after** `app.UseAuthorization()`:

```csharp
app.MapMcp("/mcp").RequireAuthorization();
```

- [ ] **Step 3: Add the spike tool**

```csharp
// src/PartnerCenterBridge.Api/Mcp/DiagnosticsTools.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;

namespace PartnerCenterBridge.Api.Mcp;

/// <summary>
/// Not a real operator tool -- exists to make the MCP transport's auth wiring independently
/// verifiable (does the caller's identity actually reach a tool call?) rather than only provable
/// as a side effect of exercising some other tool.
/// </summary>
[McpServerToolType]
public class DiagnosticsTools
{
    private readonly ITenantAccessService _access;

    public DiagnosticsTools(ITenantAccessService access) => _access = access;

    [McpServerTool, Description("Returns the identity this MCP server resolved for the caller of this tool call.")]
    public string WhoAmI() =>
        _access.CurrentUserId is { } id
            ? $"userId={id}, isSystemAdmin={_access.IsSystemAdmin}"
            : "no local user id resolved (OIDC/dev-auth caller, or auth context did not reach this tool call)";
}
```

- [ ] **Step 4: Build**

Run: `dotnet build PartnerCenterBridge.sln`
Expected: builds clean.

- [ ] **Step 5: Live-verify against a real MCP client**

This step cannot be automated in CI — it's exactly the "budget for live verification separately"
discipline `docs/dev-process.md` calls out. Run the API locally against `Auth:Mode=Local`
(docker-compose or `dotnet run`), register/login a local user through the SPA to get a bearer
token, then either:

- point the official MCP inspector at `http://localhost:5080/mcp` with that bearer token and call
  `who_am_i`, or
- `claude mcp add --transport http pcb-local http://localhost:5080/mcp --header "Authorization: Bearer <token>"`
  from a Claude Code session and call the tool there.

Expected: the response contains the real `userId` of the account that logged in, not the
"no local user id resolved" fallback. If it returns the fallback, stop here — do not proceed to
Task 2 — and investigate whether `Stateless = true` or the transport itself is dropping
`HttpContext`; this blocks every later task's authorization story.

- [ ] **Step 6: Commit**

```bash
git add src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj src/PartnerCenterBridge.Api/Program.cs src/PartnerCenterBridge.Api/Mcp/DiagnosticsTools.cs
git commit -m "Add MCP transport and verify auth context reaches tool calls"
```

---

### Task 2: `Tenant.McpApprovalMode` + admin-gated endpoint

**Files:**
- Modify: `src/PartnerCenterBridge.Core/Enums.cs`
- Modify: `src/PartnerCenterBridge.Core/Entities/Tenant.cs`
- Modify: `src/PartnerCenterBridge.Api/Auth/ITenantAccessService.cs`
- Modify: `src/PartnerCenterBridge.Api/Controllers/AdminController.cs`
- Create: `tests/PartnerCenterBridge.Tests/AdminControllerMcpModeTests.cs`
- Create: `tests/PartnerCenterBridge.Tests/TestDb.cs`

**Interfaces:**
- Produces: `TenantRole` (existing) unchanged; new `McpApprovalMode { Queue = 0, ClientTrust = 1 }`
  enum; `Tenant.McpApprovalMode` property (defaults to `Queue`); `AdminController.SetMcpMode`
  endpoint at `PATCH api/admin/tenants/{id}/mcp-mode`.

- [ ] **Step 1: Shared test DB helper**

```csharp
// tests/PartnerCenterBridge.Tests/TestDb.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// An in-memory Sqlite-backed BridgeDbContext for tests that need real EF Core behavior
/// (querying, FindAsync, SaveChanges) without a Postgres instance. The connection must stay open
/// for the context's lifetime -- Sqlite's ":memory:" database is destroyed when its one
/// connection closes.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    public BridgeDbContext Context { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite(_connection)
            .Options;
        Context = new BridgeDbContext(options);
        Context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/PartnerCenterBridge.Tests/AdminControllerMcpModeTests.cs
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class AdminControllerMcpModeTests
{
    private static Tenant NewTenant() => new() { TenantId = "t1", DisplayName = "Contoso" };

    [Fact]
    public async Task SetMcpMode_defaults_to_Queue_and_system_admin_can_change_it()
    {
        using var db = new TestDb();
        var tenant = NewTenant();
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        Assert.Equal(McpApprovalMode.Queue, tenant.McpApprovalMode);

        var controller = new AdminController(new FakeSamTokenStore(), new FakeTenantAccessService(isSystemAdmin: true), db.Context);
        var result = await controller.SetMcpMode(tenant.Id, new AdminController.SetMcpModeRequest(McpApprovalMode.ClientTrust), CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        Assert.Equal(McpApprovalMode.ClientTrust, (await db.Context.Tenants.FindAsync(tenant.Id))!.McpApprovalMode);
    }

    [Fact]
    public async Task SetMcpMode_rejects_non_system_admin()
    {
        using var db = new TestDb();
        var tenant = NewTenant();
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();

        var controller = new AdminController(new FakeSamTokenStore(), new FakeTenantAccessService(isSystemAdmin: false), db.Context);
        var result = await controller.SetMcpMode(tenant.Id, new AdminController.SetMcpModeRequest(McpApprovalMode.ClientTrust), CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>(result);
    }
}
```

This test needs two small fakes that don't exist yet — add them to the bottom of the same file:

```csharp
public class FakeSamTokenStore : PartnerCenterBridge.Core.Abstractions.ISamTokenStore
{
    public Task<string?> GetRefreshTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;
}

public class FakeTenantAccessService : PartnerCenterBridge.Api.Auth.ITenantAccessService
{
    private readonly bool _isSystemAdmin;
    public FakeTenantAccessService(bool isSystemAdmin) => _isSystemAdmin = isSystemAdmin;
    public bool IsSystemAdmin => _isSystemAdmin;
    public Guid? CurrentUserId => Guid.NewGuid();
    public Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct) => Task.FromResult(true);
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter AdminControllerMcpModeTests`
Expected: FAIL — `McpApprovalMode` and `AdminController.SetMcpMode` don't exist yet.

- [ ] **Step 4: Add the enum**

In `src/PartnerCenterBridge.Core/Enums.cs`, add after the existing `TenantRole` enum:

```csharp
/// <summary>
/// How a tenant's mutating MCP tool calls are gated. <see cref="Queue"/> is the default for every
/// tenant and the safe choice: a mutation stages an <see cref="Entities.PendingAction"/> a human
/// must approve in the SPA instead of executing immediately. <see cref="ClientTrust"/> executes
/// immediately, relying on the MCP client's own confirmation UX -- only a PCB system admin can
/// switch a tenant into this mode (see <see cref="Entities.Tenant.McpApprovalMode"/>).
/// </summary>
public enum McpApprovalMode
{
    Queue = 0,
    ClientTrust = 1
}

/// <summary>Lifecycle of an <see cref="Entities.PendingAction"/>.</summary>
public enum PendingActionStatus
{
    Pending,
    Approved,
    Rejected,
    Executed,
    Expired
}
```

- [ ] **Step 5: Add the property to `Tenant`**

In `src/PartnerCenterBridge.Core/Entities/Tenant.cs`, add after `Status`:

```csharp
    /// <summary>
    /// Gates mutating MCP tool calls against this tenant. Defaults to the safe Queue mode;
    /// settable only by a system admin (see AdminController.SetMcpMode) -- never by the tenant's
    /// own Owner, deliberately, since this is a platform safety policy, not tenant power.
    /// </summary>
    public McpApprovalMode McpApprovalMode { get; set; } = McpApprovalMode.Queue;
```

- [ ] **Step 6: Fix the stale doc on `ITenantAccessService`**

In `src/PartnerCenterBridge.Api/Auth/ITenantAccessService.cs`, replace the `HasRoleAsync` doc
comment (currently line 13):

```csharp
    /// <summary>True if the current caller holds a non-expired grant at or above <paramref name="minimum"/> on <paramref name="tenantId"/>. System admin never bypasses this -- see TenantAccessService's remarks.</summary>
    Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct);
```

- [ ] **Step 7: Add the admin endpoint**

In `src/PartnerCenterBridge.Api/Controllers/AdminController.cs`, add the `BridgeDbContext`
dependency and the new action:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly ISamTokenStore _store;
    private readonly ITenantAccessService _access;
    private readonly BridgeDbContext _db;

    public AdminController(ISamTokenStore store, ITenantAccessService access, BridgeDbContext db)
    {
        _store = store;
        _access = access;
        _db = db;
    }

    [HttpGet("sam/status")]
    public async Task<object> Status(CancellationToken ct) =>
        new { bootstrapped = await _store.GetRefreshTokenAsync(ct) is not null };

    [HttpPost("sam/seed")]
    public async Task<IActionResult> Seed([FromBody] SeedRequest req, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.RefreshToken)) return BadRequest("refreshToken is required.");
        await _store.SaveRefreshTokenAsync(req.RefreshToken, ct);
        return NoContent();
    }

    /// <summary>
    /// Switches a tenant between the default Queue approval mode and ClientTrust. Deliberately
    /// IsSystemAdmin-only and nothing else -- this changes how OTHER users' already-authorized
    /// operator actions get gated, not the admin's own access to the tenant's data, so it stays
    /// inside the boundary ITenantAccessService's remarks draw rather than crossing it.
    /// </summary>
    [HttpPatch("tenants/{id:guid}/mcp-mode")]
    public async Task<IActionResult> SetMcpMode(Guid id, [FromBody] SetMcpModeRequest req, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant is null) return NotFound();
        tenant.McpApprovalMode = req.Mode;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record SeedRequest(string RefreshToken);
    public record SetMcpModeRequest(McpApprovalMode Mode);
}
```

The class-level route moved from `[Route("api/admin/sam")]` to `[Route("api/admin")]` (it no
longer fits once this controller owns a non-SAM endpoint too), with `sam/` moved onto the two
existing action attributes instead. The resulting URLs are unchanged (`api/admin/sam/status`,
`api/admin/sam/seed`) — only the route composition moved from class-level to method-level, so no
caller (SPA or otherwise) needs updating.

- [ ] **Step 8: Run to verify it passes**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter AdminControllerMcpModeTests`
Expected: PASS (2 tests).

- [ ] **Step 9: Full build + suite**

Run: `dotnet build PartnerCenterBridge.sln && dotnet test PartnerCenterBridge.sln`
Expected: builds clean, all tests pass (existing suite unaffected).

- [ ] **Step 10: Commit**

```bash
git add src/PartnerCenterBridge.Core/Enums.cs src/PartnerCenterBridge.Core/Entities/Tenant.cs \
        src/PartnerCenterBridge.Api/Auth/ITenantAccessService.cs \
        src/PartnerCenterBridge.Api/Controllers/AdminController.cs \
        tests/PartnerCenterBridge.Tests/AdminControllerMcpModeTests.cs \
        tests/PartnerCenterBridge.Tests/TestDb.cs
git commit -m "Add Tenant.McpApprovalMode with a system-admin-only gate"
```

---

### Task 3: `PendingAction` entity + migration

**Files:**
- Create: `src/PartnerCenterBridge.Core/Entities/PendingAction.cs`
- Modify: `src/PartnerCenterBridge.Data/BridgeDbContext.cs`
- Modify: `src/PartnerCenterBridge.Data/AuditSaveChangesInterceptor.cs`

**Interfaces:**
- Produces: `PendingAction` entity — `Id, TenantId, ActionType (string), RequestedByUserId,
  PayloadJson (string), PreviewSummary (string), Status (PendingActionStatus), CreatedAt,
  ExpiresAt, DecidedByUserId (Guid?), DecidedAt (DateTimeOffset?), ExecutedAt (DateTimeOffset?),
  ExecutionError (string?)`. `BridgeDbContext.PendingActions` DbSet. Consumed by Task 4's
  `PendingActionService`.

This task has no independent tests of its own — it's schema, proven by Task 4's tests exercising
it through `PendingActionService`. Migration generation is itself the verification step.

- [ ] **Step 1: Add the entity**

```csharp
// src/PartnerCenterBridge.Core/Entities/PendingAction.cs
namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A mutating MCP tool call staged instead of executed, because its tenant is in the default
/// McpApprovalMode.Queue mode. Approving it (PendingActionsController.Approve) runs the same
/// service call a direct REST controller action would -- this record only carries the request
/// through to that point, it never carries its own copy of the orchestration logic.
/// </summary>
public class PendingAction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Matches an IPendingActionExecutor.ActionType, e.g. "workflow.remediate".</summary>
    public required string ActionType { get; set; }

    public Guid RequestedByUserId { get; set; }

    /// <summary>The staged tool call's arguments, serialized so the matching IPendingActionExecutor can deserialize and act on approval.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>Human-readable summary an operator reads before approving -- built from the same read/diagnose path the mutation itself would use.</summary>
    public required string PreviewSummary { get; set; }

    public PendingActionStatus Status { get; set; } = PendingActionStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Stale proposals can't be approved long after the LLM that requested them lost context on why.</summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);

    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? ExecutionError { get; set; }
}
```

- [ ] **Step 2: Register the DbSet and model config**

In `src/PartnerCenterBridge.Data/BridgeDbContext.cs`, add to the `DbSet` list (after
`ConfigSnapshotSections`):

```csharp
    public DbSet<PendingAction> PendingActions => Set<PendingAction>();
```

In `OnModelCreating`, add after the `ConfigSnapshotSection` block:

```csharp
        b.Entity<PendingAction>(e =>
        {
            e.HasIndex(p => new { p.TenantId, p.Status });
            e.HasOne(p => p.Tenant).WithMany()
                .HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 3: Add `PendingAction` to the audit interceptor**

In `src/PartnerCenterBridge.Data/AuditSaveChangesInterceptor.cs`, add `typeof(PendingAction)` to
the `AuditedTypes` set (line 26). This makes every stage/approve/reject/expire transition an
`AuditEvent` automatically (EntityCreated on stage, EntityModified on every status change) — no
manual `AuditEvent` construction needed anywhere else in this plan.

- [ ] **Step 4: Generate the migration**

Run:
```bash
dotnet ef migrations add AddPendingActionAndMcpApprovalMode \
  --project src/PartnerCenterBridge.Data --startup-project src/PartnerCenterBridge.Api
```

Expected: a new file under `src/PartnerCenterBridge.Data/Migrations/` adding the `PendingAction`
table and `Tenant.McpApprovalMode` column (this migration also picks up Task 2's `Tenant` change
if Task 2 was committed first, which it was).

- [ ] **Step 5: Build**

Run: `dotnet build PartnerCenterBridge.sln`
Expected: builds clean.

- [ ] **Step 6: Commit**

```bash
git add src/PartnerCenterBridge.Core/Entities/PendingAction.cs src/PartnerCenterBridge.Data/BridgeDbContext.cs \
        src/PartnerCenterBridge.Data/AuditSaveChangesInterceptor.cs src/PartnerCenterBridge.Data/Migrations/
git commit -m "Add PendingAction entity and migration"
```

---

### Task 4: `PendingActionService` — the stage/approve/reject/expire state machine

**Files:**
- Create: `src/PartnerCenterBridge.Api/Services/PendingActionService.cs`
- Create: `src/PartnerCenterBridge.Api/Services/IPendingActionExecutor.cs`
- Create: `tests/PartnerCenterBridge.Tests/PendingActionServiceTests.cs`
- Modify: `src/PartnerCenterBridge.Api/Program.cs`

**Interfaces:**
- Consumes: `TestDb` (Task 2).
- Produces: `PendingActionService.StageAsync(Guid tenantId, string actionType, Guid
  requestedByUserId, object payload, string previewSummary, CancellationToken) -> Task<PendingAction>`;
  `.ApproveAsync(Guid id, Guid decidedByUserId, Func<PendingAction, Task> execute,
  CancellationToken) -> Task<PendingAction>`; `.RejectAsync(Guid id, Guid decidedByUserId,
  CancellationToken) -> Task<PendingAction>`; `.GetAsync(Guid id, CancellationToken) ->
  Task<PendingAction?>` (lazily expires a stale Pending row on read). `IPendingActionExecutor {
  string ActionType; Task ExecuteAsync(PendingAction, CancellationToken); }` — consumed by Task 6's
  controller and implemented first in Task 8.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/PartnerCenterBridge.Tests/PendingActionServiceTests.cs
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class PendingActionServiceTests
{
    [Fact]
    public async Task Stage_creates_a_pending_row()
    {
        using var db = new TestDb();
        var svc = new PendingActionService(db.Context);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var action = await svc.StageAsync(tenantId, "test.action", userId, new { note = "x" }, "does a thing", CancellationToken.None);

        Assert.Equal(PendingActionStatus.Pending, action.Status);
        Assert.Equal(tenantId, action.TenantId);
        Assert.Contains("\"note\"", action.PayloadJson);
    }

    [Fact]
    public async Task Approve_runs_the_executor_and_marks_Executed()
    {
        using var db = new TestDb();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(Guid.NewGuid(), "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var executed = false;

        var result = await svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => { executed = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(executed);
        Assert.Equal(PendingActionStatus.Executed, result.Status);
        Assert.NotNull(result.ExecutedAt);
    }

    [Fact]
    public async Task Approve_records_the_error_and_rethrows_when_execution_fails()
    {
        using var db = new TestDb();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(Guid.NewGuid(), "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        var reloaded = await db.Context.PendingActions.FindAsync(action.Id);
        Assert.Equal("boom", reloaded!.ExecutionError);
    }

    [Fact]
    public async Task Reject_marks_Rejected_and_never_executes()
    {
        using var db = new TestDb();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(Guid.NewGuid(), "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        var result = await svc.RejectAsync(action.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(PendingActionStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Approve_a_second_time_throws_instead_of_re_executing()
    {
        using var db = new TestDb();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(Guid.NewGuid(), "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        await svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => Task.CompletedTask, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_lazily_expires_a_stale_pending_row()
    {
        using var db = new TestDb();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(Guid.NewGuid(), "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        action.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db.Context.SaveChangesAsync();

        var reloaded = await svc.GetAsync(action.Id, CancellationToken.None);

        Assert.Equal(PendingActionStatus.Expired, reloaded!.Status);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter PendingActionServiceTests`
Expected: FAIL — `PendingActionService` doesn't exist yet.

- [ ] **Step 3: Implement `IPendingActionExecutor`**

```csharp
// src/PartnerCenterBridge.Api/Services/IPendingActionExecutor.cs
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Api.Services;

/// <summary>
/// Runs the real mutation a PendingAction was staged for, once a human approves it. One
/// implementation per ActionType, resolved by PendingActionsController from all registered
/// IPendingActionExecutor instances -- this is the seam later tool-parity work adds to, not
/// PendingActionService itself.
/// </summary>
public interface IPendingActionExecutor
{
    string ActionType { get; }
    Task ExecuteAsync(PendingAction action, CancellationToken ct);
}
```

- [ ] **Step 4: Implement `PendingActionService`**

```csharp
// src/PartnerCenterBridge.Api/Services/PendingActionService.cs
using System.Text.Json;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Services;

public class PendingActionService
{
    private readonly BridgeDbContext _db;

    public PendingActionService(BridgeDbContext db) => _db = db;

    public async Task<PendingAction> StageAsync(
        Guid tenantId, string actionType, Guid requestedByUserId, object payload, string previewSummary, CancellationToken ct)
    {
        var action = new PendingAction
        {
            TenantId = tenantId,
            ActionType = actionType,
            RequestedByUserId = requestedByUserId,
            PayloadJson = JsonSerializer.Serialize(payload),
            PreviewSummary = previewSummary
        };
        _db.PendingActions.Add(action);
        await _db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<PendingAction?> GetAsync(Guid id, CancellationToken ct) =>
        await ExpireIfStaleAsync(await _db.PendingActions.FindAsync([id], ct), ct);

    /// <summary>
    /// Marks Approved, runs <paramref name="execute"/>, then marks Executed -- or records
    /// <see cref="PendingAction.ExecutionError"/> and rethrows if it fails. The caller (Task 6's
    /// controller) supplies <paramref name="execute"/> so this service never itself knows how to
    /// run any specific ActionType.
    /// </summary>
    public async Task<PendingAction> ApproveAsync(Guid id, Guid decidedByUserId, Func<PendingAction, Task> execute, CancellationToken ct)
    {
        var action = await RequireActionableAsync(id, ct);
        action.Status = PendingActionStatus.Approved;
        action.DecidedByUserId = decidedByUserId;
        action.DecidedAt = DateTimeOffset.UtcNow;
        try
        {
            await execute(action);
            action.Status = PendingActionStatus.Executed;
            action.ExecutedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            action.ExecutionError = ex.Message;
            throw;
        }
        finally
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        return action;
    }

    public async Task<PendingAction> RejectAsync(Guid id, Guid decidedByUserId, CancellationToken ct)
    {
        var action = await RequireActionableAsync(id, ct);
        action.Status = PendingActionStatus.Rejected;
        action.DecidedByUserId = decidedByUserId;
        action.DecidedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return action;
    }

    private async Task<PendingAction> RequireActionableAsync(Guid id, CancellationToken ct)
    {
        var action = await ExpireIfStaleAsync(await _db.PendingActions.FindAsync([id], ct), ct)
            ?? throw new InvalidOperationException("Pending action not found.");
        if (action.Status != PendingActionStatus.Pending)
            throw new InvalidOperationException($"Pending action is {action.Status}, not Pending.");
        return action;
    }

    private async Task<PendingAction?> ExpireIfStaleAsync(PendingAction? action, CancellationToken ct)
    {
        if (action is { Status: PendingActionStatus.Pending } && action.ExpiresAt < DateTimeOffset.UtcNow)
        {
            action.Status = PendingActionStatus.Expired;
            await _db.SaveChangesAsync(ct);
        }
        return action;
    }
}
```

- [ ] **Step 5: Register in DI**

In `src/PartnerCenterBridge.Api/Program.cs`, add near the other `AddScoped` calls:

```csharp
builder.Services.AddScoped<PartnerCenterBridge.Api.Services.PendingActionService>();
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter PendingActionServiceTests`
Expected: PASS (6 tests).

- [ ] **Step 7: Commit**

```bash
git add src/PartnerCenterBridge.Api/Services/ src/PartnerCenterBridge.Api/Program.cs tests/PartnerCenterBridge.Tests/PendingActionServiceTests.cs
git commit -m "Add PendingActionService state machine"
```

---

### Task 5: `McpToken` — PAT issuance and revocation

**Files:**
- Create: `src/PartnerCenterBridge.Core/Entities/McpToken.cs`
- Modify: `src/PartnerCenterBridge.Core/Entities/AppUser.cs`
- Modify: `src/PartnerCenterBridge.Data/BridgeDbContext.cs`
- Modify: `src/PartnerCenterBridge.Data/AuditSaveChangesInterceptor.cs`
- Modify: `src/PartnerCenterBridge.Api/Auth/LocalTokenService.cs`
- Modify: `src/PartnerCenterBridge.Api/Program.cs`
- Create: `src/PartnerCenterBridge.Api/Controllers/McpTokensController.cs`
- Create: `tests/PartnerCenterBridge.Tests/McpTokenTests.cs`

**Interfaces:**
- Consumes: `TestDb` (Task 2), `AppUser`/`LocalTokenService` (existing).
- Produces: `McpToken` entity; `LocalTokenService.IssueMcpToken(AppUser, McpToken) -> string`;
  `McpTokensController` at `api/mcp-tokens` (`POST` create+issue, `GET` list, `DELETE {id}`
  revoke).

- [ ] **Step 1: Write the failing test (token minting + claims shape)**

```csharp
// tests/PartnerCenterBridge.Tests/McpTokenTests.cs
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class McpTokenTests
{
    private static LocalTokenService NewService() =>
        new(Options.Create(new LocalAuthOptions { SigningKey = "test-signing-key-at-least-32-bytes-long!!", AccessTokenLifetimeHours = 8 }));

    [Fact]
    public void IssueMcpToken_embeds_the_token_id_as_jti()
    {
        var user = new AppUser { Email = "a@b.com", DisplayName = "A B", PasswordHash = "x" };
        var token = new McpToken { UserId = user.Id, Name = "laptop", User = user };

        var jwt = NewService().IssueMcpToken(user, token);
        var parsed = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(jwt);

        Assert.Equal(token.Id.ToString(), parsed.Claims.First(c => c.Type == "jti").Value);
        Assert.Equal(user.Id.ToString(), parsed.Claims.First(c => c.Type == LocalTokenService.UserIdClaim).Value);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter McpTokenTests`
Expected: FAIL — `McpToken` and `IssueMcpToken` don't exist yet.

- [ ] **Step 3: Add the entity**

```csharp
// src/PartnerCenterBridge.Core/Entities/McpToken.cs
namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A long-lived personal access token for headless/scripted MCP clients that don't want an
/// interactive OAuth flow. Revocation is real (unlike a normal login JWT, which is stateless and
/// unrevocable by design) because losing an MCP client's token is a realistic incident to recover
/// from -- the JWT this mints carries this row's Id as its "jti" claim, checked on every request
/// (see Program.cs's OnTokenValidated).
/// </summary>
public class McpToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
```

Add to `AppUser` (`src/PartnerCenterBridge.Core/Entities/AppUser.cs`), after `PasskeyCredentials`:

```csharp
    public ICollection<McpToken> McpTokens { get; set; } = new List<McpToken>();
```

- [ ] **Step 4: Register in `BridgeDbContext`**

DbSet (after `PendingActions`):
```csharp
    public DbSet<McpToken> McpTokens => Set<McpToken>();
```

`OnModelCreating`, after the `PendingAction` block:
```csharp
        b.Entity<McpToken>(e =>
        {
            e.HasOne(t => t.User).WithMany(u => u.McpTokens)
                .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
```

Add `typeof(McpToken)` to `AuditSaveChangesInterceptor.AuditedTypes`.

- [ ] **Step 5: Add `IssueMcpToken` to `LocalTokenService`**

In `src/PartnerCenterBridge.Api/Auth/LocalTokenService.cs`, add (needs
`Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames` or
`System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames` — use the latter, already implicitly
available via the existing `System.IdentityModel.Tokens.Jwt` using):

```csharp
    /// <summary>
    /// Issues a revocable PAT for headless MCP clients. Same claims shape as IssueAccessToken so
    /// every existing [Authorize]/ITenantAccessService check treats it identically to a normal
    /// login token -- the only addition is "jti", checked against McpToken.RevokedAt on validation.
    /// </summary>
    public string IssueMcpToken(AppUser user, McpToken token)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(UserIdClaim, user.Id.ToString()),
            new Claim(SystemAdminClaim, user.IsSystemAdmin ? "true" : "false"),
            new Claim(JwtRegisteredClaimNames.Jti, token.Id.ToString())
        };

        var credentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: (token.ExpiresAt ?? DateTimeOffset.UtcNow.AddYears(1)).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter McpTokenTests`
Expected: PASS.

- [ ] **Step 7: Wire revocation checking into JWT validation**

Add `using System.IdentityModel.Tokens.Jwt;` to `Program.cs`'s using list (for
`JwtRegisteredClaimNames`) — `System.Security.Claims`, `Microsoft.AspNetCore.Authentication.JwtBearer`,
and `PartnerCenterBridge.Data` are already imported, nothing else new is needed.

In the `case AuthModeInfo.Local:` block, add `o.Events` to the existing `AddJwtBearer(o => { ...
})` call (after the `TokenValidationParameters` assignment):

```csharp
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var jti = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                        if (jti is null) return; // a normal login token, not an MCP PAT -- nothing to check.
                        var db = context.HttpContext.RequestServices.GetRequiredService<BridgeDbContext>();
                        var token = await db.McpTokens.FindAsync(Guid.Parse(jti));
                        if (token is null || token.RevokedAt is not null)
                        {
                            context.Fail("MCP token has been revoked.");
                            return;
                        }
                        token.LastUsedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync();
                    }
                };
```

- [ ] **Step 8: Add `McpTokensController`**

```csharp
// src/PartnerCenterBridge.Api/Controllers/McpTokensController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// Self-service PAT management for headless/scripted MCP clients (a browser-based OAuth flow is a
/// separate, later addition for Auth:Mode=Local -- see the MCP server design spec). Local-account
/// only, same as PasskeyController/TotpController.
/// </summary>
[ApiController]
[Route("api/mcp-tokens")]
[Authorize]
public class McpTokensController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly LocalTokenService _tokens;
    private readonly ITenantAccessService _access;

    public McpTokensController(BridgeDbContext db, LocalTokenService tokens, ITenantAccessService access)
    {
        _db = db;
        _tokens = tokens;
        _access = access;
    }

    public record McpTokenDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt);
    public record CreateMcpTokenRequest(string Name);
    public record CreatedMcpTokenDto(Guid Id, string Name, string Jwt);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<McpTokenDto>>> List(CancellationToken ct)
    {
        if (_access.CurrentUserId is not { } userId) return BadRequest("Not a local account.");
        return Ok(await _db.McpTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new McpTokenDto(t.Id, t.Name, t.CreatedAt, t.ExpiresAt, t.LastUsedAt))
            .ToListAsync(ct));
    }

    /// <summary>Returns the raw JWT once -- like TOTP recovery codes, it is never retrievable again after this response.</summary>
    [HttpPost]
    public async Task<ActionResult<CreatedMcpTokenDto>> Create(CreateMcpTokenRequest req, CancellationToken ct)
    {
        if (_access.CurrentUserId is not { } userId) return BadRequest("Not a local account.");
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");

        var user = await _db.AppUsers.FindAsync([userId], ct);
        if (user is null) return NotFound();

        var token = new McpToken { UserId = userId, Name = req.Name.Trim() };
        _db.McpTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        var jwt = _tokens.IssueMcpToken(user, token);
        return Ok(new CreatedMcpTokenDto(token.Id, token.Name, jwt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (_access.CurrentUserId is not { } userId) return BadRequest("Not a local account.");
        var token = await _db.McpTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        if (token is null) return NotFound();
        token.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
```

- [ ] **Step 9: Generate the migration**

Run:
```bash
dotnet ef migrations add AddMcpTokens \
  --project src/PartnerCenterBridge.Data --startup-project src/PartnerCenterBridge.Api
```

- [ ] **Step 10: Full build + suite**

Run: `dotnet build PartnerCenterBridge.sln && dotnet test PartnerCenterBridge.sln`
Expected: builds clean, all tests pass.

- [ ] **Step 11: Commit**

```bash
git add src/PartnerCenterBridge.Core/Entities/McpToken.cs src/PartnerCenterBridge.Core/Entities/AppUser.cs \
        src/PartnerCenterBridge.Data/ src/PartnerCenterBridge.Api/Auth/LocalTokenService.cs \
        src/PartnerCenterBridge.Api/Program.cs src/PartnerCenterBridge.Api/Controllers/McpTokensController.cs \
        tests/PartnerCenterBridge.Tests/McpTokenTests.cs
git commit -m "Add revocable MCP PAT issuance"
```

---

### Task 6: `PendingActionsController` — the SPA-facing approval endpoints

**Files:**
- Create: `src/PartnerCenterBridge.Api/Controllers/PendingActionsController.cs`
- Create: `tests/PartnerCenterBridge.Tests/PendingActionsControllerTests.cs`

**Interfaces:**
- Consumes: `PendingActionService` (Task 4), `IPendingActionExecutor` (Task 4, implemented by
  Task 8).
- Produces: `GET api/pending-actions` (Pending items the caller can act on), `POST
  api/pending-actions/{id}/approve`, `POST api/pending-actions/{id}/reject`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/PartnerCenterBridge.Tests/PendingActionsControllerTests.cs
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class PendingActionsControllerTests
{
    private class NoopExecutor : IPendingActionExecutor
    {
        public string ActionType => "test.action";
        public bool Ran { get; private set; }
        public Task ExecuteAsync(PendingAction action, CancellationToken ct) { Ran = true; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Approve_runs_the_matching_executor()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context);
        var tenantId = Guid.NewGuid();
        var staged = await pending.StageAsync(tenantId, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var executor = new NoopExecutor();

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), new[] { executor });
        var result = await controller.Approve(staged.Id, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        Assert.True(executor.Ran);
    }

    [Fact]
    public async Task Approve_without_a_registered_executor_returns_a_server_error_not_a_silent_success()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context);
        var staged = await pending.StageAsync(Guid.NewGuid(), "unknown.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), Array.Empty<IPendingActionExecutor>());
        var result = await controller.Approve(staged.Id, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(result);
    }

    [Fact]
    public async Task Reject_never_calls_the_executor()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context);
        var staged = await pending.StageAsync(Guid.NewGuid(), "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var executor = new NoopExecutor();

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), new[] { executor });
        await controller.Reject(staged.Id, CancellationToken.None);

        Assert.False(executor.Ran);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter PendingActionsControllerTests`
Expected: FAIL — `PendingActionsController` doesn't exist yet.

- [ ] **Step 3: Implement the controller**

```csharp
// src/PartnerCenterBridge.Api/Controllers/PendingActionsController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/pending-actions")]
[Authorize]
public class PendingActionsController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly PendingActionService _pending;
    private readonly ITenantAccessService _access;
    private readonly IReadOnlyList<IPendingActionExecutor> _executors;

    public PendingActionsController(
        BridgeDbContext db, PendingActionService pending, ITenantAccessService access,
        IEnumerable<IPendingActionExecutor> executors)
    {
        _db = db;
        _pending = pending;
        _access = access;
        _executors = executors.ToList();
    }

    public record PendingActionDto(
        Guid Id, Guid TenantId, string TenantName, string ActionType, string PreviewSummary,
        PendingActionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    /// <summary>Pending items across every tenant the caller holds Operator+ access to (unrestricted for a system admin, same convention as WorkflowsController.Runs).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PendingActionDto>>> List(CancellationToken ct)
    {
        var query = _db.PendingActions.AsNoTracking().Include(a => a.Tenant)
            .Where(a => a.Status == PendingActionStatus.Pending);

        if (!_access.IsSystemAdmin)
        {
            var allowed = await _db.TenantAccessGrants.AsNoTracking()
                .Where(g => g.UserId == _access.CurrentUserId && g.Role >= TenantRole.Operator
                         && (g.ExpiresAt == null || g.ExpiresAt > DateTimeOffset.UtcNow))
                .Select(g => g.TenantId).ToListAsync(ct);
            query = query.Where(a => allowed.Contains(a.TenantId));
        }

        return Ok(await query.OrderBy(a => a.CreatedAt)
            .Select(a => new PendingActionDto(a.Id, a.TenantId, a.Tenant!.DisplayName, a.ActionType, a.PreviewSummary, a.Status, a.CreatedAt, a.ExpiresAt))
            .ToListAsync(ct));
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var action = await _db.PendingActions.FindAsync([id], ct);
        if (action is null) return NotFound();
        if (!await _access.HasRoleAsync(action.TenantId, TenantRole.Operator, ct)) return Forbid();

        var executor = _executors.FirstOrDefault(e => e.ActionType == action.ActionType);
        if (executor is null) return StatusCode(500, $"No executor registered for '{action.ActionType}'.");

        try
        {
            await _pending.ApproveAsync(id, _access.CurrentUserId ?? Guid.Empty, a => executor.ExecuteAsync(a, ct), ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
        return NoContent();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var action = await _db.PendingActions.FindAsync([id], ct);
        if (action is null) return NotFound();
        if (!await _access.HasRoleAsync(action.TenantId, TenantRole.Operator, ct)) return Forbid();

        try { await _pending.RejectAsync(id, _access.CurrentUserId ?? Guid.Empty, ct); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        return NoContent();
    }
}
```

Note: `Approve`'s executor lookup happening *before* `PendingActionService.ApproveAsync` is
called (rather than inside the `execute` callback) is deliberate — a missing executor is a server
misconfiguration, not a legitimate "this approval failed" outcome, and should never leave the
`PendingAction` sitting in `Approved`-then-failed state for something that was never going to work.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter PendingActionsControllerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Full build + suite**

Run: `dotnet build PartnerCenterBridge.sln && dotnet test PartnerCenterBridge.sln`
Expected: builds clean, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/PartnerCenterBridge.Api/Controllers/PendingActionsController.cs tests/PartnerCenterBridge.Tests/PendingActionsControllerTests.cs
git commit -m "Add PendingActionsController approve/reject endpoints"
```

---

### Task 7: Read-only MCP tools — Dashboard, Tenants, Workflow diagnose

**Files:**
- Create: `src/PartnerCenterBridge.Api/Mcp/DashboardTools.cs`
- Create: `src/PartnerCenterBridge.Api/Mcp/TenantTools.cs`
- Create: `src/PartnerCenterBridge.Api/Mcp/WorkflowTools.cs`

**Interfaces:**
- Consumes: `ITenantAccessService`, `BridgeDbContext`, `WorkflowCatalog` (all existing).
- Produces: `WorkflowTools` class, extended by Task 8 with the mutating `Remediate` method.

No new automated tests in this task — these tools are thin, read-only mirrors of
`DashboardController`/`TenantsController.List`/`WorkflowsController.Diagnose`, whose underlying
logic is already covered. They're proven the same way Task 1's spike tool was: live, against a
real MCP client, in Task 11.

- [ ] **Step 1: `DashboardTools`**

```csharp
// src/PartnerCenterBridge.Api/Mcp/DashboardTools.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

[McpServerToolType]
public class DashboardTools
{
    private readonly DashboardController _dashboard;

    public DashboardTools(BridgeDbContext db) => _dashboard = new DashboardController(db);

    [McpServerTool, Description("Landing-page stats and the operator triage list: failed deployments, tenants missing GDAP delegation, and recent failed workflow runs.")]
    public async Task<DashboardDto> GetDashboard(CancellationToken ct) => await _dashboard.Get(ct);
}
```

- [ ] **Step 2: `TenantTools`**

```csharp
// src/PartnerCenterBridge.Api/Mcp/TenantTools.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

[McpServerToolType]
public class TenantTools
{
    private readonly TenantsController _tenants;

    public TenantTools(BridgeDbContext db, ITenantAccessService access) => _tenants = new TenantsController(db, access);

    [McpServerTool, Description("Lists customer tenants the caller has access to, with GDAP delegation status.")]
    public async Task<IReadOnlyList<TenantDto>> ListTenants(CancellationToken ct) => await _tenants.List(ct);
}
```

- [ ] **Step 3: `WorkflowTools` (diagnose only for now)**

```csharp
// src/PartnerCenterBridge.Api/Mcp/WorkflowTools.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Controllers; // WorkflowSummaryDto lives here (defined in WorkflowsController.cs)
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

[McpServerToolType]
public class WorkflowTools
{
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;

    public WorkflowTools(WorkflowCatalog catalog, BridgeDbContext db, ITenantAccessService access)
    {
        _catalog = catalog;
        _db = db;
        _access = access;
    }

    [McpServerTool, Description("Lists the known-fix workflow catalog (MFA reset, password reset, license repair, mailbox archive, etc.) with their required inputs.")]
    public IReadOnlyList<WorkflowSummaryDto> ListWorkflows() =>
        _catalog.All.OrderBy(w => w.Category).ThenBy(w => w.Name)
            .Select(w => new WorkflowSummaryDto(w.Id, w.Name, w.Description, w.Category, w.Inputs))
            .ToList();

    [McpServerTool, Description("Runs a workflow's read-only diagnosis against a tenant. Never mutates anything -- safe to call regardless of the tenant's MCP approval mode.")]
    public async Task<DiagnosisResult> DiagnoseWorkflow(string workflowId, Guid tenantId, Dictionary<string, string> inputs, CancellationToken ct)
    {
        var workflow = _catalog.Find(workflowId) ?? throw new InvalidOperationException($"Unknown workflow '{workflowId}'.");
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct))
            throw new UnauthorizedAccessException("Caller does not have access to this tenant.");
        var tenant = await _db.Tenants.FindAsync([tenantId], ct) ?? throw new InvalidOperationException("Tenant not found.");
        return await workflow.DiagnoseAsync(tenant, inputs, ct);
    }
}
```

Reusing `WorkflowSummaryDto`/`DashboardDto`/`TenantDto` by constructing the existing controllers
directly (rather than re-deriving their query logic in the tool class) keeps this DRY: both
`DashboardController(BridgeDbContext)` and `TenantsController(BridgeDbContext,
ITenantAccessService)` take only DI-resolvable dependencies, so a plain `new` call works fine
outside ASP.NET Core's own controller activation.

- [ ] **Step 4: Build**

Run: `dotnet build PartnerCenterBridge.sln`
Expected: builds clean.

- [ ] **Step 5: Commit**

```bash
git add src/PartnerCenterBridge.Api/Mcp/DashboardTools.cs src/PartnerCenterBridge.Api/Mcp/TenantTools.cs src/PartnerCenterBridge.Api/Mcp/WorkflowTools.cs
git commit -m "Add read-only MCP tools: dashboard, tenants, workflow diagnose"
```

---

### Task 8: The proof-of-concept mutating tool — `RemediateWorkflow`

**Files:**
- Create: `src/PartnerCenterBridge.Api/Mcp/WorkflowRemediateExecutor.cs`
- Modify: `src/PartnerCenterBridge.Api/Mcp/WorkflowTools.cs`
- Modify: `src/PartnerCenterBridge.Api/Program.cs`
- Create: `tests/PartnerCenterBridge.Tests/WorkflowRemediateExecutorTests.cs`

**Interfaces:**
- Consumes: `IPendingActionExecutor` (Task 4), `PendingActionService` (Task 4), `WorkflowCatalog`
  (existing).
- Produces: `WorkflowRemediateExecutor : IPendingActionExecutor` (`ActionType =
  "workflow.remediate"`); `WorkflowTools.RemediateWorkflow(...)` — the first tool that branches on
  `Tenant.McpApprovalMode`.

- [ ] **Step 1: Write the failing test (executor)**

```csharp
// tests/PartnerCenterBridge.Tests/WorkflowRemediateExecutorTests.cs
using PartnerCenterBridge.Api.Mcp;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Tests;

public class WorkflowRemediateExecutorTests
{
    // DiagnosisResult/WorkflowRunResult are plain classes with computed Healthy/Succeeded
    // properties (derived from Findings/Steps respectively), not positional-constructor records --
    // and IWorkflow's own methods take IReadOnlyDictionary<string, string>, not Dictionary, so an
    // implementer's signature must match that exactly to satisfy the interface.
    private class FakeWorkflow : IWorkflow
    {
        public string Id => "fake.workflow";
        public string Name => "Fake";
        public string Description => "test";
        public string Category => "Test";
        public IReadOnlyList<WorkflowInput> Inputs => Array.Empty<WorkflowInput>();
        public bool Ran;
        public Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default) =>
            Task.FromResult(new DiagnosisResult());
        public Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
        {
            Ran = true;
            return Task.FromResult(new WorkflowRunResult { Steps = new List<ProvisioningStep> { new("fake step", true) } });
        }
    }

    [Fact]
    public async Task ExecuteAsync_deserializes_the_payload_and_runs_the_matching_workflow()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t1", DisplayName = "Contoso" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();

        var workflow = new FakeWorkflow();
        var catalog = new WorkflowCatalog(new[] { workflow });
        var executor = new WorkflowRemediateExecutor(catalog, db.Context);

        var action = new PendingAction
        {
            TenantId = tenant.Id,
            ActionType = "workflow.remediate",
            RequestedByUserId = Guid.NewGuid(),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new WorkflowRemediatePayload("fake.workflow", new())),
            PreviewSummary = "runs fake workflow"
        };

        await executor.ExecuteAsync(action, CancellationToken.None);

        Assert.True(workflow.Ran);
    }
}
```

`WorkflowCatalog`'s constructor is `WorkflowCatalog(IEnumerable<IWorkflow> workflows)`, confirmed
against `src/PartnerCenterBridge.Core/Workflows/WorkflowCatalog.cs` — `new WorkflowCatalog(new[]
{ workflow })` above is exact, not a paraphrase.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter WorkflowRemediateExecutorTests`
Expected: FAIL — `WorkflowRemediateExecutor` doesn't exist yet.

- [ ] **Step 3: Implement the executor**

```csharp
// src/PartnerCenterBridge.Api/Mcp/WorkflowRemediateExecutor.cs
using System.Text.Json;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Mcp;

public record WorkflowRemediatePayload(string WorkflowId, Dictionary<string, string> Inputs);

/// <summary>Runs the same WorkflowCatalog.Find(...).RemediateAsync(...) call WorkflowsController.Remediate makes -- this is the "approval invokes the same service call a direct controller action would" half of the design.</summary>
public class WorkflowRemediateExecutor : IPendingActionExecutor
{
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;

    public WorkflowRemediateExecutor(WorkflowCatalog catalog, BridgeDbContext db)
    {
        _catalog = catalog;
        _db = db;
    }

    public string ActionType => "workflow.remediate";

    public async Task ExecuteAsync(PendingAction action, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<WorkflowRemediatePayload>(action.PayloadJson)
            ?? throw new InvalidOperationException("Malformed workflow.remediate payload.");
        var workflow = _catalog.Find(payload.WorkflowId)
            ?? throw new InvalidOperationException($"Unknown workflow '{payload.WorkflowId}'.");
        var tenant = await _db.Tenants.FindAsync([action.TenantId], ct)
            ?? throw new InvalidOperationException("Tenant not found.");
        await workflow.RemediateAsync(tenant, payload.Inputs, ct);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PartnerCenterBridge.Tests --filter WorkflowRemediateExecutorTests`
Expected: PASS.

- [ ] **Step 5: Add the mutating tool method to `WorkflowTools`**

Add `using PartnerCenterBridge.Api.Services;` to `src/PartnerCenterBridge.Api/Mcp/WorkflowTools.cs`'s
using list. Replace the class's field declarations and constructor (from Task 7's Step 3) with:

```csharp
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;
    private readonly PendingActionService _pending;

    public WorkflowTools(WorkflowCatalog catalog, BridgeDbContext db, ITenantAccessService access, PendingActionService pending)
    {
        _catalog = catalog;
        _db = db;
        _access = access;
        _pending = pending;
    }
```

Then add the new method alongside the existing `ListWorkflows`/`DiagnoseWorkflow` methods:

```csharp
    [McpServerTool, Description(
        "Runs a workflow's fix. If the tenant is in Queue approval mode (the default), this stages " +
        "the fix for a human to approve in the Approvals tab and returns its pending-action id " +
        "instead of running anything -- call check_pending_action with that id to see whether it " +
        "has been approved yet. If the tenant is in ClientTrust mode, this runs the fix immediately.")]
    public async Task<string> RemediateWorkflow(string workflowId, Guid tenantId, Dictionary<string, string> inputs, CancellationToken ct)
    {
        var workflow = _catalog.Find(workflowId) ?? throw new InvalidOperationException($"Unknown workflow '{workflowId}'.");
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Operator, ct))
            throw new UnauthorizedAccessException("Caller does not have Operator+ access to this tenant.");
        var tenant = await _db.Tenants.FindAsync([tenantId], ct) ?? throw new InvalidOperationException("Tenant not found.");

        if (tenant.McpApprovalMode == McpApprovalMode.ClientTrust)
        {
            var result = await workflow.RemediateAsync(tenant, inputs, ct);
            return $"Executed immediately (tenant is in ClientTrust mode). Succeeded={result.Succeeded}.";
        }

        var diagnosis = await workflow.DiagnoseAsync(tenant, inputs, ct);
        var preview = $"Remediate '{workflow.Name}' on {tenant.DisplayName}. Current diagnosis: " +
            string.Join("; ", diagnosis.Findings.Select(f => $"{f.Name}={f.Status}"));
        var payload = new WorkflowRemediatePayload(workflowId, inputs);
        var staged = await _pending.StageAsync(tenantId, "workflow.remediate", _access.CurrentUserId ?? Guid.Empty, payload, preview, ct);
        return $"Staged for approval (tenant is in Queue mode, the default). PendingActionId={staged.Id}. Preview: {preview}";
    }
```

`McpApprovalMode` (Task 2) lives in `namespace PartnerCenterBridge.Core;` directly, same as
`TenantRole` — already covered by `WorkflowTools.cs`'s existing `using PartnerCenterBridge.Core;`,
no new using needed for it.

- [ ] **Step 6: Register the executor in DI**

In `Program.cs`, add near `PendingActionService`'s registration:

```csharp
builder.Services.AddScoped<PartnerCenterBridge.Api.Services.IPendingActionExecutor, PartnerCenterBridge.Api.Mcp.WorkflowRemediateExecutor>();
```

- [ ] **Step 7: Full build + suite**

Run: `dotnet build PartnerCenterBridge.sln && dotnet test PartnerCenterBridge.sln`
Expected: builds clean, all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/PartnerCenterBridge.Api/Mcp/WorkflowRemediateExecutor.cs src/PartnerCenterBridge.Api/Mcp/WorkflowTools.cs \
        src/PartnerCenterBridge.Api/Program.cs tests/PartnerCenterBridge.Tests/WorkflowRemediateExecutorTests.cs
git commit -m "Add RemediateWorkflow MCP tool with Queue/ClientTrust branching"
```

---

### Task 9: Approvals SPA tab

**Files:**
- Modify: `web/src/types.ts`
- Modify: `web/src/api.ts`
- Create: `web/src/components/Approvals.tsx`
- Modify: `web/src/App.tsx`

**Interfaces:**
- Consumes: `GET/POST api/pending-actions*` (Task 6).
- Produces: `Approvals` component, a new `"approvals"` tab.

- [ ] **Step 1: Add types**

In `web/src/types.ts`, add:

```ts
export type PendingActionStatus = "Pending" | "Approved" | "Rejected" | "Executed" | "Expired";

export interface PendingAction {
  id: string;
  tenantId: string;
  tenantName: string;
  actionType: string;
  previewSummary: string;
  status: PendingActionStatus;
  createdAt: string;
  expiresAt: string;
}
```

- [ ] **Step 2: Add API client methods**

In `web/src/api.ts`, add a `pendingActions` group using the same `request<T>(path, init)` helper
every other group already uses (there is no separate `get`/`post` wrapper — `request` takes a
`RequestInit`, defaulting to GET when `init` is omitted, per `api.tenants`/`api.workflows` above
it):

```ts
  pendingActions: {
    list: () => request<PendingAction[]>("/api/pending-actions"),
    approve: (id: string) => request<void>(`/api/pending-actions/${id}/approve`, { method: "POST" }),
    reject: (id: string) => request<void>(`/api/pending-actions/${id}/reject`, { method: "POST" }),
  },
```

(Import `PendingAction` from `./types` at the top of the file alongside the other type imports.)

- [ ] **Step 3: Build the component**

```tsx
// web/src/components/Approvals.tsx
import { useEffect, useState } from "react";
import { api } from "../api";
import type { PendingAction } from "../types";

export function Approvals() {
  const [items, setItems] = useState<PendingAction[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = () => api.pendingActions.list().then(setItems).catch((e) => setError(String(e)));
  useEffect(() => { load(); }, []);

  const decide = async (id: string, action: "approve" | "reject") => {
    setBusyId(id);
    setError(null);
    try {
      await (action === "approve" ? api.pendingActions.approve(id) : api.pendingActions.reject(id));
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusyId(null);
    }
  };

  return (
    <section>
      <h2>Approvals</h2>
      <p className="muted">
        Mutating actions requested through MCP land here for tenants in the default Queue approval
        mode. Nothing runs until you approve it.
      </p>
      {error && <p className="error">{error}</p>}
      {items.length === 0 && <p className="muted">No pending approvals.</p>}
      <table>
        <thead><tr><th>Tenant</th><th>Action</th><th>Preview</th><th>Requested</th><th>Expires</th><th></th></tr></thead>
        <tbody>
          {items.map((item) => (
            <tr key={item.id}>
              <td>{item.tenantName}</td>
              <td>{item.actionType}</td>
              <td>{item.previewSummary}</td>
              <td>{new Date(item.createdAt).toLocaleString()}</td>
              <td>{new Date(item.expiresAt).toLocaleString()}</td>
              <td>
                <button disabled={busyId === item.id} onClick={() => decide(item.id, "approve")}>Approve</button>
                <button disabled={busyId === item.id} onClick={() => decide(item.id, "reject")}>Reject</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
```

- [ ] **Step 4: Wire the tab**

In `web/src/App.tsx`: add `import { Approvals } from "./components/Approvals";` near the other
component imports, add `{ key: "approvals", label: "Approvals" }` to the `TABS` array (near the
`"workflows"` entry), and add `{tab === "approvals" && <Approvals />}` alongside the other
`{tab === ... && <...>}` lines.

- [ ] **Step 5: Build and manually verify**

Run: `cd web && npm run build`
Expected: `tsc -b && vite build` succeeds.

Manually: run the stack (`docker compose up --build` or the two dev servers), log in, confirm the
Approvals tab renders (empty state is fine at this point — nothing stages one until Task 8's tool
is called through a real MCP client, in Task 11).

- [ ] **Step 6: Commit**

```bash
git add web/src/types.ts web/src/api.ts web/src/components/Approvals.tsx web/src/App.tsx
git commit -m "Add Approvals tab to the SPA"
```

---

### Task 10: MCP token management UI

**Files:**
- Modify: `web/src/types.ts`
- Modify: `web/src/api.ts`
- Modify: `web/src/components/Security.tsx`

**Interfaces:**
- Consumes: `GET/POST/DELETE api/mcp-tokens*` (Task 5).

- [ ] **Step 1: Add types**

In `web/src/types.ts`:

```ts
export interface McpTokenInfo {
  id: string;
  name: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
}
```

- [ ] **Step 2: Add API client methods**

In `web/src/api.ts`, an `mcpTokens` group using `request<T>`, matching `api.passkey.remove`'s
exact shape for the DELETE call (`request<void>(path, { method: "DELETE" })`):

```ts
  mcpTokens: {
    list: () => request<McpTokenInfo[]>("/api/mcp-tokens"),
    create: (name: string) =>
      request<{ id: string; name: string; jwt: string }>("/api/mcp-tokens", { method: "POST", body: JSON.stringify({ name }) }),
    revoke: (id: string) => request<void>(`/api/mcp-tokens/${id}`, { method: "DELETE" }),
  },
```

- [ ] **Step 3: Add a fieldset to `Security.tsx`**

Add state near the existing `passkeys` state:

```tsx
  const [mcpTokens, setMcpTokens] = useState<McpTokenInfo[]>([]);
  const [newTokenName, setNewTokenName] = useState("");
  const [issuedJwt, setIssuedJwt] = useState<string | null>(null);

  const loadMcpTokens = () => api.mcpTokens.list().then(setMcpTokens).catch((e) => setError(String(e)));
  useEffect(() => { loadMcpTokens(); }, []);

  const createMcpToken = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.mcpTokens.create(newTokenName);
      setIssuedJwt(r.jwt);
      setNewTokenName("");
      await loadMcpTokens();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const revokeMcpToken = async (id: string) => {
    await api.mcpTokens.revoke(id);
    await loadMcpTokens();
  };
```

Add a fieldset after the existing TOTP one (before the closing `</section>`):

```tsx
      <fieldset>
        <legend>MCP access tokens</legend>
        <p className="muted">
          For headless/scripted MCP clients that can't do an interactive login. Each token has the
          same access as your account -- revoke one immediately if a client using it is
          decommissioned or compromised.
        </p>
        {issuedJwt ? (
          <div className="password">
            <p><strong>Copy this token now -- it will not be shown again.</strong></p>
            <p className="mono">{issuedJwt}</p>
            <button onClick={() => setIssuedJwt(null)}>I've copied it</button>
          </div>
        ) : (
          <form className="field" onSubmit={createMcpToken}>
            <label>Name this token (e.g. "Claude Desktop")</label>
            <input value={newTokenName} onChange={(e) => setNewTokenName(e.target.value)} />
            <button disabled={busy || !newTokenName.trim()}>{busy ? "Creating…" : "Create token"}</button>
          </form>
        )}
        <table>
          <thead><tr><th>Name</th><th>Created</th><th>Last used</th><th></th></tr></thead>
          <tbody>
            {mcpTokens.map((t) => (
              <tr key={t.id}>
                <td>{t.name}</td>
                <td>{new Date(t.createdAt).toLocaleDateString()}</td>
                <td>{t.lastUsedAt ? new Date(t.lastUsedAt).toLocaleDateString() : "never"}</td>
                <td><button onClick={() => revokeMcpToken(t.id)}>Revoke</button></td>
              </tr>
            ))}
            {mcpTokens.length === 0 && <tr><td colSpan={4} className="muted">No MCP tokens yet.</td></tr>}
          </tbody>
        </table>
      </fieldset>
```

Add `McpTokenInfo` to the `import type { ... } from "../types"` line at the top of the file.

- [ ] **Step 4: Build**

Run: `cd web && npm run build`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add web/src/types.ts web/src/api.ts web/src/components/Security.tsx
git commit -m "Add MCP access token management to the Security tab"
```

---

### Task 11: End-to-end live verification

Not automatable — this is the "budget for live verification separately" step, proving the whole
loop (mint a token, stage a mutation, approve it, watch it actually run) against a real MCP
client, not just a diff read.

- [ ] **Step 1: Run the stack locally** with `Auth:Mode=Local` (docker-compose or the two dev
  servers per `CLAUDE.md`).

- [ ] **Step 2: Register/log in**, go to Security, create an MCP token, copy it.

- [ ] **Step 3: Connect a real MCP client** (Claude Code: `claude mcp add --transport http
  pcb-local http://localhost:5080/mcp --header "Authorization: Bearer <token>"`).

- [ ] **Step 4: Call `list_tenants`** and confirm it returns only tenants the logged-in account
  actually has a grant on (register/sync at least one tenant first if the account has none).

- [ ] **Step 5: Call `diagnose_workflow`** against a real tenant and confirm the findings match
  what the Workflows tab in the SPA shows for the same tenant/workflow.

- [ ] **Step 6: Call `remediate_workflow`** with the tenant left in its default Queue mode.
  Confirm the tool response includes a `PendingActionId` and does NOT actually change anything
  yet — re-run `diagnose_workflow` and confirm the diagnosis is unchanged.

- [ ] **Step 7: Open the Approvals tab in the SPA**, confirm the staged action appears with a
  sensible preview, and click Approve.

- [ ] **Step 8: Re-run `diagnose_workflow`** and confirm the fix actually ran this time.

- [ ] **Step 9: Flip the tenant to `ClientTrust`** via `PATCH api/admin/tenants/{id}/mcp-mode`
  (curl with a system-admin's token, or a quick temporary button — this plan doesn't build
  permanent SPA UI for this admin action, since it's expected to be rare). Call
  `remediate_workflow` again and confirm it executes immediately with no `PendingAction` created.

- [ ] **Step 10: Log the unit** in `docs/dev-process.md`'s Log table — task type ("MCP server
  foundation, 11 tasks, ~20 files"), author/reviewer per whichever `codex-*` agents actually did
  the implementation and review work for this plan, and a one-line verdict summarizing what Step
  4-9 above actually proved.

No commit for this task — it's verification of what Tasks 1-10 already committed, not new code.
