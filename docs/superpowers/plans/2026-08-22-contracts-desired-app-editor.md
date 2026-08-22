# Contracts Desired-App Editor Implementation Plan

> **Status: implemented and review-corrected.** The task-by-task body below records the original
> plan. The corrected architecture and final behavior are authoritative in the linked design spec
> and the Review outcome section at the end; do not re-execute the original snippets verbatim.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a system admin add/remove app templates from a contract's desired-app list, from the Contracts screen, without navigating away.

**Architecture:** Two new idempotent `ContractsController` endpoints mutate a corrected many-to-many `Contract.DesiredApps` relationship and return a `ContractDto` that carries `DesiredAppIds`. The migration seeds the new join from legacy `AppTemplate.ContractId` assignments. `Contracts.tsx` gains an inline "Manage apps" checklist per contract row, defaulting to package-ready templates only, with Set-based toggle/upload concurrency guards and a playful, keyboard-accessible quest-chip package trigger.

**Tech Stack:** ASP.NET Core 8 / EF Core (Sqlite in-memory for tests) on the backend; React 19 + MUI v9 + Vitest/Testing Library on the frontend.

**Spec:** `docs/superpowers/specs/2026-08-22-contracts-desired-app-editor-design.md`

## Global Constraints

- No AI attribution in commits (repo-wide convention).
- Keep all C# string literals ASCII-only (this project's toolchain has mis-decoded non-ASCII in string literals before; comments are unaffected).
- Both new endpoints are idempotent: adding an already-desired app, or removing an absent one, is a harmless no-op success.
- Both new endpoints gate on `ITenantAccessService.IsSystemAdmin`, matching `AppTemplatesController`'s existing pattern exactly -- this is a contract-level (not tenant-scoped) mutation, not the tenant-bypass anti-pattern `CLAUDE.md` warns about.
- Add a many-to-many join migration and preserve every existing `AppTemplate.ContractId` assignment.
- New desired memberships require an attached package; already-desired legacy package-less rows remain removable and repairable.
- Frontend error toasts show the bare `e.message`, never an `Error: ...`-wrapped string.

---

## File Structure

- `src/PartnerCenterBridge.Api/Contracts/Dtos.cs` -- `ContractDto` gains `DesiredAppIds`.
- `src/PartnerCenterBridge.Api/Controllers/ContractsController.cs` -- constructor gains `ITenantAccessService`; two new endpoints.
- `src/PartnerCenterBridge.Api/Controllers/AppTemplatesController.cs` -- system-admin gates for create/upload while preserving legacy owner metadata without bypassing package readiness.
- `src/PartnerCenterBridge.Core/Entities/AppTemplate.cs` -- inverse desired-contract navigation while retaining the legacy owner.
- `src/PartnerCenterBridge.Data/BridgeDbContext.cs` and `Migrations/*AddContractDesiredApps*` -- explicit join mapping and data-preserving migration.
- `tests/PartnerCenterBridge.Tests/AppTemplatesControllerTests.cs` -- create/upload authorization and legacy-assignment coverage.
- `tests/PartnerCenterBridge.Tests/ContractsControllerTests.cs` -- new file, backend test coverage (this controller currently has none).
- `web/src/types.ts` -- `Contract` gains `desiredAppIds: string[]`.
- `web/src/api.ts` -- `contracts.addDesiredApp` / `contracts.removeDesiredApp`.
- `web/src/App.tsx` -- passes `me` to `<Contracts>`.
- `web/src/components/Contracts.tsx` -- `me` prop, template list load, "Manage apps" checklist UI, quest chip.
- `web/src/components/Contracts.test.tsx` -- new test cases for the above.

---

### Task 1: Backend -- `ContractDto.DesiredAppIds` and the two mutation endpoints

**Files:**
- Modify: `src/PartnerCenterBridge.Api/Contracts/Dtos.cs:13-16`
- Modify: `src/PartnerCenterBridge.Api/Controllers/ContractsController.cs`
- Create: `tests/PartnerCenterBridge.Tests/ContractsControllerTests.cs`

**Interfaces:**
- Consumes: `PartnerCenterBridge.Api.Auth.ITenantAccessService` (existing interface; `IsSystemAdmin` bool property), `PartnerCenterBridge.Core.Entities.Contract.DesiredApps` (`ICollection<AppTemplate>`), `PartnerCenterBridge.Core.Entities.AppTemplate`, `Tests.TestDb`, `Tests.FakeTenantAccessService(bool isSystemAdmin)` (both already exist under `tests/PartnerCenterBridge.Tests/`).
- Produces: `ContractDto(Guid Id, string Name, string? Notes, int TenantCount, int DesiredAppCount, IReadOnlyList<Guid> DesiredAppIds)`; `ContractsController.AddDesiredApp(Guid id, Guid templateId, CancellationToken ct)` and `ContractsController.RemoveDesiredApp(Guid id, Guid templateId, CancellationToken ct)`, both returning `Task<ActionResult<ContractDto>>`, routed at `POST /api/contracts/{id}/desired-apps/{templateId}` and `DELETE /api/contracts/{id}/desired-apps/{templateId}`.

- [ ] **Step 1: Write the failing tests**

Create `tests/PartnerCenterBridge.Tests/ContractsControllerTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class ContractsControllerTests
{
    private static Contract MakeContract() => new() { Name = "Contoso baseline" };
    private static AppTemplate MakeTemplate(string name = "Defender") => new()
    {
        DisplayName = name,
        InstallCommandLine = "install.exe",
        UninstallCommandLine = "uninstall.exe"
    };

    [Fact]
    public async Task AddDesiredApp_as_system_admin_adds_the_template_and_returns_updated_dto()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(contract.Id, template.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Contains(template.Id, dto.DesiredAppIds);
        Assert.Equal(1, dto.DesiredAppCount);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps).FirstAsync(c => c.Id == contract.Id);
        Assert.Contains(persisted.DesiredApps, a => a.Id == template.Id);
    }

    [Fact]
    public async Task AddDesiredApp_is_idempotent_for_an_already_desired_template()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        contract.DesiredApps.Add(template);
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(contract.Id, template.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(1, dto.DesiredAppCount);
    }

    [Fact]
    public async Task AddDesiredApp_rejects_a_non_system_admin_caller()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: false));

        var result = await controller.AddDesiredApp(contract.Id, template.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps).FirstAsync(c => c.Id == contract.Id);
        Assert.Empty(persisted.DesiredApps);
    }

    [Fact]
    public async Task AddDesiredApp_returns_NotFound_for_an_unknown_contract()
    {
        using var db = new TestDb();
        var template = MakeTemplate();
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(Guid.NewGuid(), template.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AddDesiredApp_returns_NotFound_for_an_unknown_template()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        db.Context.Contracts.Add(contract);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(contract.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RemoveDesiredApp_as_system_admin_removes_the_template_and_returns_updated_dto()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        contract.DesiredApps.Add(template);
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.RemoveDesiredApp(contract.Id, template.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.DoesNotContain(template.Id, dto.DesiredAppIds);
        Assert.Equal(0, dto.DesiredAppCount);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps).FirstAsync(c => c.Id == contract.Id);
        Assert.Empty(persisted.DesiredApps);
    }

    [Fact]
    public async Task RemoveDesiredApp_is_idempotent_for_an_absent_template()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.RemoveDesiredApp(contract.Id, template.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(0, dto.DesiredAppCount);
    }

    [Fact]
    public async Task RemoveDesiredApp_rejects_a_non_system_admin_caller()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        contract.DesiredApps.Add(template);
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: false));

        var result = await controller.RemoveDesiredApp(contract.Id, template.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps).FirstAsync(c => c.Id == contract.Id);
        Assert.NotEmpty(persisted.DesiredApps);
    }

    [Fact]
    public async Task RemoveDesiredApp_returns_NotFound_for_an_unknown_contract()
    {
        using var db = new TestDb();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.RemoveDesiredApp(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task List_reflects_DesiredAppIds()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        contract.DesiredApps.Add(template);
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var list = await controller.List(CancellationToken.None);

        Assert.Contains(template.Id, list.Single(c => c.Id == contract.Id).DesiredAppIds);
    }
}
```

Note: this file uses `db.Context.Contracts.Include(c => c.DesiredApps).FirstAsync(...)` and `list.Single(...)`, both of which need `Microsoft.EntityFrameworkCore` / `System.Linq` -- confirm whether an explicit `using Microsoft.EntityFrameworkCore;` is required by checking whether `AppTemplatesControllerTests.cs` needed one for its own `FindAsync` calls (it didn't -- implicit usings are enabled project-wide for this test project, per `AppTemplatesControllerTests.cs` having no explicit EF Core `using` despite calling `db.Context.AppTemplates.FindAsync`). If the build step below reports `Contracts`/`Include`/`FirstAsync`/`Single` as unresolved, add `using Microsoft.EntityFrameworkCore;` and `using System.Linq;` to the top of the new test file.

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test PartnerCenterBridge.sln --filter ContractsControllerTests`
Expected: build error -- `ContractsController` has no constructor taking `(BridgeDbContext, ITenantAccessService)`, and no `AddDesiredApp`/`RemoveDesiredApp` members exist yet.

- [ ] **Step 3: Add `DesiredAppIds` to `ContractDto`**

In `src/PartnerCenterBridge.Api/Contracts/Dtos.cs`, replace lines 13-16:

```csharp
public record ContractDto(Guid Id, string Name, string? Notes, int TenantCount, int DesiredAppCount,
    IReadOnlyList<Guid> DesiredAppIds)
{
    public static ContractDto From(Contract c) => new(
        c.Id, c.Name, c.Notes, c.Tenants.Count, c.DesiredApps.Count,
        c.DesiredApps.Select(a => a.Id).ToList());
}
```

- [ ] **Step 4: Add `ITenantAccessService` and the two endpoints to `ContractsController`**

Replace the full contents of `src/PartnerCenterBridge.Api/Controllers/ContractsController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Reconcile;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ContractsController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;

    public ContractsController(BridgeDbContext db, ITenantAccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<IReadOnlyList<ContractDto>> List(CancellationToken ct) =>
        (await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps).ToListAsync(ct))
        .Select(ContractDto.From).ToList();

    [HttpPost]
    public async Task<ActionResult<ContractDto>> Create(CreateContractRequest req, CancellationToken ct)
    {
        var contract = new Contract { Name = req.Name, Notes = req.Notes };
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), ContractDto.From(contract));
    }

    /// <summary>
    /// Plan (dry-run) what would happen to bring every tenant on the contract to its desired
    /// state. Pure diff — no Graph calls — so it is safe to call freely from the UI.
    /// </summary>
    [HttpGet("{id:guid}/plan")]
    public async Task<ActionResult<IReadOnlyList<ReconcilePlanItemDto>>> Plan(Guid id, CancellationToken ct)
    {
        var contract = await _db.Contracts
            .Include(c => c.Tenants)
            .Include(c => c.DesiredApps)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();

        var templateIds = contract.DesiredApps.Select(a => a.Id).ToList();
        var tenantIds = contract.Tenants.Select(t => t.Id).ToList();
        var deployments = await _db.Deployments
            .Where(d => tenantIds.Contains(d.TenantId) && templateIds.Contains(d.AppTemplateId))
            .ToListAsync(ct);

        var plan = DesiredStateReconciler.Plan(contract.Tenants, contract.DesiredApps, deployments);
        return Ok(plan.Select(p => new ReconcilePlanItemDto(
            p.Tenant.Id, p.Tenant.DisplayName, p.Template.Id, p.Template.DisplayName, p.Action.ToString())).ToList());
    }

    /// <summary>
    /// Adds a template to the contract's desired-app list. Idempotent: adding an already-desired
    /// template is a harmless no-op success, so the frontend never has to check first.
    /// </summary>
    [HttpPost("{id:guid}/desired-apps/{templateId:guid}")]
    public async Task<ActionResult<ContractDto>> AddDesiredApp(Guid id, Guid templateId, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();

        var contract = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();
        var template = await _db.AppTemplates.FindAsync([templateId], ct);
        if (template is null) return NotFound();

        if (!contract.DesiredApps.Any(a => a.Id == templateId)) contract.DesiredApps.Add(template);
        await _db.SaveChangesAsync(ct);
        return Ok(ContractDto.From(contract));
    }

    /// <summary>
    /// Removes a template from the contract's desired-app list. Idempotent: removing a template
    /// that isn't there is a harmless no-op success.
    /// </summary>
    [HttpDelete("{id:guid}/desired-apps/{templateId:guid}")]
    public async Task<ActionResult<ContractDto>> RemoveDesiredApp(Guid id, Guid templateId, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();

        var contract = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();

        var existing = contract.DesiredApps.FirstOrDefault(a => a.Id == templateId);
        if (existing is not null) contract.DesiredApps.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return Ok(ContractDto.From(contract));
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PartnerCenterBridge.sln --filter ContractsControllerTests`
Expected: PASS (10 tests). If the `using` note from Step 1 was needed, add it now and re-run.

- [ ] **Step 6: Run the full backend suite to check for regressions**

Run: `dotnet build PartnerCenterBridge.sln && dotnet test PartnerCenterBridge.sln`
Expected: build succeeds, all tests pass (was 162/162 before this change; expect 172/172 now).

- [ ] **Step 7: Commit**

```bash
git add src/PartnerCenterBridge.Api/Contracts/Dtos.cs src/PartnerCenterBridge.Api/Controllers/ContractsController.cs tests/PartnerCenterBridge.Tests/ContractsControllerTests.cs
git commit -m "Add desired-app add/remove endpoints to ContractsController"
```

---

### Task 2: Frontend plumbing -- types, api client, `App.tsx` wiring

**Files:**
- Modify: `web/src/types.ts:12-18`
- Modify: `web/src/api.ts:57-64`
- Modify: `web/src/App.tsx:114`

**Interfaces:**
- Consumes: `ContractDto` shape from Task 1 (`desiredAppIds: string[]` after JSON serialization -- .NET's default JSON serializer camel-cases `DesiredAppIds` to `desiredAppIds`, matching every other DTO field already consumed this way, e.g. `desiredAppCount`).
- Produces: `Contract.desiredAppIds: string[]` (consumed by Task 3); `api.contracts.addDesiredApp(contractId: string, templateId: string): Promise<Contract>` and `api.contracts.removeDesiredApp(contractId: string, templateId: string): Promise<Contract>` (consumed by Task 3); `<Contracts me={me} />` call shape in `App.tsx` (consumed by Task 3, which changes `Contracts`'s signature to require `me`).

There's no isolated unit test for this task -- it's pure type/wiring glue with no behavior of its own. Its correctness is verified by the frontend build (which fails loudly on any type mismatch) and by Task 3's tests, which depend on these exact names.

- [ ] **Step 1: Add `desiredAppIds` to the `Contract` type**

In `web/src/types.ts`, replace lines 12-18:

```typescript
export interface Contract {
  id: string;
  name: string;
  notes?: string;
  tenantCount: number;
  desiredAppCount: number;
  desiredAppIds: string[];
}
```

- [ ] **Step 2: Add the two new API client methods**

In `web/src/api.ts`, replace lines 57-64:

```typescript
  contracts: {
    list: () => request<Contract[]>("/api/contracts"),
    create: (name: string, notes?: string) =>
      request<Contract>("/api/contracts", { method: "POST", body: JSON.stringify({ name, notes }) }),
    plan: (id: string) =>
      request<{ tenantId: string; tenantName: string; templateId: string; templateName: string; action: string }[]>(
        `/api/contracts/${id}/plan`),
    addDesiredApp: (contractId: string, templateId: string) =>
      request<Contract>(`/api/contracts/${contractId}/desired-apps/${templateId}`, { method: "POST" }),
    removeDesiredApp: (contractId: string, templateId: string) =>
      request<Contract>(`/api/contracts/${contractId}/desired-apps/${templateId}`, { method: "DELETE" })
  },
```

- [ ] **Step 3: Pass `me` to `Contracts` in `App.tsx`**

In `web/src/App.tsx`, change line 114:

```typescript
      {tab === "contracts" && <Contracts me={me} />}
```

(This will not type-check until Task 3 changes `Contracts`'s signature to accept `me` -- that's expected; Task 3 completes this pairing.)

- [ ] **Step 4: Commit**

```bash
git add web/src/types.ts web/src/api.ts web/src/App.tsx
git commit -m "Add desired-app API client methods and Contract.desiredAppIds"
```

---

### Task 3: Frontend -- Contracts.tsx "Manage apps" checklist

**Files:**
- Modify: `web/src/components/Contracts.tsx`
- Modify: `web/src/components/Contracts.test.tsx`

**Interfaces:**
- Consumes: `Contract.desiredAppIds: string[]` and `api.contracts.addDesiredApp`/`removeDesiredApp` (Task 2); `AppTemplate` (`id`, `displayName`, `hasPackage`) and `api.templates.list()`/`api.templates.uploadPackage(id: string, file: File): Promise<AppTemplate>` (both already exist in `web/src/types.ts` / `web/src/api.ts`); `MeProfile.isSystemAdmin` (already exists); `useToast()` returning `(message: string, severity?: ToastSeverity) => void` (already exists in `web/src/hooks/useToast.tsx`).
- Produces: `Contracts({ me }: { me: MeProfile | null })` -- the new required prop signature completing Task 2's `<Contracts me={me} />` call.

This is the last task in the plan -- nothing downstream consumes new interfaces from this one.

- [ ] **Step 1: Write the failing tests**

Replace `web/src/components/Contracts.test.tsx` in full:

```typescript
import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider } from "../hooks/useToast";
import { Contracts } from "./Contracts";
import type { Contract, AppTemplate, MeProfile } from "../types";

vi.mock("../api", () => ({
  api: {
    contracts: {
      list: vi.fn(),
      create: vi.fn(),
      plan: vi.fn(),
      addDesiredApp: vi.fn(),
      removeDesiredApp: vi.fn()
    },
    templates: {
      list: vi.fn(),
      uploadPackage: vi.fn()
    }
  }
}));

import { api } from "../api";

const admin: MeProfile = {
  id: "u1", email: "admin@contoso.com", displayName: "Admin", isSystemAdmin: true,
  totpEnabled: false, tenantAccess: []
};
const nonAdmin: MeProfile = { ...admin, id: "u2", isSystemAdmin: false };

const contract: Contract = {
  id: "c1",
  name: "Contoso baseline",
  notes: "Standard apps",
  tenantCount: 2,
  desiredAppCount: 1,
  desiredAppIds: ["t1"]
};

const readyTemplate: AppTemplate = {
  id: "t1", displayName: "Defender", installCommandLine: "install.exe",
  uninstallCommandLine: "uninstall.exe", contentVersion: 1, hasPackage: true,
  detectionRules: [], assignments: []
};
const noPackageTemplate: AppTemplate = {
  id: "t2", displayName: "Zoom", installCommandLine: "install.exe",
  uninstallCommandLine: "uninstall.exe", contentVersion: 0, hasPackage: false,
  detectionRules: [], assignments: []
};

function renderContracts(me: MeProfile | null = admin) {
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <Contracts me={me} />
      </ToastProvider>
    </ThemeProvider>
  );
}

describe("Contracts", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.contracts.list).mockResolvedValue([]);
    vi.mocked(api.contracts.create).mockResolvedValue(contract);
    vi.mocked(api.contracts.plan).mockResolvedValue([]);
    vi.mocked(api.templates.list).mockResolvedValue([]);
  });

  it("renders the contracts list", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    renderContracts();

    expect(await screen.findByText("Contoso baseline")).toBeInTheDocument();
    expect(screen.getByText("2")).toBeInTheDocument();
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("creates a contract and shows a success toast", async () => {
    const user = userEvent.setup();
    renderContracts();

    await user.type(await screen.findByLabelText("Contract name"), "Fabrikam baseline");
    await user.type(screen.getByLabelText("Notes (optional)"), "Pilot apps");
    await user.click(screen.getByRole("button", { name: "Add contract" }));

    await waitFor(() => expect(api.contracts.create).toHaveBeenCalledWith("Fabrikam baseline", "Pilot apps"));
    expect(await screen.findByText("Fabrikam baseline added")).toBeInTheDocument();
  });

  it("shows an error alert when loading contracts fails", async () => {
    vi.mocked(api.contracts.list).mockRejectedValue(new Error("boom"));
    renderContracts();

    expect(await screen.findByText("boom")).toBeInTheDocument();
  });

  it("renders a preview plan", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan).mockResolvedValue([
      { tenantId: "t1", tenantName: "Contoso", templateId: "a1", templateName: "Defender", action: "Install" }
    ]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("Reconcile plan (dry run)")).toBeInTheDocument();
    expect(screen.getByText("Contoso")).toBeInTheDocument();
    expect(screen.getByText("Defender")).toBeInTheDocument();
    expect(screen.getByText("Install")).toBeInTheDocument();
  });

  it("shows the empty preview plan state", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("Nothing to do.")).toBeInTheDocument();
  });

  it("shows an error when the plan preview fails", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan).mockRejectedValue(new Error("plan boom"));
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("plan boom")).toBeInTheDocument();
  });

  it("clears a previously shown plan once a new preview attempt fails", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan)
      .mockResolvedValueOnce([
        { tenantId: "t1", tenantName: "Contoso", templateId: "a1", templateName: "Defender", action: "Install" }
      ])
      .mockRejectedValueOnce(new Error("plan boom"));
    const user = userEvent.setup();
    renderContracts();

    const previewButton = await screen.findByRole("button", { name: "Preview plan" });
    await user.click(previewButton);
    expect(await screen.findByText("Defender")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Preview plan" }));
    expect(await screen.findByText("plan boom")).toBeInTheDocument();
    expect(screen.queryByText("Defender")).not.toBeInTheDocument();
  });

  it("does not render Manage apps for a non-admin", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    renderContracts(nonAdmin);

    await screen.findByText("Contoso baseline");
    expect(screen.queryByRole("button", { name: "Manage apps" })).not.toBeInTheDocument();
  });

  it("renders Manage apps when me is null", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    renderContracts(null);

    expect(await screen.findByRole("button", { name: "Manage apps" })).toBeInTheDocument();
  });

  it("shows only package-ready templates by default, and reveals the rest via the switch", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate, noPackageTemplate]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));

    expect(await screen.findByText("Defender")).toBeInTheDocument();
    expect(screen.queryByText("Zoom")).not.toBeInTheDocument();

    await user.click(screen.getByRole("checkbox", { name: "Show templates without a package" }));

    expect(await screen.findByText("Zoom")).toBeInTheDocument();
    expect(screen.getByText("So close! Attach a package to unlock →")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Zoom" })).toBeDisabled();
  });

  it("checking a box calls addDesiredApp and reflects the response", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate, noPackageTemplate]);
    const updated: Contract = { ...contract, desiredAppIds: ["t1", "t2"], desiredAppCount: 2 };
    vi.mocked(api.contracts.addDesiredApp).mockResolvedValue(updated);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    await user.click(screen.getByRole("checkbox", { name: "Show templates without a package" }));
    await user.click(screen.getByRole("checkbox", { name: "Defender" }));

    await waitFor(() => expect(api.contracts.addDesiredApp).toHaveBeenCalledWith("c1", "t1"));
  });

  it("unchecking a box calls removeDesiredApp and reflects the response", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate]);
    const updated: Contract = { ...contract, desiredAppIds: [], desiredAppCount: 0 };
    vi.mocked(api.contracts.removeDesiredApp).mockResolvedValue(updated);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    const checkbox = await screen.findByRole("checkbox", { name: "Defender" });
    expect(checkbox).toBeChecked();
    await user.click(checkbox);

    await waitFor(() => expect(api.contracts.removeDesiredApp).toHaveBeenCalledWith("c1", "t1"));
  });

  it("two different templates toggled close together resolve independently", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    const secondReady: AppTemplate = { ...readyTemplate, id: "t3", displayName: "Zoom Ready" };
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate, secondReady]);

    let resolveFirst!: (c: Contract) => void;
    const firstCall = new Promise<Contract>((resolve) => { resolveFirst = resolve; });
    vi.mocked(api.contracts.removeDesiredApp).mockImplementation((cid, tid) =>
      tid === "t1" ? firstCall : Promise.resolve({ ...contract, desiredAppIds: [], desiredAppCount: 0 })
    );
    vi.mocked(api.contracts.addDesiredApp).mockResolvedValue({ ...contract, desiredAppIds: ["t1", "t3"], desiredAppCount: 2 });

    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    const defenderBox = await screen.findByRole("checkbox", { name: "Defender" });
    const zoomReadyBox = screen.getByRole("checkbox", { name: "Zoom Ready" });

    await user.click(defenderBox);
    expect(defenderBox).toBeDisabled();
    await user.click(zoomReadyBox);

    await waitFor(() => expect(api.contracts.addDesiredApp).toHaveBeenCalledWith("c1", "t3"));
    expect(zoomReadyBox).not.toBeDisabled();
    expect(defenderBox).toBeDisabled();

    resolveFirst({ ...contract, desiredAppIds: [], desiredAppCount: 0 });
    await waitFor(() => expect(defenderBox).not.toBeDisabled());
  });

  it("a failed toggle shows the bare error message and leaves the checkbox in its prior state", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate]);
    vi.mocked(api.contracts.removeDesiredApp).mockRejectedValue(new Error("network down"));
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    const checkbox = await screen.findByRole("checkbox", { name: "Defender" });
    await user.click(checkbox);

    expect(await screen.findByText("network down")).toBeInTheDocument();
    expect(checkbox).toBeChecked();
  });

  it("clicking the quest chip uploads a package and moves the template into the normal list", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list)
      .mockResolvedValueOnce([readyTemplate, noPackageTemplate])
      .mockResolvedValueOnce([readyTemplate, { ...noPackageTemplate, hasPackage: true }]);
    vi.mocked(api.templates.uploadPackage).mockResolvedValue({ ...noPackageTemplate, hasPackage: true });
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    await user.click(screen.getByRole("checkbox", { name: "Show templates without a package" }));
    expect(await screen.findByText("Zoom")).toBeInTheDocument();

    const file = new File(["x"], "zoom.intunewin");
    const input = screen.getByLabelText("Upload package for Zoom");
    await user.upload(input, file);

    await waitFor(() => expect(api.templates.uploadPackage).toHaveBeenCalledWith("t2", file));
    await waitFor(() => expect(screen.getByRole("checkbox", { name: "Zoom" })).not.toBeDisabled());
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd web && npx vitest run src/components/Contracts.test.tsx`
Expected: FAIL -- `Contracts` doesn't accept a `me` prop yet, `api.templates` isn't loaded, no "Manage apps" button exists.

- [ ] **Step 3: Implement the checklist**

Replace `web/src/components/Contracts.tsx` in full:

```typescript
import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Checkbox from "@mui/material/Checkbox";
import Chip from "@mui/material/Chip";
import FormControlLabel from "@mui/material/FormControlLabel";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useToast } from "../hooks/useToast";
import type { AppTemplate, Contract, MeProfile } from "../types";

type PlanItem = Awaited<ReturnType<typeof api.contracts.plan>>[number];

function planActionColor(action: string): "default" | "success" | "warning" {
  switch (action.toLowerCase()) {
    case "install": return "success";
    case "remove": return "warning";
    default: return "default";
  }
}

export function Contracts({ me }: { me: MeProfile | null }) {
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [templates, setTemplates] = useState<AppTemplate[]>([]);
  const [name, setName] = useState("");
  const [notes, setNotes] = useState("");
  const [plan, setPlan] = useState<PlanItem[] | null>(null);
  const [lastAction, setLastAction] = useState<"load" | "create" | "plan" | null>(null);
  const [managingId, setManagingId] = useState<string | null>(null);
  const [showNoPackage, setShowNoPackage] = useState(false);
  const [pendingIds, setPendingIds] = useState<Set<string>>(new Set());
  const canManage = !me || me.isSystemAdmin;
  const toast = useToast();

  const loadAction = useAsyncAction(async () => {
    setContracts(await api.contracts.list());
  });
  const loadTemplatesAction = useAsyncAction(async () => {
    setTemplates(await api.templates.list());
  });
  const createAction = useAsyncAction(async () => {
    const addedName = name;
    await api.contracts.create(name, notes || undefined);
    setName(""); setNotes("");
    await loadAction.run();
    toast(`${addedName} added`, "success");
  });
  const planAction = useAsyncAction(async (id: string) => { setPlan(await api.contracts.plan(id)); });

  useEffect(() => {
    setLastAction("load");
    void loadAction.run();
    void loadTemplatesAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const error =
    lastAction === "load" ? loadAction.error :
    lastAction === "create" ? createAction.error :
    lastAction === "plan" ? planAction.error :
    null;

  const toggle = async (contractId: string, templateId: string, checked: boolean) => {
    setPendingIds((prev) => new Set(prev).add(templateId));
    try {
      const updated = checked
        ? await api.contracts.addDesiredApp(contractId, templateId)
        : await api.contracts.removeDesiredApp(contractId, templateId);
      setContracts((prev) => prev.map((c) => (c.id === contractId ? updated : c)));
    } catch (e) {
      toast(e instanceof Error ? e.message : String(e), "error");
    } finally {
      setPendingIds((prev) => { const next = new Set(prev); next.delete(templateId); return next; });
    }
  };

  const uploadFromChip = async (templateId: string, file: File) => {
    try {
      await api.templates.uploadPackage(templateId, file);
      setTemplates(await api.templates.list());
      toast("Package uploaded.", "success");
    } catch (e) {
      toast(e instanceof Error ? e.message : String(e), "error");
    }
  };

  return (
    <Box component="section">
      <Typography variant="h5" component="h2" gutterBottom>Contracts</Typography>
      <Stack component="form" direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ mb: 2 }} onSubmit={(ev) => {
        ev.preventDefault();
        if (!name.trim()) return;
        setLastAction("create");
        void createAction.run();
      }}>
        <TextField size="small" label="Contract name" value={name} onChange={(e) => setName(e.target.value)} />
        <TextField size="small" label="Notes (optional)" value={notes} onChange={(e) => setNotes(e.target.value)} />
        <Button type="submit" variant="contained" disabled={createAction.busy}>{createAction.busy ? "Adding..." : "Add contract"}</Button>
      </Stack>
      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
      <TableContainer sx={{ mb: 3, overflowX: "auto" }}>
        <Table size="small">
          <TableHead><TableRow><TableCell>Name</TableCell><TableCell>Tenants</TableCell><TableCell>Desired apps</TableCell><TableCell></TableCell></TableRow></TableHead>
          <TableBody>{contracts.map((contract) => (
            <TableRow key={contract.id}>
              <TableCell>{contract.name}</TableCell><TableCell>{contract.tenantCount}</TableCell><TableCell>{contract.desiredAppCount}</TableCell>
              <TableCell>
                <Stack direction="row" spacing={1}>
                  <Button size="small" onClick={() => { setLastAction("plan"); setPlan(null); void planAction.run(contract.id); }} disabled={planAction.busy}>{planAction.busy ? "Loading plan..." : "Preview plan"}</Button>
                  {canManage && (
                    <Button size="small" onClick={() => setManagingId(managingId === contract.id ? null : contract.id)}>
                      Manage apps
                    </Button>
                  )}
                </Stack>
              </TableCell>
            </TableRow>
          ))}</TableBody>
        </Table>
      </TableContainer>
      {managingId && (() => {
        const contract = contracts.find((c) => c.id === managingId);
        if (!contract) return null;
        const visible = templates.filter((t) => t.hasPackage || showNoPackage);
        return (
          <Box sx={{ mb: 3 }}>
            <Typography variant="h6" component="h3" gutterBottom>Manage apps -- {contract.name}</Typography>
            <FormControlLabel
              control={<Switch checked={showNoPackage} onChange={(e) => setShowNoPackage(e.target.checked)} />}
              label="Show templates without a package"
              sx={{ mb: 1 }}
            />
            <Stack spacing={1}>
              {visible.map((t) => (
                t.hasPackage ? (
                  <FormControlLabel
                    key={t.id}
                    control={
                      <Checkbox
                        checked={contract.desiredAppIds.includes(t.id)}
                        disabled={pendingIds.has(t.id)}
                        onChange={(e) => void toggle(contract.id, t.id, e.target.checked)}
                      />
                    }
                    label={t.displayName}
                  />
                ) : (
                  <Stack key={t.id} direction="row" spacing={1} sx={{ alignItems: "center" }}>
                    <Checkbox checked={false} disabled aria-label={t.displayName} />
                    <Typography variant="body2">{t.displayName}</Typography>
                    <Box component="label" sx={{ cursor: "pointer" }}>
                      <Chip
                        size="small"
                        color="warning"
                        label="So close! Attach a package to unlock →"
                        sx={{ cursor: "pointer" }}
                      />
                      <input
                        type="file"
                        accept=".intunewin"
                        hidden
                        aria-label={`Upload package for ${t.displayName}`}
                        onChange={(e) => {
                          const file = e.target.files?.[0];
                          e.target.value = "";
                          if (file) void uploadFromChip(t.id, file);
                        }}
                      />
                    </Box>
                  </Stack>
                )
              ))}
              {visible.length === 0 && (
                <Typography variant="body2" color="text.secondary">No templates yet.</Typography>
              )}
            </Stack>
          </Box>
        );
      })()}
      {plan && (
        <Box>
          <Typography variant="h6" component="h3" gutterBottom>Reconcile plan (dry run)</Typography>
          <TableContainer sx={{ overflowX: "auto" }}>
            <Table size="small">
              <TableHead><TableRow><TableCell>Tenant</TableCell><TableCell>Template</TableCell><TableCell>Action</TableCell></TableRow></TableHead>
              <TableBody>
                {plan.map((item) => (
                  <TableRow key={`${item.tenantId}-${item.templateId}`}>
                    <TableCell>{item.tenantName}</TableCell><TableCell>{item.templateName}</TableCell>
                    <TableCell><Chip size="small" label={item.action} color={planActionColor(item.action)} /></TableCell>
                  </TableRow>
                ))}
                {plan.length === 0 && <TableRow><TableCell colSpan={3}><Typography variant="body2" color="text.secondary">Nothing to do.</Typography></TableCell></TableRow>}
              </TableBody>
            </Table>
          </TableContainer>
        </Box>
      )}
    </Box>
  );
}
```

Note on the disabled no-package row: it renders a plain `<Checkbox disabled aria-label={t.displayName} />` (not wrapped in `FormControlLabel`) precisely so the accessible name comes from `aria-label` alone and doesn't collide with a `FormControlLabel`-computed name -- this keeps `getByRole("checkbox", { name: "Zoom" })` unambiguous between the disabled-row checkbox and any other "Zoom"-labeled control in the test in Step 1.

The quest-chip literal must stay ASCII in the *string literal* per this repo's `CLAUDE.md` convention -- `"So close! Attach a package to unlock →"` uses a `→` escape (which compiles to the arrow character) rather than a literal non-ASCII byte in the source file. (This is a TSX/JS string, not a C# string literal, so the mis-decoding bug the convention describes doesn't technically apply here -- but using the escape costs nothing and keeps the file scanning as pure ASCII like the rest of the codebase.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd web && npx vitest run src/components/Contracts.test.tsx`
Expected: PASS (all cases, including the two new "Manage apps" gating cases, the default-filter/switch case, both toggle-direction cases, the independent-concurrency case, the error case, and the quest-chip upload case).

- [ ] **Step 5: Run the full frontend build and test suite to check for regressions**

Run: `cd web && npm run build && npx vitest run`
Expected: `tsc -b && vite build` succeeds with no type errors (this is what catches any drift between `App.tsx`'s `<Contracts me={me} />` call from Task 2 and this task's new `Contracts` signature); full test suite passes.

- [ ] **Step 6: Commit**

```bash
git add web/src/components/Contracts.tsx web/src/components/Contracts.test.tsx
git commit -m "Add Manage apps checklist to Contracts with quest-chip package nudge"
```

---

## After all tasks: adversarial review

Per this session's working agreement, get one adversarial review pass (Sol/high) before merging, focused specifically on:
- The two new mutating endpoints (`AddDesiredApp`/`RemoveDesiredApp`) -- idempotency, the `IsSystemAdmin` gate, and that `NotFound` is returned before any mutation happens.
- The checklist's concurrency handling -- the `pendingIds` `Set` update must use the functional `setPendingIds(prev => ...)` form everywhere (never a stale closure over `pendingIds` directly), and a failed toggle must leave `desiredAppIds` untouched rather than optimistically flipping state that then needs reverting.

Verify every finding against the actual code before trusting it, per this session's established practice.

### Review outcome

- [x] Sol/high review found the original one-to-many EF mapping contradicted the spec. Corrected it
  with `ContractDesiredApps`, preserved existing assignments, and added a two-contract regression test.
- [x] Added server-side package-readiness enforcement and system-admin gates for package create/upload.
- [x] Replaced whole-DTO toggle updates with targeted membership merges; guarded older list/plan
  responses and reconciled ambiguous post-commit failures.
- [x] Added per-template upload pending state, keyboard activation, inline table-row placement, and
  explicit template-load error/empty-filter states.
- [x] Two Sol/high re-review rounds completed; the final scoped residual pass returned CLEAN.
- [x] Full gate: `dotnet build` clean, 136/136 backend tests, production web build, and 178/178
  frontend tests. The test timeout moved from 10 to 20 seconds after direct reproduction showed
  interaction-heavy MUI tests passed alone/bounded but narrowly timed out only under full parallel load.
