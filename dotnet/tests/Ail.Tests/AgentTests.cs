using System.Net;
using System.Text.Json;
using Ail.Agent;
using Ail.Schemas;
using Ail.Simulator;

namespace Ail.Tests;

public sealed class AgentTests : IDisposable
{
    private const string MemInfo = "MemTotal:       16384000 kB\nMemFree:         1000000 kB\nMemAvailable:    8192000 kB\nBuffers:          100000 kB\n";
    private const string VmStat = "Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages free:                               20000.\n" +
        "Pages active:                            400000.\nPages inactive:                          300000.\n" +
        "Pages wired down:                        100000.\nPages occupied by compressor:             50000.\n";
    private static readonly SystemInfo FakeInfo = new("TestOS", "1.0", "arm64", "generic-arm64", "arm64-8c", 16);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ail-agent-test-" + Guid.NewGuid().ToString("N"));
    private ConsentStore Store => new(_dir);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void LinuxAndMacParsersUseTheDocumentedFormulas()
    {
        var (used, total) = Collectors.ParseMeminfo(MemInfo);
        Assert.Equal((8000, 16000), ((int)Math.Round(used), (int)Math.Round(total)));
        (used, total) = Collectors.ParseVmStat(VmStat, 16L * 1024 * 1024 * 1024);
        Assert.Equal(550000.0 * 16384 / 1048576, used, 3);
        Assert.Equal(16384, total, 3);
        Assert.Equal(1.23, Collectors.ParseLoadAverage("{ 1.23 1.45 1.67 }"), 3);
        Assert.Equal(0.52, Collectors.ParseLoadAverage("0.52 0.58 0.59 1/467 12345"), 3);
    }

    [Fact]
    public void AgentRefusesWithoutConsentAndRevokeDeletesIt()
    {
        Assert.Null(Store.Load());
        Store.Grant(new[] { "memory_mb" }, "http://127.0.0.1:8000");
        Assert.NotNull(Store.Load());
        Assert.True(Store.Revoke());
        Assert.Null(Store.Load());
        Assert.False(Store.Revoke());
    }

    [Fact]
    public void GrantRejectsUnknownSignalsAndKeepsAStableRandomDeviceId()
    {
        Assert.Throws<ArgumentException>(() => Store.Grant(new[] { "keystrokes" }, "http://x"));
        Assert.Throws<ArgumentException>(() => Store.Grant(Array.Empty<string>(), "http://x"));
        var first = Store.Grant(new[] { "memory_mb" }, "http://x");
        var again = Store.Grant(new[] { "memory_mb", "cpu_load_pct" }, "http://x");
        Assert.Equal(first.DeviceId, again.DeviceId);
        Assert.Equal(32, first.DeviceId.Length);
    }

    [Fact]
    public void ConsentFileHoldsOnlyTheApprovedFieldsInTheSharedFormat()
    {
        Store.Grant(new[] { "session_heartbeat", "memory_mb" }, "http://x");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "consent.json")));
        Assert.Equal(new[] { "signals", "endpoint", "device_id", "granted_at" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ConsentWithAnUnknownSignalIsTreatedAsNotEnrolled()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "consent.json"), """{"signals":["keystrokes"],"endpoint":"http://x","device_id":"abc","granted_at":"now"}""");
        Assert.Null(Store.Load());
    }

    [Fact]
    public void PayloadContainsOnlyApprovedSignalsAndPassesTheBackendSchema()
    {
        var consent = Store.Grant(new[] { "session_heartbeat", "memory_mb" }, "http://x");
        var payload = AgentRunner.BuildPayload(consent, new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
            s => s == "memory_mb" ? 1234.5 : null, FakeInfo);
        Assert.All(payload, e => Assert.Empty(e.Validate()));
        Assert.Equal(new[] { ("session", (string?)null), ("metric", "memory_mb") }, payload.Select(e => (e.EventType!, e.MetricName)).ToArray());
        Assert.All(payload, e => Assert.Equal(consent.DeviceId, e.DeviceId));
        var json = IngestionClient.Serialize(payload);
        Assert.DoesNotContain("cpu_load_pct", json);
        Assert.All(JsonDocument.Parse(json).RootElement.EnumerateArray(), e => JsonSerializer.Deserialize<TelemetryEvent>(e.GetRawText(), AilJson.Strict));
    }

    [Fact]
    public void UnapprovedSignalsAreNeverCollected()
    {
        var consent = Store.Grant(new[] { "session_heartbeat" }, "http://x");
        var called = new List<string>();
        AgentRunner.BuildPayload(consent, collector: s => { called.Add(s); return null; }, info: FakeInfo);
        Assert.Empty(called);
    }

    [Fact]
    public void LiveCollectorsReturnPlausibleValuesOnThisMachine()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())) return;
        var (used, total) = Collectors.MemoryMb();
        Assert.InRange(used, 1, total);
        Assert.InRange(Collectors.Collect("cpu_load_pct")!.Value, 0.0, 100.0);
        var info = Collectors.GetSystemInfo();
        Assert.NotEqual(Environment.MachineName, info.DeviceModel);
        var consent = Store.Grant(Collectors.Signals.Keys, "http://x");
        Assert.All(AgentRunner.BuildPayload(consent), e => Assert.Empty(e.Validate()));
    }

    private sealed class ScriptedHandler(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        private int _i;
        public int Calls => _i;
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var code = codes[Math.Min(_i++, codes.Length - 1)];
            var res = new HttpResponseMessage(code) { Content = new StringContent("""{"accepted":2,"rejected":[]}""") };
            if (code == HttpStatusCode.ServiceUnavailable) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return res;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void ClientRetriesAfterBackpressureAndHonoursRetryAfter()
    {
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.Accepted);
        var sleeps = new List<TimeSpan>();
        using var client = new IngestionClient(http: new HttpClient(handler) { BaseAddress = new Uri("http://x") }, sleep: sleeps.Add);
        var result = client.SendEvents(new[] { new TelemetryEvent(), new TelemetryEvent() });
        Assert.Equal(new SendResult(2, 0), result);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, sleeps);
    }

    [Fact]
    public void ClientDoesNotRetryAuthenticationFailures()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Unauthorized);
        using var client = new IngestionClient(http: new HttpClient(handler) { BaseAddress = new Uri("http://x") }, sleep: _ => { });
        Assert.Throws<HttpRequestException>(() => client.SendEvents(new[] { new TelemetryEvent() }));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void SimulatedEventsAreValidAndMatchTheScenarioFleet()
    {
        var sim = new FleetSimulator(ScenarioLoader.Load("scenario-003"), 2000);
        var events = EventStream.Events(sim, minutes: 5).ToList();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Empty(e.Validate()));
        var sessions = events.Count(e => e.EventType == "session");
        Assert.Equal(sessions * 2, events.Count(e => e.EventType == "metric"));
        Assert.All(events, e => Assert.InRange(e.Timestamp.ToUnixTimeSeconds(), ScenarioLoader.Load("scenario-003").Start.ToUnixTimeSeconds(),
            ScenarioLoader.Load("scenario-003").Start.AddMinutes(5).ToUnixTimeSeconds()));
    }

    [Fact]
    public async Task AgentAndStreamEventsReachARealServer()
    {
        await using var api = await ApiFixture.StartAsync(apiToken: "s3cret");
        var url = api.Http.BaseAddress!.ToString();
        var consent = Store.Grant(new[] { "session_heartbeat", "memory_mb" }, url);

        var (agent, stream, deployments) = await Task.Run(() =>
        {
            using var client = new IngestionClient(url, "s3cret");
            var sim = new FleetSimulator(ScenarioLoader.Load("scenario-003"), 1000);
            return (AgentRunner.RunOnce(consent, client), client.SendEvents(EventStream.Events(sim, 3)), client.SendDeployments(sim.ChangeEvents()));
        });

        Assert.Equal(new SendResult(2, 0), agent);
        Assert.True(stream.Accepted > 0 && stream.Rejected == 0);
        Assert.True(deployments.Accepted > 0 && deployments.Rejected == 0);
        var meta = JsonDocument.Parse(await api.Http.GetStringAsync("/api/v1/meta")).RootElement;
        Assert.Equal(stream.Accepted + 2, meta.GetProperty("counts").GetProperty("raw_events").GetInt32());
    }
}
