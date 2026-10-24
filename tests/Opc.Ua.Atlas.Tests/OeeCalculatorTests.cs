using NUnit.Framework;
using Opc.Ua.Atlas.Mes;
using Opc.Ua.Atlas.Models;
using Opc.Ua.Atlas.Serving;

namespace Opc.Ua.Atlas.Tests;

[TestFixture]
public class OeeCalculatorTests
{
    [Test]
    public void Calculate_ComputesAccurateOeeAndCompletion()
    {
        var calculator = new OeeCalculator();

        // 480 minutes planned - 30 min pause = 450 min planned net = 27,000 seconds
        var workOrder = new WorkOrderContext
        {
            OrderId = "OF-2026-9481",
            PartNumber = "VALVE-BODY-50",
            TargetQuantity = 500,
            NominalCycleTimeSeconds = 60.0, // 1 min per part
            PlannedProductionMinutes = 480.0,
            PlannedDowntimeMinutes = 30.0,
            DeclaredScrapParts = 5
        };

        // Simulated: 24,000 seconds operating time
        calculator.SetOperatingTimeSeconds(24000.0);

        var partsEntry = new CatalogEntry
        {
            Id = "ns=2;s=PartsCounter",
            Path = "Objects/CNC/Parts",
            Inferred = new InferredSemantics("parts_produced", 0.95, [])
        };

        var stateEntry = new CatalogEntry
        {
            Id = "ns=2;s=State",
            Path = "Objects/CNC/State",
            Inferred = new InferredSemantics("machine_state", 0.95, [])
        };

        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>
        {
            ["parts"] = (partsEntry, 350), // 350 parts produced
            ["state"] = (stateEntry, "RUNNING")
        };

        var result = calculator.Calculate(workOrder, snapshot);

        Assert.That(result.OrderId, Is.EqualTo("OF-2026-9481"));
        Assert.That(result.TargetQuantity, Is.EqualTo(500));
        Assert.That(result.TotalPartsProduced, Is.EqualTo(350));
        Assert.That(result.GoodPartsProduced, Is.EqualTo(345)); // 350 - 5 = 345
        Assert.That(result.ScrapPartsProduced, Is.EqualTo(5));

        // Availability: 24,000s / 27,000s = 0.8889
        Assert.That(result.Availability, Is.EqualTo(0.8889).Within(0.0001));

        // Performance: (350 * 60) / 24,000 = 21,000 / 24,000 = 0.875
        Assert.That(result.Performance, Is.EqualTo(0.8750).Within(0.0001));

        // Quality: 345 / 350 = 0.9857
        Assert.That(result.Quality, Is.EqualTo(0.9857).Within(0.0001));

        // Overall OEE = A * P * Q = 0.8889 * 0.8750 * 0.9857 = 0.7667
        Assert.That(result.OeeOverall, Is.GreaterThanOrEqualTo(0.76).And.LessThanOrEqualTo(0.77));

        // Completion percent: 350 / 500 = 70.0%
        Assert.That(result.CompletionPercent, Is.EqualTo(70.0));

        // ETA: 150 remaining parts * 60s = 9,000 seconds
        Assert.That(result.EstimatedTimeToCompleteSeconds, Is.EqualTo(9000.0));
    }

    [Test]
    public void PrometheusExporter_SerializesOeeMetrics()
    {
        var workOrder = new WorkOrderContext
        {
            OrderId = "OF-TEST",
            PartNumber = "SKU-99",
            TargetQuantity = 100,
            NominalCycleTimeSeconds = 30.0,
            PlannedProductionMinutes = 60.0,
            PlannedDowntimeMinutes = 0
        };

        var calculator = new OeeCalculator();
        calculator.SetOperatingTimeSeconds(1800.0);

        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>();
        var oee = calculator.Calculate(workOrder, snapshot);

        string output = PrometheusExporter.SerializeMetrics(
            snapshot,
            machineName: "line_01",
            oeeResult: oee);

        Assert.That(output, Does.Contain("opcua_oee_overall_ratio{machine=\"line_01\",order_id=\"OF-TEST\",part_number=\"SKU-99\"}"));
        Assert.That(output, Does.Contain("opcua_oee_availability_ratio{machine=\"line_01\",order_id=\"OF-TEST\",part_number=\"SKU-99\"}"));
        Assert.That(output, Does.Contain("opcua_work_order_completion_percent{machine=\"line_01\",order_id=\"OF-TEST\",part_number=\"SKU-99\"}"));
    }

    [Test]
    public void Calculate_BoundaryConditions_ZeroPartsAndZeroOperatingTime_DoesNotThrowAndYieldsZeroOee()
    {
        var calculator = new OeeCalculator();
        var workOrder = new WorkOrderContext
        {
            OrderId = "OF-EMPTY",
            TargetQuantity = 100,
            NominalCycleTimeSeconds = 30.0,
            PlannedProductionMinutes = 60.0
        };

        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>();
        var result = calculator.Calculate(workOrder, snapshot);

        Assert.That(result.Availability, Is.EqualTo(0.0));
        Assert.That(result.Performance, Is.EqualTo(0.0));
        Assert.That(result.Quality, Is.EqualTo(1.0)); // No scrap produced
        Assert.That(result.OeeOverall, Is.EqualTo(0.0));
    }

    [Test]
    public void Calculate_WorkOrderTransition_ResetsOperatingTimeAccumulator()
    {
        var calculator = new OeeCalculator();
        calculator.SetOperatingTimeSeconds(3600.0);

        var of1 = new WorkOrderContext { OrderId = "OF-01", PlannedProductionMinutes = 60.0 };
        var of2 = new WorkOrderContext { OrderId = "OF-02", PlannedProductionMinutes = 60.0 };

        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>();

        // Step 1: Run with OF-01
        var r1 = calculator.Calculate(of1, snapshot);
        Assert.That(r1.OperatingTimeSeconds, Is.EqualTo(3600.0));

        // Step 2: Switch to OF-02 -> Accumulator automatically resets
        var r2 = calculator.Calculate(of2, snapshot);
        Assert.That(r2.OrderId, Is.EqualTo("OF-02"));
        Assert.That(r2.OperatingTimeSeconds, Is.EqualTo(0.0));
    }
}
