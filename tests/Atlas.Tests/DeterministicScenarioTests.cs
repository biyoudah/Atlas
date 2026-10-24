using Atlas.FakeCnc;
using Atlas.Serving;
using NUnit.Framework;

namespace Atlas.Tests;

[TestFixture]
public class DeterministicScenarioTests
{
    [Test]
    public void ScenarioNormalProduction_ProducesPredictablePhysicsAndParts()
    {
        var model = new CncSimulationModel(seed: 1234, scenario: SimulationScenario.NormalProduction);

        Assert.That(model.ExecutionState, Is.EqualTo("ACTIVE"));
        Assert.That(model.ActualSpeed, Is.EqualTo(10000.0));
        Assert.That(model.ActiveAlarmsCount, Is.EqualTo(0));

        uint initialParts = model.PartsProduced;

        // Run 50 seconds of simulation ticks (50 * 1s)
        for (int i = 0; i < 50; i++)
        {
            model.Tick(1.0);
        }

        // Nominal cycle time is 45s, so at least 1 part must have completed
        Assert.That(model.PartsProduced, Is.GreaterThan(initialParts));
        Assert.That(model.GoodParts, Is.GreaterThanOrEqualTo(1));
        Assert.That(model.SpindleStatusCode, Is.EqualTo(StatusCodes.Good));
    }

    [Test]
    public void ScenarioCounterReset_QualityGuardAbsorbsResetWithoutNegativeSpike()
    {
        var model = new CncSimulationModel(seed: 42, scenario: SimulationScenario.CounterReset);
        var guard = new QualityGuard();

        // Feed initial baseline
        var initialRec = guard.ProcessGoodValue("ns=2;s=PartsProduced", 150, DateTime.UtcNow, isCounter: true);
        Assert.That(initialRec.Value, Is.EqualTo(150.0));

        // Advance model to tick 5 where CounterReset triggers (PartsProduced drops to 0)
        for (int i = 0; i < 5; i++)
        {
            model.Tick(1.0);
        }

        Assert.That(model.PartsProduced, Is.EqualTo(0), "Model should have reset counter to 0 at tick 5");

        // Feed the reset counter to QualityGuard
        var smoothedRec = guard.ProcessGoodValue("ns=2;s=PartsProduced", (int)model.PartsProduced, DateTime.UtcNow, isCounter: true);

        // QualityGuard must absorb the offset (150) so downstream rate remains non-negative (150 + 0 = 150)
        Assert.That(smoothedRec.Value, Is.EqualTo(150.0), "Counter reset must be absorbed without negative delta spike");
        Assert.That(smoothedRec.CounterOffset, Is.EqualTo(150.0));
    }

    [Test]
    public void ScenarioQualityFault_GeneratesBadStatusAndThermalDrift()
    {
        var model = new CncSimulationModel(seed: 42, scenario: SimulationScenario.QualityFault);

        // Advance to tick 3
        for (int i = 0; i < 3; i++)
        {
            model.Tick(1.0);
        }

        Assert.That(model.SpindleStatusCode, Is.EqualTo(StatusCodes.BadSensorFailure));
        Assert.That(model.SpindleTemperature, Is.GreaterThan(100.0), "Thermal drift should exceed standard EURange limit");
        Assert.That(model.VibrationRMS, Is.GreaterThan(20.0), "Vibration should indicate extreme bearing distress");
    }

    [Test]
    public void ScenarioProductionDisruption_TripsAlarmAndHaltsSpindle()
    {
        var model = new CncSimulationModel(seed: 42, scenario: SimulationScenario.ProductionDisruption);

        // Advance to tick 4 where tool breakage alarm trips
        for (int i = 0; i < 5; i++)
        {
            model.Tick(1.0);
        }

        Assert.That(model.ExecutionState, Is.EqualTo("INTERRUPTED"));
        Assert.That(model.FeedHold, Is.True);
        Assert.That(model.ActiveAlarmsCount, Is.EqualTo(3));
        Assert.That(model.ToolLifeRemaining, Is.EqualTo(0.0));
        Assert.That(model.ActualSpeed, Is.LessThan(10000.0), "Spindle must decelerate upon feed hold");
    }

    [Test]
    public void ScenarioNetworkInterruption_FreezesUpdatesDuringOutage()
    {
        var model = new CncSimulationModel(seed: 42, scenario: SimulationScenario.NetworkInterruption);

        // Tick 1 & 2: normal
        model.Tick(1.0);
        model.Tick(1.0);
        Assert.That(model.SimulateNetworkDrop, Is.False);

        // Tick 3: enters simulated outage
        model.Tick(1.0);
        Assert.That(model.SimulateNetworkDrop, Is.True);

        // Tick 8: recovers from outage
        for (int i = 0; i < 5; i++)
        {
            model.Tick(1.0);
        }
        Assert.That(model.SimulateNetworkDrop, Is.False);
    }
}
