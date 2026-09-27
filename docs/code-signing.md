# Code signing

PartnerCenterBridge's Local Workbench exe is Authenticode-signed with
[Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) (the service
formerly called Trusted Signing), the same setup already running for
[SpoolSmith](https://github.com/Spillers-Technology/spoolsmith) -- see that repo's
`docs/code-signing.md` for the full background on why Artifact Signing, timestamping, and the
OIDC subject work the way they do. This page only covers what is specific to this repository.

`.github/workflows/release.yml`'s `windows` job signs `PartnerCenterBridge.exe` after publishing
it and before it goes in the release zip, verifies the result, and refuses to publish anything it
could not sign -- the same fail-closed behavior as SpoolSmith's release pipeline.

## Setup

This reuses the existing Artifact Signing account and certificate profile; it does not create a
new one. Run SpoolSmith's setup script once, from a checkout of that repo, against this
repository:

```powershell
az login
./scripts/setup-signing.ps1 -AccountName jdspille -ResourceGroup RG0 -ProfileName primary-profile `
    -Repo Spillers-Technology/PartnerCenterBridge -EnableImmutableSubject
```

It needs `az login` as someone who can create app registrations and assign roles, and `gh auth
login` (or a `GH_TOKEN` with `repo` scope) as a repository admin. It creates a **separate** app
registration (`PartnerCenterBridge-release-signing`), federated credential, and `release`
GitHub environment scoped to this repository only -- one repository's credential cannot sign for
another, even though both share the same certificate profile and publish under the same
`CN=Joseph Spillers` identity. `-EnableImmutableSubject` matches the numeric-ID subject claims
already turned on for this GitHub organization.

## GitHub configuration

Everything is scoped to the **`release` environment** (Settings -> Environments -> release), so
only a job that declares that environment can read it. `setup-signing.ps1` sets all of it.

| Variable | Value here | Meaning |
| --- | --- | --- |
| `SIGNING_ENDPOINT` | `https://eus.codesigning.azure.net` | Must match the account's region |
| `SIGNING_ACCOUNT` | `jdspille` | Artifact Signing account name |
| `SIGNING_PROFILE` | `primary-profile` | Certificate profile name |
| `SIGNING_EXPECTED_SUBJECT` | `CN=Joseph Spillers` | Substring the signer subject must contain |

| Secret | Where it comes from |
| --- | --- |
| `AZURE_CLIENT_ID` | The app registration's application (client) ID |
| `AZURE_TENANT_ID` | The directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | The subscription holding the signing account |

The `release` environment's deployment restriction only allows `main` and `v*` tags to run in
it, same as SpoolSmith -- a published release runs as its `vX.Y.Z` tag, and `workflow_dispatch`
runs as `main`.

## Fail-closed behavior

`.github/workflows/release.yml`'s `windows` job checks all six values above (three variables,
three secrets) before publishing anything for that job, using the same check and message
SpoolSmith's release job uses:

> Code signing is not configured; missing: <names>. Set them under Settings > Environments >
> release, or run scripts/setup-signing.ps1 (from a SpoolSmith checkout). Setup steps are in
> docs/code-signing.md. Releases are never published unsigned.

A `workflow_dispatch` dry run still exercises the same check (and the same sign + verify steps)
so a broken credential shows up on a manual run, not on tag day; a dry run publishes nothing
regardless of whether signing succeeds.

## Verifying a download

Anyone can check a release without installing anything:

```powershell
Get-AuthenticodeSignature .\PartnerCenterBridge.exe | Format-List Status, SignerCertificate, TimeStamperCertificate

# Or with a repo checkout:
./scripts/verify-signature.ps1 -Files PartnerCenterBridge.exe
```

Check the zip against its published `.sha256` sidecar:

```powershell
(Get-FileHash PartnerCenterBridge-vX.Y.Z-win-x64.zip -Algorithm SHA256).Hash.ToLower()
Get-Content PartnerCenterBridge-vX.Y.Z-win-x64.zip.sha256
```

```sh
sha256sum -c PartnerCenterBridge-vX.Y.Z-win-x64.zip.sha256
```

## Checking signing without releasing

`.github/workflows/signing-check.yml` publishes the real Local Workbench exe, signs it through
the same composite action a release uses, verifies it, and keeps it as a one-day workflow
artifact. It publishes nothing. Run it after changing signing configuration and before tagging:

```sh
gh workflow run signing-check.yml --ref main
gh run download <run-id> -n signed-binaries      # then run scripts/verify-signature.ps1 on it
```

`release.yml`'s `workflow_dispatch` dry-run input runs the same sign-and-verify steps too, as
part of proving the whole release pipeline rather than just signing in isolation -- use whichever
is more convenient. Both need to run from `main` or a `v*` tag, per the deployment restriction
above.

## Troubleshooting

See SpoolSmith's `docs/code-signing.md` troubleshooting table -- every failure mode there
(`AADSTS700213`, `EnvironmentCredential authentication unavailable`, region/endpoint mismatch,
deployment restriction rejections) applies here unchanged, since this repository uses the same
signing action, the same verification script, and the same account and certificate profile.
