namespace PartnerCenterBridge.Api.GitSync;

/// <summary>
/// Optional git-backed history for config snapshots: each capture is written as one JSON file per
/// section under <c>tenants/{tenant}/{section}.json</c> and committed/pushed to a real git remote
/// (GitHub or any host that speaks standard HTTPS git). Disabled unless <see cref="RepoUrl"/> is
/// set -- this is purely additive on top of the Postgres-stored snapshot, never a replacement.
/// </summary>
public class GitSyncOptions
{
    public const string SectionName = "GitSync";

    /// <summary>e.g. <c>https://github.com/your-org/tenant-config-history.git</c>. Leave unset to disable git sync entirely.</summary>
    public string? RepoUrl { get; set; }

    public string Branch { get; set; } = "main";

    /// <summary>Secret. A token with push access (a GitHub PAT with `repo` scope, or equivalent). Sent per-request as an HTTP auth header, never persisted to the working copy's git config.</summary>
    public string? Token { get; set; }

    public string CommitterName { get; set; } = "Partner Center Bridge";
    public string CommitterEmail { get; set; } = "pcbridge@localhost";

    /// <summary>Local working copy, expected to be a persistent volume (like <c>/keys</c>, <c>/packages</c>).</summary>
    public string LocalPath { get; set; } = "/git-sync";

    public bool Enabled => !string.IsNullOrWhiteSpace(RepoUrl);
}
