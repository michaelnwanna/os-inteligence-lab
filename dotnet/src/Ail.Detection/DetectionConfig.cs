using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ail.Schemas;

namespace Ail.Detection;

public sealed class CohortConfig
{
    public int WindowMinutes { get; set; } = 10;
    public int MaxDepth { get; set; } = 3;
    public int TopK { get; set; } = 3;
    /// <summary>Bonferroni-adjusted significance across all candidate cohorts.</summary>
    public double Alpha { get; set; } = 1e-3;
    /// <summary>LLR points charged per extra dimension (prefers simple cohorts).</summary>
    public double DepthPenalty { get; set; } = 2.0;
    public double MinEvents { get; set; } = 15;
    public double MinRatioRate { get; set; } = 1.5;
    public double MinRatioGauge { get; set; } = 1.1;
    /// <summary>Rate expectations are estimates; require this much headroom before alerting.</summary>
    public double ExpectationMargin { get; set; } = 0.25;
    public double PriorSessionsRate { get; set; } = 20_000;
    public double PriorSessionsGauge { get; set; } = 500;
    public int Consecutive { get; set; } = 2;
}

public sealed class CorrelationConfig
{
    public double TauMinutes { get; set; } = 60.0;
    public double OnsetToleranceMinutes { get; set; } = 3.0;
    public double WeightTemporal { get; set; } = 0.4;
    public double WeightOverlap { get; set; } = 0.6;
    public double LookbackMinutes { get; set; } = 360.0;
}

/// <summary>Detection engine configuration; hashed into every incident for reproducibility.</summary>
public sealed class DetectionConfig
{
    public int BaselineMinutes { get; set; } = 60;
    public List<string> Metrics { get; set; } = new() { "failure_rate", "latency_ms", "memory_mb" };
    public int WindowMinutes { get; set; } = 5;
    public double ZThreshold { get; set; } = 5.0;
    public int MinConsecutive { get; set; } = 3;
    public int ConfirmMinutes { get; set; } = 2;
    public double CusumK { get; set; } = 0.5;
    public double CusumH { get; set; } = 8.0;
    public double EwmaLambda { get; set; } = 0.2;
    public double EwmaLimit { get; set; } = 4.5;
    public double ChangepointLlr { get; set; } = 15.0;
    public double MinRelativeChange { get; set; } = 0.10;
    public int ShiftWindow { get; set; } = 10;
    public double ShiftZ { get; set; } = 5.0;
    public double MlThreshold { get; set; } = 40.0;
    public int MergeMinutes { get; set; } = 30;
    public int IncidentOnsetMinutes { get; set; } = 20;
    public double RecoveryZ { get; set; } = 2.0;
    public int RecoveryMinutes { get; set; } = 15;
    public int RebaselineGapMinutes { get; set; } = 5;
    public int PipelineStageMinutes { get; set; } = 2;
    public CohortConfig Cohort { get; set; } = new();
    public CorrelationConfig Correlation { get; set; } = new();

    public string Fingerprint() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, AilJson.Options)))).ToLowerInvariant()[..12];
}
