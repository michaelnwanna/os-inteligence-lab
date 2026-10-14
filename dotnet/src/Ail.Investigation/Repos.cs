using System.Text.Json;
using Ail.Simulator;
using Ail.Schemas;

namespace Ail.Investigation;

public sealed class FileChange
{
    public string Path { get; set; } = "";
    public string Status { get; set; } = "modified";
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public string Patch { get; set; } = "";
}

public sealed class CommitInfo
{
    public string Sha { get; set; } = "";
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public List<FileChange> Files { get; set; } = new();
}

public interface IRepoProvider
{
    string Name { get; }
    List<CommitInfo> Compare(string baseRef, string headRef);
}

/// <summary>Serves commit ranges from a JSON file, for offline demos and deterministic tests.</summary>
public sealed class FixtureRepo : IRepoProvider
{
    private sealed class Compared { public List<CommitInfo> Commits { get; set; } = new(); }
    private sealed class FixtureFile
    {
        public string Name { get; set; } = "";
        public Dictionary<string, Compared> Compares { get; set; } = new();
    }

    private readonly FixtureFile _data;
    public string Name => _data.Name;

    public FixtureRepo(string path) =>
        _data = JsonSerializer.Deserialize<FixtureFile>(File.ReadAllText(path), AilJson.Options) ?? throw new InvalidDataException(path);

    public List<CommitInfo> Compare(string baseRef, string headRef) =>
        _data.Compares.TryGetValue($"{baseRef}...{headRef}", out var c) ? c.Commits : new();
}

public static class RepoProviders
{
    /// <summary>mode: auto (fixture for fixture/* names), fixture or off. The GitHub REST provider is not ported to .NET.</summary>
    public static IRepoProvider? Get(string? repository, string mode = "auto")
    {
        if (string.IsNullOrEmpty(repository) || mode == "off") return null;
        if (mode == "fixture" || (mode == "auto" && repository.StartsWith("fixture/")))
        {
            var name = repository.Split('/', 2)[^1];
            var path = Path.Combine(ScenarioLoader.RepoRoot(), "docs", "scenarios", "repos", $"{name}.json");
            return File.Exists(path) ? new FixtureRepo(path) : null;
        }
        return null;
    }
}
