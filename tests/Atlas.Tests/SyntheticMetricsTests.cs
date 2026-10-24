using NUnit.Framework;
using Atlas.Models;
using Atlas.Serving;

namespace Atlas.Tests;

[TestFixture]
public class SyntheticMetricsTests
{
    [Test]
    public void ComputeMetrics_CalculatesThermalDriftAndStarvation()
    {
        var engine = new SyntheticMetricsEngine
        {
            DefaultAmbientTemperatureCelsius = 20.0
        };

        var spindleEntry = new CatalogEntry
        {
            Id = "ns=2;s=SpindleTemp",
            Path = "SpindleTemp",
            BrowseName = "SpindleTemperature",
            Inferred = new InferredSemantics("spindle_temperature", 0.9, [])
        };

        var ambientEntry = new CatalogEntry
        {
            Id = "ns=2;s=AmbientTemp",
            Path = "AmbientTemp",
            BrowseName = "AmbientTemperature",
            Inferred = new InferredSemantics("ambient_temperature", 0.9, [])
        };

        var cycleEntry = new CatalogEntry
        {
            Id = "ns=2;s=CycleTime",
            Path = "CycleTime",
            BrowseName = "CycleTime",
            Inferred = new InferredSemantics("cycle_time", 0.9, [])
        };

        var stockEntry = new CatalogEntry
        {
            Id = "ns=2;s=PalletStock",
            Path = "PalletStock",
            BrowseName = "StockRemaining",
            Inferred = new InferredSemantics("pallet_parts_remaining", 0.9, [])
        };

        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>
        {
            ["spindle"] = (spindleEntry, 38.5),
            ["ambient"] = (ambientEntry, 22.0),
            ["cycle"] = (cycleEntry, 45.0),
            ["stock"] = (stockEntry, 10.0)
        };

        var metrics = engine.ComputeMetrics(snapshot);

        // 1. Thermal drift: 38.5 - 22.0 = 16.5 °C
        var drift = metrics.FirstOrDefault(m => m.Label == "spindle_thermal_drift");
        Assert.That(drift, Is.Not.Null);
        Assert.That(drift!.Value, Is.EqualTo(16.5));
        Assert.That(drift.MetricName, Is.EqualTo("opcua_spindle_thermal_drift_celsius"));

        // 2. Time to starvation: 10 parts * 45s = 450s
        var tts = metrics.FirstOrDefault(m => m.Label == "time_to_starvation");
        Assert.That(tts, Is.Not.Null);
        Assert.That(tts!.Value, Is.EqualTo(450.0));
        Assert.That(tts.MetricName, Is.EqualTo("opcua_time_to_starvation_seconds"));
    }

    [Test]
    public void PrometheusExporter_SerializesSyntheticMetrics()
    {
        var engine = new SyntheticMetricsEngine();

        var spindleEntry = new CatalogEntry
        {
            Id = "ns=2;s=SpindleTemp",
            Path = "SpindleTemp",
            Inferred = new InferredSemantics("spindle_temperature", 0.9, [])
        };

        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>
        {
            ["spindle"] = (spindleEntry, 42.0)
        };

        string output = PrometheusExporter.SerializeMetrics(
            snapshot,
            machineName: "cnc_01",
            syntheticEngine: engine);

        Assert.That(output, Does.Contain("opcua_spindle_thermal_drift_celsius{machine=\"cnc_01\"} 20"));
    }
}
