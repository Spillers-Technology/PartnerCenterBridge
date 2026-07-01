using System.Diagnostics;
using PartnerCenterBridge.Exchange;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Exercises the real out-of-process plumbing (temp payload file, -PayloadPath arg, stdout capture)
/// with a trivial script — not the EXO module. Soft-skips where pwsh is unavailable.
/// </summary>
public class PwshRunnerTests
{
    [Fact]
    public async Task Runner_passes_payload_and_captures_stdout()
    {
        if (!PwshAvailable()) return; // soft skip on machines without pwsh 7

        var script = Path.Combine(Path.GetTempPath(), $"pcb-runner-test-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(script,
            """
            param([string]$PayloadPath)
            $j = Get-Content -Raw $PayloadPath | ConvertFrom-Json
            [ordered]@{ ok = $true; op = $j.operation } | ConvertTo-Json -Compress
            """);
        try
        {
            var runner = new PwshRunner("pwsh", timeoutSeconds: 60);
            var result = await runner.RunAsync(script, """{"operation":"ping"}""");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("\"op\":\"ping\"", result.Stdout);
        }
        finally { File.Delete(script); }
    }

    private static bool PwshAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = "-NoProfile -Command \"exit 0\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            p.WaitForExit(10000);
            return true;
        }
        catch { return false; }
    }
}
