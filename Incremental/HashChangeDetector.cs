using System.Security.Cryptography;

namespace Code2Obsidian.Incremental;

/// <summary>
/// Fallback change detector for non-git repositories or when git detection fails.
/// Compares SHA256 content hashes of .cs files against stored hashes from prior state.
/// Cannot detect renames -- renames appear as a Delete + Add pair.
/// </summary>
public sealed class HashChangeDetector : IChangeDetector
{
    /// <inheritdoc />
    public ChangeSet? DetectChanges(string repoOrProjectPath, IncrementalState? priorState)
    {
        // No prior state means first run -- full rebuild.
        if (priorState is null)
        {
            return new ChangeSet([], CommitSha: null, IsFullRebuild: true);
        }

        var storedHashes = priorState.GetFileHashes();

        // If stored hashes are empty, treat as full rebuild (no prior data).
        if (storedHashes.Count == 0)
        {
            return new ChangeSet([], CommitSha: null, IsFullRebuild: true);
        }

        // Normalize stored hash keys to forward-slash relative paths for consistent
        // comparison. Stored hashes may be absolute (legacy) or already relative.
        var normalizedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in storedHashes)
        {
            var relativePath = Path.IsPathRooted(path)
                ? Path.GetRelativePath(repoOrProjectPath, path).Replace('\\', '/')
                : path.Replace('\\', '/');
            normalizedHashes[relativePath] = hash;
        }

        var changes = new List<FileChange>();

        // Enumerate all current .cs files under the project path.
        var currentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(repoOrProjectPath, "*.cs", SearchOption.AllDirectories))
        {
            // Normalize to relative path for consistent comparison with stored hashes.
            var relativePath = Path.GetRelativePath(repoOrProjectPath, filePath)
                .Replace('\\', '/');

            currentFiles.Add(relativePath);

            var currentHash = ComputeFileHash(filePath);

            if (!normalizedHashes.TryGetValue(relativePath, out var storedHash))
            {
                // File not in stored state -- it's new.
                changes.Add(new FileChange(relativePath, OldPath: null, FileChangeKind.Added));
            }
            else if (!string.Equals(currentHash, storedHash, StringComparison.OrdinalIgnoreCase))
            {
                // Hash mismatch -- file was modified.
                changes.Add(new FileChange(relativePath, OldPath: null, FileChangeKind.Modified));
            }
            // Else: hash matches, file is unchanged -- skip.
        }

        // Files in stored state but not on disk are deleted.
        foreach (var storedPath in normalizedHashes.Keys)
        {
            if (!currentFiles.Contains(storedPath))
            {
                changes.Add(new FileChange(storedPath, OldPath: null, FileChangeKind.Deleted));
            }
        }

        return new ChangeSet(changes, CommitSha: null, IsFullRebuild: false);
    }

    /// <summary>
    /// Computes the SHA256 hash of a file's contents, returned as an uppercase hex string.
    /// </summary>
    internal static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes);
    }
}
