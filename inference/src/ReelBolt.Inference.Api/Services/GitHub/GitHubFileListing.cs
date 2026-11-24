namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>One file or folder of a repository at one commit, for the file selection tree.</summary>
public sealed record GitHubRepositoryFile(string Path, bool IsDirectory, long Size);

/// <summary>
/// Every file and folder of one branch, flattened, for the file selection tree. <see cref="CommitSha"/>
/// is null for a repository with no commits yet. <see cref="Truncated"/> is true when GitHub could not
/// list the whole tree, or it held more entries than one answer carries — and a repository GitHub
/// cannot list in full is also one a pull refuses, which is why the dashboard explains it rather than
/// offering a partial tree.
/// </summary>
public sealed record GitHubFileListing(
    string Repository,
    string Branch,
    string? CommitSha,
    IReadOnlyList<GitHubRepositoryFile> Entries,
    bool Truncated);

/// <summary>A branch that does not exist in the repository (a typo, or deleted since the page loaded).</summary>
public sealed class GitHubBranchNotFoundException : Exception
{
    public GitHubBranchNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads a repository's file tree and branches for the selection UI. Both entry points resolve the
/// commit from a branch NAME on the server — no tree SHA ever comes from a request — so a caller can
/// only see the tree of a repository it was already allowed to reach.
/// </summary>
public static class GitHubFileTree
{
    /// <summary>Most entries one listing carries, so a huge repository cannot flood the dashboard.</summary>
    public const int MaxEntriesListed = 20_000;

    /// <summary>
    /// The files of <paramref name="branch"/> (null = the default branch) at its current head. A branch
    /// that does not exist throws <see cref="GitHubBranchNotFoundException"/>; the default branch of an
    /// empty repository yields an empty listing instead, because "nothing to sync yet" is the truth.
    /// </summary>
    public static async Task<GitHubFileListing> ListAsync(
        IGitHubAppClient gitHub,
        long installationId,
        string fullName,
        string defaultBranch,
        string? branch,
        CancellationToken ct)
    {
        (string owner, string repo) = GitHubRepositoryPullService.SplitFullName(fullName);
        bool explicitBranch = !string.IsNullOrWhiteSpace(branch);
        string target = explicitBranch ? branch!.Trim() : defaultBranch;
        if (explicitBranch && !GitHubBranchName.IsValid(target))
            throw new GitHubBranchNotFoundException($"'{target}' is not a valid branch name.");

        string head;
        try
        {
            head = await gitHub.GetBranchHeadAsync(installationId, owner, repo, target, ct);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (!explicitBranch || string.Equals(target, defaultBranch, StringComparison.Ordinal))
                return new GitHubFileListing(fullName, target, null, [], Truncated: false);
            throw new GitHubBranchNotFoundException($"'{target}' is not a branch of {fullName}.");
        }

        GitHubRepoTree tree = await gitHub.GetTreeAsync(installationId, owner, repo, head, ct);
        return FromTree(fullName, target, tree);
    }

    /// <summary>The branches, with the default branch first so the picker opens on it.</summary>
    public static async Task<GitHubBranchList> BranchesAsync(
        IGitHubAppClient gitHub,
        long installationId,
        string fullName,
        string defaultBranch,
        CancellationToken ct)
    {
        (string owner, string repo) = GitHubRepositoryPullService.SplitFullName(fullName);
        GitHubBranchList list = await gitHub.ListBranchesAsync(installationId, owner, repo, ct);

        List<string> names = list.Names
            .Where(n => !string.Equals(n, defaultBranch, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The default branch is listed even when it fell off the first page, because it is the one
        // almost everybody wants.
        names.Insert(0, defaultBranch);
        return new GitHubBranchList(names, list.Truncated);
    }

    /// <summary>
    /// Flattens a recursive tree into what the selection UI shows: files and folders, with submodules
    /// and the directories a pull never mirrors left out.
    /// </summary>
    internal static GitHubFileListing FromTree(string fullName, string branch, GitHubRepoTree tree)
    {
        List<GitHubRepositoryFile> entries = [];
        bool truncated = tree.Truncated;

        foreach (GitHubTreeEntry entry in tree.Entries)
        {
            bool isDirectory = string.Equals(entry.Type, "tree", StringComparison.Ordinal);
            bool isFile = string.Equals(entry.Type, "blob", StringComparison.Ordinal);
            if (!isDirectory && !isFile) continue; // a submodule is a commit, not content
            if (GitHubRepositoryPullService.IsIgnored(entry.Path)) continue;

            if (entries.Count >= MaxEntriesListed)
            {
                truncated = true;
                break;
            }

            entries.Add(new GitHubRepositoryFile(entry.Path, isDirectory, isDirectory ? 0 : entry.Size));
        }

        return new GitHubFileListing(fullName, branch, tree.CommitSha, entries, truncated);
    }
}
