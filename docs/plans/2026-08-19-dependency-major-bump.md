# Dependency Major-Version Bump Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring web (React/Vite/TypeScript) and non-runtime-locked NuGet dependencies up to their latest stable major versions, keeping the app building and all tests green.

**Architecture:** Two independent upgrade tracks (npm in `web/`, NuGet in the solution), each done package-by-package or in small logical groups, with a build+test checkpoint after every group so a regression is traceable to one change. No app code redesign — only what's needed to satisfy the new major versions' breaking changes.

**Tech Stack:** React 18->19, Vite 5->8, TypeScript 5->7, @vitejs/plugin-react 4->6, @types/react(-dom) 18->19; NuGet: Microsoft.Identity.Client, Swashbuckle.AspNetCore, WireMock.Net, xunit, xunit.runner.visualstudio, coverlet.collector, Microsoft.NET.Test.Sdk, Scriban.Signed, SQLitePCLRaw.lib.e_sqlite3, Microsoft.Extensions.Logging.Abstractions/Http/Options.

**Spec:** N/A (dependency-upgrade task defined via user conversation, not a written spec doc)

## Global Constraints

- Stay on `net8.0` TFM for all 7 .NET projects — EF Core / ASP.NET Core packages (`Microsoft.EntityFrameworkCore*`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.AspNetCore.*`) are runtime-locked to net10.0 at their latest version and are explicitly OUT OF SCOPE for this pass (deferred to a future net8.0->net10.0 runtime migration).
- No AI attribution in commits (repo convention).
- ASCII-only C# string literals (repo convention).
- Work happens on a feature branch (`chore/dependency-bump`), never directly on `main`; confirm with user before push/merge.
- `dotnet build` + `dotnet test` (52+ tests) and `cd web && npm run build` must be green at the end of each task.

---

## File Structure

- Modify: `web/package.json` — bump React, Vite, TypeScript, plugin-react, @types/react(-dom) majors.
- Modify: `web/src/**` — only touched if React 19 / TS 7 breaking changes require code fixes (exact files TBD by what the build/lint surfaces — no changes made speculatively).
- Modify: `src/PartnerCenterBridge.PartnerCenter/PartnerCenterBridge.PartnerCenter.csproj` — `Microsoft.Identity.Client`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`.
- Modify: `src/PartnerCenterBridge.Graph/PartnerCenterBridge.Graph.csproj` — `Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Abstractions`.
- Modify: `src/PartnerCenterBridge.Exchange/PartnerCenterBridge.Exchange.csproj` — `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`.
- Modify: `src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj` — `Swashbuckle.AspNetCore` only (JwtBearer/OpenApi/DataProtection.Extensions stay pinned, see Global Constraints).
- Modify: `tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj` — `coverlet.collector`, `Microsoft.NET.Test.Sdk`, `Scriban.Signed`, `SQLitePCLRaw.lib.e_sqlite3`, `WireMock.Net`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Abstractions` (`Microsoft.EntityFrameworkCore.Sqlite` stays pinned).

---

### Task 1: npm major bump (web/)

**Files:**
- Modify: `web/package.json`
- Test: existing `web` build/typecheck (no dedicated test suite in `web/`)

**Interfaces:** N/A (leaf task, no downstream code depends on this)

- [ ] **Step 1: Create the branch**

```bash
git checkout -b chore/dependency-bump
```

- [ ] **Step 2: Bump the npm packages to latest major**

```bash
cd web
npm install react@19 react-dom@19 @types/react@19 @types/react-dom@19 @vitejs/plugin-react@6 typescript@7 vite@8
```

- [ ] **Step 3: Build and typecheck**

```bash
npm run build
```

Expected: FAILS the first time — React 19 removed `React.FC` implicit `children`, changed some `ReactDOM` entry points (e.g. `react-dom/client` `createRoot` usage, which this app likely already uses under React 18 anyway), and TS 7 tightens some inference. Read the actual errors; do not guess ahead of them.

- [ ] **Step 4: Fix whatever `npm run build` actually reports**

Go file-by-file through the compiler/build errors. Common React 19 fixes if they show up: drop implicit-children typing on function components that don't explicitly declare a `children` prop, update any `ReactDOM.render` call to `createRoot(...).render(...)` if not already, check `useRef` calls that relied on the old default generic (React 19 requires an explicit initial value argument). Re-run `npm run build` after each fix until it passes clean.

- [ ] **Step 5: Manually smoke-test the app**

```bash
npm run dev
```

Open the app in a browser, confirm the login flow and at least one main screen render without console errors, then stop the dev server.

- [ ] **Step 6: Commit**

```bash
git add web/package.json web/package-lock.json
git commit -m "chore(web): bump react, vite, typescript to latest major"
```

If Step 4 required source changes, include those files in the same commit (or a second commit — whichever keeps `git log` readable given how many files were actually touched).

---

### Task 2: NuGet non-runtime-locked package bump

**Files:**
- Modify: `src/PartnerCenterBridge.PartnerCenter/PartnerCenterBridge.PartnerCenter.csproj`
- Modify: `src/PartnerCenterBridge.Graph/PartnerCenterBridge.Graph.csproj`
- Modify: `src/PartnerCenterBridge.Exchange/PartnerCenterBridge.Exchange.csproj`
- Modify: `src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj`
- Modify: `tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj`

**Interfaces:** N/A (leaf task, independent of Task 1)

- [ ] **Step 1: Bump each package via `dotnet add package`**

```bash
dotnet add src/PartnerCenterBridge.PartnerCenter/PartnerCenterBridge.PartnerCenter.csproj package Microsoft.Identity.Client -v 4.87.0
dotnet add src/PartnerCenterBridge.PartnerCenter/PartnerCenterBridge.PartnerCenter.csproj package Microsoft.Extensions.Logging.Abstractions -v 10.0.11
dotnet add src/PartnerCenterBridge.PartnerCenter/PartnerCenterBridge.PartnerCenter.csproj package Microsoft.Extensions.Options -v 10.0.11
dotnet add src/PartnerCenterBridge.Graph/PartnerCenterBridge.Graph.csproj package Microsoft.Extensions.Http -v 10.0.11
dotnet add src/PartnerCenterBridge.Graph/PartnerCenterBridge.Graph.csproj package Microsoft.Extensions.Logging.Abstractions -v 10.0.11
dotnet add src/PartnerCenterBridge.Exchange/PartnerCenterBridge.Exchange.csproj package Microsoft.Extensions.Logging.Abstractions -v 10.0.11
dotnet add src/PartnerCenterBridge.Exchange/PartnerCenterBridge.Exchange.csproj package Microsoft.Extensions.Options -v 10.0.11
dotnet add src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj package Swashbuckle.AspNetCore -v 10.2.3
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package coverlet.collector -v 10.0.1
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package Microsoft.NET.Test.Sdk -v 18.9.0
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package Scriban.Signed -v 7.2.6
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package SQLitePCLRaw.lib.e_sqlite3 -v 3.53.3
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package WireMock.Net -v 2.15.0
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package xunit -v 2.9.3
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package xunit.runner.visualstudio -v 4.0.0
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package Microsoft.Extensions.Http -v 10.0.11
dotnet add tests/PartnerCenterBridge.Tests/PartnerCenterBridge.Tests.csproj package Microsoft.Extensions.Logging.Abstractions -v 10.0.11
```

- [ ] **Step 2: Build the solution**

```bash
dotnet build PartnerCenterBridge.sln
```

Expected: may fail on `WireMock.Net` 1.x->2.x (known breaking API changes in mock builder syntax) or `xunit.runner.visualstudio` 2->4 (test discovery). Read actual errors before fixing.

- [ ] **Step 3: Fix whatever the build actually reports**

Only touch what the compiler flags. Do not preemptively rewrite WireMock/xunit usage that already compiles.

- [ ] **Step 4: Run the full test suite**

```bash
dotnet test PartnerCenterBridge.sln
```

Expected: all 52+ tests pass. If WireMock.Net 2.x changed runtime mock-matching behavior (not just API surface), a test may fail rather than fail to compile — fix the specific failing test/mock setup, re-run until green.

- [ ] **Step 5: Commit**

```bash
git add -A -- src tests
git commit -m "chore: bump non-runtime-locked NuGet packages to latest major"
```

---

### Task 3: Final verification and handoff

**Files:** none (verification only)

**Interfaces:** Consumes: Task 1 and Task 2 both merged into `chore/dependency-bump`.

- [ ] **Step 1: Full clean build of both stacks**

```bash
dotnet build PartnerCenterBridge.sln
dotnet test PartnerCenterBridge.sln
cd web && npm run build
```

All three must be green.

- [ ] **Step 2: Report to user**

Summarize what moved (old version -> new version per package), what broke and how it was fixed, and confirm the six Dependabot alerts / four npm audit findings from the original request are still unaddressed (out of scope, called out separately). Ask whether to push the branch and open a PR, per repo convention (feature branch -> PR -> merge commit, user confirms before push/merge).
