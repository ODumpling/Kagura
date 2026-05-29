namespace Kagura.Core;

/// <summary>
/// Single source of truth for Kagura's on-disk state directory and its subpaths.
/// Everything Kagura writes — the SQLite DB, DataProtection keys, worktrees,
/// transcripts, the per-source scratch worktree, and runtime pid files — lives
/// under <see cref="Root"/>.
///
/// The directory was originally <c>~/.devflow/</c>; it is now <c>~/.kagura/</c>.
/// <see cref="MigrateLegacyIfPresent"/> handles the one-shot rename for users
/// whose box still has the old location.
/// </summary>
public static class KaguraPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Root { get; } = Path.Combine(Home, ".kagura");
    public static string LegacyRoot { get; } = Path.Combine(Home, ".devflow");

    public static string DbPath => Path.Combine(Root, "kagura.db");
    public static string KeysDir => Path.Combine(Root, "keys");
    public static string WorktreesRoot => Path.Combine(Root, "worktrees");
    public static string TranscriptsRoot => Path.Combine(Root, "transcripts");
    public static string ScratchRoot => Path.Combine(Root, "scratch");
    public static string RuntimeDir => Path.Combine(Root, "runtime");

    public static string LegacyDbPath => Path.Combine(LegacyRoot, "kagura.db");
    public static string LegacyRuntimeDir => Path.Combine(LegacyRoot, "runtime");

    /// <summary>
    /// One-shot rename of <c>~/.devflow/</c> → <c>~/.kagura/</c> for boxes that
    /// predate the rename. Returns <c>true</c> if the move happened. No-op when
    /// the new location already exists (both present → prefer new, leave old
    /// untouched, no destructive merge).
    /// </summary>
    public static bool MigrateLegacyIfPresent()
    {
        if (Directory.Exists(Root)) return false;
        if (!Directory.Exists(LegacyRoot)) return false;
        Directory.Move(LegacyRoot, Root);
        return true;
    }
}
