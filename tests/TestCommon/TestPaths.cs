namespace PoMode.TestCommon;

/// <summary>Shared filesystem anchors for tests that must reach outside their own bin folder.
/// Linked (not referenced) into each test project, like <see cref="TestAudio"/>.</summary>
public static class TestPaths
{
    /// <summary>The repo root: the nearest ancestor of the test bin folder holding PoMode.slnx.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoMode.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("PoMode.slnx not found above test bin dir.");
    }
}
