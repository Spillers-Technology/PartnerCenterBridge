# Microsoft sign-in publisher registration

Windows builds bundle one publisher-owned multitenant public client. Technicians sign in with
each organization's admin account and consent under its policies; they do not register a customer app.

- Name: PartnerCenterBridge
- Public client ID: `065d8cdd-4146-4d2b-bb5f-377ed8f659cc`
- Application object ID: `568412cb-7a7d-4e71-9efd-1585a9d991db`
- Owner: Joseph Spillers
- Audience: work/school accounts in any Microsoft Entra organization
- Desktop redirect: `http://localhost`
- Client secrets/certificates: none

This registration uses the existing Entra tenant and provisions no VM, App Service, hosted
authentication server, paid hosting plan, or new subscription. See Microsoft's
[Entra ID Free documentation](https://learn.microsoft.com/azure/cost-management-billing/manage/microsoft-entra-id-free).
Microsoft 365/Intune/premium-directory features still require the customer's existing licenses;
the registration neither purchases nor bypasses them.

The release variable `MICROSOFT_SIGN_IN_CLIENT_ID` and the Local Workbench's default
`PcbMicrosoftClientId` metadata use this public ID. Private distributors can override it through
`publish-local.ps1 -MicrosoftClientId`. The normal technician flow has no app-setup menu.

## Declared delegated Graph scopes

These are consent requests, not automatically granted access. No unattended application
permissions or tenant-wide admin consent were granted during creation. Scope IDs were resolved
from Microsoft Graph's current delegated-scope catalog.

| Capability | Delegated scopes |
| --- | --- |
| Tenant/directory discovery | Organization.Read.All, Directory.Read.All |
| User onboarding/profile changes | User.ReadWrite.All |
| Account disable, password reset, session revocation | User.EnableDisableAccount.All, User-PasswordProfile.ReadWrite.All, User.RevokeSessions.All |
| MFA-method administration | UserAuthenticationMethod.ReadWrite.All |
| License assignments | LicenseAssignment.ReadWrite.All |
| Groups and role visibility | Group.ReadWrite.All, RoleManagement.Read.Directory |
| Intune app deployment | DeviceManagementApps.ReadWrite.All |
| Managed-device visibility/retirement | DeviceManagementManagedDevices.Read.All, DeviceManagementManagedDevices.PrivilegedOperations.All |
| Configuration capture | DeviceManagementConfiguration.Read.All, Policy.Read.All |
| Inbox-rule repair where mailbox access permits it | MailboxSettings.ReadWrite |
| Optional sign-in history | AuditLog.Read.All |

Graph also requires the signed-in admin's appropriate roles. Mailbox access and premium licenses
remain independent requirements. MSAL requests the registered Graph `.default` scopes and
handles browser credentials/MFA; PCB persists protected per-operator/tenant token state locally.

Publisher verification is not complete. Organizations requiring verified publishers or their
own admin-consent workflow may restrict sign-in. Live customer consent/sign-in still needs
operator validation. Partner Center/GDAP and certificate-based Exchange remain separate integrations.
