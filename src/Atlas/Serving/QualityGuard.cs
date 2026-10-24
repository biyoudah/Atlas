using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Atlas.Serving;

/// <summary>
/// Status of an industrial telemetry point after hysteresis filtering.
/// </summary>
public enum QualityState
{
    Good,
    Uncertain,
    Bad
}

/// <summary>
/// Tracking state and metadata for a guarded node.
/// </summary>
public sealed class NodeQualityRecord
{
    public string NodeId { get; init; } = string.Empty;
    public QualityState State { get; set; } = QualityState.Good;
    public object? Value { get; set; }
    public object? LastGoodValue { get; set; }
    public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastGoodUpdateUtc { get; set; } = DateTime.UtcNow;
    public int ConsecutiveFailures { get; set; }
    public bool IsStale { get; set; }
    public double CounterOffset { get; set; }
    public double LastRawCounterValue { get; set; }
    public bool HasCounterBaseline { get; set; }
}

/// <summary>
/// Industrial robustness guardrails:
/// 1. Network micro-cut hysteresis (retains last good value as 'Uncertain' before marking 'Bad').
/// 2. Stale data detection (flags frozen or unrefreshed telemetry after a configurable timeout).
/// 3. Monotonic counter protection (absorbs PLC daily resets / roll-overs without negative spikes).
/// </summary>
public sealed class QualityGuard
{
    private readonly ConcurrentDictionary<string, NodeQualityRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public int FailureThreshold { get; set; } = 3;
    public TimeSpan StaleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public QualityGuard(int failureThreshold = 3, TimeSpan? staleTimeout = null, ILogger? logger = null)
    {
        FailureThreshold = failureThreshold > 0 ? failureThreshold : 3;
        StaleTimeout = staleTimeout ?? TimeSpan.FromSeconds(10);
        _logger = logger;
    }

    /// <summary>
    /// Processes a successful read or subscription notification for a node.
    /// Resets consecutive failure counter and smooths counters if marked monotonic.
    /// </summary>
    public NodeQualityRecord ProcessGoodValue(string nodeId, object? rawValue, DateTime timestampUtc, bool isCounter = false)
    {
        var record = _records.GetOrAdd(nodeId, id => new NodeQualityRecord { NodeId = id });

        lock (record)
        {
            record.ConsecutiveFailures = 0;
            record.State = QualityState.Good;
            record.LastUpdateUtc = timestampUtc;
            record.LastGoodUpdateUtc = timestampUtc;
            record.IsStale = false;

            object? processedValue = rawValue;

            if (isCounter && TryConvertToDouble(rawValue, out double currentCounter))
            {
                if (record.HasCounterBaseline)
                {
                    // Detect counter reset or PLC restart (drop in counter value)
                    if (currentCounter < record.LastRawCounterValue)
                    {
                        _logger?.LogWarning(
                            "PLC counter reset detected for node {NodeId}: dropped from {Prev} to {Curr}. Absorbing reset into offset.",
                            nodeId, record.LastRawCounterValue, currentCounter);

                        record.CounterOffset += record.LastRawCounterValue;
                    }
                }
                else
                {
                    record.HasCounterBaseline = true;
                }

                record.LastRawCounterValue = currentCounter;
                processedValue = record.CounterOffset + currentCounter;
            }

            record.Value = processedValue;
            record.LastGoodValue = processedValue;

            return CloneRecord(record);
        }
    }

    /// <summary>
    /// Processes a read error or failed communication attempt for a node.
    /// Returns Uncertain while below FailureThreshold, Bad once threshold reached.
    /// </summary>
    public NodeQualityRecord ProcessFailure(string nodeId, string? reason = null)
    {
        var record = _records.GetOrAdd(nodeId, id => new NodeQualityRecord { NodeId = id });

        lock (record)
        {
            record.ConsecutiveFailures++;
            record.LastUpdateUtc = DateTime.UtcNow;

            if (record.ConsecutiveFailures < FailureThreshold)
            {
                record.State = QualityState.Uncertain;
                // Preserve last known good value during transient micro-cuts
                record.Value = record.LastGoodValue;
                _logger?.LogDebug(
                    "Node {NodeId} micro-cut ({Count}/{Threshold}): marked Uncertain. Reason: {Reason}",
                    nodeId, record.ConsecutiveFailures, FailureThreshold, reason);
            }
            else
            {
                record.State = QualityState.Bad;
                record.Value = null;
                _logger?.LogWarning(
                    "Node {NodeId} exceeded failure threshold ({Count}): marked Bad. Reason: {Reason}",
                    nodeId, record.ConsecutiveFailures, reason);
            }

            return CloneRecord(record);
        }
    }

    /// <summary>
    /// Evaluates staleness across all tracked nodes against the StaleTimeout.
    /// Returns any nodes whose staleness state transitioned or is stale.
    /// </summary>
    public IReadOnlyList<NodeQualityRecord> EvaluateStaleness(DateTime? nowUtc = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var results = new List<NodeQualityRecord>();

        foreach (var record in _records.Values)
        {
            lock (record)
            {
                bool wasStale = record.IsStale;
                bool isStaleNow = (now - record.LastGoodUpdateUtc) > StaleTimeout;

                if (isStaleNow != wasStale)
                {
                    record.IsStale = isStaleNow;
                    if (isStaleNow)
                    {
                        _logger?.LogWarning("Node {NodeId} telemetry is STALE (no update since {LastUpdateUtc}).",
                            record.NodeId, record.LastGoodUpdateUtc);
                    }
                }

                if (record.IsStale)
                {
                    results.Add(CloneRecord(record));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Gets the quality record for a specific node if tracked.
    /// </summary>
    public NodeQualityRecord? GetRecord(string nodeId)
    {
        if (_records.TryGetValue(nodeId, out var record))
        {
            lock (record)
            {
                return CloneRecord(record);
            }
        }
        return null;
    }

    /// <summary>
    /// Gets all current quality records.
    /// </summary>
    public IReadOnlyDictionary<string, NodeQualityRecord> GetAllRecords()
    {
        var dict = new Dictionary<string, NodeQualityRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _records)
        {
            lock (v)
            {
                dict[k] = CloneRecord(v);
            }
        }
        return dict;
    }

    private static NodeQualityRecord CloneRecord(NodeQualityRecord r) => new()
    {
        NodeId = r.NodeId,
        State = r.State,
        Value = r.Value,
        LastGoodValue = r.LastGoodValue,
        LastUpdateUtc = r.LastUpdateUtc,
        LastGoodUpdateUtc = r.LastGoodUpdateUtc,
        ConsecutiveFailures = r.ConsecutiveFailures,
        IsStale = r.IsStale,
        CounterOffset = r.CounterOffset,
        LastRawCounterValue = r.LastRawCounterValue,
        HasCounterBaseline = r.HasCounterBaseline
    };

    private static bool TryConvertToDouble(object? val, out double result)
    {
        switch (val)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case uint u: result = u; return true;
            case long l: result = l; return true;
            case ulong ul: result = ul; return true;
            case short s: result = s; return true;
            case ushort us: result = us; return true;
            case byte b: result = b; return true;
            case sbyte sb: result = sb; return true;
            default:
                result = 0;
                return false;
        }
    }
}
