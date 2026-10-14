using Ail.Schemas;

namespace Ail.Agent;

/// <summary>Payload construction and delivery. <see cref="BuildPayload"/> is what `preview` prints and `run` sends.</summary>
public static class AgentRunner
{
    public const string AgentName = "ail-agent";
    public const string AgentVersion = "0.1";

    /// <summary>TelemetryEvents containing only the approved signals.</summary>
    public static List<TelemetryEvent> BuildPayload(Consent consent, DateTimeOffset? now = null,
        Func<string, double?>? collector = null, SystemInfo? info = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var machine = info ?? Collectors.GetSystemInfo();
        collector ??= Collectors.Collect;
        TelemetryEvent Make(string type) => new()
        {
            Timestamp = at, DeviceId = consent.DeviceId,
            SessionId = $"{consent.DeviceId[..Math.Min(8, consent.DeviceId.Length)]}-{at.ToUnixTimeSeconds()}",
            Os = machine.Os, OsVersion = machine.OsVersion, Architecture = machine.Architecture, DeviceModel = machine.DeviceModel,
            Cpu = machine.Cpu, MemoryGb = machine.MemoryGb, Application = AgentName, ApplicationVersion = AgentVersion, EventType = type,
        };

        var events = new List<TelemetryEvent>();
        if (consent.Signals.Contains("session_heartbeat")) events.Add(Make("session"));
        foreach (var signal in consent.Signals)
        {
            if (signal == "session_heartbeat") continue;
            if (collector(signal) is not { } value) continue;
            var ev = Make("metric");
            ev.MetricName = signal;
            ev.MetricValue = value;
            events.Add(ev);
        }
        return events;
    }

    public static SendResult RunOnce(Consent consent, IngestionClient client) => client.SendEvents(BuildPayload(consent));

    public static void RunLoop(Consent consent, TimeSpan interval, int? count = null, string? token = null, Action<SendResult>? report = null)
    {
        using var client = new IngestionClient(consent.Endpoint, token);
        for (var sent = 0; count is null || sent < count;)
        {
            report?.Invoke(RunOnce(consent, client));
            sent++;
            if (count is null || sent < count) Thread.Sleep(interval);
        }
    }
}
