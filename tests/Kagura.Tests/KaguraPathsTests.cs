using Kagura.Core;

namespace Kagura.Tests;

public class KaguraPathsTests
{
    [Fact]
    public void Root_is_dot_kagura_under_user_profile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".kagura"), KaguraPaths.Root);
    }

    [Fact]
    public void LegacyRoot_is_dot_devflow_under_user_profile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".devflow"), KaguraPaths.LegacyRoot);
    }

    [Fact]
    public void Subpaths_are_anchored_under_root()
    {
        Assert.Equal(Path.Combine(KaguraPaths.Root, "kagura.db"), KaguraPaths.DbPath);
        Assert.Equal(Path.Combine(KaguraPaths.Root, "keys"), KaguraPaths.KeysDir);
        Assert.Equal(Path.Combine(KaguraPaths.Root, "worktrees"), KaguraPaths.WorktreesRoot);
        Assert.Equal(Path.Combine(KaguraPaths.Root, "transcripts"), KaguraPaths.TranscriptsRoot);
        Assert.Equal(Path.Combine(KaguraPaths.Root, "scratch"), KaguraPaths.ScratchRoot);
        Assert.Equal(Path.Combine(KaguraPaths.Root, "runtime"), KaguraPaths.RuntimeDir);
    }

    [Fact]
    public void Legacy_subpaths_are_anchored_under_legacy_root()
    {
        Assert.Equal(Path.Combine(KaguraPaths.LegacyRoot, "kagura.db"), KaguraPaths.LegacyDbPath);
        Assert.Equal(Path.Combine(KaguraPaths.LegacyRoot, "runtime"), KaguraPaths.LegacyRuntimeDir);
    }
}
