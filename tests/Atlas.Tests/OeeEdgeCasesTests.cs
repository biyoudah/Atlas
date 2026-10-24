using Atlas.Mes;
using Atlas.Models;
using NUnit.Framework;

namespace Atlas.Tests;

[TestFixture]
public class OeeEdgeCasesTests
{
    private static CatalogMetadata CreateTestMetadata() => new()
    {
        EndpointUrl = "opc.tcp://localhost:4840",
        ServerName = "OeeEdgeCasesTestServer",
        Statistics = new CrawlStatistics(10, 5, 5, 0, 0, 2, 10)
    };

    [Test]
    public void WorkOrderTransition_ResetsOperatingTimeAccumulatorCleanly()
    {
        var calculator = new OeeCalculator();
        var startTime = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);

        var wo1 = new WorkOrderContext
        {
            OrderId = "OF-BATCH-001",
            PartNumber = "VALVE-A",
            TargetQuantity = 100,
            NominalCycleTimeSeconds = 60.0
        };

        var catalog = new AtlasCatalog
        {
            Metadata = CreateTestMetadata(),
            Entries = [
                new() { Id = "ns=2;s=State", BrowseName = "machine_state", Path = "State", NodeClass = "Variable" },
                new() { Id = "ns=2;s=Parts", BrowseName = "parts_produced", Path = "Parts", NodeClass = "Variable" }
            ]
        };

        var snapshot1 = new Dictionary<string, (CatalogEntry Entry, object? Value)>
        {
            ["ns=2;s=State"] = (catalog.Entries[0], "ACTIVE"),
            ["ns=2;s=Parts"] = (catalog.Entries[1], 50)
        };

        // Advance 1800s (30 min) in Order 1
        calculator.Calculate(wo1, snapshot1, startTime);
        var result1 = calculator.Calculate(wo1, snapshot1, startTime.AddSeconds(1800));

        Assert.That(result1.OperatingTimeSeconds, Is.EqualTo(1800.0));
        Assert.That(result1.OrderId, Is.EqualTo("OF-BATCH-001"));

        // Transition to Order 2
        var wo2 = new WorkOrderContext
        {
            OrderId = "OF-BATCH-002",
            PartNumber = "VALVE-B",
            TargetQuantity = 200,
            NominalCycleTimeSeconds = 45.0
        };

        var snapshot2 = new Dictionary<string, (CatalogEntry Entry, object? Value)>
        {
            ["ns=2;s=State"] = (catalog.Entries[0], "ACTIVE"),
            ["ns=2;s=Parts"] = (catalog.Entries[1], 0)
        };

        var transitionResult = calculator.Calculate(wo2, snapshot2, startTime.AddSeconds(1800));
        // Operating time must reset to 0 for the new order
        Assert.That(transitionResult.OperatingTimeSeconds, Is.EqualTo(0.0));
        Assert.That(transitionResult.OrderId, Is.EqualTo("OF-BATCH-002"));
    }

    [Test]
    public void ScrapAnomalies_ExceedingTotalParts_ClampedToZeroGoodParts()
    {
        var calculator = new OeeCalculator();
        var wo = new WorkOrderContext
        {
            OrderId = "OF-SCRAP-TEST",
            PartNumber = "GEAR-10",
            TargetQuantity = 100,
            NominalCycleTimeSeconds = 30.0,
            DeclaredScrapParts = 50 // Declared scrap is 50
        };

        var catalog = new AtlasCatalog
        {
            Metadata = CreateTestMetadata(),
            Entries = [
                new() { Id = "ns=2;s=Parts", BrowseName = "parts_produced", Path = "Parts", NodeClass = "Variable" }
            ]
        };

        // PLC reports only 30 total parts produced (Scrap > Total anomaly)
        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>
        {
            ["ns=2;s=Parts"] = (catalog.Entries[0], 30)
        };

        var result = calculator.Calculate(wo, snapshot, DateTime.UtcNow);

        Assert.That(result.TotalPartsProduced, Is.EqualTo(30));
        Assert.That(result.ScrapPartsProduced, Is.EqualTo(50));
        // Good parts must clamp to 0 rather than negative (-20)
        Assert.That(result.GoodPartsProduced, Is.EqualTo(0));
        Assert.That(result.Quality, Is.EqualTo(0.0));
    }

    [Test]
    public void ZeroOperatingTime_DoesNotTriggerDivisionByZero()
    {
        var calculator = new OeeCalculator();
        var wo = new WorkOrderContext
        {
            OrderId = "OF-IDLE-START",
            PartNumber = "IDLE-01",
            TargetQuantity = 50,
            NominalCycleTimeSeconds = 60.0
        };

        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>();

        var result = calculator.Calculate(wo, snapshot, DateTime.UtcNow);

        Assert.That(result.Availability, Is.EqualTo(0.0));
        Assert.That(result.Performance, Is.EqualTo(0.0));
        Assert.That(result.OeeOverall, Is.EqualTo(0.0));
        Assert.That(double.IsNaN(result.OeeOverall), Is.False);
        Assert.That(double.IsInfinity(result.OeeOverall), Is.False);
    }
}
