using LibGit2Sharp;

namespace Code2Obsidian.Incremental;

/// <summary>
/// Detects changed .cs files by comparing a stored commit SHA against the current
/// HEAD and working tree using LibGit2Sharp.
/// Returns null if the path is not a git repository, allowing the caller to
/// fall back to <see cref="HashChangeDetector"/>.
/// </summary>
public sealed class GitChangeDetector : IChangeDetector
{
    /// <inheritdoc />
    public ChangeSet? DetectChanges(string repoOrProjectPath, IncrementalState? priorState)
    {
        try
        {
            return DetectChangesCore(repoOrProjectPath, priorState);
        }
        catch (RepositoryNotFoundException)
        {
            // Not a git repository -- caller should fall back to HashChangeDetector.
            return null;
        }
    }

    private static ChangeSet? DetectChangesCore(string repoOrProjectPath, IncrementalState? priorState)
    {
        using var repo = new Repository(Repository.Discover(repoOrProjectPath));

        var headTip = repo.Head.Tip;
        var currentSha = headTip?.Sha;

        // If no prior state or no stored commit, signal full rebuild.
        if (priorState is null || priorState.CommitSha is null)
        {
            return new ChangeSet([], currentSha, IsFullRebuild: true);
        }

        var storedSha = priorState.CommitSha;

        // Look up the stored commit. If it no longer exists (force-push, shallow clone), full rebuild.
        var oldCommit = repo.Lookup<Commit>(storedSha);
        if (oldCommit is null)
        {
            return new ChangeSet([], currentSha, IsFullRebuild: true);
        }

        var compareOptions = new CompareOptions
        {
            Similarity = SimilarityOptions.Renames
        };

        // Map keyed by relative path to avoid duplicates between committed and working changes.
        var changesByPath = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);

        // 1. Committed changes: stored commit tree vs HEAD tree.
        if (headTip is not null)
        {
            var committedChanges = repo.Diff.Compare<TreeChanges>(
                oldCommit.Tree, headTip.Tree, compareOptions);

            foreach (var entry in committedChanges)
            {
                var change = MapChange(entry);
                if (change is not null)
                {
                    changesByPath[change.Path] = change;
                }
            }
        }

        // 2. Working directory changes: HEAD tree vs index + working directory.
        // The Tree+DiffTargets overload does not accept CompareOptions directly.
        // Working directory renames are uncommon; they appear as Delete + Add pairs.
        if (headTip is not null)
        {
            var workingChanges = repo.Diff.Compare<TreeChanges>(
                headTip.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory);

            foreach (var entry in workingChanges)
            {
                var change = MapChange(entry);
                if (change is not null && !changesByPath.ContainsKey(change.Path))
                {
                    changesByPath[change.Path] = change;
                }
            }
        }

        var changes = changesByPath.Values.ToList();
        return new ChangeSet(changes, currentSha, IsFullRebuild: false);
    }

    /// <summary>
    /// Maps a LibGit2Sharp tree entry change to our domain model.
    /// Returns null for non-.cs files.
    /// </summary>
    private static FileChange? MapChange(TreeEntryChanges entry)
    {
        var path = entry.Path;

        // Filter to .cs files only.
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var kind = entry.Status switch
        {
            ChangeKind.Added => FileChangeKind.Added,
            ChangeKind.Modified => FileChangeKind.Modified,
            ChangeKind.Deleted => FileChangeKind.Deleted,
            ChangeKind.Renamed => FileChangeKind.Renamed,
            ChangeKind.Copied => FileChangeKind.Added,
            ChangeKind.TypeChanged => FileChangeKind.Modified,
            _ => FileChangeKind.Modified // Conservative: treat unknown as modified
        };

        var oldPath = kind == FileChangeKind.Renamed ? entry.OldPath : null;

        return new FileChange(path, oldPath, kind);
    }
}
