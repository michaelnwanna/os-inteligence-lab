using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ail.Schemas;
using Microsoft.Data.Sqlite;

namespace Ail.Ingestion;

/// <summary>
/// SQLite telemetry store (raw events, minute aggregates, change events, incidents). The only place that knows SQL;
/// a PostgreSQL or ClickHouse implementation needs the same public methods.
/// </summary>
public sealed class TelemetryStore : IAggregateSink, IDisposable
{
    private static readonly string DimCols = string.Join(", ", Dimensions.All);
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public string Path { get; }

    public TelemetryStore(string path = ":memory:")
    {
        Path = path;
        if (path != ":memory:")
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec(@"
CREATE TABLE IF NOT EXISTS events (event_id TEXT PRIMARY KEY, ts INTEGER NOT NULL, payload TEXT NOT NULL) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS events_ts ON events (ts);
CREATE TABLE IF NOT EXISTS agg (
    bucket INTEGER NOT NULL, metric TEXT NOT NULL,
    " + string.Join(" TEXT NOT NULL, ", Dimensions.All) + @" TEXT NOT NULL,
    n REAL NOT NULL, k REAL NOT NULL, s REAL NOT NULL, ss REAL NOT NULL,
    PRIMARY KEY (bucket, metric, " + DimCols + @")
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS agg_metric ON agg (metric, bucket);
CREATE TABLE IF NOT EXISTS changes (deployment_id TEXT PRIMARY KEY, ts INTEGER NOT NULL, payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS incidents (id TEXT PRIMARY KEY, seq INTEGER NOT NULL, payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        if (path != ":memory:") Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { lock (_lock) _conn.Dispose(); }

    // --- raw events ----------------------------------------------------------------------------
    /// <summary>Inserts raw events; the result says which were new (false = already stored).</summary>
    public bool[] InsertRaw(IReadOnlyList<(string Id, long Ts, string Payload)> events)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO events (event_id, ts, payload) VALUES ($id, $ts, $p)";
            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var p = cmd.Parameters.Add("$p", SqliteType.Text);
            var fresh = new bool[events.Count];
            for (var i = 0; i < events.Count; i++)
            {
                id.Value = events[i].Id; ts.Value = events[i].Ts; p.Value = events[i].Payload;
                fresh[i] = cmd.ExecuteNonQuery() == 1;
            }
            tx.Commit();
            return fresh;
        }
    }

    // --- aggregates ----------------------------------------------------------------------------
    public void AddAggregates(IEnumerable<AggRow> rows)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            var dimParams = string.Join(", ", Dimensions.All.Select((_, i) => $"$d{i}"));
            cmd.CommandText = $@"INSERT INTO agg (bucket, metric, {DimCols}, n, k, s, ss) VALUES ($b, $m, {dimParams}, $n, $k, $s, $ss)
ON CONFLICT(bucket, metric, {DimCols}) DO UPDATE SET n = n + excluded.n, k = k + excluded.k, s = s + excluded.s, ss = ss + excluded.ss";
            var b = cmd.Parameters.Add("$b", SqliteType.Integer);
            var m = cmd.Parameters.Add("$m", SqliteType.Text);
            var d = Dimensions.All.Select((_, i) => cmd.Parameters.Add($"$d{i}", SqliteType.Text)).ToArray();
            var n = cmd.Parameters.Add("$n", SqliteType.Real);
            var k = cmd.Parameters.Add("$k", SqliteType.Real);
            var s = cmd.Parameters.Add("$s", SqliteType.Real);
            var ss = cmd.Parameters.Add("$ss", SqliteType.Real);
            foreach (var r in rows)
            {
                b.Value = r.Bucket; m.Value = r.Metric;
                for (var i = 0; i < d.Length; i++) d[i].Value = r.Dims[i];
                n.Value = r.N; k.Value = r.K; s.Value = r.S; ss.Value = r.Ss;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Commit() { }

    /// <summary>All aggregate rows of one metric, ordered by bucket.</summary>
    public List<AggRow> AggRows(string metric)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT bucket, {DimCols}, n, k, s, ss FROM agg WHERE metric = $m ORDER BY bucket";
            cmd.Parameters.AddWithValue("$m", metric);
            using var rd = cmd.ExecuteReader();
            var rows = new List<AggRow>();
            var nd = Dimensions.All.Length;
            while (rd.Read())
            {
                var dims = new string[nd];
                for (var i = 0; i < nd; i++) dims[i] = rd.GetString(1 + i);
                rows.Add(new AggRow(rd.GetInt64(0), metric, dims, rd.GetDouble(1 + nd), rd.GetDouble(2 + nd), rd.GetDouble(3 + nd), rd.GetDouble(4 + nd)));
            }
            return rows;
        }
    }

    public List<string> Metrics()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT metric FROM agg ORDER BY 1";
            using var rd = cmd.ExecuteReader();
            var list = new List<string>();
            while (rd.Read()) list.Add(rd.GetString(0));
            return list;
        }
    }

    public (long Lo, long Hi)? TimeRange()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT MIN(bucket), MAX(bucket) FROM agg";
            using var rd = cmd.ExecuteReader();
            rd.Read();
            return rd.IsDBNull(0) ? null : (rd.GetInt64(0), rd.GetInt64(1));
        }
    }

    // --- change events -------------------------------------------------------------------------
    public void AddChange(ChangeEvent change)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO changes (deployment_id, ts, payload) VALUES ($id, $ts, $p)";
            cmd.Parameters.AddWithValue("$id", change.DeploymentId);
            cmd.Parameters.AddWithValue("$ts", change.Timestamp.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(change, AilJson.Options));
            cmd.ExecuteNonQuery();
        }
    }

    public List<ChangeEvent> ListChanges() => Payloads<ChangeEvent>("SELECT payload FROM changes ORDER BY ts");

    // --- incidents -----------------------------------------------------------------------------
    public void ReplaceIncidents(IEnumerable<Incident> incidents)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM incidents"; del.ExecuteNonQuery(); }
            var seq = 0;
            foreach (var i in incidents)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO incidents (id, seq, payload) VALUES ($id, $seq, $p)";
                cmd.Parameters.AddWithValue("$id", i.Id);
                cmd.Parameters.AddWithValue("$seq", seq++);
                cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(i, AilJson.Options));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public Incident? GetIncident(string id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM incidents WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            var payload = cmd.ExecuteScalar() as string;
            return payload is null ? null : JsonSerializer.Deserialize<Incident>(payload, AilJson.Options);
        }
    }

    public List<Incident> ListIncidents() => Payloads<Incident>("SELECT payload FROM incidents ORDER BY seq");

    private List<T> Payloads<T>(string sql)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            using var rd = cmd.ExecuteReader();
            var list = new List<T>();
            while (rd.Read()) list.Add(JsonSerializer.Deserialize<T>(rd.GetString(0), AilJson.Options)!);
            return list;
        }
    }

    // --- metadata and stats --------------------------------------------------------------------
    public void SetMeta(string key, object value)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(value, AilJson.Options));
            cmd.ExecuteNonQuery();
        }
    }

    public T? GetMeta<T>(string key)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() is string v ? JsonSerializer.Deserialize<T>(v, AilJson.Options) : default;
        }
    }

    public Dictionary<string, long> Counts()
    {
        lock (_lock)
        {
            long Count(string table)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                return (long)cmd.ExecuteScalar()!;
            }
            return new()
            {
                ["raw_events"] = Count("events"), ["aggregate_rows"] = Count("agg"),
                ["changes"] = Count("changes"), ["incidents"] = Count("incidents"),
            };
        }
    }

    /// <summary>Order-independent hash of all aggregates; proves an analysis is reproducible.</summary>
    public string DataFingerprint()
    {
        lock (_lock)
        {
            using var sha = SHA256.Create();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT bucket, metric, {DimCols}, n, k, s, ss FROM agg ORDER BY bucket, metric, {DimCols}";
            using var rd = cmd.ExecuteReader();
            var nd = Dimensions.All.Length;
            var sb = new StringBuilder();
            while (rd.Read())
            {
                sb.Clear();
                for (var i = 0; i < 2 + nd; i++) sb.Append(rd.GetValue(i)).Append('|');
                for (var i = 2 + nd; i < 6 + nd; i++) sb.Append(Math.Round(rd.GetDouble(i), 6).ToString("R", CultureInfo.InvariantCulture)).Append('|');
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant()[..16];
        }
    }
}
