using System.Text.Json;
using Ail.Schemas;

namespace Ail.Tests;

public class SchemaTests
{
    internal const string ValidEvent =
        """{"timestamp":"2026-03-10T12:00:00Z","device_id":"d1","session_id":"s1","os":"Windows","os_version":"11","architecture":"x64","device_model":"M1","application":"Atlas","application_version":"2.0","event_type":"session"}""";

    private static TelemetryEvent Parse(string json) => JsonSerializer.Deserialize<TelemetryEvent>(json, AilJson.Strict)!;

    [Fact]
    public void ValidEventPasses() => Assert.Empty(Parse(ValidEvent).Validate());

    [Fact]
    public void UnknownFieldsAreRejected() =>
        Assert.Throws<JsonException>(() => Parse(ValidEvent.Replace("\"os\"", "\"hostname\":\"x\",\"os\"")));

    [Theory]
    [InlineData("email")]
    [InlineData("user_name")]
    [InlineData("client-ip")]
    public void PersonalDataDimensionsAreRejected(string key)
    {
        var ev = Parse(ValidEvent);
        ev.Dimensions[key] = "x";
        Assert.Contains(ev.Validate(), e => e.Contains("personal data"));
    }

    [Fact]
    public void MetricEventsNeedNameAndFiniteValue()
    {
        var ev = Parse(ValidEvent);
        ev.EventType = "metric";
        Assert.NotEmpty(ev.Validate());
        ev.MetricName = "latency_ms"; ev.MetricValue = double.NaN;
        Assert.NotEmpty(ev.Validate());
        ev.MetricValue = 12.5;
        Assert.Empty(ev.Validate());
    }

    [Fact]
    public void EventIdIsStableForDuplicateDeliveries() =>
        Assert.Equal(Parse(ValidEvent).ResolvedEventId(), Parse(ValidEvent).ResolvedEventId());

    [Fact]
    public void TimeBucketsRoundTrip()
    {
        var t = new DateTimeOffset(2026, 3, 10, 12, 34, 56, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 12, 34, 0, TimeSpan.Zero), TimeUtil.DtOf(TimeUtil.BucketOf(t)));
    }
}
