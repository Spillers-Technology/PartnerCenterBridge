# CLAUDE.md

Guidance for Claude Code (or any agent) working in this repository.

## What this is

A two-part MSP bridge (ASP.NET Core 8 API + React/Vite/TS SPA) fronting Microsoft Graph and the
Partner Center REST API. See [README.md](README.md) for the architecture summary and
[docs/architecture.html](docs/architecture.html) / [docs/authentication.html](docs/authentication.html)
for the long version.

## Conventions

- **No AI attribution anywhere.** Commits and PR bodies must not include `Co-Authored-By` lines or
  other robot footers.
- **Feature branches, PR, merge commit.** `feat/*` branch -> PR -> merge commit -> delete branch.
  Confirm with the user before merging or pushing; don't do it unilaterally unless they've said so
  for the current session.
- **Keep string literals ASCII-only.** Non-ASCII characters (em-dashes, curly quotes) in an actual
  C# string literal have previously been mis-decoded by the compiler on this project's toolchain
  and shipped as mojibake in the UI. Comments (`//`, `///`) are unaffected and freely use en/em
  dashes elsewhere in this codebase -- the rule is about compiled string literals specifically.
- **Local Docker is disposable.** OK to `docker rm -f` / prune local containers and volumes to free
  ports during testing; nothing running locally is a system of record.

## Build / test

```bash
dotnet build PartnerCenterBridge.sln
dotnet test PartnerCenterBridge.sln        # ~160 tests, no live tenant needed (WireMock)
cd web && npx vitest run                   # ~180 SPA component tests
cd web && npm run build                    # tsc -b && vite build
```

EF Core migrations (needs the Api project as `--startup-project`; it's the one with
`Microsoft.EntityFrameworkCore.Design`):

```bash
dotnet ef migrations add <Name> --project src/PartnerCenterBridge.Data --startup-project src/PartnerCenterBridge.Api
```

Every model change needs a migration for **both** providers: the Postgres set above and the SQLite
set used by the Local Workbench (its own design-time factory, so it is its own startup project):

```bash
dotnet ef migrations add <Name> --project src/PartnerCenterBridge.Data.Sqlite --startup-project src/PartnerCenterBridge.Data.Sqlite
```

Local Workbench single-file build (`artifacts/local/win-x64/PartnerCenterBridge.exe`, needs Node for
the SPA): `./scripts/publish-local.ps1`. Run it with `--help` for the CLI (`doctor`, `--port`, ...).

## Release checklist

Release process is **automated** from a pushed tag. Three workflows exist:

- `.github/workflows/ui-overflow.yml` -- mobile overflow capture matrix (Playwright at phone
  widths against the mocked dev server). Runs on `pull_request`/`push` to `main`.
- `.github/workflows/ci.yml` -- the PR/`main` gate (`dotnet`, `web`, `local-workbench`,
  `docker` jobs; see the header comment there for what each checks). Also declared
  `workflow_call:` so `release.yml` can run the exact same gate against a tag, instead of a
  second copy that can drift from it.
- `.github/workflows/release.yml` -- triggered by pushing a tag `vX.Y.Z`, or manually via
  `workflow_dispatch` with `dry_run: true` (the default) to prove the pipeline without
  publishing anything. Jobs, in order:
  1. `verify` -- fails unless the tag equals `v<web/package.json version>` and
     `v<csproj Version>`.
  2. `ci` -- calls `ci.yml` as a reusable workflow.
  3. `windows` (`environment: release`) -- publishes the Local Workbench, signs
     `PartnerCenterBridge.exe` with Azure Artifact Signing (fails closed if signing isn't
     configured; see `docs/code-signing.md`), smoke-tests the signed exe
     (`scripts/smoke-local.ps1`, the same script `ci.yml`'s `local-workbench` job uses), and
     packages `PartnerCenterBridge-vX.Y.Z-win-x64.zip` (exe + `LICENSE` + `README-FIRST.txt`)
     plus its `.sha256`.
  4. `images` -- builds and pushes both container images to GHCR, tagged `vX.Y.Z` and `latest`
     (not `latest` for a prerelease tag containing `-`). Skips the push on a dry run.
  5. `publish` -- creates the GitHub release from `releases/vX.Y.Z.md` (fails if that file is
     missing), attaches the zip and checksum, marks it prerelease if the tag contains `-`, then
     downloads the published assets back and re-checks the checksum. Skipped on a dry run.
  6. `pages` -- builds the public docs site (`scripts/build-site.sh`: `docs/*.html`,
     `docs/styles.css`, `docs/assets/**` except the mobile capture-matrix working directory,
     `docs/.nojekyll`) and deploys it via `actions/deploy-pages`. Skipped on a dry run.
  `.github/workflows/pages.yml` runs the same site build and deploy on its own
  (`workflow_dispatch` only), for a doc-only fix that doesn't warrant a full release.

The steps that are still yours:

1. Branch `release/vX.Y.Z` (or a normal feature branch/PR is fine too): bump `web/package.json`
   version (`npm version X.Y.Z --no-git-tag-version` in `web/` covers the lockfile too) and
   `<Version>` in `src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj`, and write
   `releases/vX.Y.Z.md` (operator-facing notes -- see `releases/v0.9.0.md` for the shape).
2. **Update the GitHub Pages docs site** (`docs/`, plain static HTML, no build step) so it
   matches what actually shipped: the version string and any headline claim in `docs/index.html`,
   whichever of `getting-started.html` / `architecture.html` / `authentication.html` /
   `workflows.html` / `sam-bootstrap.html` / `deployment.html` covers what changed (new
   user-facing capability gets its own paragraph, not just a changelog mention), regenerated
   screenshots if a new screen shipped (`node docs/scripts/capture-product-media.mjs`), and both
   the sidebar and footer "Docs" nav lists in *every* `docs/*.html` file if a page was added.
3. Open the PR; CI (`ci.yml` + `ui-overflow.yml`) must be green.
4. Operator merges the PR to `main`.
5. Push the tag from `main`: `git tag vX.Y.Z && git push origin vX.Y.Z`. This is what starts
   `release.yml` and does everything listed above.
6. Once it finishes, verify the result: download the release zip and check its signature
   (`Get-AuthenticodeSignature`) and checksum (`sha256sum -c` or `Get-FileHash`), confirm both
   `docker pull ghcr.io/spillers-technology/partnercenterbridge-{api,web}:vX.Y.Z` work, and open
   the docs site to confirm the Pages deploy landed.

Before the first tag under this pipeline: **Settings > Pages > Source** must be set to
**"GitHub Actions"** (it now deploys from `release.yml`/`pages.yml`, not `main:/docs`), and the
`release` GitHub environment needs its Artifact Signing variables/secrets set up per
`docs/code-signing.md`. Both are one-time, done outside this repo's files.

## Things to re-verify before trusting them

- `deploy/base/` + `deploy/overlays/production/` in this repo are a **template** (placeholder
  `example.com` host, placeholder `postgres.databases.svc`), not a live deployment. If asked to
  stand this up in `homelab_ac`, that means authoring a *new* `apps/partnercenterbridge/` there
  following the CNPG-per-app pattern other apps use (see that repo's `anchordesk`/`guacamole`
  app folders), not assuming this repo's `deploy/` is already wired to anything.
- `Auth:Mode=Local` has two deliberately separate authorization planes. Fixed instance roles are
  resolved through `IInstanceAccessService` for shared configuration (role delegation, SAM,
  catalog authoring, MCP policy, and tenant onboarding). Per-tenant Viewer/Operator/Owner power is
  resolved through `ITenantAccessService` from `TenantAccessGrant`. Neither plane may satisfy a
  check in the other; even an Administrator needs an explicit tenant grant.
