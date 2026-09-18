# Spec — Win32 app lifecycle ownership (package generation, versioning, in-place update)

**Status:** draft r3. Phase A implemented; round-4 findings closed in §14.2 pending re-review. 0 and C1 unblocked by r3 decisions (§13). Written as the contract first, per STD-001.
**Review history:** r1 adversarially reviewed by Codex `gpt-6-astra` (high), 2026-09-10 -- 11
findings, all adjudicated and accepted after reproducing each code claim against source (§12).
r2 re-reviewed by the same model: 5 of 11 r1 findings fully resolved, 6 partially; 10 new
findings. Verdict: A ready, 0 and C1 blocked, the rest deferrable to B/C2. r3 makes the decisions
that unblock 0 and C1 and records the rest as open (§13).
r2 also reorganizes §6 around the operator's framing that PCB must handle apps it did **not**
create, which the r1 design treated as an edge state.
**Decision record:** corporate-strategy `D-0043` (board review deliberately declined).
**Scope owner:** PartnerCenterBridge.

## 1. What changes

Today PCB consumes a `.intunewin` produced elsewhere. `README.md` states this as a boundary:

> `.intunewin` packages must be produced by the Microsoft **Win32 Content Prep Tool**
> (`IntuneWinAppUtil.exe`) — the bridge consumes them, it doesn't repackage.

That boundary is being removed. After this work, an operator can go from "I have an installer" to
"it is in Company Portal in every tenant on the contract" without leaving PCB, and can update it
in place afterwards.

The operator lifecycle, end to end:

1. Tenant is already onboarded (SAM + GDAP). **Unchanged** — no new auth surface.
2. Create an app. Name, publisher, description.
3. Upload install media (MSI or EXE, plus any supporting files).
4. Optionally author a **single** install script, edited in-app in a plain text editor.
5. Declare detection + assignment (groups, `Available` for Company Portal / `Required`).
6. PCB **builds** the `.intunewin` itself and deploys it to every tenant on the contract.
7. Later: edit the media or the script, PCB rebuilds and pushes a new content version —
   but only where PCB can still account for what is actually deployed (§6).

## 2. Why this is not a gRPC/PowerShell daemon

`IntuneWinAppUtil.exe` is a thin wrapper over a documented format. `IntuneWinPackageReader`
already **decodes** that format in this repo: outer zip, `IntuneWinPackage/Metadata/Detection.xml`,
`IntuneWinPackage/Contents/IntunePackage.intunewin`, AES/HMAC values echoed to Graph at commit.
Encoding is the mirror image and is pure .NET — cross-platform, in-process, no Windows, no
PowerShell, no second deployable.

`PwshRunner` exists because Exchange Online V3 has no REST equivalent. Content prep has no such
excuse and does not get the same exemption.

**If** a genuinely Windows-only need appears later (PSADT wrapping, Authenticode signing against a
Windows cert store, vendor extractors), the answer is an **outbound-polling build agent** that
pulls jobs from the queue in §5 — never an inbound gRPC listener. A packaging box sits inside an
ops network; it dials out.

## 3. The format contract (what the writer must produce)

`IIntuneWinPackageWriter` produces a package that this repo's own `IntuneWinPackageReader` can
read back. That round trip is the primary test.

Outer zip (stored/deflate), containing:

- `IntuneWinPackage/Metadata/Detection.xml`
- `IntuneWinPackage/Contents/IntunePackage.intunewin` — the encrypted payload

Payload construction, in order:

1. Zip the source folder (all files, relative paths preserved).
2. Generate a random 256-bit AES key and a random 128-bit IV.
3. Encrypt the zip with **AES-256-CBC, PKCS7 padding**.
4. Generate a random 256-bit HMAC key. Compute **HMAC-SHA256** over `IV || ciphertext`.
5. Emit `MAC || IV || ciphertext` as the payload file.
6. `FileDigest` = SHA-256 of the **unencrypted** zip; `FileDigestAlgorithm` = `SHA256`.

`Detection.xml` carries `Name`, `SetupFile`, `UnencryptedContentSize` (byte length of the
**inner, unencrypted zip**), `FileName` (`IntunePackage.intunewin`), and an `EncryptionInfo`
element with `EncryptionKey`, `MacKey`, `InitializationVector`, `Mac`, `ProfileIdentifier`,
`FileDigest`, `FileDigestAlgorithm`. All binary values are Base64. `ProfileIdentifier` is exactly
`ProfileVersion1`. `Name` and `FileName` are not required by this repo's reader but are read by
Microsoft's own Win32 samples; PCB emits them so its output is a generally consumable
`.intunewin`, not one only PCB can read.

The byte layout in steps 1-6 matches Microsoft's published LOB encryption sample (confirmed in r1
review): `MAC[32] || IV[16] || ciphertext`, HMAC covering everything after the first 32 bytes.

### 3.0 Acceptance test -- the reader round trip is NOT sufficient

`IntuneWinPackageReader` never decrypts, authenticates, or unzips anything. It parses XML strings
and copies an opaque entry; its own tests pass with the literal payload `encrypted-bytes-here` and
3-byte "keys". A writer that emits plaintext, omits the IV header, or MACs the wrong span would
pass a reader round trip. Phase A acceptance therefore requires:

- an **independent** test-side decryptor: verify HMAC over `IV || ciphertext` with the recorded
  MAC key, decrypt with the recorded key/IV, check PKCS7, check SHA-256 of the plaintext against
  `FileDigest`, open the inner zip, and compare entries byte-for-byte to the source folder;
- key/IV/MAC length assertions (32/16/32 bytes decoded);
- corruption tests: a flipped ciphertext byte, a flipped IV byte, and a truncated payload must
  each fail MAC verification;
- a fixture produced by Microsoft's `IntuneWinAppUtil.exe`, decrypted by the same test-side
  decryptor, to prove the decryptor itself agrees with the reference tool.

**Offline tests prove construction, not deployability.** Before the capability is labelled
Stable, one PCB-built package must be committed through Graph to a real tenant and installed on a
real Windows endpoint. Until that has happened, the README must say it has not.

### 3.1 Builds are not reproducible -- and what that does and does not rule out

Every build draws a fresh AES key, IV, and MAC key, so rebuilding the same inputs produces a
**different** artifact with different `EncryptionInfo`. Intune does not care; each tenant gets its
own upload regardless.

What this rules out: comparing **two independent builds** for byte equality, and "rebuild and
compare" as a verification strategy.

What it does **not** rule out (r1 corrected an overreach here): each `PackageBuild` records its own
artifact SHA-256 and its encryption descriptor, and re-hashing a stored artifact against **its
own** recorded digest is a valid integrity check -- it detects a stored package swapped for a
different, internally valid one.

Identity of a version is **not** the source hash either. Changing only the install script leaves
the source hash identical while changing what runs as SYSTEM. Version identity is the
`PackageRelease` in §4: a hash over the source manifest, the recipe version, and the builder
version.

## 4. Data model, and the retention rules that make it affordable

`AppTemplate.Content` (a single embedded `Win32ContentInfo`) is today's model: one template, one
uploaded package, no history. It splits into entities with different economics:

| Entity | What it is | Size | Retention |
|---|---|---|---|
| `PackageRecipe` | install/uninstall commands, the install script, detection + requirement rules. **Immutable per version.** | bytes-KB | **all versions, forever** |
| `PackageSource` | uploaded install media: a manifest of files, each content-addressed by SHA-256 | MB-GB | bound to releases (below) |
| `PackageRelease` | the **logical version**: (source manifest hash, recipe version, builder version) | a row | policy-bounded |
| `PackageBuild` | one produced `.intunewin` for a release, with its own artifact digest | MB-GB | **pure cache** |

`AppTemplate` keeps identity, publisher, assignments, and a pointer to its current release.

### 4.1 The operator's constraint, restated as invariants

> "We are NOT going to have package tracking for every version for every app for every tenant."

PCB's existing shape already avoids the worst axis: artifacts are per **template**, not per
**tenant**. A template on 40 tenants is one artifact streamed 40 times. Nothing here may regress
that into per-tenant artifacts.

r1 found the original R2-R4 conflated two different things -- **evicting a cached build of a
version PCB still promises to recover** vs. **deliberately expiring that promise**. They are
separated now:

- **R1 -- Recipes are the system of record.** Every recipe version is kept, immutable. "Edit the
  script from before" works even after every artifact is gone. Because versions are immutable and
  stored whole, the recipe table is also the content audit (§7).
- **R2 -- A release is either *retained* or *expired*, and that is the only recoverability
  promise.** `Retention:Releases` (default 2: current + one rollback) per template. A retained
  release promises it can be deployed. Expiring a release is an explicit, logged transition that
  relinquishes that promise -- never a side effect of pruning something else.
- **R3 -- Sources are pinned by retained releases.** A source blob is deletable only when no
  retained release references it. Blobs are refcounted across templates (identical media stored once).
- **R4 -- Builds are pure cache.** Any `PackageBuild` of a retained release whose source is
  present may be evicted at any time and regenerated (§3.1: regenerated, not reproduced).
- **R5 -- A release with no source is pinned to its artifact.** Imported legacy packages (§4.2)
  and any release whose media was never supplied have `Rebuildable = false`. Their build is the
  only representation and is not evictable while the release is retained.
- **R6 -- Pins are durable and cover the whole attempt.** A `DeploymentAttempt` (§5.1) pins its
  release before any tenant is touched, including queued targets, retries, and recovery -- not
  just the tenant currently executing. The retention sweep takes the same pin in the same
  transaction as its delete decision.

**Given up, stated plainly for the UI:** you cannot redeploy an arbitrary historical version.
Rollback depth is exactly `Retention:Releases`, and the UI shows that number rather than a
version list that half-works.

### 4.2 Migration of existing templates

Existing templates have a package, not a separately retained source. Each becomes release 1 with
the imported artifact as its only build and `Rebuildable = false` (R5). "You can always rebuild the
current version" is **false** for these until the operator re-uploads the media; the UI says so
on the app rather than implying otherwise.

## 5. Build queue and deployment attempts

`PackageBuild` states: `Queued -> Packing -> Encrypting -> Ready | Failed`.

- Runs in a `BackgroundService`. **This repo has no hosted services today** -- a new pattern here,
  reviewed as such: startup/shutdown ordering, one DI scope per job, cancellation on host stop,
  and what a half-finished build looks like after a crash (answer: `Failed`, scratch swept on
  next start).
- Bounded concurrency (`Packaging:MaxConcurrentBuilds`, default 1). A build needs roughly 2x the
  source size in scratch; unbounded concurrency fills the disk.
- Explicit scratch (`Packaging:ScratchPath`), cleaned on success, failure, and startup.
- Progress is **new work**, not reuse: today `DeploymentOrchestrator` passes `progress: null` and
  the SPA waits for the final response. v1 delivers progress by polling persisted status.

### 5.1 `DeploymentAttempt` -- the durable record the current orchestrator lacks

Today `DeploymentOrchestrator` deploys tenants sequentially and calls `SaveChangesAsync` **once,
after the whole loop**. A crash after tenant A's remote commit but before that save leaves PCB
believing A is still on the old version while A has actually changed. That is a shipped defect (§8).

A `DeploymentAttempt` records: the requesting operator, the **immutable release id**, the explicit
tenant set, per-tenant checkpoints (created / uploaded / committed / patched / assigned), and the
release pin (R6). Each tenant's checkpoint is persisted as it is reached, not batched.

**An ambiguous failure invalidates knowledge.** "Failed" does not prove nothing changed remotely
-- a timeout after `commit` is exactly the case where it did. Any failure after the first remote
write sets that deployment's knowledge to `Unknown` (§6) and queues a re-observation.

## 6. Knowledge -- what PCB can account for, including apps it did not create

### 6.1 The actual problem

A tenant's Win32 apps were not all put there by PCB. Some were uploaded directly in the Intune
portal -- the exact behavior this feature exists to discourage -- and some were created by PCB and
later changed in the portal. Partner Center gives an MSP no cross-tenant view of any of it. The
r1 design treated non-PCB apps as an error state (`Orphaned`/`Unknown`). That was backwards:
**they are the majority case on day one**, and bringing them under account is a large part of the
value -- auditability, convenience, and normalization across tenants.

So knowledge is a **ladder** an app climbs, not a flag on apps PCB already owns.

### 6.2 The ladder

| Rung | What PCB holds | What the operator can do | How an app gets here |
|---|---|---|---|
| **Observed** | the tenant's current config for the app: name, publisher, version, commands, detection + requirement rules, assignments, whether PCB created it, when it last changed | read, compare across tenants, export | automatic -- the inventory sweep (§6.4) |
| **Adopted** | an immutable **baseline** of that config, bound to a template | drift detection; edit **metadata** (commands, detection, assignments) | operator binds an Observed app to a template |
| **Managed** | the above plus a **retained, rebuildable release** (media + recipe) | edit **content**: replace the installer, edit the script, rebuild, push | operator supplies media (§6.5) |

PCB-created apps enter at Managed. Portal-created apps enter at Observed and climb only by
operator action. Nothing climbs automatically: a fingerprint match is a **suggestion**, and binding
is always an explicit, audited operator act.

### 6.3 Freshness is orthogonal to the rung

For Adopted and Managed apps, per deployment:

| State | Meaning |
|---|---|
| `Known` | the latest observation matches this deployment's **last-applied baseline** |
| `Drifted` | the latest observation differs from the baseline -- someone changed it outside PCB |
| `Stale` | no observation within `Drift:FreshnessHours` (default 24) |
| `Unknown` | an ambiguous failure (§5.1) or never observed |
| `Missing` | PCB has a record; the app is gone from the tenant -- needs **recreate**, not update |

Two r1 corrections are built in here:

- **Compare against the per-deployment baseline, never the current template.** Tenant B lagging
  on v1 while A is on v2 is *lag*, not *drift*; comparing B to the v2 template would wrongly mark
  it `Drifted` and push the operator to "reconcile" away a legitimate holdback. The baseline is
  small (config + resolved assignments), immutable, and one per deployment -- it does **not**
  require per-tenant package artifacts.
- **The comparison covers all executable state**, not a subset: install and uninstall command
  lines, every detection rule including PowerShell script content, requirement rules, run-as
  context, and the resolved assignment set. r1 showed the original field list would call an app
  `Known` after someone swapped its PowerShell detection script.

### 6.4 Inventory sweep -- one mechanism, not two

The operator asked for tracking "on a configurable set / interval." The sweep that produces
Observed state is the same sweep that produces freshness, over `Drift:Scope` (by contract, tenant,
or template) on `Drift:Interval`. There is no separate drift checker.

Mechanism (r3, replacing r2's single expanded listing -- `$expand=assignments` on the `mobileApps`
**list** is deprecated by Microsoft): page the Win32 app list per tenant, then fetch each app with
its assignments individually, under bounded per-tenant concurrency.

**Observations carry completeness, and incomplete never means empty.**
- Each app observation records whether its assignment fetch succeeded. A failed fetch is
  `AssignmentsUnavailable`, never "no assignments".
- Each tenant sweep records whether the app listing completed every page. **A partial listing
  can never produce `Missing`**; only a complete listing that lacks the app can.
- An incomplete observation never promotes a deployment to `Known`.

Observations are kept as history, not overwritten. **What that history is, precisely:** a
timestamped record of observed state and the differences between successive observations. It is
**not** an audit trail of every external change -- a change made and reverted between two sweeps
is invisible to it, as is who made it. Claiming full external-change audit would need a separate
event source (e.g. Intune audit logs) with its own established coverage; that is out of scope. Reuse candidate, not a
commitment: Config Snapshots' `IConfigSection` already captures, diffs, and git-syncs tenant
config; a Win32 apps section could share capture code, while the ladder needs structured per-app
state that a snapshot blob alone does not give it.

### 6.5 Adoption does not recover media

Adopting a portal-created app captures its config, never its installer: PCB has no supported way
to download the committed content back out of Intune (§11, to be verified against a real tenant).
An Adopted app becomes Managed only when the operator uploads the media. The UI must not imply
that adoption made an app rebuildable.

### 6.6 The gate

| Action | Requires |
|---|---|
| view, compare, export | Observed |
| edit metadata / assignments | Adopted **and** `Known` |
| edit content (installer, script) and rebuild | Managed **and** `Known` |
| recreate | `Missing`, explicit operator action |

**Authoring is not gated by tenant state.** An operator may create a new release of a template at
any time -- the gate applies to *pushing it to a tenant*, per deployment. One stale tenant does
not freeze the app for the rest; it is simply excluded from the push until it is `Known` again.

**There is no acknowledge-and-push-anyway path.** r1 was right that acknowledgement is consent to
overwrite, not knowledge of what is overwritten -- the r1 proposal in the old §6.3 contradicted
the invariant it claimed to honor. A `Drifted` deployment reaches `Known` by one of two explicit
operator acts, both of which make PCB's record true again:

- **Accept drift** -- the tenant's current config becomes the new baseline (PCB learns it); or
- **Revert** -- PCB re-applies the last-applied baseline. Revert is its **own transition out of
  `Drifted`**, not a normal gated mutation (a normal mutation requires `Known`, which revert is
  trying to establish -- r2 caught that circularity). It starts from a fresh, complete, recorded
  observation of the drifted state, re-observes immediately before writing and aborts if the
  drift changed, applies the baseline, then re-observes to verify.

Only then can a new release be pushed to it.

### 6.7 The residual race, stated honestly

Freshness is not protection against a concurrent portal change. Checked `Known` at 09:00, an admin
removes an assignment at 09:01, PCB pushes at 09:02 and `/assign` restores it.

Mitigations: **re-observe the deployment immediately before mutating** and abort on any diff;
serialize PCB mutations per deployment; reject a sweep result that predates the attempt's own
observation. What remains is the window between that last GET and the write -- seconds, not 24
hours. Whether Graph's `mobileApps` endpoints honor conditional writes (`If-Match`) to close it
entirely is unverified (§11). Until verified, the product's promise is narrowed to: *PCB never
pushes over a change it could have seen*, not *PCB never pushes over any change*.

## 7. Security -- the part that deserves the most review

**Arbitrary SYSTEM execution on every endpoint in every tenant on a contract already exists
today**, through `InstallCommandLine` (editable in the current SPA) and PowerShell detection
rules. r1 corrected the original framing here: requiring an externally produced `.intunewin` was
never a meaningful boundary, since an author can point an inline PowerShell command at any
existing package. This work does not create the capability; it makes it convenient, and it is
the right moment to put the controls around **all** executable inputs, not just the new script
field.

- **Authoring** any executable recipe input (commands, script, detection, requirements) or media
  requires `InstanceRole.CatalogManager`. **Pushing** to a tenant requires tenant `Operator`.
  Neither plane satisfies the other -- `CLAUDE.md`'s two-plane rule holds without exception.
- **Deploys bind to an immutable release.** Today `DeployRequest` is `(TemplateId, TenantIds)` and
  the orchestrator reads whatever the template's content is at execution time, so an Operator who
  reviewed release 1 can end up pushing release 2 if a CatalogManager publishes in between. The
  request carries `ReleaseId` and an explicit tenant set; execution rejects a mismatch.
- **`Known` never authorizes anything.** There is no automatic fan-out. Every push is initiated
  by an Operator on each target tenant, executed on that operator's authority; `Known` is a
  precondition, never a trigger. A worker holding service credentials must not treat tenant state
  as permission.
- **Content audit.** The current `AuditSaveChangesInterceptor` records changed property **names**,
  not values -- "who changed the script" without "to what." Immutable recipe versions (R1) close
  this: each `AuditEvent` references the recipe version created, and the version row holds the
  full content. The same applies to adoption, accept-drift, and revert.
- **Media handling.** PCB never **executes** uploaded media and is not an AV scanner. It does zip
  it, and -- if MSI metadata extraction ships (§8) -- it performs **bounded, read-only parsing** of
  MSI tables. r1 correctly noted the original "never inspected" wording contradicted that; it is
  replaced by this narrower promise.
- **Staging is the trust boundary for packaging** (Phase A code review, §14). The writer refuses
  symbolic links, names Windows would misread (separators, reserved device names, trailing
  dots/spaces, invalid characters), case-insensitive name collisions, and any group- or
  other-writable directory. It does **not** refuse FIFOs, hardlinks, or files swapped by a
  concurrent writer. The managed `FileSystemInfo` API reports a FIFO and a hardlink identically to
  a regular file (probed on .NET 8). Detecting them is possible with `openat`/`O_NOFOLLOW`/`fstat`
  interop on the opened descriptor. That was **deliberately not built**: it's a boundary choice, not
  a runtime impossibility. **Phase B must establish the whole precondition:** a PCB-created
  owner-only (0700) staging directory with trusted, non-replaceable ancestors, populated only by PCB
  writing upload bytes into regular files it creates itself, with no other writer (including same-UID
  processes). The writer's mode-bit check is a partial guard for that, not enforcement of it. Archive
  uploads that PCB unpacks into staging are bounded for expansion ratio and entry count.
- Scratch holds unencrypted customer install media: STD-006 handling posture, deterministic cleanup.

## 8. Defects in shipped code, and problems this work must fix

**Shipped defects, found by r1 and reproduced against source. These affect a capability the
README currently labels Stable, independent of this feature:**

- **D1 -- In-place update does not update the app.** `IntuneWin32Service.DeployAsync` calls
  `BuildWin32App` only on create. For an existing app the PATCH body is `committedContentVersion`
  alone. Changing a template's install command or detection rules and redeploying pushes new
  content under the **old** command and **old** detection -- and an old detection rule that still
  matches v1 makes Intune skip installing v2 entirely.
- **D2 -- Deploy saves once, after the whole fan-out.** A crash mid-loop loses the record of
  remote mutations already made (§5.1).
- **D3 -- `/assign` replaces the tenant's assignment set** with the template's, silently reverting
  any assignment change made in the portal. This is the concrete mechanism behind §6.7's race, and
  it happens today on every redeploy.

### 8.1 Phase 0 contract (r3 -- decided, from r2's findings)

- **D1: PATCH an explicit owned-field projection, never the create body.** The create body
  hardcodes `runAsAccount=system`, restart suppression, `x64`, and default return codes; reusing
  it on update would overwrite portal-adjusted values. On update PCB PATCHes only the fields
  `AppTemplate` actually models: `displayName`, `description`, `publisher`,
  `installCommandLine`, `uninstallCommandLine`, `setupFilePath`, `detectionRules`.
  Everything else (`installExperience`, `applicableArchitectures`, `returnCodes`, requirement
  rules, minimum OS) is **preserved** by omission. Known limitation, not fixed in Phase 0: if an
  app has `activeInstallScript` / `activeUninstallScript` set, those override the command lines,
  and patching the command lines has no effect. Phase 0 does not detect this; C1 observes it.
- **D3: redeploying an existing app does not call `/assign`.** `/assign` replaces the full
  assignment set, and PCB has no reconciliation state to compute a correct replacement (union
  resurrects portal-deleted groups; group-keyed merge loses exclusions and filters). Initial
  create still assigns. Changing assignments on an existing app becomes a separate, explicit
  operation, designed with C2's baselines -- not a side effect of a content push.
- **D2: reduced, not fixed -- and the release notes say so.** Phase 0 persists per tenant instead
  of once per fan-out, persists `IntuneAppId` immediately after the create call returns (so a
  retry reuses it instead of creating a duplicate), and persists a non-terminal status before the
  first remote write of each stage, so a crash leaves a visibly in-progress deployment rather than
  a stale `Succeeded`. It does **not** close the window between a remote write and the save that
  records it, nor the lost-response case on the very first create. That needs a durable operation
  journal with startup recovery, which is Phase B (`DeploymentAttempt`).

**Problems this work must fix:**

- **Upload path.** r1 corrected the original diagnosis: `UploadPackage` already has
  `[RequestSizeLimit(2 GiB)]`, so Kestrel's 30 MB default does not apply. The real limit is
  `IFormFile` form buffering (128 MB multipart default) -- a 200 MB upload fails before the action
  runs. Fix: disable form model binding and stream with `MultipartReader` under explicitly set
  limits, plus **authenticated resumable upload sessions** with quotas, immutable finalization
  (hash verified on finalize), and cleanup of abandoned chunks. A single 2 GiB file cannot fit a
  2 GiB whole-request limit once multipart overhead is added; chunking removes that problem.
- **`FilePackageStore`** is local disk, single instance. Content addressing and refcounts are
  needed for R3. Swapping to S3 does not by itself give atomic retention decisions or durable pins
  (R6) -- those live in the database either way.
- **MSI property extraction** (ProductCode/ProductVersion/UpgradeCode) is **deferred** past v1.
  v1 keeps manual ProductCode entry, which works today. When it ships it is bounded read-only
  parsing (§7), not "just zipping."
- **`.claude/agents/codex-*.agent.md` hardcode one machine's Windows repo path**
  (`c:\Users\stadmin.ST-SURFACE0\...`). Correct on that machine, broken everywhere else (this spec
  was written on a Linux host where it does not exist). Make the path relative or derived before
  relying on the dispatchers from more than one machine.

## 9. Phasing

| Phase | Content | Depends on |
|---|---|---|
| **0** | Fix D1 and D3, reduce D2, in shipped code -- exact contract in §8.1 | nothing -- ships alone |
| **A** | `PartnerCenterBridge.Packaging`: the writer plus the §3.0 independent decrypt/verify test suite | nothing -- isolated project |
| **C1** | Inventory sweep + **Observed** rung, read-only, cross-tenant view | nothing -- read-only Graph |
| **B** | Recipe/Source/Release/Build model, migration (§4.2), retention sweep, build queue, `DeploymentAttempt` | A |
| **C2** | Adopted + Managed rungs, baselines, freshness, the §6.6 gate, accept-drift/revert | B, C1 |
| **D** | SPA: app wizard, script editor, resumable upload, update-in-place UX | B, C2 |

**0, A, and C1 are independent and can proceed in parallel.** C1 is worth calling out: it is
read-only, needs no packaging at all, and delivers the cross-tenant auditability the operator
named as the core Partner Center gap -- the lowest-risk slice with the most immediate value.

Phase A carries a hard constraint: **no dependency on `Graph`, `PartnerCenter`, or `Data`.** This
is what keeps a future spin-out a move rather than a rewrite (D-0043). Architectural requirement,
not preference.

## 10. Conventions this work inherits

From `CLAUDE.md`: no AI attribution in commits or PRs; `feat/*` branch -> PR -> merge commit;
**string literals stay ASCII-only**; confirm before pushing or merging.

## 11. Unverified assumptions -- check against a real tenant before depending on them

- **U1** -- Graph offers no supported way to download committed Win32 app content back out of
  Intune (§6.5). If wrong, adoption could recover media and §6.5 gets simpler.
- **U2** -- whether `mobileApps` PATCH and `/assign` honor `If-Match` / ETags (§6.7). If they do,
  the residual race closes.
- **U3** -- PATCHing detection rules on an existing `win32LobApp` is part of Microsoft's published
  update contract (r2 cited it), so this is documented, not speculative -- but actual tenant
  behavior still gets integration-validated before Phase 0 is called done.
- **U5** -- r2's claim that `$expand=assignments` on the `mobileApps` list is deprecated was not
  independently verified (post-dates the orchestrator's knowledge and needs live docs). The r3
  per-app mechanism is correct whether or not the claim holds, so it is adopted on that basis.
- **U4** -- that a PCB-built package installs on a real Windows endpoint (§3.0). No offline test
  can substitute. Includes one observed difference from the reference tool (§14.2): PCB writes
  `/` in inner zip entry names, IntuneWinAppUtil 1.8.7 writes `\`. `/` is what the ZIP
  specification requires and what .NET extraction on Windows accepts, but whether the Intune
  Management Extension extracts it identically is part of this check, not assumed.

## 12. r1 adversarial review -- adjudication

Reviewer: Codex `gpt-6-astra`, reasoning effort high, read-only, spec + source. Every CONFIRMED
code claim was reproduced against source by the orchestrator before acceptance.

| # | Sev | Finding | Reproduced | Verdict | Where addressed |
|---|---|---|---|---|---|
| 1 | High | Reader round trip cannot validate the writer; reader never decrypts | yes -- test payload is `encrypted-bytes-here` | accept | §3, §3.0 |
| 2 | High | Existing update PATCHes `committedContentVersion` only | yes -- `IntuneWin32Service` step 8 | accept; **shipped defect** | §8 D1, Phase 0 |
| 3 | High | 24h freshness does not protect against concurrent change | design argument, sound | accept; invariant narrowed | §6.7 |
| 4 | High | Comparison omits executable state; compares to mutable template | yes -- r1 field list | accept | §6.3 baseline |
| 5 | High | Acknowledge-and-push contradicts the invariant | design argument, sound | accept; path removed | §6.6 |
| 6 | High | R2-R4 conflate eviction and expiry; migration unaddressed | sequence reproduced on paper | accept | §4.1 R2-R6, §4.2 |
| 7 | High | No durable attempt; save-at-end crash window | yes -- single `SaveChangesAsync` after loop | accept; **shipped defect** | §5.1, §8 D2 |
| 8 | High | Banning artifact hashes was wrong; source hash is not version identity | yes -- script-only change | accept | §3.1 |
| 9 | High (suspected) | Deploy not bound to a release; `Known` could be read as authorization | yes -- `DeployRequest(TemplateId, TenantIds)` | accept | §7 |
| 10 | Med | External packaging was never a security boundary; audit records names only | yes -- interceptor `changedProps` | accept | §7 |
| 11 | Med | 30 MB diagnosis wrong (2 GiB already set); real limit is form buffering | yes -- `[RequestSizeLimit]` present | accept; r1 diagnosis corrected | §8 |

Rejected: none. Every finding survived reproduction. Where a finding proposed a fix, the fix here
was chosen by the orchestrator (e.g. the ladder in §6 is a broader restructuring than finding 5
asked for, driven by the operator's own framing of non-PCB apps).

## 13. r2 adversarial review -- adjudication and what stays open

Reviewer: Codex `gpt-6-astra`, high, read-only; re-checked r2 against its own r1 findings and the
source. Code claims reproduced by the orchestrator: create body hardcodes execution fields (yes);
`Deployment.AssignmentIds` is never written anywhere (yes -- declared and mapped, zero writers).

**Resolved in r3 (unblocks Phase 0 and C1):**

| # | Finding | r3 decision |
|---|---|---|
| D1 regr. | Reusing create body on update clobbers portal fields | owned-field projection, §8.1 |
| D3 | "Stop blind `/assign`" was undefined | no `/assign` on existing apps, §8.1 |
| N4 | Per-tenant saves do not close the crash window | Phase 0 claims *reduce*, not *fix*; journal is B, §8.1 |
| N5 | List `$expand=assignments` deprecated | per-app fetch, §6.4 |
| N5 | Missing data read as empty | completeness flags; partial never yields `Missing` or `Known`, §6.4 |
| N10 | Observation history oversold as audit | claim narrowed, §6.4 |
| N2 | Revert required `Known`, which it establishes | revert is its own transition, §6.6 |

**Open -- must be resolved before B / C2, not before 0 / A / C1:**

- **N1 -- configuration knowledge is not content provenance.** Accept-drift can make an app's
  *config* known while its installer bytes were replaced in the portal. The ladder needs a
  separate provenance axis: remote content-version id mapped to a PCB build. Accepting external
  content must drop provenance, and with it the Managed rung, until the operator re-establishes
  it (a new PCB build pushed over it). `activeInstallScript`/`activeUninstallScript` join the
  compared executable state.
- **N3 -- the ladder conflates three capabilities.** "Can deploy an available artifact",
  "can rebuild a release", and "can account for this deployment" are different, and imported
  non-rebuildable releases (§4.2) must keep today's push capability. Release expiry must not
  strand a deployment that is still `Known` on it.
- **N6 -- assignments are outside the immutable authorization envelope.** A `DeploymentAttempt`
  must bind the full proposed mutation -- release **and** assignment plan -- not read mutable
  `AppTemplate.Assignments` at execution time.
- **N7 -- a release pin does not protect the selected build mid-upload.** Attempts record the
  selected build and hold a use-lease on it through upload and recovery, or recovery must abandon
  the remote upload and start a fresh content version.
- **N8 -- pins need terminal states.** Attempts need cancel/abandon transitions that release pins,
  and the spec must say whether active pins may temporarily exceed `Retention:Releases` (proposed:
  yes, visibly).
- **N9 -- blob delete vs. reuse race; builder-version retention.** Content-addressed deletion needs
  a tombstone/generation protocol so a new reference cannot land between the decision and the
  physical delete. A retained release whose builder version can no longer run must pin its artifact.

Rejected: none.

## 14. Phase A code review -- adjudication

Reviewer: Codex `gpt-6-astra`, high, read-only, against the Phase A diff and §3/§3.0/§3.1/§7.
Verdict on first submission: **not mergeable**. Crypto construction confirmed correct (layout,
HMAC span, digest target, independent RNG draws); blockers were input handling, scratch
confidentiality, output contract, and the missing fixture. Test grade given: **C** overall
(B crypto construction, D adversarial input/lifetime/scale).

| # | Sev | Finding | Reproduced | Verdict | Fix |
|---|---|---|---|---|---|
| 1 | P1 | A Linux file named `..\x.exe` became a `../x.exe` zip entry via separator rewriting; setup name accepted backslashes | yes, by reading `CollectFiles` | accept | entry names built from validated segments; Windows name rules; case-insensitive collision rejection (file/file and file/dir) |
| 2 | P1 | Symlink check is time-of-check; hardlinks pass | yes; .NET 8 cannot detect hardlinks (probed) | accept problem, **fix at the boundary** | trust boundary moved to staging (§7) and enforced by refusing group/other-writable dirs; residual race documented in the class contract |
| 3 | P1 | Scratch created 0644 under default umask; plaintext readable by other users | yes, default umask 022 | accept | per-build 0700 dir, 0600 files, both atomic at create |
| 4 | P1 | Microsoft-tool fixture required by §3.0 is missing | yes | accept -- **still open** | test written, skipped with reason; needs a real `IntuneWinAppUtil.exe` package |
| 5 | P2 | A FIFO in the source hangs the open past cancellation | yes; .NET 8 cannot detect FIFOs (probed) | accept problem, **fix at the boundary** | same as #2; plus cancellation checks and a `MaxEntries` bound in the walk |
| 6 | P2 | `ZipArchive` writes synchronously to the caller's stream | yes | accept | outer zip assembled in scratch; caller's stream receives only async writes |

Test changes adopted from the grading: tamper tests assert a valid baseline and require rejection
**at MAC verification** specifically; oracle checks the `ApplicationInfo` root and rejects duplicate
entries; reader-compatibility compares every field; AES key and MAC key must differ; new tests for
hostile names, collisions, shared-writable staging, scratch modes observed mid-build, cleanup on
output failure and mid-copy cancellation, and async-only/left-open output.

Mutation check after the fix round (orchestrator-run, each mutant built and run against the 34
writer tests): HMAC skips IV -- 12 fail; payload is plaintext -- 13 fail; IV absent from payload --
13 fail; MAC key reuses AES key -- 1 fail; name validation disabled -- 10 fail. All killed. The
key-reuse mutant is caught by a single assertion, which is thin coverage.

**Not covered, stated rather than implied:** multi-GB / ZIP64 streaming (the oracle buffers whole
packages in memory and is unsuitable for it); Microsoft-tool interoperability (#4); a real Graph
commit and endpoint install (§3.0, the Stable gate).

### 14.1 Scoped re-review of the fix round (round 4)

Same reviewer. Arrived after `c282070` was pushed. Status of the six: **#3 and #6 resolved; #2
resolved for the revised contract (staging boundary accepted); #1 and #5 partial; #4 open.**
Test re-grade: **B-** (B+ crypto construction, C+ adversarial input/lifetime, D scale).
Verdict: **not mergeable yet**. Every code claim below was checked against the source by the
orchestrator and holds.

| # | Sev | Finding | Status |
|---|---|---|---|
| R4-1 | P2 | `ReservedNames` lacks `COM1`-`COM3` and `LPT1`-`LPT3` written with superscript digits (U+00B9, U+00B2, U+00B3), which Windows also reserves; leading ASCII spaces accepted (Windows strips them, so `" config.ini"` and `"config.ini"` can collide on extract) | **fixed** -- superscript forms plus `COM0`/`LPT0` (also documented as reserved) added; a leading space is refused |
| R4-2 | P2 | `MaxEntries` counts files only, so a staging tree of empty directories is unbounded; no cancellation check inside the enumeration loop | **fixed** -- the bound counts every entry; cancellation is checked per entry |
| R4-3 | P2 | Regression: inner zip, payload, and outer zip are all live at once -- peak scratch ~3N, spec §5 budgets 2x. Dispose the inner zip after encryption, before outer assembly | **fixed** -- inner is deleted before outer exists, payload before copy-out; a test asserts the live set at every scratch creation and at copy-out |
| R4-4 | P2 | "Cancelled midway" test cancels on the first write before any byte persists; partial-output failure, cancellation after a written prefix, and flush failure are uncovered | **fixed** -- I/O failure and cancellation at write 0 and after a persisted prefix, plus flush failure, each assert propagation and scratch removal |
| R4-5 | P3 | `char.IsControl` also rejects DEL and C1 controls, stricter than Windows' 0-31 rule. Decide: intentional portability policy or narrow it | **fixed** -- operator decision: match Windows; only U+0000-U+001F is refused, and DEL/C1 names are tested as accepted |
| R4-6 | -- | Round-3 adjudication overstated ".NET cannot detect" FIFOs/hardlinks; interop could. Wording corrected in §7 and the class doc | **fixed (wording)** |
| R4-7 | -- | When the #4 fixture lands, the test project needs a copy-to-output rule for `Fixtures/**` | **fixed** with #4 (§14.2) |

The reviewer confirmed there was no regression in the collision sets (every combination of files
and directories colliding, in any enumeration order), ADS handling, disposal ordering,
`OpenScratch`, or `CopyHashedAsync`. It recommended **against** Unicode compatibility normalization.

### 14.2 Round-5 fixes and the reference fixture

Finding #4 is closed. `tests/PartnerCenterBridge.Tests/Fixtures/IntuneWinAppUtil/reference.intunewin`
was built on a Windows 11 host by `IntuneWinAppUtil.exe` **1.8.7** (release asset sha256
`c1ba45b5cb939e84af064bb7ff4b38fb3dfe33c8dc1078fd9b157672eae671f6`) from a three-file folder
(`setup.cmd` that only writes a marker file, `config/settings.json`, 5,000 random bytes in
`config/blob.bin`). It was decrypted twice before being trusted: once by a throwaway
Python + OpenSSL script sharing nothing with this repo, and once by the test oracle. Both agree:
MAC over `IV || ciphertext`, IV echoed in the header, SHA-256 `FileDigest` of the plaintext zip,
`UnencryptedContentSize` = plaintext zip length, `ProfileVersion1`. The oracle is now calibrated
against the reference tool, and a tampered copy of the fixture is rejected at MAC verification.

What the fixture showed that the spec did not predict:

- **Inner entry separators differ.** The reference tool writes `config\blob.bin`; PCB writes
  `config/blob.bin`. PCB keeps `/` (ZIP APPNOTE 4.4.17). Tracked under U4 rather than silently
  matched, because copying the tool's quirk would make PCB packages non-conformant everywhere
  else.
- **Detection.xml carries extras PCB does not emit:** `xsi`/`xsd` namespace declarations and a
  `ToolVersion="1.8.7.0"` attribute on `ApplicationInfo`, and no XML declaration. (No BOM -- an
  earlier draft of this section claimed one; round 5 caught it, see §14.3.) None are read by
  this repo's reader or by the oracle. Both outer entries are stored uncompressed; PCB deflates
  `Detection.xml`, which any zip reader handles.

Mutation check for the round-5 tests (orchestrator-run, each mutant built and run against the 48
writer tests then present): leading-space check removed -- 1 fails; superscript `COM` names removed
-- 1 fails; directories not counted toward the bound -- 1 fails; strict `char.IsControl` restored --
2 fail; outer zip created before the inner zip is deleted -- 1 fails; an extra scratch file kept
alive through copy-out -- 2 fail. All killed; most by a single assertion, which is thin but
targeted.

Still not covered: multi-GB / ZIP64 streaming, cancellation observed *during* directory
enumeration (the check exists; a deterministic test for it would need a filesystem hook), and U4.

### 14.3 Round-5 review -- adjudication

Same reviewer, against the round-5 diff and the fixture. It independently decrypted the fixture
with Python/OpenSSL and confirmed the MAC, IV, digest, size, content hashes, and tamper rejection.
Status of the carried items: **R4-1, R4-2, R4-3, R4-4, R4-5, R4-7 and #4 all resolved.**
Test re-grade: **B** (A- crypto construction, B adversarial input/lifetime, D scale).
Verdict: **approve Phase A** after the P3s below. **Stable stays blocked on U4.**

| # | Sev | Finding | Reproduced | Fix |
|---|---|---|---|---|
| R5-1 | P3 | A throwing `ScratchCreated` observer leaked the just-opened scratch handle, because the stream had not yet reached an owning `using` | yes, by reading `OpenScratch` | dispose the stream before rethrowing; a theory throws from the observer for each scratch purpose and asserts scratch is empty |
| R5-2 | P3 | Moving outer disposal into `finally` meant a throwing `DisposeAsync` skipped the directory delete | yes, by reading `WriteAsync` | nested `try/finally`, so the directory delete always runs |
| R5-3 | P3 | §14.2 claimed the reference `Detection.xml` starts with a UTF-8 BOM; it does not | yes -- the orchestrator's own check decoded with `utf-8-sig`, which accepts either, so it never actually looked | claim removed |

Reviewer's note on U4, adopted: closing U4 must show that **nested** files land in their intended
directories on a real endpoint. A marker-only setup script succeeding does not establish that.

Not covered, still: asynchronous output that fails while a write is genuinely pending, and
multi-GB / ZIP64 streaming.
