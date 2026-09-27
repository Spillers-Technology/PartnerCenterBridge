using System.Diagnostics;
using System.Text.Json;
using PartnerCenterBridge.Exchange;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Exercises the real out-of-process plumbing (payload on stdin, stdout capture) with a trivial
/// script — not the EXO module. Soft-skips where pwsh is unavailable.
/// </summary>
public class PwshRunnerTests
{
    // Reads the payload the way exo-op.ps1 does: raw UTF-8 bytes from stdin until it is closed.
    private const string ReadPayload =
        """
        $buffer = [IO.MemoryStream]::new()
        [Console]::OpenStandardInput().CopyTo($buffer)
        $j = [Text.Encoding]::UTF8.GetString($buffer.ToArray()) | ConvertFrom-Json
        """;

    [Fact]
    public async Task Runner_passes_payload_on_stdin_and_captures_stdout()
    {
        if (!PwshAvailable()) return; // soft skip on machines without pwsh 7

        var result = await RunScriptAsync(ReadPayload + "\n[ordered]@{ ok = $true; op = $j.operation } | ConvertTo-Json -Compress",
            """{"operation":"ping"}""");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"op\":\"ping\"", result.Stdout);
    }

    /// <summary>
    /// The payload carries the PFX password. While the script runs, the password must be readable
    /// from stdin (non-ASCII intact) yet absent from pwsh's command line and from the legacy
    /// %TEMP%\exo-*.json payload files. Narrowing the scan to that name keeps it bounded on busy
    /// CI runners, where other tests may create many unrelated temporary files.
    /// </summary>
    [Fact]
    public async Task Runner_never_puts_the_payload_on_disk_or_the_command_line()
    {
        if (!PwshAvailable()) return;
        var secret = "pfx-" + Guid.NewGuid().ToString("N") + "-" + (char)0xE4 + (char)0x20AC;   // non-ASCII: a-umlaut, euro sign

        var result = await RunScriptAsync(ReadPayload +
            """

            $secret = $j.connect.certificatePassword
            $leaks = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'exo-*.json' -File -ErrorAction SilentlyContinue |
                Where-Object { try { (Get-Content -Raw -LiteralPath $_.FullName -ErrorAction Stop) -like "*$secret*" } catch { $false } } |
                ForEach-Object Name)
            [ordered]@{
                secretBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($secret))
                onCommandLine = [Environment]::CommandLine.Contains($secret)
                leaks = $leaks
            } | ConvertTo-Json -Compress
            """,
            JsonSerializer.Serialize(new { operation = "getMailbox", connect = new { certificatePassword = secret } }));

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(ExchangeOnlineService.ExtractJson(result.Stdout)!);
        Assert.Equal(secret, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(output.RootElement.GetProperty("secretBase64").GetString()!)));
        Assert.False(output.RootElement.GetProperty("onCommandLine").GetBoolean());
        Assert.Empty(output.RootElement.GetProperty("leaks").EnumerateArray());
    }

    [Fact]
    public async Task Runner_times_out_and_kills_a_script_that_never_finishes()
    {
        if (!PwshAvailable()) return;

        await Assert.ThrowsAsync<TimeoutException>(() => RunScriptAsync("Start-Sleep -Seconds 60", "{}", timeoutSeconds: 3));
    }

    private static async Task<PwshResult> RunScriptAsync(string body, string payload, int timeoutSeconds = 60)
    {
        var script = Path.Combine(Path.GetTempPath(), $"pcb-runner-test-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(script, body);
        try
        {
            return await new PwshRunner("pwsh", timeoutSeconds).RunAsync(script, payload);
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
