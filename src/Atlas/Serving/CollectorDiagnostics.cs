using System.Text.Json.Serialization;

namespace Atlas.Serving;

/// <summary>
/// Health and operational diagnostics for the Atlas edge gateway / collector.
/// Allows operators and monitoring to distinguish PLC outages from gateway network failures.
/// </summary>
public record CollectorDiagnostics(
    [property: JsonPropertyName("uptimeSeconds")] double UptimeSeconds,
    [property: JsonPropertyName("lastScrapeLatencyMs")] double LastScrapeLatencyMs,
    [property: JsonPropertyName("readErrorsTotal")] long ReadErrorsTotal,
    [property: JsonPropertyName("reconnectsTotal")] long ReconnectsTotal,
    [property: JsonPropertyName("activeMonitoredItemsCount")] int ActiveMonitoredItemsCount,
    [property: JsonPropertyName("isConnected")] bool IsConnected);
