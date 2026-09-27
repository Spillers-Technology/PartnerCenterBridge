using System.Diagnostics;
using System.Text;

namespace PartnerCenterBridge.Exchange;

public record PwshResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs a PowerShell 7 script out-of-process, passing a JSON payload and capturing output.</summary>
public interface IPwshRunner
{
    /// <summary>
    /// Invoke <paramref name="scriptPath"/> and write <paramref name="payloadJson"/> (UTF-8) to its
    /// standard input, then close it; the script reads the payload from stdin. The payload may carry
    /// secrets, so it is never written to disk or put on the command line. Returns exit code +
    /// captured stdout/stderr.
    /// </summary>
    Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IPwshRunner"/> that shells out to <c>pwsh</c>. Kept deliberately thin so the
/// higher-level <see cref="ExchangeOnlineService"/> is unit-testable against a fake runner.
/// </summary>
public class PwshRunner : IPwshRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pwshPath;
    private readonly int _timeoutSeconds;

    public PwshRunner(string pwshPath = "pwsh", int timeoutSeconds = 180)
    {
        _pwshPath = pwshPath;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<PwshResult> RunAsync(string scriptPath, string payloadJson, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _pwshPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);

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
            // Hand the payload over the pipe and close it so the script's read sees end-of-input.
            // Raw UTF-8 bytes: independent of the console input code page on either side.
            var stdin = proc.StandardInput.BaseStream;
            try
            {
                await stdin.WriteAsync(Utf8NoBom.GetBytes(payloadJson), timeout.Token);
                await stdin.FlushAsync(timeout.Token);
            }
            catch (IOException)
            {
                // pwsh exited before reading its input (e.g. it could not start the script); its
                // exit code and stderr below say why.
            }
            finally
            {
                try { proc.StandardInput.Close(); } catch (IOException) { }
            }

            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            // Reap the killed process so it does not linger as a zombie/handle.
            try { proc.WaitForExit(5000); } catch { /* best effort */ }
            throw new TimeoutException($"pwsh script '{Path.GetFileName(scriptPath)}' timed out after {_timeoutSeconds}s.");
        }

        return new PwshResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
