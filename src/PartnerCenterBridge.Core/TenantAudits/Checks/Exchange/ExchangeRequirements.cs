namespace PartnerCenterBridge.Core.TenantAudits.Checks.Exchange;

internal static class ExchangeRequirements
{
    public static readonly AuditRequirement AppOnly = AuditRequirement.Dependency(
        "Exchange Online app-only access",
        "PCB's Exchange app registration and certificate (Settings > Workbench), PowerShell 7 and the ExchangeOnlineManagement module; the app needs Exchange.ManageAsApp and an Exchange role that can read mailboxes (for example View-Only Organization Management).");

    public static void AddPartialErrors(AuditCheckOutput output, AuditMailboxReport report)
    {
        foreach (var e in report.PartialErrors) output.Note($"Exchange read incomplete: {e}");
    }
}
