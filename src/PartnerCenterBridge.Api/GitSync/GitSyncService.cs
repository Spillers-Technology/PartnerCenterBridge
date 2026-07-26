using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Api.GitSync;

/// <summary>
/// Commits a completed <see cref="ConfigSnapshotRun"/> to the configured git remote by shelling
/// out to the <c>git</c> CLI -- same out-of-process pattern as <c>PwshRunner</c> for EXO, and
/// deliberately not LibGit2Sharp: no new native-binary/libc compatibility surface to worry about
/// in the container, just a well-known tool already installed via the Dockerfile.
/// </summary>
public class GitSyncService
{
    private readonly GitSyncOptions _options;
    private readonly ILogger<GitSyncService> _log;

    // Every operation touches the one shared working copy on disk; serialize so two concurrent
    // snapshot captures (different tenants) can't interleave writes/commits into the same tree.
    private static readonly SemaphoreSlim RepoLock = new(1, 1);

    public GitSyncService(IOptions<GitSyncOptions> options, ILogger<GitSyncService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Writes each successful section to disk and commits+pushes if anything changed. Returns the
    /// resulting commit sha, or null if git sync is disabled or the push failed -- failure here
    /// never fails the snapshot itself, since the Postgres copy is already the source of truth.
    /// </summary>
    public async Task<string?> CommitSnapshotAsync(Tenant tenant, ConfigSnapshotRun run, IReadOnlyList<ConfigSnapshotSection> sections, CancellationToken ct)
    {
        if (!Enabled) return null;

        await RepoLock.WaitAsync(ct);
        try
        {
            return await CommitCoreAsync(tenant, run, sections, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Git sync failed for tenant {Tenant}; the snapshot is still saved.", tenant.DisplayName);
            return null;
        }
        finally
        {
            RepoLock.Release();
        }
    }

    private async Task<string?> CommitCoreAsync(Tenant tenant, ConfigSnapshotRun run, IReadOnlyList<ConfigSnapshotSection> sections, CancellationToken ct)
    {
        await EnsureRepoAsync(ct);

        var dir = Path.Combine(_options.LocalPath, "tenants", Slugify(tenant.DisplayName));
        Directory.CreateDirectory(dir);
        foreach (var section in sections.Where(s => s.Error is null))
        {
            var pretty = JsonSerializer.Serialize(JsonNode.Parse(section.ContentJson), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(dir, $"{section.SectionId}.json"), pretty, ct);
        }

        await RunGitAsync(needsAuth: false, ct, "add", "-A");
        var status = await RunGitAsync(needsAuth: false, ct, "status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status.Stdout))
            return (await RunGitAsync(needsAuth: false, ct, "rev-parse", "HEAD")).Stdout.Trim();

        await RunGitAsync(needsAuth: false, ct,
            "-c", $"user.name={_options.CommitterName}", "-c", $"user.email={_options.CommitterEmail}",
            "commit", "-m", $"Snapshot: {tenant.DisplayName} at {run.StartedAt:u} by {run.Operator}");
        await RunGitAsync(needsAuth: true, ct, "push", "origin", _options.Branch);
        return (await RunGitAsync(needsAuth: false, ct, "rev-parse", "HEAD")).Stdout.Trim();
    }

    private async Task EnsureRepoAsync(CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(_options.LocalPath, ".git")))
        {
            await RunGitAsync(needsAuth: true, ct, "fetch", "origin", _options.Branch);
            await RunGitAsync(needsAuth: false, ct, "checkout", _options.Branch);
            await RunGitAsync(needsAuth: false, ct, "reset", "--hard", $"origin/{_options.Branch}");
            return;
        }

        Directory.CreateDirectory(_options.LocalPath);
        await RunGitAsync(needsAuth: true, ct, "clone", "--branch", _options.Branch, _options.RepoUrl!, ".");
    }

    /// <summary>
    /// Auth is a per-request HTTP header (<c>-c http.extraHeader=...</c>), not a credential stored
    /// in the working copy's config or embedded in the remote URL -- the token never touches disk.
    /// </summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(bool needsAuth, CancellationToken ct, params string[] args)
    {
        var argList = new List<string>();
        if (needsAuth)
        {
            var basicAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{_options.Token}"));
            argList.Add("-c");
            argList.Add($"http.extraHeader=AUTHORIZATION: basic {basicAuth}");
        }
        argList.AddRange(args);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _options.LocalPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in argList) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"git {string.Join(' ', args)} timed out.");
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Slugify(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
