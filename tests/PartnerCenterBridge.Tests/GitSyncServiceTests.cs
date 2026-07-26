using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.GitSync;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Exercises GitSyncService against a real local bare repo standing in for GitHub -- git clone,
/// fetch, and push all work the same over a filesystem path as over HTTPS, so this is a faithful
/// (and hermetic, no network) test of the actual commit/push mechanics. Requires `git` on PATH,
/// same assumption the repo itself already makes (you needed git to clone it).
/// </summary>
public class GitSyncServiceTests : IDisposable
{
    private readonly string _remoteDir;
    private readonly string _localDir;

    public GitSyncServiceTests()
    {
        _remoteDir = CreateTempDir("pcb-git-remote");
        _localDir = TempPath("pcb-git-local"); // never created here -- GitSyncService creates it itself on first clone.

        RunGit(_remoteDir, "init", "--bare", "-b", "main");

        // Seed an initial commit on main -- `git clone --branch main` needs the branch to exist.
        var seedDir = CreateTempDir("pcb-git-seed");
        try
        {
            RunGit(seedDir, "init", "-b", "main");
            File.WriteAllText(Path.Combine(seedDir, "README.md"), "seed");
            RunGit(seedDir, "-c", "user.name=seed", "-c", "user.email=seed@example.com", "add", "-A");
            RunGit(seedDir, "-c", "user.name=seed", "-c", "user.email=seed@example.com", "commit", "-m", "seed");
            RunGit(seedDir, "remote", "add", "origin", _remoteDir);
            RunGit(seedDir, "push", "origin", "main");
        }
        finally
        {
            DeleteWithRetry(seedDir);
        }
    }

    [Fact]
    public async Task CommitSnapshotAsync_WritesSectionFilesAndPushesToRemote()
    {
        var service = NewService();
        var tenant = new Tenant { TenantId = "11111111-1111-1111-1111-111111111111", DisplayName = "Contoso Ltd" };
        var run = new ConfigSnapshotRun { TenantId = tenant.Id, Operator = "tester" };
        var sections = new List<ConfigSnapshotSection>
        {
            new()
            {
                RunId = run.Id, SectionId = "conditional-access-policies", SectionName = "Conditional Access Policies",
                ContentJson = """[{"id":"1","displayName":"Require MFA"}]"""
            }
        };

        var sha = await service.CommitSnapshotAsync(tenant, run, sections, CancellationToken.None);

        Assert.NotNull(sha);
        var filePath = Path.Combine(_localDir, "tenants", "contoso-ltd", "conditional-access-policies.json");
        Assert.True(File.Exists(filePath));
        Assert.Contains("Require MFA", await File.ReadAllTextAsync(filePath));

        // Prove it's really on the remote, not just the local working copy: clone fresh elsewhere.
        var verifyDir = TempPath("pcb-git-verify");
        try
        {
            RunGit(Path.GetTempPath(), "clone", _remoteDir, verifyDir);
            Assert.True(File.Exists(Path.Combine(verifyDir, "tenants", "contoso-ltd", "conditional-access-policies.json")));
        }
        finally
        {
            DeleteWithRetry(verifyDir);
        }
    }

    [Fact]
    public async Task CommitSnapshotAsync_SecondIdenticalCapture_DoesNotCreateEmptyCommit()
    {
        var service = NewService();
        var tenant = new Tenant { TenantId = "22222222-2222-2222-2222-222222222222", DisplayName = "Fabrikam" };
        var sections = new List<ConfigSnapshotSection>
        {
            new() { SectionId = "named-locations", SectionName = "Named Locations", ContentJson = """[{"id":"1","displayName":"HQ"}]""" }
        };

        var run1 = new ConfigSnapshotRun { TenantId = tenant.Id, Operator = "tester" };
        var sha1 = await service.CommitSnapshotAsync(tenant, run1, sections, CancellationToken.None);

        var run2 = new ConfigSnapshotRun { TenantId = tenant.Id, Operator = "tester" };
        var sha2 = await service.CommitSnapshotAsync(tenant, run2, sections, CancellationToken.None);

        Assert.Equal(sha1, sha2); // nothing changed, so no second commit was made
    }

    [Fact]
    public async Task CommitSnapshotAsync_Disabled_ReturnsNull()
    {
        var options = Options.Create(new GitSyncOptions { RepoUrl = null });
        var service = new GitSyncService(options, NullLogger<GitSyncService>.Instance);
        var tenant = new Tenant { TenantId = "1", DisplayName = "X" };
        var run = new ConfigSnapshotRun { TenantId = tenant.Id, Operator = "tester" };

        var sha = await service.CommitSnapshotAsync(tenant, run, [], CancellationToken.None);

        Assert.Null(sha);
    }

    private GitSyncService NewService()
    {
        var options = Options.Create(new GitSyncOptions
        {
            RepoUrl = _remoteDir,
            Branch = "main",
            Token = "unused-for-local-path-remote",
            LocalPath = _localDir
        });
        return new GitSyncService(options, NullLogger<GitSyncService>.Instance);
    }

    public void Dispose()
    {
        DeleteWithRetry(_remoteDir);
        DeleteWithRetry(_localDir);
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = TempPath(prefix);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempPath(string prefix) => Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");

    /// <summary>
    /// Windows doesn't always release file handles the instant a child git.exe process exits
    /// (antivirus scanning, delayed I/O completion), so an immediate Directory.Delete on a
    /// just-used .git folder can throw UnauthorizedAccessException. Retry with backoff rather than
    /// failing test cleanup over a timing quirk that has nothing to do with GitSyncService itself.
    /// </summary>
    private static void DeleteWithRetry(string dir)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private static void RunGit(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} (in {workDir}) failed: {stderr}");
    }
}
