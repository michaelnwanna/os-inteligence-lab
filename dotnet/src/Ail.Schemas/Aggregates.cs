namespace Ail.Schemas;

/// <summary>One minute x cohort x metric aggregate. For rates N = sessions and K = failures; for gauges S/Ss are sum and sum of squares.</summary>
public readonly record struct AggRow(long Bucket, string Metric, string[] Dims, double N, double K, double S, double Ss);

/// <summary>Where the simulator and the ingestion pipeline write aggregates and change events.</summary>
public interface IAggregateSink
{
    void AddAggregates(IEnumerable<AggRow> rows);
    void AddChange(ChangeEvent change);
    void SetMeta(string key, object value);
    void Commit();
}
