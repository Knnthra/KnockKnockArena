using System.Text.RegularExpressions;

namespace KnockKnockArena.Tests.M12;

/// <summary>
/// Module 12: YOUR Dockerfile and .dockerignore at the repo root, read as text - no Docker
/// needed. What the image is built from, what it opens, where the accounts live, and what the
/// build context (everything `docker build .` sends to Docker) keeps out.
/// </summary>
public class DockerfileTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile")))
                return dir.FullName;
        throw new InvalidOperationException("no Dockerfile in any folder above the test dll - it belongs at the repo root");
    }

    // Instruction lines only: no comments, no blank lines, continuation lines joined.
    private static List<string> Instructions()
    {
        string text = File.ReadAllText(Path.Combine(Root, "Dockerfile")).Replace("\r\n", "\n");
        text = Regex.Replace(text, @"\\\n\s*", " ");
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
    }

    [Fact]
    public void TheImage_IsBuiltWithTheSdk_AndRunsOnTheAspNetRuntime()
    {
        List<string> from = Instructions().Where(l => l.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(from.Count >= 2, "a multi-stage build: one FROM to build with the SDK, one to run");
        Assert.Contains("dotnet/sdk", from[0]);
        Assert.Contains("dotnet/aspnet", from[^1]);   // Kestrel (REST) needs ASP.NET; the SDK stays out of the image
    }

    [Theory]
    [InlineData("36363/tcp")]
    [InlineData("36363/udp")]   // the game port is TWO ports: EXPOSE 36363 alone means TCP only
    [InlineData("8080/tcp")]
    public void ThePorts_AreExposed(string port)
    {
        Assert.Contains(Instructions(), l => Regex.IsMatch(l, @"^EXPOSE\s.*\b" + Regex.Escape(port) + @"\b", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void TheAccounts_LiveInAVolume_OutsideTheImage()
    {
        List<string> lines = Instructions();

        Assert.Contains(lines, l => Regex.IsMatch(l, @"^ENV\s.*KKARENA_DATA_DIR=/data\b"));
        Assert.Contains(lines, l => Regex.IsMatch(l, @"^VOLUME\s+(\[\s*""/data""\s*\]|/data)\s*$"));
    }

    [Fact]
    public void TheImage_NeverCopiesAccountsJson()
    {
        List<string> copies = Instructions().Where(l => l.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.DoesNotContain(copies, l => l.Contains("accounts.json"));
        Assert.DoesNotContain(copies, l => Regex.IsMatch(l, @"^COPY\s+(\./)?data/?\s"));   // the whole data folder
    }

    [Theory]
    [InlineData("data/accounts.json")]                       // password hashes: never in a build context
    [InlineData("KnockKnockClient/Library/ArtifactDB")]      // the Unity project's gigabytes
    [InlineData("src/KnockKnockArena.Server/bin/Debug/KnockKnockArena.Server.dll")]
    [InlineData(".git/HEAD")]
    public void TheDockerignore_KeepsOut(string path)
    {
        Assert.True(Dockerignore.IsIgnored(Root, path), $"{path} is sent to Docker with the build context");
    }

    [Theory]
    [InlineData("data/arena.json")]
    [InlineData("data/weapons.json")]
    [InlineData("KnockKnockClient/Assets/Shared/Protocol/TcpFraming.cs")]   // the shared code the server compiles
    [InlineData("src/KnockKnockArena.Server/Program.cs")]
    public void TheDockerignore_LetsThrough(string path)
    {
        Assert.False(Dockerignore.IsIgnored(Root, path), $"{path} is kept out, but the build needs it");
    }
}

/// <summary>
/// The .dockerignore rules, as Docker applies them: one pattern per line, * within a folder,
/// ** across folders, a pattern matches a path or anything below it, ! lets a path back in,
/// and the LAST matching line decides.
/// </summary>
public static class Dockerignore
{
    public static bool IsIgnored(string root, string path)
    {
        bool ignored = false;
        foreach (string raw in File.ReadAllLines(Path.Combine(root, ".dockerignore")))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            bool negate = line.StartsWith('!');
            string pattern = (negate ? line[1..] : line).Trim().TrimStart('/').TrimEnd('/');
            if (Matches(pattern, path))
                ignored = !negate;
        }
        return ignored;
    }

    private static bool Matches(string pattern, string path)
    {
        string regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*/", "(.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "(/.*)?$";
        return Regex.IsMatch(path, regex);
    }
}
