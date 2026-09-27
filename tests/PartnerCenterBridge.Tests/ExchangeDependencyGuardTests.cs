using PartnerCenterBridge.Api.Diagnostics;
using PartnerCenterBridge.Exchange;

namespace PartnerCenterBridge.Tests;

public class ExchangeDependencyGuardTests
{
    private static ExchangeDependencyState State(string? pwshPath = "C:\\pwsh.exe", string? module = "3.5.0",
        bool appId = true, bool certificate = true) =>
        new("pwsh", pwshPath, pwshPath is null ? null : "7.4.6", null, module, null, appId, "C:\\certs\\exo.pfx", certificate);

    [Theory]
    [InlineData(false, true, true, true, "PowerShell 7")]
    [InlineData(true, false, true, true, "ExchangeOnlineManagement")]
    [InlineData(true, true, false, true, "Exchange:AppId")]
    [InlineData(true, true, true, false, "certificate")]
    public async Task Missing_dependency_fails_fast_with_its_reason(bool pwsh, bool module, bool appId, bool certificate, string mentioned)
    {
        var inner = new CountingRunner();
        var runner = new GuardedPwshRunner(inner,
            new FixedProbe(State(pwsh ? "C:\\pwsh.exe" : null, module ? "3.5.0" : null, appId, certificate)));

        var error = await Assert.ThrowsAsync<ExchangeDependencyException>(() => runner.RunAsync("script.ps1", "{}"));

        Assert.StartsWith("Exchange Online dependency not configured: ", error.Message);
        Assert.Contains(mentioned, error.Message);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Ready_dependencies_run_the_script()
    {
        var inner = new CountingRunner();
        var result = await new GuardedPwshRunner(inner, new FixedProbe(State())).RunAsync("script.ps1", "{}");

        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Guard_uses_the_freshly_discovered_executable_path()
    {
        var expected = "C:\\new-install\\pwsh.exe";
        string? selected = null;
        var inner = new CountingRunner();
        var runner = new GuardedPwshRunner(inner, new FixedProbe(State(pwshPath: expected)), path =>
        {
            selected = path;
            return inner;
        });

        await runner.RunAsync("script.ps1", "{}");

        Assert.Equal(expected, selected);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void Locator_finds_nothing_for_a_missing_command()
    {
        Assert.Null(ExecutableLocator.Find("pcb-definitely-not-a-real-command"));
    }

    private sealed class FixedProbe(ExchangeDependencyState state) : IExchangeDependencyProbe
    {
        public Task<ExchangeDependencyState> GetAsync(CancellationToken ct) => Task.FromResult(state);
        public Task InvalidateAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingRunner : IPwshRunner
    {
        public int Calls { get; private set; }

        public Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new PwshResult(0, "{}", ""));
        }
    }
}
