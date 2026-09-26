**Tenant sign-in design review — PartnerCenterBridge**

Reviewed branch `feat/ops-workbench`, commit `9ab8ab9bfed717348c9ead8c1eb373acfb8b0687`, on September 26, 2026.

This was a read-only review. No files changed. Findings below distinguish repository behavior, verified Microsoft guidance, and proposed design. No live tenant credentials, consent grants, GDAP relationships, or Conditional Access policies were inspected; this establishes what the implementation supports, not which deployed customers currently satisfy its prerequisites.

**Recommendation:** retain GDAP/SAM for delegated customer administration, make connection health and consent coverage explicit, and add guided system-browser authentication first. Add certificate-based app-only connections for direct customers. Evaluate WAM as a separate desktop token provider, not as a replacement dialog for the existing SAM refresh-token extractor. Do not make embedded WebView2 the default Microsoft sign-in mechanism.

---

**1. Current authentication model**

**1.1 Operator authorization and Microsoft authorization are separate**

PCB authenticates its operators independently of Microsoft:

- Local accounts receive PCB JWTs and explicit tenant `Viewer`/`Operator`/`Owner` grants.
- Instance permissions control shared configuration, including SAM credentials and tenant onboarding.
- Local instance Administrator status does not itself bypass tenant grants.
- OIDC and Dev retain an all-access model: a principal without the local user-ID claim is treated as authorized for every tenant and as an instance Administrator.

Evidence: [Program.cs:107](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Api/Program.cs:107), [TenantAccessService.cs:32](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Api/Auth/TenantAccessService.cs:32), [InstanceAccessService.cs:25](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Api/Auth/InstanceAccessService.cs:25).

Signing into PCB with a passkey does **not** authenticate the operator to Entra, satisfy Microsoft MFA, or create GDAP access. Conversely, possessing a usable Microsoft connection should not confer PCB tenant authorization.

**1.2 SAM bootstrap**

`PartnerOptions` holds one instance-wide `ClientId`, `ClientSecret`, `PartnerTenantId`, and optional `SeedRefreshToken`. There is no per-tenant credential configuration.

`SamBootstrapService`:

1. Builds an MSAL **public client** using the configured application ID.
2. Uses the partner tenant’s authority.
3. Requests `https://graph.microsoft.com/.default` through device-code authentication.
4. Serializes the MSAL cache and extracts a refresh-token secret.
5. Saves that token to the shared protected store.

It does not request a Partner Center access token during bootstrap, validate customer access, or enforce MFA itself. Those depend on Entra policy and subsequent resource authorization. MSAL normally adds the OIDC scopes needed for refresh-token issuance.

Evidence: [PartnerOptions.cs:12](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.PartnerCenter/PartnerOptions.cs:12), [SamBootstrapService.cs:28](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.PartnerCenter/SamBootstrapService.cs:28).

Bootstrap runs as a separate CLI invocation and exits before starting the web server: [Program.cs:238](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Api/Program.cs:238). The app registration must support this public-client flow as well as the confidential-client redemption used afterward; configuration values alone do not establish that.

**1.3 Partner Center authentication**

`PartnerCenterClient.ListCustomersAsync` requests a token with:

- Authority: `Partner:PartnerTenantId`.
- Resource: `https://api.partnercenter.microsoft.com`.
- Scope: that resource’s `/.default`.
- Grant: the stored SAM refresh token, redeemed using the configured client secret.

It then calls `GET /v1/customers`. Despite comments describing “REST v3,” this is the actual endpoint.

Evidence: [PartnerCenterClient.cs:29](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.PartnerCenter/PartnerCenterClient.cs:29).

The documented delegated Partner Center permission is `user_impersonation`; PCB’s `.default` request does not grant it automatically. Partner Center account/program authorization is also required. Microsoft enforces MFA for App+User Partner Center API calls from April 1, 2026; missing MFA can produce HTTP 401 with error `900421`. [Partner authentication](https://learn.microsoft.com/en-us/partner-center/developer/partner-center-authentication), [enforcement announcement](https://learn.microsoft.com/en-us/partner-center/announcements/2026-march).

**1.4 Customer Graph authentication**

For every customer, `SamTokenService` uses the **same shared partner user refresh token**, but builds the confidential client against:

```text
https://login.microsoftonline.com/{customerTenantId}
```

It requests `https://graph.microsoft.com/.default` using `AcquireTokenByRefreshToken`.

This is refresh-token redemption against a customer authority. It is not client-credentials authentication, and it is not OAuth’s JWT-bearer on-behalf-of grant. A client secret authenticates the application; it does not turn the resulting delegated token into app-only access.

Evidence: [SamTokenService.cs:45](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.PartnerCenter/SamTokenService.cs:45).

For intended GDAP operation, effective access requires all of:

- A valid partner App+User connection.
- An active customer GDAP relationship.
- An access assignment linking the required roles to a partner security group.
- Membership of the SAM user in that group.
- Customer-tenant application consent for the delegated permissions.
- Workload, target-object, licensing, and CA requirements satisfied.

Microsoft explicitly documents reusing the partner refresh token across customers, while requiring customer app consent and appropriate GDAP assignments. A reseller/customer-list relationship alone is insufficient. [GDAP and SAM](https://learn.microsoft.com/en-us/partner-center/developer/gdap-and-secure-application-model), [GDAP FAQ](https://learn.microsoft.com/en-us/partner-center/customers/gdap-faq).

**1.5 Caching, renewal, and rotation**

There are two substantially different Graph paths:

| Path | Actual behavior |
|---|---|
| `GraphTenantClientFactory` | Constructs a tenant-bound SDK client. Its authentication callback calls the SAM provider whenever a token is requested. |
| `TenantGraphRest`, `GraphUserService`, `IntuneWin32Service` | Acquire one token and construct a `GraphRestClient` that retains that token string for its lifetime. |

Evidence: [GraphTenantClientFactory.cs:18](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Graph/GraphTenantClientFactory.cs:18), [TenantGraphRest.cs:21](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Graph/TenantGraphRest.cs:21), `GraphUserService.cs:35`, `IntuneWin32Service.cs:77`.

Every `SamTokenService` acquisition creates a fresh confidential client with an initially empty cache. PCB neither restores a persistent MSAL cache nor calls `AcquireTokenSilent`. It captures the resulting serialized cache only to extract and persist a replacement refresh token. `AuthenticationResult.ExpiresOn` is discarded.

Consequences:

- There is no effective shared access-token cache.
- Repeated reads can cause repeated token-endpoint calls.
- A long REST operation cannot renew its retained access token.
- There is no refresh schedule when the workbench is unused or closed.
- Rotation only persists when extraction returns a non-null token different from the input.

Evidence: `SamTokenService.cs:56–81`; [GraphRestClient.cs:16](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Graph/GraphRestClient.cs:16).

**1.6 Storage and protection**

The refresh token is a single `SecretRecord` named `sam-refresh-token`, encrypted with ASP.NET Data Protection under purpose `PartnerCenterBridge.SamRefreshToken.v1`.

- Windows Local profile persists the key ring and wraps its keys with current-user DPAPI.
- Server profile persists the key ring, defaulting to `/keys`, but this configuration does not explicitly wrap those keys with another encryption mechanism.
- `ClientSecret`, `SeedRefreshToken`, and the Exchange PFX password remain ordinary configuration strings. Their protection depends on how configuration is supplied.
- The token record has no stored Microsoft account identity, originating client ID, connection generation, or concurrency token.

Evidence: [ProtectedSamTokenStore.cs:14](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Data/ProtectedSamTokenStore.cs:14), [LocalDataDirectory.cs:199](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Api/Hosting/LocalDataDirectory.cs:199), [HostingExtensions.cs:99](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Api/Hosting/HostingExtensions.cs:99).

`GET /api/admin/sam/status` reports whether a token exists, not whether it can acquire tokens. Manual seeding accepts any nonempty string and immediately replaces the shared credential, with an explicit audit event. Evidence: [AdminController.cs:32](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Api/Controllers/AdminController.cs:32), `Diagnostics/SamStatusService.cs:33`.

**1.7 Tenant onboarding**

Manual onboarding inserts a tenant record. Partner Center sync imports customer IDs, names, and domains. Neither path checks delegation, consent, roles, or Graph connectivity.

`Tenant.Status` defaults to `Active`; `GdapRelationshipId` exists but these onboarding paths never populate it. Repository searches found display/consumer code for `NoDelegation`, but no implemented discovery process that sets it from Microsoft.

Evidence: [TenantsController.cs:53](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Api/Controllers/TenantsController.cs:53), `TenantsController.cs:81`, [Tenant.cs:19](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Core/Entities/Tenant.cs:19).

Therefore the claims in `docs/sam-bootstrap.html:155–159` and `docs/local-workbench.html:135–140` about detecting missing GDAP exceed the implementation.

**1.8 Exchange Online**

Exchange uses a completely separate authentication path:

```text
Configured Exchange AppId + PFX
    → Connect-ExchangeOnline
    → customer organization
    → one operation
    → disconnect
```

It does not use SAM, the Graph token, or `-DelegatedOrganization`.

The organization is currently:

```csharp
tenant.DefaultDomain ?? tenant.TenantId
```

Every customer shares the same configured Exchange application and certificate. The script passes `AppId`, `Organization`, `CertificateFilePath`, and optionally `CertificatePassword`.

Evidence: [ExchangeOnlineService.cs:125](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Exchange/ExchangeOnlineService.cs:125), [exo-op.ps1:70](/C:/Users/kubert/Documents/GitHub/PartnerCenterBridge/src/PartnerCenterBridge.Exchange/Scripts/exo-op.ps1:70), `ExchangeOptions.cs:12`.

Microsoft’s app-only model requires `Exchange.ManageAsApp` consent plus suitable Exchange authorization for the application in each customer. Custom Exchange role groups can restrict cmdlets and recipient write scope; broad Exchange Administrator is not the only option. Microsoft directs callers to use the customer’s primary `.onmicrosoft.com` domain for `-Organization`. [Exchange app-only authentication](https://learn.microsoft.com/en-us/powershell/exchange/app-only-auth-powershell-v2).

PCB implements mailbox lookup/listing, shared conversion, forwarding during conversion, archive enablement/auto-expansion, retention-policy assignment, processing-block changes, and Managed Folder Assistant invocation. Hiding from the GAL and granting a manager mailbox access remain unimplemented.

**1.9 Where zero per-tenant sign-in works today**

“Zero per-tenant sign-in” means during routine operation **after required authorization has been established**.

| Tenant/operation | Current outcome |
|---|---|
| Partner Center customer-list sync | One usable partner SAM connection; no customer sign-in. |
| GDAP customer with appropriate roles, group membership, consent, and policies | Graph directory/Intune operations can run without customer credentials. Coverage is operation-specific. |
| GDAP customer missing consent or a required role | A token request or API call fails. PCB has no automated repair flow. Repeating sign-in alone will not create missing authorization. |
| Direct customer with ten distinct customer-admin identities | Unsupported as a first-class Graph connection model. There is only one shared refresh-token slot. |
| Direct customer where the same SAM identity already has independently valid guest/direct access | May work through ordinary Entra authorization; PCB does not require a GDAP ID before token acquisition. This is incidental compatibility, not a managed direct-tenant feature. |
| GDAP or direct customer with independently authorized Exchange application | Implemented Exchange operations can run without an interactive customer sign-in. |
| GDAP customer with no Exchange application consent/RBAC | GDAP alone does not enable PCB’s existing Exchange path. |
| Graph inbox-rule portion of compromised-account lockdown | Cannot be promised from GDAP directory roles alone; see below. |
| GAL hiding, manager mailbox permissions, on-premises directory changes | Not implemented by these PCB paths, regardless of authentication. |

No current PCB operation opens ten separate customer sign-in dialogs. The browser pain occurs while establishing permissions outside PCB, working in customer portals, or administering direct tenants PCB cannot yet connect.

---

**2. Correctness and security findings**

Severity reflects impact in the relevant deployment, not proof of exploitation.

| Severity | Finding and evidence | Recommended fix |
|---|---|---|
| **High — authorization integrity** | **Tenant registration is mistaken for connection readiness.** New tenants default to `Active`; diagnostics count active records rather than verify Microsoft access. `TenantsController.cs:62,94`; `Tenant.cs:22`; `SystemDiagnostics.cs:192`. | Separate registry lifecycle from connection health. Start at `Unverified`. Track Graph, GDAP, consent, and Exchange independently, with timestamps and evidence. |
| **High — credential integrity** | **Concurrent rotation can overwrite a newer connection.** The shared token uses read/modify/save without a generation or compare-and-swap. An acquisition started before a manual re-seed can later save its old identity’s replacement token over the new credential. `SamTokenService.cs:51,74`; `ProtectedSamTokenStore.cs:32`. | Serialize refresh per connection, add optimistic concurrency and a connection generation, and reject saves from superseded generations. Cover CLI and multiple server instances, not just one process. |
| **High — tenant targeting** | **Exchange target is not bound to the Entra tenant ID.** A supplied/imported `DefaultDomain` determines the Exchange organization, while Graph uses `TenantId`. A mismatch can connect to another customer that the shared app can access. `ExchangeOnlineService.cs:133`; `TenantsController.cs:64–66`. | Discover and persist a verified Exchange organization domain tied to the immutable tenant ID. Verify the connected organization before mutations. Never use an arbitrary display domain as an authorization target. |
| **High in exposed/shared deployments** | **OIDC authenticates into unrestricted operator power.** All non-local principals receive tenant access and instance administration. `TenantAccessService.cs:40`; `InstanceAccessService.cs:27,35`. | Map external identities into explicit instance and tenant grants. Until then, clearly constrain OIDC deployment to an explicitly trusted administrator population. |
| **High where temp storage is shared; otherwise Medium** | **Exchange writes the PFX password to plaintext temporary JSON.** Cleanup is best-effort and cannot run after every crash. The extracted script also uses predictable `%TEMP%/pcb-exo-op.ps1`. `ExchangeOnlineService.cs:135,181`; `PwshRunner.cs:35–36,78`. | Prefer certificate-store/private-key access on Windows. Pass sensitive payloads through a protected pipe or stdin. Extract scripts into a private location using collision-safe creation and integrity checking. |
| **Medium — reliability** | **Access-token lifetime is discarded.** REST clients retain one token; there is no expiry-aware renewal or claims-challenge handling. `SamTokenService.cs:81`; `GraphRestClient.cs:17,66`; `IntuneWin32Service.cs:77`. | Return a token lease with expiry and connection identity. Acquire through a handler/provider per request, refresh before expiry, and handle challenges deliberately. Do not blindly replay destructive requests after ambiguous failures. |
| **Medium — recovery** | **Stored token means “healthy,” even if expired, revoked, wrong-account, or missing MFA.** `SamStatusService.cs:33`; `AdminController.cs:49`; `SystemDiagnostics.cs:187`. | Stage replacement credentials, validate identity plus partner/customer capability, then activate atomically. Report configuration, stored credential, successful authentication, and operation authorization separately. |
| **Medium — consent correctness** | **Permission documentation is incomplete and implies customer consent inherits updates.** `docs/getting-started.html:195–246` omits managed-device retire/read and inbox-rule permissions, and says customers inherit after partner re-consent. | Maintain versioned operation permission bundles and compare each customer grant. Updating the app manifest or partner consent does not automatically update existing customer grants. |
| **Medium — secret management** | **DPAPI protection covers the stored SAM token, not all configuration secrets.** Server key wrapping is absent from this configuration. `PartnerOptions.cs:15,25`; `ExchangeOptions.cs:18`; `HostingExtensions.cs:99`. | Add secret references/protected settings, use certificates where feasible, and explicitly protect server key rings. Include configured paths outside the default data directory in security validation. |
| **Medium — misleading failure reporting** | **Some Exchange failures become “nothing found.”** `ListSharedMailboxesAsync` returns an empty list without data; `GetArchiveStateAsync` returns null, which the archive workflow describes as mailbox missing. `ExchangeOnlineService.cs:61–71`; `MailboxArchiveWorkflow.cs:36–39`. | Apply the structured failure-versus-absence behavior already present in `GetMailboxAsync` to all Exchange reads. Preserve consent, certificate, RBAC, and transport failures. |
| **Medium — feature authorization** | **Lockdown assumes Graph access to another user’s inbox rules.** The SAM directory-admin identity is not automatically a mailbox-data principal. `CompromisedAccountLockdownWorkflow.cs:50,85,91,142`. | Give this step its own capability. Implement appropriately scoped app-only mailbox access or an explicitly authorized Exchange operation. Allow directory containment steps to proceed with truthful partial evidence. |
| **Medium — audit completeness** | **Not all Microsoft changes are captured by database interception.** The hire endpoint returns Graph results without recording a workflow run. CLI bootstrap directly replaces the token; `SecretRecord` is excluded from generic auditing. `ProvisioningController.cs:39–45`; `SamBootstrapService.cs:53`; `AuditSaveChangesInterceptor.cs:19–27`. | Record provisioning runs and every credential activation/replacement, including CLI origin and Microsoft identity metadata. Never record token values or initial passwords. |
| **Low/Medium — defense in depth** | **REST bearer attachment accepts arbitrary absolute URLs.** Pagination follows `@odata.nextLink` without origin validation. `GraphRestClient.cs:29,58`. | Require HTTPS and an explicit Graph cloud-host allowlist before attaching tokens. Validate continuation links and configure redirect behavior. This review found no demonstrated attacker-controlled input path to exploit it. |
| **Low/Medium — scalability** | **Customer sync only parses one response page.** `PartnerCenterClient.cs:34–47`. | Implement documented Partner Center pagination and report partial discovery instead of implying the whole customer portfolio was synchronized. |

The old `GraphUserService.TerminateUserAsync` also catches individual group-removal errors and returns a successful aggregate group step (`GraphUserService.cs:143–157`). The current HTTP termination endpoint uses the newer planned offboarding service (`ProvisioningController.cs:98`), so this is a latent legacy-path defect, not evidence that the current endpoint always hides these failures.

**2.1 Token lifetime and rotation need a more accurate contract**

Microsoft documents a default 90-day refresh-token lifetime for ordinary non-SPA scenarios, replacement on use, and possible earlier revocation. Old tokens are not automatically invalidated just because a replacement was issued. [Refresh-token behavior](https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens).

For PCB:

- An unused workbench can exceed the inactivity window and require partner reauthentication.
- Refreshing does not override sign-in-frequency policy, revocation, account disablement, or a new CA requirement.
- Concurrent saves are a correctness problem, but not evidence that Microsoft treats each refresh token as strictly single-use.
- An exact expiry countdown cannot be calculated from `SecretRecord.UpdatedAt`.
- A valid Graph token does not prove that a Partner Center token satisfies that resource’s requirements.

Replace the promise at `docs/sam-bootstrap.html:141–142` that a healthy bridge never needs re-seeding with: **“PCB renews credentials during use; Microsoft policy or prolonged inactivity can require reconnecting the partner account.”**

CA sign-in-frequency controls explicitly govern when reauthentication is required. [Session controls](https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-conditional-access-session).

**2.2 MSAL cache extraction is an unnecessary maintenance risk**

`ExtractRefreshToken` chooses the newest secret from serialized cache entries without checking the client or account (`SamTokenService.cs:85`). Fresh per-call caches reduce immediate ambiguity, but this will become unsafe if multiple accounts or persistent caches are introduced.

The repository references MSAL `4.87.0`. Current Microsoft API documentation describes `AuthenticationResultExtensions.GetRefreshToken` for advanced **confidential-client** scenarios; it explicitly excludes public-client flows and several other cases. Verify availability and behavior against the pinned package before using it. It is not a fix for exporting WAM credentials. [Documented extension](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identity.client.extensibility.authenticationresultextensions.getrefreshtoken?view=msal-dotnet-latest).

For ordinary interactive connections, prefer MSAL-managed account/cache handling and silent acquisition. Keep any SAM-specific raw refresh-token handling behind a narrowly tested adapter.

**2.3 What missing GDAP roles look like today**

An insufficient relationship can fail at token issuance or at the resource API. Successfully obtaining a token does not prove every operation is authorized.

Current operator experiences include:

- Person sections report a generic app-permission-or-GDAP explanation for 403.
- Access Parity reports insufficient privileges for the particular group.
- Intune offboarding sometimes asserts that app permissions are missing, although GDAP/workload authorization can also be responsible.
- Partner sync only translates `InvalidOperationException`; MSAL and upstream HTTP failures have no dedicated connection-recovery response there.
- Raw REST exceptions preserve method, URI, status, and body, but not a structured claims challenge or complete response-header diagnostics.

Evidence: `PersonDirectoryReader.cs:98`, `AccessParityOperation.cs:272`, `OffboardingOperation.cs:274`, `TenantsController.cs:87`, `GraphRestClient.cs:66–83`.

A 403 should initially mean **“authorization refused; cause requires diagnosis.”** It should only become “missing Authentication Administrator” when relationship/access-assignment evidence supports that conclusion.

---

**3. Operation-to-permission and role coverage**

These are proposed least-privilege capability requirements based on current Microsoft documentation and the calls PCB makes. They are not claims that the deployed application already has these grants.

For delegated Graph calls, evaluate both the OAuth permission and the SAM user’s effective role. Roles listed below are task-appropriate built-in GDAP choices; object ownership, custom roles, and target-user privileges can change the minimum. Workflow read/verification calls add requirements beyond the write itself.

| PCB operation and code | Delegated Graph permission | GDAP role/workload authorization |
|---|---|---|
| Create cloud user — `GraphUserService.cs:52` | Current create-user documentation lists `User.Create`; `User.ReadWrite.All` is the broader permission documented by PCB. Manager/profile follow-up requires additional update authority. | **User Administrator** for the ordinary administrative workflow. Validate the narrower scope against PCB’s beta calls before replacing the existing bundle. [Create user](https://learn.microsoft.com/graph/api/user-post-users), [role definitions](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/permissions-reference). |
| Disable account — `OffboardingOperation.cs:397` | `User.EnableDisableAccount.All` plus `User.Read.All` is the documented least-privilege combination. | **User Administrator** for ordinary users; **Privileged Authentication Administrator** for unrestricted administrator targets. [Update user](https://learn.microsoft.com/en-us/graph/api/user-update?view=graph-rest-1.0), [emergency revocation](https://learn.microsoft.com/azure/active-directory/enterprise-users/users-revoke-access). |
| Revoke sessions — `OffboardingOperation.cs:401`; `MfaResetWorkflow.cs:68` | `User.RevokeSessions.All`; broader delegated permissions are also documented. | **Password Administrator** can cover ordinary non-admin targets; roles already needed by the workflow may cover it. Administrator targets require the applicable privileged-role hierarchy. [Revoke sessions](https://learn.microsoft.com/en-us/graph/api/user-revokesigninsessions?view=graph-rest-1.0), [target-role restrictions](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/privileged-roles-permissions). |
| Add/remove ordinary group membership — `GraphUserService.cs:94`; `AccessParityOperation.cs:261`; `OffboardingOperation.cs:453` | `GroupMember.ReadWrite.All`, plus permissions to read the properties used in planning. PCB’s `Group.ReadWrite.All` is broader. | **Groups Administrator** is the focused administrative choice; User Administrator also works. Group-owner authority can suffice outside a generic cross-customer GDAP bundle. [Group membership permissions](https://learn.microsoft.com/en-us/graph/api/group-post-members). |
| Role-assignable group membership | Additional `RoleManagement.ReadWrite.Directory`. | **Privileged Role Administrator**. Access Parity deliberately excludes these groups; preserve that exclusion. `GroupClassifier.cs:76`. [Role-assignable group requirements](https://learn.microsoft.com/en-us/graph/api/group-post-members). |
| Reset another user’s authentication methods — `MfaResetWorkflow.cs:74–84` | `UserAuthenticationMethod.ReadWrite.All` covers the implemented multi-method workflow; method-specific permissions can narrow specialized operations. Add session-revoke and user-lookup permissions. | **Authentication Administrator** for non-admin/allowed targets; **Privileged Authentication Administrator** for privileged targets. **User Administrator is not a substitute.** [Authenticator deletion](https://learn.microsoft.com/en-us/graph/api/microsoftauthenticatorauthenticationmethod-delete?view=graph-rest-1.0). |
| Reset password — `PasswordResetWorkflow.cs:77` | `User-PasswordProfile.ReadWrite.All`, plus lookup/revoke requirements. | **Password Administrator** for ordinary users; higher roles only as target hierarchy demands. Guest passwords belong to the home tenant; hybrid cases require supported writeback. [Update user](https://learn.microsoft.com/en-us/graph/api/user-update?view=graph-rest-1.0), [password-reset restrictions](https://learn.microsoft.com/en-us/entra/fundamentals/users-reset-password-azure-portal). |
| Assign/remove licenses — `GraphUserService.cs:83`; `OffboardingOperation.cs:470` | `LicenseAssignment.ReadWrite.All`. The license-repair workflow also patches `usageLocation`, so assignment permission alone is insufficient. | **License Administrator** for assignment/removal; **User Administrator** covers broader user changes. [Assign license](https://learn.microsoft.com/en-us/graph/api/user-assignlicense?view=graph-rest-1.0). |
| Read subscribed SKUs / user license detail — `GraphUserService.cs:166`; `PersonDirectoryReader.cs:58` | `LicenseAssignment.Read.All` is documented as least privilege. | **Directory Readers** is a suitable read-only baseline; license details also support License Administrator/User Administrator. [Subscribed SKUs](https://learn.microsoft.com/en-us/graph/api/subscribedsku-list?view=graph-rest-1.0), [license details](https://learn.microsoft.com/en-us/graph/api/user-list-licensedetails?view=graph-rest-1.0). |
| List and retire Intune managed devices — `OffboardingOperation.cs:261,492` | `DeviceManagementManagedDevices.Read.All` for discovery; **`DeviceManagementManagedDevices.PrivilegedOperations.All`** for retire. App-management permission does not cover retire. | **Intune Administrator** is the practical supported GDAP administrative role. Intune licensing and workload scope also apply. Do not label it the globally narrowest possible Intune RBAC configuration. [Retire](https://learn.microsoft.com/en-us/graph/api/intune-devices-manageddevice-retire?view=graph-rest-beta), [GDAP workloads](https://learn.microsoft.com/en-us/partner-center/customers/gdap-supported-workloads). |
| Deploy/update Win32 applications — `IntuneWin32Service.cs:77` onward | `DeviceManagementApps.ReadWrite.All` for the beta application workflow. | **Intune Administrator**, with an active Intune workload. [Win32 application creation](https://learn.microsoft.com/en-us/graph/api/intune-apps-win32lobapp-create?view=graph-rest-beta). |
| Read CA policies/named locations; device compliance snapshots | `Policy.Read.All`; `DeviceManagementConfiguration.Read.All` respectively. | **Security Reader** for standard CA policy reads; appropriate supported Intune read role for compliance data. Validate the exact snapshot properties. [CA policy permissions](https://learn.microsoft.com/en-us/graph/api/conditionalaccessroot-list-policies?view=graph-rest-1.0), [GDAP workload roles](https://learn.microsoft.com/en-us/partner-center/customers/gdap-supported-workloads). |
| Inspect/disable another user’s inbox rules — `CompromisedAccountLockdownWorkflow.cs:91,142` | `MailboxSettings.Read` / `MailboxSettings.ReadWrite`. | No GDAP directory role automatically grants arbitrary mailbox data access. Treat app-only mailbox authorization or an Exchange implementation as a separate capability. [Update rule](https://learn.microsoft.com/en-us/graph/api/messagerule-update?view=graph-rest-1.0), [permission definitions](https://learn.microsoft.com/en-us/graph/permissions-reference). |
| Exchange mailbox operations — `exo-op.ps1:83–169` | **No Graph permission substitutes for Exchange authorization.** Current connection requires Exchange application authorization. | Current code uses application RBAC, not GDAP user roles. A future delegated path would normally use **Exchange Administrator** for the full set; investigate Exchange Recipient Administrator/custom cmdlet roles for narrower coverage. [Exchange authorization](https://learn.microsoft.com/en-us/powershell/exchange/app-only-auth-powershell-v2). |

Do not describe “terminate user” as one permission. The implemented operation combines disablement, session revocation, group cleanup, licensing, optional Intune retirement, and optional Exchange changes. Each step can have a different authorization outcome.

Likewise, successful session revocation is not proof that every existing access token or application session immediately stopped working. Microsoft documents propagation delays and explicitly excludes external users’ home-tenant sessions from this API. The wording in `README.md:392` is too strong. [Revoke-session semantics](https://learn.microsoft.com/en-us/graph/api/user-revokesigninsessions?view=graph-rest-1.0).

**Consent must be represented separately from roles.** For delegated app consent through the Partner Center consent API, Microsoft currently identifies Application Administrator or Cloud Application Administrator, appropriate GDAP group membership, and partner AdminAgents membership. This consent authority should preferably be temporary rather than part of every routine operator bundle. [GDAP application consent](https://learn.microsoft.com/en-us/partner-center/developer/gdap-and-secure-application-model).

---

**4. Where customer sign-in still leaks through**

The operator’s ten-browser-session problem has several distinct causes:

| Cause | What reduces the sign-ins | What cannot be skipped |
|---|---|---|
| Customers already have complete GDAP authorization | One partner connection and per-operation capability verification. | Occasional partner reauthentication required by Microsoft policy. |
| Customer has GDAP but missing app consent | Partner consent automation when the caller has the required authority. | Appropriate authorization to grant consent. |
| Customer GDAP lacks a role or is expired | Generate a focused relationship request and track approval/assignment. | Customer approval of new delegated authority. Microsoft documents Global Administrator approval for a GDAP request. |
| Independent non-GDAP customer | App-only onboarding or an isolated direct delegated connection. | Initial customer authorization; delegated connections can later need reauthentication. |
| Exchange application is unconsented or lacks RBAC | Guided Exchange provisioning and verification. | Customer-specific app consent and RBAC establishment. |
| Operation is not implemented in PCB | A tenant-context portal link can reduce navigation friction. | The portal’s own authentication and authorization. |
| CA requires stronger authentication or a compliant device | A supported broker/browser and a device that satisfies policy. | The customer’s policy requirements. |

Source for GDAP approval: [Delegated administration](https://learn.microsoft.com/en-us/entra/identity/users/directory-delegated-administration-primer).

“Domain admin” is not the relevant permission category for these cloud APIs. PCB should ask for the required Entra/Exchange authority, never collect domain-admin passwords.

Global Administrator is also not universally necessary for app consent. Microsoft documents Privileged Role Administrator for granting any API permission, and Application/Cloud Application Administrator for many consent scenarios **excluding Microsoft Graph application permissions**. [Tenant-wide admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent).

---

**5. Windows sign-in design options**

| Option | UX value | Security/support | PCB feasibility |
|---|---|---|---|
| **A. MSAL.NET + WAM** | Windows-integrated account reuse; strongest native experience where supported. | Supported Windows broker; requires real parent HWND and correct account/tenant handling. | Good desktop addition through a native coordinator/helper. Unsuitable inside a headless server session. |
| **B. System browser + loopback** | Familiar Microsoft sign-in; account selection and reauthentication without token pasting. | Supported MSAL flow with PKCE. Browser cookies remain shared unless profiles are separated. | Lowest-risk first interactive improvement for Local mode. Server mode needs a server web callback. |
| **C. Isolated WebView2** | Attractive tenant tabs and separate cookie stores. | WebView2 itself is supported; MSAL.NET’s WebView2 path is explicitly unsupported for Entra authorities. | Suitable for PCB’s own shell; poor default for Microsoft authentication. |
| **D. Device code** | Works when the host lacks an interactive browser. | Supported protocol, but Microsoft recommends blocking it where possible. | Already implemented; retain only as an explicit supported-policy fallback. |
| **E. Customer consent + app-only** | Removes routine human sign-in for supported automation. | Durable application authority; certificate lifecycle and customer revocation remain essential. | New Graph connection provider and per-tenant configuration required. Existing Exchange is already this model. |
| **F. GDAP-first onboarding** | Largest reduction in customer-account handling for CSP portfolios. | Preserves delegated, time-bounded authority. | Extend discovery, consent, relationship tracking, and capability checks in both hosting modes. |

**5.1 A — WAM from the local ASP.NET process**

A loopback server does not gain an HWND by serving HTML. PCB currently launches the default browser through `Process.Start`; the Edge window belongs to another process (`LocalWorkbenchLifetime.cs:87–94`).

An interactive console launch may provide a usable console/terminal owner handle. Microsoft supplies a console-parenting pattern. A detached or hidden launch may not; a Windows service cannot reliably prompt the remote web operator. Use a small native window with a UI thread and message loop, or an authenticated per-user desktop helper. Do not guess the foreground Edge HWND. [WAM desktop integration](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam).

Use `Microsoft.Identity.Client.Broker`, `WithBroker`, and `WithParentActivityOrWindow`. Register the broker redirect URI. WAM integrates device authentication, Windows Hello/FIDO capabilities, and broker-managed credential protection. It still obeys CA and cannot make an unmanaged device compliant.

Account-picker behavior needs care: current documentation says tenant-specific or organizational-only authorities can show the generic Microsoft prompt rather than the native Windows picker. Do not promise the native picker for every customer connection. [MSAL.NET WAM guidance](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam).

Proposed account binding:

- Store connection ID, home-account identifier, resource tenant, client ID, and connection owner.
- Attempt silent acquisition for the explicitly bound account.
- Offer **Change account** deliberately.
- Recheck the resulting account and tenant before activating the connection.
- Never let “first account returned by MSAL” decide which customer credential is used.

Guest and partner identities are valid Entra scenarios, but broker sign-in does not establish guest membership, GDAP, consent, or cross-tenant device trust. Test guest-resource-tenant acquisition, partner GDAP acquisition, federated accounts, phishing-resistant authentication strengths, and customer device-trust policies. Treat full WAM-based replacement of PCB’s SAM path as **verify**, not proven compatibility.

**Crucial constraint:** WAM owns the powerful refresh credentials; it is not a portable SAM-refresh-token export mechanism. Build a broker token provider that asks WAM for tokens. Do not attach WAM to `SamBootstrapService` and expect `SerializeMsalV3` to yield the secret the current server model requires. [Microsoft’s broker design](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/wiki/WAM/978cfafce739580bf12e0575401498ccf2b5f030).

**5.2 B — System browser with loopback redirect**

For Local mode, MSAL supports authorization-code authentication with PKCE and a registered `http://localhost` desktop redirect. MSAL can allocate a free port. Use a separate short-lived callback listener rather than competing with Kestrel on port 5080. [MSAL browser support](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers).

Make the screen explicit: **“Connect partner Contoso MSP”** or **“Connect customer Fabrikam.”**

- `prompt=select_account` requests account choice.
- `login_hint` pre-fills a known account.
- `domain_hint` assists identity-provider discovery.
- None of these is a security boundary or a substitute for tenant-specific authority and returned-identity validation.

[Authorization-code parameters](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow).

A normal browser supports Microsoft MFA and available phishing-resistant methods, subject to device/browser/policy configuration. Separate Edge profiles can help with repeated direct-account use, but profile launching is browser-specific integration, not portable MSAL behavior.

InPrivate is a poor universal workaround: Microsoft documents failed device checks in private mode and treats Edge InPrivate as noncompliant for relevant CA controls. [Browser/device support](https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-conditional-access-conditions), [grant controls](https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-conditional-access-grant).

For server mode, the operator’s `localhost` is not the server. Use a normal HTTPS authorization-code callback on the server, with server-side credential/cache storage. Keep the Microsoft connection callback distinct from PCB’s own operator sign-in.

**5.3 C — WebView2 with one user-data folder per tenant**

Separate WebView2 folders or profiles can isolate cookies and browser storage. They do not isolate Microsoft API tokens automatically, and they do not create a security boundary against the Windows user/process hosting those folders. Multiple folders also cost resources. [WebView2 storage model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder).

Two verified constraints rule this out as the default:

1. MSAL.NET documentation explicitly says its WebView2 integration does not support Entra ID authorities and falls back to the legacy embedded view.
2. Microsoft’s Windows guidance recommends system-browser/broker OAuth rather than hosting login pages and scraping redirects/cookies.

[MSAL WebView2 limitation](https://learn.microsoft.com/en-us/entra/msal/dotnet/advanced/webview2), [Windows WebView2 authentication guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/webview2).

An embedded renderer’s ability to display a login page does not establish device-compliance, federation, security-key, or support compatibility. Do not bypass an embedded-browser restriction with user-agent tricks or token scraping.

WebView2 remains reasonable for hosting **PCB’s own React UI**, while WAM/system browser performs Microsoft authentication.

**5.4 D — Device code**

Device code is already implemented and useful for genuinely browserless hosts. It is unnecessarily awkward for the primary desktop experience.

Microsoft recommends blocking device code where possible because it enables phishing and unmanaged-device access patterns. CA can track a device-code-originated session through later refreshes, so moving the token into a confidential-client redemption does not erase the original flow classification. [Authentication-flow controls](https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-authentication-flows).

Device code can involve MFA on the verification device, but it does not offer the same requesting-device assurance as brokered sign-in. Keep it as **“Use device code, if permitted by your organization”**, not “recommended bootstrap.”

**5.5 E — Consent once, then app-only**

This is the best fit for supported unattended operations in customers outside GDAP.

A guided onboarding sequence should:

1. Identify the immutable customer tenant.
2. Explain the exact application permissions and workloads requested.
3. Open a tenant-specific admin-consent link.
4. Establish application credentials controlled by this installation/customer.
5. Verify access and, separately, Exchange RBAC.
6. Store the connection as **Connected via application consent**.

A multitenant application requires a service principal and grants in each customer; the certificate is associated with the app registration. A separate customer application/certificate offers a smaller compromise radius at the cost of more lifecycle management.

Do not distribute one publisher-owned private key or client secret in every desktop executable. Prefer customer/MSP-owned credentials or locally generated per-installation keys with a supported onboarding design.

“Never sign in again” is too strong. Routine client-credentials acquisition has no human refresh token, but certificates expire, consent can be revoked, applications can be disabled, and changed permissions need authorization. Application authority also survives GDAP expiry unless separately revoked; the UI must make that distinction visible.

**Concrete endpoint limitation:** PCB calls `/users/{id}/licenseDetails` in `PersonDirectoryReader.cs:58` and `OffboardingOperation.cs:836`. Microsoft documents that API as not supporting application permissions. Before enabling an app-only profile, replace the necessary reads with supported alternatives where possible—such as user license properties plus SKU metadata—or explicitly mark the detailed capability unavailable. Do not promise that adding `AcquireTokenForClient` converts every existing workflow unchanged. [License-details support](https://learn.microsoft.com/en-us/graph/api/user-list-licensedetails?view=graph-rest-1.0).

**5.6 F — GDAP-first onboarding and coverage checks**

Use the right API for each task:

- Partner Center customer inventory: existing `/v1/customers`.
- GDAP relationships: Microsoft Graph `/v1.0/tenantRelationships/delegatedAdminRelationships`.
- Submission: relationship `/requests` with `lockForApproval`.
- Security-group role assignment: relationship `/accessAssignments`.
- Delegated application consent: Partner Center `/v1/customers/{customerId}/applicationconsents`.

These are documented surfaces, not hypothetical Partner Center method names. [Create relationship](https://learn.microsoft.com/en-us/graph/api/tenantrelationship-post-delegatedadminrelationships?view=graph-rest-1.0), [submit request](https://learn.microsoft.com/graph/api/delegatedadminrelationship-post-requests?view=graph-rest-1.0), [assign access](https://learn.microsoft.com/en-us/graph/api/delegatedadminrelationship-post-accessassignments?view=graph-rest-1.0), [customer consent API](https://learn.microsoft.com/en-us/partner-center/developer/control-panel-vendor-apis).

Start with read-only relationship inventory; add `DelegatedAdminRelationship.ReadWrite.All` only for an explicit relationship-management feature. Verify partner-program, user-role, application-permission, and provisioning prerequisites against the selected API mode.

Coverage must consider active relationships, their access assignments, and the SAM user’s effective group membership. A role merely listed in a relationship is not enough. Support multiple relationships per customer rather than relying on the existing single `GdapRelationshipId`.

Generate focused requests for gaps and track customer approval. Automate consent only after the necessary authority is established. Do not attempt to convert delegated consent into app-only consent through this API.

For Exchange, a future delegated provider could reduce app-only provisioning for some portfolios: Microsoft documents `Connect-ExchangeOnline -DelegatedOrganization` for CSP/GDAP/guest access. That is a separate implementation and validation project, not behavior PCB already has. [Delegated Exchange connections](https://learn.microsoft.com/en-us/powershell/exchange/connect-to-exchange-online-powershell).

---

**6. Concrete phased design**

**Phase 1 — Make existing connections truthful and recoverable**

This delivers the largest immediate UX improvement with the smallest architectural change.

- Introduce a connection record distinct from `Tenant.Status`.
- Correct token lifetime, GDAP detection, and permission documentation.
- Implement generation-safe SAM rotation and expiry-aware access-token handling.
- Add staged credential validation and a structured error model.
- Fix Exchange organization binding and plaintext temporary secret handling.
- Present **Reconnect partner account** in Settings through the system browser; retain the existing CLI as a fallback.
- Verify both Partner Center and a selected customer Graph capability before calling the shared connection healthy.

Do not require the operator to understand refresh tokens or paste one into a textbox for normal setup.

**Phase 2 — Make GDAP customers arrive with actionable state**

Extend sync to discover relationships, assignments, expiration, consent state where inspectable, and operation coverage.

Offer concrete actions:

- **Verify access**
- **Grant application consent**
- **Request missing role**
- **Complete group assignment**
- **Reconnect partner account**
- **Configure Exchange**

Show a reviewable batch plan before creating relationships or modifying grants. Existing relationships with sufficient authority should need no customer credential handling; new or expanded authority still needs the appropriate customer approval.

Keep structural evidence separate from successful probes. A read probe cannot prove write authorization, and probes must not create/delete customer objects to test permissions.

**Phase 3 — Add direct-customer application connections**

Add explicit connection kinds, for example:

```text
PartnerSamDelegated
DirectApplication
DirectUserDelegated
```

These are proposed PCB types, not Microsoft API names.

Resolve connections by tenant, workload, capability, and permitted execution context. Support separate Graph and Exchange connections for the same customer.

Implement app-only endpoint adaptations, certificate rotation, consent verification, and customer disconnect/revocation instructions. If a delegated connection fails, never silently switch to a more powerful app-only connection; that changes the authority under which the operation runs.

**Phase 4 — Add WAM for desktop delegated connections**

Introduce a Windows interaction coordinator with a real parent window and account binding. Keep browser fallback.

Use WAM where the connection can remain broker-managed in the current Windows user session. Keep server automation and portable SAM credentials separate. Validate GDAP and guest behavior against real test tenants before offering it as the default partner connection.

This avoids putting the largest Windows integration change on the critical path to fixing consent and role visibility.

**6.1 Proposed tenant connection presentation**

Use workload-specific state rather than one green tenant badge.

| Display | Meaning | Action |
|---|---|---|
| **Connected via GDAP** | Partner connection valid; relationship and effective assignment verified; required consent observed. | View roles, scopes, expiry, last verification. |
| **Connected via application consent** | Customer application connection verified for named capabilities. | View permissions, certificate expiry, revoke instructions. |
| **Connected via signed-in account** | Direct delegated account bound to this customer. | Show account; reconnect/change account. |
| **Needs partner sign-in** | Shared partner credential needs user interaction. | Reconnect once; identify affected tenants. |
| **Needs customer sign-in** | A specific direct delegated connection needs interaction. | Sign in for that tenant. |
| **Needs consent** | Required app grant is absent or incomplete. | Grant consent using the appropriate identity. |
| **Limited access** | Some capabilities available; others lack verified authorization. | Show exact gaps and permitted work. |
| **Awaiting customer approval / delegation expired** | Relationship lifecycle blocks access. | Open approval request or request renewal. |
| **Exchange not connected** | Graph can work, but Exchange prerequisites/authorization are incomplete. | Configure and verify Exchange. |
| **Unverified / temporarily unavailable** | Insufficient evidence or transient upstream failure. | Retry verification; retain last-known result and time. |

Example:

> **Fabrikam — Connected via GDAP · Limited access**  
> Users, groups, and licensing verified.  
> MFA reset requires Authentication Administrator.  
> Device retirement requires Intune authorization and managed-device permissions.  
> Exchange requires application consent and RBAC.  
> Last verified: 14:32.

Distinguish what is **observed**, **required by the operation**, and **not yet verified**. Avoid asserting a precise missing role from a generic 403.

A recovery response should include the connection and resource involved, sanitized error code, correlation/request ID, retryability, permitted next action, and whether any operation steps already completed.

**6.2 Invariants the implementation must preserve**

1. **Instance administration and tenant authorization stay independent.** Managing a shared partner credential must not grant access to customer data. Creating a connection must not quietly create a tenant Owner grant outside the established onboarding policy.
2. **Browser/WAM account selection is not authorization.** Verify the selected identity and target tenant, then apply PCB grants.
3. **Tokens stay outside React.** Do not store Microsoft tokens in browser local storage, callback URLs, logs, evidence, or clipboard-based setup.
4. **No persisted plaintext credentials.** Include temporary files and configuration—not just the database—in this requirement.
5. **Access-token cache keys include connection, account, authority/cloud, target tenant, client, resource/scopes, and relevant claims context.** A SAM refresh token can legitimately span customers; access tokens cannot be reused merely because the same user obtained them.
6. **Credential replacement invalidates affected cache generations.** Concurrent work must not resurrect superseded connections.
7. **Interactive prompts are explicitly initiated and serialized.** A background fan-out must not launch ten simultaneous authentication dialogs.
8. **Callbacks bind to an authenticated initiating operator and connection attempt.** Use state, PKCE, appropriate nonce validation, expiration, and exact redirect validation. A consent success query parameter alone is not proof of a grant.
9. **Audit both actors.** Record the PCB operator and the Microsoft user/application connection used, along with customer, workload, operation, result, and correlation IDs.
10. **Revocation and partial failure remain visible.** GDAP expiry does not revoke independent app-only consent. A failed license cleanup must not obscure a successful account block.

**6.3 Validation before release**

Use controlled tenants to test:

- Complete GDAP, missing role, missing group assignment, missing consent, and expired relationship.
- Partner MFA enforcement, sign-in-frequency challenge, blocked device code, and prolonged inactivity recovery.
- Concurrent refresh versus manual reconnect, including separate processes.
- Two direct customer accounts with similar usernames and guest-resource-tenant access.
- WAM with and without an available console HWND; browser fallback; phishing-resistant authentication and device-compliance policies.
- Exchange consent without RBAC, wrong organization mapping, expired certificate, and partial cmdlet failure.
- App-only execution of every read, write, and verification endpoint—including delegated-only license details.
- Disconnect/revocation, audit redaction, and preservation of PCB tenant grants.

The release criterion should be demonstrable: an authorized operator connects the partner once, sees which customer capabilities are actually ready, completes focused onboarding for the exceptions, and performs routine supported work without juggling customer browser sessions.