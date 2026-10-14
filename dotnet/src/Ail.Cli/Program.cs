using System.Diagnostics;
using Ail.Api;
using Ail.Detection;
using Ail.Ingestion;
using Ail.Investigation;
using Ail.Schemas;
using Ail.Simulator;

try { return Cli.Run(args); }
catch (CliException e) { Console.Error.WriteLine($"error: {e.Message}"); return 2; }
catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException or ArgumentException)
{ Console.Error.WriteLine($"error: {e.Message}"); return 1; }

sealed class CliException(string message) : Exception(message);

sealed class Opts
{
    private readonly Dictionary<string, string?> _values = new();
    public List<string> Positional { get; } = new();

    public static Opts Parse(IEnumerable<string> args, params string[] flags)
    {
        var o = new Opts(); var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--")) { o.Positional.Add(a); continue; }
            var name = a[2..];
            if (flags.Contains(name)) { o._values[name] = "true"; continue; }
            if (i + 1 >= list.Count) throw new CliException($"option --{name} needs a value");
            o._values[name] = list[++i];
        }
        return o;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public string Get(string name, string fallback) => Get(name) ?? fallback;
    public bool Has(string name) => _values.ContainsKey(name);
    public int? Int(string name) => Get(name) is { } v ? (int.TryParse(v, out var n) && n > 0 ? n : throw new CliException($"--{name} must be a positive integer")) : null;
}

static class Cli
{
    private const string Flagship = "scenario-003";
    private const string DefaultDb = "data/ail.db";

    private const string Usage = """
        ail - OS Intelligence Lab (.NET edition)

        commands:
          demo      [--scenario ID] [--devices N] [--llm NAME] [--db PATH] [--serve] [--host H] [--port P]
          simulate  [--scenario ID] [--devices N] [--seed N] [--db PATH] [--force]
          analyze   [--db PATH] [--llm NAME] [--repo-mode auto|fixture|off] [--no-investigate]
          serve     [--db PATH] [--host H] [--port P]
          report    INCIDENT_ID [--db PATH]
          scenarios
          evaluate  [--devices N] [--seeds N] [--heldout] [--output FILE]
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
        var rest = args.Skip(1).ToArray();
        switch (args[0])
        {
            case "scenarios": Scenarios(); break;
            case "simulate": Simulate(Opts.Parse(rest, "force")); break;
            case "analyze": Analyze(Opts.Parse(rest, "no-investigate")); break;
            case "serve": Serve(Opts.Parse(rest)); break;
            case "demo": Demo(Opts.Parse(rest, "serve")); break;
            case "report": Report(Opts.Parse(rest)); break;
            case "evaluate": Evaluate(Opts.Parse(rest, "heldout")); break;
            default: Console.Error.WriteLine($"unknown command '{args[0]}'\n\n{Usage}"); return 2;
        }
        return 0;
    }

    private static TelemetryStore FreshStore(string path, bool force)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        if (File.Exists(path))
        {
            if (!force) throw new CliException($"{path} already exists; pass --force to overwrite");
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
        return new TelemetryStore(path);
    }

    private static Action<int, int> Progress() => (done, total) =>
    {
        if (done % 60 == 0 || done == total) Console.Write($"\r  simulated {done}/{total} minutes");
        if (done == total) Console.WriteLine();
    };

    private static void PrintSummary(List<Incident> incidents)
    {
        if (incidents.Count == 0) { Console.WriteLine("No incidents detected."); return; }
        foreach (var inc in incidents)
        {
            var dims = inc.Cohorts.FirstOrDefault()?.Dimensions;
            Console.WriteLine($"\n{inc.Id}  {inc.Title}  [{inc.Severity}, {inc.Status}]");
            Console.WriteLine($"  onset {inc.Onset:HH:mm} UTC, first detection {inc.FirstDetection:HH:mm} UTC, {inc.Metric}: {inc.Baseline:G4} -> {inc.Observed:G4}");
            if (dims is { Count: > 0 }) Console.WriteLine($"  cohort: {Labels.CohortLabel(dims)}");
            if (inc.Correlations.Count > 0)
            {
                var t = inc.Correlations[0];
                Console.WriteLine($"  top correlated change: {t.Change.Component} {t.Change.ToVersion} (score {t.Score:0.00})");
            }
            if (inc.Investigation?.Hypotheses.FirstOrDefault() is { } h) Console.WriteLine($"  hypothesis [{h.Confidence}]: {h.Title}");
        }
    }

    private static void Scenarios()
    {
        foreach (var sc in ScenarioLoader.LoadAll())
            Console.WriteLine($"{sc.Id,-14} {sc.Name,-48} {sc.Population.Devices,8:N0} devices, {sc.Expected.AllEpisodes().Count} expected incident(s)");
    }

    private static void Simulate(Opts o)
    {
        var sc = ScenarioLoader.Load(o.Get("scenario", Flagship));
        var db = o.Get("db", DefaultDb);
        using var store = FreshStore(db, o.Has("force"));
        var devices = o.Int("devices");
        Console.WriteLine($"Simulating {devices ?? sc.Population.Devices:N0} devices for '{sc.Name}'");
        FleetSimulator.SimulateToStore(sc, store, devices, o.Int("seed"), Progress());
        var c = store.Counts();
        Console.WriteLine($"Stored {c["aggregate_rows"]:N0} aggregate rows in {db}");
    }

    private static List<Incident> RunAnalysis(TelemetryStore store, Opts o)
    {
        var provider = LlmProviders.Create(o.Get("llm"));
        return Workflow.RunAnalysis(store, new DetectionConfig(), provider, o.Get("repo-mode", "auto"), investigate: !o.Has("no-investigate"));
    }

    private static void Analyze(Opts o)
    {
        var db = o.Get("db", DefaultDb);
        if (!File.Exists(db)) throw new CliException($"{db} not found; run `ail simulate` first");
        using var store = new TelemetryStore(db);
        PrintSummary(RunAnalysis(store, o));
    }

    private static void Serve(Opts o, string? dbOverride = null)
    {
        var db = dbOverride ?? o.Get("db", DefaultDb);
        var url = $"http://{o.Get("host", "127.0.0.1")}:{o.Int("port") ?? 8000}";
        var app = ApiHost.Create(new ApiOptions { DbPath = db, Urls = new[] { url } });
        Console.WriteLine($"Serving portal and API on {url} (Ctrl+C to stop)");
        app.Run();
    }

    private static void Demo(Opts o)
    {
        var sc = ScenarioLoader.Load(o.Get("scenario", Flagship));
        var db = o.Get("db", "data/demo.db");
        using (var store = FreshStore(db, true))
        {
            var devices = o.Int("devices");
            Console.WriteLine($"1/3 Simulating {devices ?? sc.Population.Devices:N0} devices for '{sc.Name}'");
            FleetSimulator.SimulateToStore(sc, store, devices, null, Progress());
            Console.WriteLine("2/3 Detecting anomalies, isolating cohorts, correlating changes, investigating");
            var sw = Stopwatch.StartNew();
            PrintSummary(RunAnalysis(store, o));
            Console.WriteLine($"\n3/3 Failure Time Machine and reports are ready (analysis {sw.Elapsed.TotalSeconds:0.0}s).");
        }
        if (o.Has("serve")) Serve(o, db);
        else Console.WriteLine($"Run `ail serve --db {db}` to open the portal.");
    }

    private static void Report(Opts o)
    {
        if (o.Positional.Count != 1) throw new CliException("usage: ail report INCIDENT_ID [--db PATH]");
        using var store = new TelemetryStore(o.Get("db", DefaultDb));
        var inc = store.GetIncident(o.Positional[0]) ?? throw new CliException($"incident {o.Positional[0]} not found");
        Console.WriteLine(Ail.Investigation.Report.RenderMarkdown(inc));
    }

    private static void Evaluate(Opts o)
    {
        var heldout = o.Has("heldout");
        var text = Evaluation.Evaluate(o.Int("devices") ?? (heldout ? null : 50_000), o.Int("seeds") ?? 3, Console.Error.WriteLine, heldout);
        if (o.Get("output") is { } path) { File.WriteAllText(path, text); Console.WriteLine($"wrote {path}"); }
        else Console.WriteLine(text);
    }
}
