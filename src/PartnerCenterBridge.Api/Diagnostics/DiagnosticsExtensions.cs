using Microsoft.Extensions.Options;
using PartnerCenterBridge.Exchange;

namespace PartnerCenterBridge.Api.Diagnostics;

public static class DiagnosticsExtensions
{
    /// <summary>
    /// System diagnostics, the SAM status service, and the Exchange Online runner guarded by the
    /// dependency probe (so EXO operations fail fast with a clear reason).
    /// </summary>
    public static IServiceCollection AddBridgeDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<IExchangeDependencyProbe, ExchangeDependencyProbe>();
        services.AddSingleton<IPwshRunner>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value;
            return new GuardedPwshRunner(new PwshRunner(o.PwshPath, o.TimeoutSeconds),
                sp.GetRequiredService<IExchangeDependencyProbe>());
        });
        services.AddScoped<ISamStatusService, SamStatusService>();
        services.AddScoped<ISystemDiagnostics, SystemDiagnostics>();
        return services;
    }

    /// <summary>
    /// <c>doctor</c>: the same checks as /api/system/diagnostics (full detail -- whoever can run the
    /// binary already has its configuration), in human form. Exit 0 unless a check is an Error.
    /// </summary>
    public static async Task<int> RunDoctorAsync(IServiceProvider services, TextWriter output, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var report = await scope.ServiceProvider.GetRequiredService<ISystemDiagnostics>().RunAsync(ct);

        output.WriteLine($"Partner Center Bridge {Hosting.HostingInfo.ProductVersion} -- doctor");
        output.WriteLine();
        foreach (var check in report.Checks)
        {
            var tag = check.Status switch
            {
                SystemCheckStatus.Ok => "  OK  ",
                SystemCheckStatus.Warning => " WARN ",
                SystemCheckStatus.Error => "ERROR ",
                _ => " N/C  "
            };
            output.WriteLine($"[{tag}] {check.Label}: {check.Detail}");
            if (check.Fix is { } fix)
            {
                output.WriteLine($"         fix: {fix.Label}");
                if (fix.Command is not null) output.WriteLine($"              run: {fix.Command}");
                if (fix.Route is not null) output.WriteLine($"              open: {fix.Route} in the app");
            }
        }
        output.WriteLine();
        output.WriteLine($"Capabilities: Graph={YesNo(report.Capabilities.Graph)}, Partner Center={YesNo(report.Capabilities.PartnerCenter)}, " +
                         $"Exchange Online={YesNo(report.Capabilities.Exchange)}");
        var errors = report.Checks.Count(check => check.Status == SystemCheckStatus.Error);
        output.WriteLine(errors == 0 ? "No errors." : $"{errors} error(s).");
        return errors == 0 ? 0 : 1;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
