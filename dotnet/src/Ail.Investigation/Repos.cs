using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

/// <summary>Read-only GitHub access. A token with contents:read and metadata:read is sufficient.</summary>
public sealed class GitHubRepo : IRepoProvider
{
    private static readonly Regex RepoName = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
    private readonly HttpClient _http;
    private readonly int _maxCommits;
    public string Name { get; }

    public static bool IsValidName(string repository) => RepoName.IsMatch(repository);

    public GitHubRepo(string repository, string? token = null, HttpClient? client = null, int maxCommits = 30)
    {
        if (!IsValidName(repository)) throw new ArgumentException("repository must look like 'owner/name'");
        Name = repository;
        _maxCommits = maxCommits;
        token ??= Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _http = client ?? new HttpClient { BaseAddress = new Uri("https://api.github.com"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("os-intelligence-lab");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private JsonElement Get(string path)
    {
        using var resp = _http.GetAsync(path).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
    }

    public List<CommitInfo> Compare(string baseRef, string headRef)
    {
        var compare = Get($"/repos/{Name}/compare/{Uri.EscapeDataString(baseRef)}...{Uri.EscapeDataString(headRef)}");
        var output = new List<CommitInfo>();
        if (!compare.TryGetProperty("commits", out var commits)) return output;
        foreach (var c in commits.EnumerateArray().Take(_maxCommits))
        {
            var sha = c.GetProperty("sha").GetString()!;
            var detail = Get($"/repos/{Name}/commits/{sha}");
            var commit = c.GetProperty("commit");
            var info = new CommitInfo
            {
                Sha = sha,
                Message = (commit.GetProperty("message").GetString() ?? "").Split('\n')[0],
                Author = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var l) ? l.GetString() ?? "unknown" : "unknown",
                Date = commit.GetProperty("author").GetProperty("date").GetString() ?? "",
            };
            if (detail.TryGetProperty("files", out var files))
                foreach (var f in files.EnumerateArray())
                    info.Files.Add(new FileChange
                    {
                        Path = f.GetProperty("filename").GetString() ?? "",
                        Status = f.TryGetProperty("status", out var st) ? st.GetString() ?? "modified" : "modified",
                        Additions = f.TryGetProperty("additions", out var ad) ? ad.GetInt32() : 0,
                        Deletions = f.TryGetProperty("deletions", out var de) ? de.GetInt32() : 0,
                        Patch = f.TryGetProperty("patch", out var pa) ? pa.GetString() ?? "" : "",
                    });
            output.Add(info);
        }
        return output;
    }

    /// <summary>Needs issues:write. Only called when the user explicitly asks for it.</summary>
    public string CreateIssue(string title, string body, IEnumerable<string>? labels = null)
    {
        var payload = JsonSerializer.Serialize(new { title, body, labels = labels ?? Array.Empty<string>() });
        using var resp = _http.PostAsync($"/repos/{Name}/issues", new StringContent(payload, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.GetProperty("html_url").GetString()!;
    }
}

public static class RepoProviders
{
    /// <summary>mode: auto (fixture for fixture/* names, GitHub otherwise), fixture, github or off.</summary>
    public static IRepoProvider? Get(string? repository, string mode = "auto")
    {
        if (string.IsNullOrEmpty(repository) || mode == "off") return null;
        if (mode == "fixture" || (mode == "auto" && repository.StartsWith("fixture/")))
        {
            var name = repository.Split('/', 2)[^1];
            var path = Path.Combine(ScenarioLoader.RepoRoot(), "docs", "scenarios", "repos", $"{name}.json");
            return File.Exists(path) ? new FixtureRepo(path) : null;
        }
        if (mode is "github" or "auto") return GitHubRepo.IsValidName(repository) ? new GitHubRepo(repository) : null;
        return null;
    }
}
