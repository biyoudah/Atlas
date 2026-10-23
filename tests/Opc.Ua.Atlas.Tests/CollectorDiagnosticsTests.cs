using NUnit.Framework;
using Opc.Ua.Atlas.Models;
using Opc.Ua.Atlas.Serving;

namespace Opc.Ua.Atlas.Tests;

[TestFixture]
public class CollectorDiagnosticsTests
{
    [Test]
    public void SubscriptionMultiplexer_ExposesAccurateDiagnostics()
    {
        var catalog = new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://localhost:4840",
                ServerUri = "urn:test",
                CrawledAtUtc = DateTime.UtcNow
            },
            Entries = []
        };

        var multiplexer = new SubscriptionMultiplexer(catalog);
        multiplexer.RecordReadError();
        multiplexer.RecordReadError();
        multiplexer.RecordReconnect();

        var diag = multiplexer.GetDiagnostics();

        Assert.That(diag.ReadErrorsTotal, Is.EqualTo(2));
        Assert.That(diag.ReconnectsTotal, Is.EqualTo(1));
        Assert.That(diag.IsConnected, Is.False); // No active live session
        Assert.That(diag.UptimeSeconds, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void PrometheusExporter_SerializesCollectorDiagnostics()
    {
        var diag = new CollectorDiagnostics(
            UptimeSeconds: 3600.5,
            LastScrapeLatencyMs: 12.4,
            ReadErrorsTotal: 4,
            ReconnectsTotal: 1,
            ActiveMonitoredItemsCount: 42,
            IsConnected: true);

        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>();

        string output = PrometheusExporter.SerializeMetrics(
            snapshot,
            machineName: "lathe_03",
            diagnostics: diag);

        Assert.That(output, Does.Contain("atlas_collector_uptime_seconds{machine=\"lathe_03\"} 3600.5"));
        Assert.That(output, Does.Contain("atlas_collector_scrape_latency_seconds{machine=\"lathe_03\"} 0.0124"));
        Assert.That(output, Does.Contain("atlas_collector_read_errors_total{machine=\"lathe_03\"} 4"));
        Assert.That(output, Does.Contain("atlas_collector_reconnects_total{machine=\"lathe_03\"} 1"));
        Assert.That(output, Does.Contain("atlas_collector_monitored_items_active{machine=\"lathe_03\"} 42"));
        Assert.That(output, Does.Contain("atlas_machine_connected{machine=\"lathe_03\"} 1"));
    }
}
