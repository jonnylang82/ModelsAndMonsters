namespace ModelsAndMonsters.Tests;

/// <summary>
/// Guards against defects that compile cleanly, pass review, and silently disable behaviour at runtime.
/// </summary>
public sealed class SourceHygieneTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "README.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepositoryRoot();
        foreach (var directory in new[] { "src", "tests" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}runs{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    [Fact]
    public void No_source_file_contains_a_stray_control_character()
    {
        // A real defect, three times over in one working session: a shell escape turned `\b` in a regex
        // literal into an actual backspace byte (U+0008). The file compiled, the pattern was well-formed,
        // and the alternative it belonged to simply never matched anything — a silently dead branch inside a
        // guard whose whole job is to catch things. Nothing else in the build would have noticed: it is not
        // a syntax error, not a warning, and not visible in a diff or a terminal.
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsControl(c) && c is not '\r' and not '\n' and not '\t')
                {
                    offenders.Add($"{Path.GetFileName(file)}: U+{(int)c:X4} at offset {i}");
                    break;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Stray control characters found (almost certainly a mangled regex escape): " + string.Join("; ", offenders));
    }
}
