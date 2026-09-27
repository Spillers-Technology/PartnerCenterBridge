# Desktop Workbench implementation review

The v0.9.2 branch is based on the local v0.9.1 workbench commit, not merged into main. The
v0.9.1 branding, tray, dependency setup, and direct tenant sign-in stay in that earlier commit.

## Host design

The existing API startup, embedded SPA, SQLite migrations, protected local data directory,
operator authorization, and token services remain the application. The new
`src/PartnerCenterBridge.Desktop` WPF project adds a WebView2 window and calls
`Program.RunAsync` in-process. Controller discovery explicitly uses the API assembly. There is
no new frontend, Graph implementation, credential store, or JavaScript RPC layer.

The desktop host probes an available IPv4/IPv6 loopback port. It prefers its previous available
port (saved as `desktop-port`) to preserve origin-scoped browser state. The API validates its
listeners, supplies the canonical URL through `DesktopHostSession`, and answers a health check
before WebView2 navigates. There is a short interval between probing and binding: an intervening
port collision produces a startup error and requires relaunch, rather than silently binding
elsewhere. A startup deadline prevents an indefinite loading screen.

Normal launch is a Windows GUI executable. Explicit `--browser`, `--no-browser`, `--port`,
`--listen`, or administrative commands select the existing API/CLI startup instead. The CLI
preserves redirected output. PowerShell scripts should use `Start-Process -Wait -PassThru` for
exit codes, because a GUI executable is not automatically awaited like a console program.

A named mutex scoped to the current Windows user prevents a second GUI host. A second launch
focuses the existing window. CLI commands bypass the GUI mutex. Closing the window disposes
WebView2, requests ASP.NET shutdown, and waits up to ten seconds for the host.

## WebView and security boundaries

The WebView profile is under the normal protected workbench data root. Navigation within the
window must remain on the API's HTTP host and port. External HTTP/HTTPS/mailto links open with
the system handler; other schemes are refused. Popups follow the same rule. Normal exports,
downloads, clipboard operations, and SPA routes use WebView2's browser behavior.

Microsoft acquisition stays in `DirectTenantConnection`/MSAL's system-browser flow, so credential
entry and MFA remain with Microsoft. The request initiated by React stays pending while sign-in
finishes, then refreshes connection state. The WPF project never sees Microsoft credentials or
tokens. Existing local-auth, origin, loopback, tenant-grant, and encrypted-storage rules still
apply. Native startup errors show a stage, exception type, and log location without dumping raw
exception messages or secrets.

`ITokenProvider` is the current token seam. Direct delegated connections are tenant/operator
specific; SAM remains a separate fallback. Capability-aware connection selection, direct
certificate connections, and a separable WAM provider are follow-up items in `ROADMAP.md`.

## Packaging and changed boundaries

`scripts/publish-local.ps1` publishes the WPF project with `PcbLocalWorkbench=true` and
`PcbDesktop=true`. WPF/.NET and native dependencies use single-file extraction, with trimming
disabled. The API assembly keeps its own name inside the desktop bundle. WebView2 reference
XML files and the unused API runtimeconfig are removed from the publish directory.

The resulting local unsigned build is one `PartnerCenterBridge.exe`, approximately 106.8 MiB.
The installed Evergreen WebView2 Runtime is an external prerequisite, not part of that file.
See [Microsoft's distribution guidance](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution).
The release workflow signs the desktop executable, smoke-tests it, and packages it with LICENSE
and README-FIRST. All three product version files are checked against the release tag. Server
containers continue to build the API and web projects independently.

The project, API startup/hosting handoff, solution, publish and smoke scripts, Windows CI/release
jobs, versions, release notes, README, website/getting-started/local-workbench/architecture docs,
and authentication roadmap are the affected areas. No database model or migration changes were
needed. Implementation is kept in two local commits: v0.9.1 workbench, then v0.9.2 desktop host.

## Validation and remaining manual checks

Local validation completed:

- Full .NET suite: 382 passed, 7 skipped.
- Full web suite: 300 passed.
- Self-contained single-file Windows desktop publish.
- Desktop smoke: redirected version output, native window, healthy loopback API, SQLite,
  initialized WebView2 child process, second-launch handoff, graceful window shutdown, and
  available-origin reuse after restart.
- CLI smoke: health, Local profile, embedded SPA deep-link fallback, loopback-only listeners,
  and SQLite persistence after restart.
- Website image/overflow checks at 1440, 390, and 320 pixels in both themes.

Exact commands: `dotnet build PartnerCenterBridge.sln -c Release --no-restore -v quiet`
(0 errors, 37 existing test warnings), `dotnet test PartnerCenterBridge.sln -c Release --no-restore -v quiet`,
`npm test -- --run` and `npm run build` in `web`, `./scripts/publish-local.ps1 -SkipSpaBuild`
with the existing built SPA, `./scripts/smoke-local.ps1`, `./scripts/smoke-desktop.ps1`, and
`node docs/scripts/check-site-brand.mjs` with `PLAYWRIGHT_NODE_MODULES` set to the temporary
Playwright installation. The final TypeScript/Vite build passed.

Security assumes a protected Windows user account and the existing private data-root ACLs and
DPAPI-protected key ring. No-account mode continues to grant the local operator instance-owner
access through the existing one-time launch ticket; it does not bypass tenant grants. Remaining
1.0 readiness work includes live account/consent/reconnect tests, clean-machine Windows/signing
validation, and the separately tracked SAM generation-safe rotation/partner-reconnect work.

The Windows smoke scripts are now part of CI and run against the signed executable during a
release. GitHub-hosted CI, Azure signing, container builds, Windows 10/clean-machine runtime
installation, passkeys, exports/download dialogs, clipboard interaction, and live Microsoft
tenant sign-in still need external/manual validation. Live tenant testing requires a configured
multitenant public-client app ID and tenant consent. The local preview is unsigned; no push,
merge, tag, or release publication has been performed.

## Microsoft connection UX follow-up

Microsoft connections is now the primary direct-tenant entry in Settings and at the top of
Tenants. The old source-checkout bootstrap command is removed from the app. Partner Center/GDAP,
manual tenant registration, and manual partner token recovery are collapsed optional controls.
Unconfigured optional partner credentials no longer appear as mandatory Home onboarding. The
landing page focuses its gallery on administration rather than account authentication features.

The intended distributor model is one publisher-owned multitenant public client, like Graph
PowerShell's Microsoft-owned default registration. `publish-local.ps1 -MicrosoftClientId` embeds
the public ID as API assembly metadata. The release workflow requires the release variable
`MICROSOFT_SIGN_IN_CLIENT_ID`. Technicians sign in and consent per customer; they do not create
an application per customer. The publisher registration has now been created and bundled;
see `docs/microsoft-publisher.md` for its public identity and permission inventory.

For optional custom deployments, an in-app setup endpoint saves a protected application ID and
applies it immediately. Setup requires local loopback hosting and the instance credentials role.
The saved setting overrides configuration, which overrides the bundled publisher ID. Changes are
serialized with account acquisition/cache writes and cannot switch client IDs while account
caches exist. No password, client secret, or raw Microsoft token enters the setup form.

Follow-up validation: 21 direct-auth/local-host tests, 54 targeted web regression tests plus two
new onboarding/settings tests, TypeScript/Vite build, desktop and CLI publish smokes, and website
image/overflow checks passed. Refreshed tenant/connection screenshots passed 390px overflow and
default-hidden recovery checks. Live Microsoft sign-in/consent still needs operator validation.
