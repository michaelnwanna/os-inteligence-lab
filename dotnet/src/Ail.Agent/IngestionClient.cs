using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ail.Schemas;

namespace Ail.Agent;

public sealed record SendResult(int Accepted, int Rejected)
{
    public override string ToString() => $"accepted={Accepted} rejected={Rejected}";
}

/// <summary>Batching, retrying ingestion client. Retrying is safe: the server de-duplicates events.</summary>
public sealed class IngestionClient : IDisposable
{
    private static readonly JsonSerializerOptions Wire = new(AilJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action<TimeSpan> _sleep;

    public int BatchSize { get; init; } = 500;
    public int MaxRetries { get; init; } = 5;

    public IngestionClient(string baseUrl = "http://127.0.0.1:8000", string? token = null, HttpClient? http = null, Action<TimeSpan>? sleep = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(token)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _sleep = sleep ?? Thread.Sleep;
    }

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? new JsonSerializerOptions(Wire) { WriteIndented = true } : Wire);

    private SendResult Post(string path, string body)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                using var resp = _http.Send(req);
                if ((resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) && attempt < MaxRetries)
                {
                    var hinted = resp.Headers.RetryAfter?.Delta;
                    _sleep(hinted is { } h ? TimeSpan.FromSeconds(Math.Min(h.TotalSeconds, 30)) : delay);
                    delay *= 2;
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(resp.Content.ReadAsStream());
                return new SendResult(doc.RootElement.GetProperty("accepted").GetInt32(), doc.RootElement.GetProperty("rejected").GetArrayLength());
            }
            catch (Exception ex) when ((ex is HttpRequestException { StatusCode: null } or TaskCanceledException) && attempt < MaxRetries)
            {
                _sleep(delay);
                delay *= 2;
            }
        }
    }

    public SendResult Send<T>(string path, IEnumerable<T> records)
    {
        int accepted = 0, rejected = 0;
        var batch = new List<T>();
        void Flush()
        {
            if (batch.Count == 0) return;
            var r = Post(path, JsonSerializer.Serialize(batch, Wire));
            accepted += r.Accepted; rejected += r.Rejected;
            batch.Clear();
        }
        foreach (var rec in records)
        {
            batch.Add(rec);
            if (batch.Count >= BatchSize) Flush();
        }
        Flush();
        return new SendResult(accepted, rejected);
    }

    public SendResult SendEvents(IEnumerable<TelemetryEvent> events) => Send("/api/v1/events", events);
    public SendResult SendDeployments(IEnumerable<ChangeEvent> changes) => Send("/api/v1/deployments", changes);

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
