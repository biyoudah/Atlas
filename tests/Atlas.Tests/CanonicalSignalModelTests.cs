using Atlas.Models;
using Atlas.Serving;
using NUnit.Framework;

namespace Atlas.Tests;

[TestFixture]
public class CanonicalSignalModelTests
{
    private static CatalogMetadata CreateTestMetadata() => new()
    {
        EndpointUrl = "opc.tcp://localhost:4840",
        ServerName = "CanonicalSignalTestServer",
        Statistics = new CrawlStatistics(10, 5, 5, 0, 0, 2, 10)
    };

    [Test]
    public void CanonicalSignal_PreservesAllMetadataWithoutLossOrAmbiguity()
    {
        var srcTs = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var srvTs = new DateTime(2026, 10, 24, 12, 0, 1, DateTimeKind.Utc);

        var signal = new CanonicalSignal
        {
            NodeId = "ns=2;s=SpindleSpeed",
            StableNodeId = "nsu=http://example.com/cnc/;s=SpindleSpeed",
            RawValue = 12000.5,
            NumericValue = 12000.5,
            DataType = "Double",
            StatusCode = StatusCodes.Good.Code,
            Quality = QualityState.Good,
            SourceTimestampUtc = srcTs,
            ServerTimestampUtc = srvTs,
            ReceivedTimestampUtc = DateTime.UtcNow,
            IsStale = false,
            SamplingIntervalMs = 100.0,
            SemanticLabel = "spindle_speed",
            Unit = "rpm",
            Provenance = SignalProvenance.StandardCompanionSpec,
            Confidence = 1.0,
            Category = SignalCategory.Sample,
            RawState = "12000.5",
            StandardizedState = null
        };

        Assert.That(signal.NodeId, Is.EqualTo("ns=2;s=SpindleSpeed"));
        Assert.That(signal.StableNodeId, Is.EqualTo("nsu=http://example.com/cnc/;s=SpindleSpeed"));
        Assert.That(signal.NumericValue, Is.EqualTo(12000.5));
        Assert.That(signal.Quality, Is.EqualTo(QualityState.Good));
        Assert.That(signal.SourceTimestampUtc, Is.EqualTo(srcTs));
        Assert.That(signal.ServerTimestampUtc, Is.EqualTo(srvTs));
        Assert.That(signal.Provenance, Is.EqualTo(SignalProvenance.StandardCompanionSpec));
        Assert.That(signal.Confidence, Is.EqualTo(1.0));
        Assert.That(signal.IsStale, Is.False);
    }

    [Test]
    public async Task SubscriptionMultiplexer_PopulatesCanonicalSignalsCorrectly()
    {
        var catalog = new AtlasCatalog
        {
            Metadata = CreateTestMetadata(),
            Entries = new List<CatalogEntry>
            {
                new()
                {
                    Id = "ns=2;s=ExecutionState",
                    BrowseName = "ExecutionState",
                    Path = "Objects/Machine/ExecutionState",
                    NodeClass = "Variable",
                    DataType = "String",
                    Inferred = new InferredSemantics(
                        Label: "machine_state",
                        Confidence: 1.0,
                        Evidence: ["OPC 40501"],
                        Provenance: SignalProvenance.StandardCompanionSpec)
                }
            }
        };

        await using var mux = new SubscriptionMultiplexer(catalog);

        var srcTs = DateTime.UtcNow.AddSeconds(-2);
        var srvTs = DateTime.UtcNow.AddSeconds(-1);

        mux.UpdateValue("ns=2;s=ExecutionState", "ACTIVE", srcTs, srvTs, StatusCodes.Good);

        var canonical = mux.GetCanonicalSignal("ns=2;s=ExecutionState");
        Assert.That(canonical, Is.Not.Null);
        Assert.That(canonical!.RawValue, Is.EqualTo("ACTIVE"));
        Assert.That(canonical.RawState, Is.EqualTo("ACTIVE"));
        Assert.That(canonical.StandardizedState, Is.EqualTo("Active"));
        Assert.That(canonical.Provenance, Is.EqualTo(SignalProvenance.StandardCompanionSpec));
        Assert.That(canonical.Confidence, Is.EqualTo(1.0));
        Assert.That(canonical.SourceTimestampUtc, Is.EqualTo(srcTs));
        Assert.That(canonical.ServerTimestampUtc, Is.EqualTo(srvTs));
        Assert.That(canonical.Quality, Is.EqualTo(QualityState.Good));
        Assert.That(canonical.IsStale, Is.False);
    }
}
