using System.Diagnostics;
using System.Text;

namespace PartnerCenterBridge.Exchange;

public record PwshResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs a PowerShell 7 script out-of-process, passing a JSON payload and capturing output.</summary>
public interface IPwshRunner
{
    /// <summary>
    /// Invoke <paramref name="scriptPath"/> with the given JSON payload written to a temp file and
    /// passed as <c>-PayloadPath</c>. Returns exit code + captured stdout/stderr.
    /// </summary>
    Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IPwshRunner"/> that shells out to <c>pwsh</c>. Kept deliberately thin so the
/// higher-level <see cref="ExchangeOnlineService"/> is unit-testable against a fake runner.
/// </summary>
public class PwshRunner : IPwshRunner
{
    private readonly string _pwshPath;
    private readonly int _timeoutSeconds;

    public PwshRunner(string pwshPath = "pwsh", int timeoutSeconds = 180)
    {
        _pwshPath = pwshPath;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
    {
        var payloadPath = Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(payloadPath, payloadJson, ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pwshPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-PayloadPath");
            psi.ArgumentList.Add(payloadPath);

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new TimeoutException($"pwsh script '{Path.GetFileName(scriptPath)}' timed out after {_timeoutSeconds}s.");
            }

            return new PwshResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            try { File.Delete(payloadPath); } catch { /* best effort */ }
        }
    }
}
