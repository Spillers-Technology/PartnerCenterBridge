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
dotnet test PartnerCenterBridge.sln        # 52+ tests, no live tenant needed (WireMock)
cd web && npm run build                    # tsc -b && vite build
```

EF Core migrations (needs the Api project as `--startup-project`; it's the one with
`Microsoft.EntityFrameworkCore.Design`):

```bash
dotnet ef migrations add <Name> --project src/PartnerCenterBridge.Data --startup-project src/PartnerCenterBridge.Api
```

## Release checklist

Release process is **manual** -- there are no GitHub Actions workflows. Bumping this list is
itself a candidate for automation later; until then, work through it by hand:

1. Bump `web/package.json` version.
2. `dotnet build` + `dotnet test` + `cd web && npm run build` all green.
3. Build and push both images, tagged `vX.Y.Z` and `latest`:
   ```bash
   docker build -t ghcr.io/spillers-technology/partnercenterbridge-api:vX.Y.Z -f src/PartnerCenterBridge.Api/Dockerfile .
   docker build -t ghcr.io/spillers-technology/partnercenterbridge-web:vX.Y.Z ./web
   # tag :latest too, docker push both tags for both images
   ```
   `docker login ghcr.io` uses `gh auth token` (needs `write:packages`).
4. `git tag vX.Y.Z` and `gh release create`.
5. **Update the GitHub Pages docs site** (`docs/`, plain static HTML, no build step) so it matches
   what actually shipped:
   - `docs/index.html`: the release badge/version string in the hero, and the `og:` meta if a
     headline claim changed.
   - Whichever of `getting-started.html` / `architecture.html` / `authentication.html` /
     `workflows.html` / `sam-bootstrap.html` / `deployment.html` covers what changed. New user-facing
     capability (a new auth mode, a new workflow, a new deploy target) gets its own paragraph, not
     just a changelog mention -- these pages are supposed to describe the *current* app, not a
     history of it.
   - If a new screen shipped, regenerate the screenshots: `node docs/scripts/capture-product-media.mjs`
     (mocked API data, no live backend or tenant needed), then check the new files into
     `docs/assets/screenshots/`.
   - Nav links: every `docs/*.html` file has its own hardcoded `<nav class="topnav">` (no shared
     header include) -- a new page needs a `nav-link` added to *all of them*, not just its own.
6. Confirm the docs-site changes actually publish (GitHub Pages serves `main:/docs`; a push is
   enough, no separate deploy step).

## Things to re-verify before trusting them

- `deploy/base/` + `deploy/overlays/production/` in this repo are a **template** (placeholder
  `example.com` host, placeholder `postgres.databases.svc`), not a live deployment. If asked to
  stand this up in `homelab_ac`, that means authoring a *new* `apps/partnercenterbridge/` there
  following the CNPG-per-app pattern other apps use (see that repo's `anchordesk`/`guacamole`
  app folders), not assuming this repo's `deploy/` is already wired to anything.
- `Auth:Mode=Local`'s tenant-access model has exactly one privileged flag
  (`ITenantAccessService.IsSystemAdmin`). It gates instance-wide configuration such as
  `/api/admin/sam/*`, app-template authoring/package replacement, and contract desired-state
  membership. If you're about to use it to bypass a per-tenant role, stop -- that's the exact
  muddle this model was built to avoid. Tenant power comes from `TenantAccessGrant` alone.
