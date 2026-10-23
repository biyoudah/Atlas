using NUnit.Framework;
using Opc.Ua.Atlas.Serving;

namespace Opc.Ua.Atlas.Tests;

[TestFixture]
public class QualityGuardTests
{
    [Test]
    public void MicroCut_BelowFailureThreshold_RetainsLastGoodValueAsUncertain()
    {
        var guard = new QualityGuard(failureThreshold: 3);
        string nodeId = "ns=2;s=SpindleSpeed";

        // Step 1: Good reading
        var rec1 = guard.ProcessGoodValue(nodeId, 4500.0, DateTime.UtcNow);
        Assert.That(rec1.State, Is.EqualTo(QualityState.Good));
        Assert.That(rec1.Value, Is.EqualTo(4500.0));
        Assert.That(rec1.ConsecutiveFailures, Is.EqualTo(0));

        // Step 2: 1st failure (transient network micro-cut)
        var rec2 = guard.ProcessFailure(nodeId, "Network timeout");
        Assert.That(rec2.State, Is.EqualTo(QualityState.Uncertain));
        Assert.That(rec2.Value, Is.EqualTo(4500.0)); // Preserved!
        Assert.That(rec2.ConsecutiveFailures, Is.EqualTo(1));

        // Step 3: 2nd failure
        var rec3 = guard.ProcessFailure(nodeId, "Socket reset");
        Assert.That(rec3.State, Is.EqualTo(QualityState.Uncertain));
        Assert.That(rec3.Value, Is.EqualTo(4500.0)); // Still preserved
        Assert.That(rec3.ConsecutiveFailures, Is.EqualTo(2));

        // Step 4: 3rd failure reaches threshold -> Transitions to Bad and clears value
        var rec4 = guard.ProcessFailure(nodeId, "Host unreachable");
        Assert.That(rec4.State, Is.EqualTo(QualityState.Bad));
        Assert.That(rec4.Value, Is.Null);
        Assert.That(rec4.ConsecutiveFailures, Is.EqualTo(3));

        // Step 5: Network recovers -> Restores Good state
        var rec5 = guard.ProcessGoodValue(nodeId, 4550.0, DateTime.UtcNow);
        Assert.That(rec5.State, Is.EqualTo(QualityState.Good));
        Assert.That(rec5.Value, Is.EqualTo(4550.0));
        Assert.That(rec5.ConsecutiveFailures, Is.EqualTo(0));
    }

    [Test]
    public void MonotonicCounter_AbsorbsPlcReset_WithoutNegativeSpikes()
    {
        var guard = new QualityGuard();
        string nodeId = "ns=2;s=PartsProduced";

        // Step 1: Normal production count up to 100
        var r1 = guard.ProcessGoodValue(nodeId, 100, DateTime.UtcNow, isCounter: true);
        Assert.That(r1.Value, Is.EqualTo(100.0));

        // Step 2: Normal count increments to 150
        var r2 = guard.ProcessGoodValue(nodeId, 150, DateTime.UtcNow, isCounter: true);
        Assert.That(r2.Value, Is.EqualTo(150.0));

        // Step 3: Shift change / PLC daily reset: counter drops to 0!
        var r3 = guard.ProcessGoodValue(nodeId, 0, DateTime.UtcNow, isCounter: true);
        // The reset of 150 is absorbed into offset: 150 + 0 = 150
        Assert.That(r3.Value, Is.EqualTo(150.0));
        Assert.That(r3.CounterOffset, Is.EqualTo(150.0));

        // Step 4: Next shift produces 20 parts: 150 + 20 = 170
        var r4 = guard.ProcessGoodValue(nodeId, 20, DateTime.UtcNow, isCounter: true);
        Assert.That(r4.Value, Is.EqualTo(170.0));

        // Step 5: Another 10 parts: 150 + 30 = 180
        var r5 = guard.ProcessGoodValue(nodeId, 30, DateTime.UtcNow, isCounter: true);
        Assert.That(r5.Value, Is.EqualTo(180.0));
    }

    [Test]
    public void StaleDataDetection_FlagsFrozenSignalsAfterTimeout()
    {
        var guard = new QualityGuard(staleTimeout: TimeSpan.FromSeconds(5));
        string nodeId = "ns=2;s=CoolantPressure";

        DateTime t0 = DateTime.UtcNow.AddSeconds(-10); // 10 seconds ago
        guard.ProcessGoodValue(nodeId, 40.5, t0);

        // Evaluate staleness against current time
        var staleRecords = guard.EvaluateStaleness(DateTime.UtcNow);
        Assert.That(staleRecords, Has.Count.EqualTo(1));
        Assert.That(staleRecords[0].IsStale, Is.True);
        Assert.That(staleRecords[0].NodeId, Is.EqualTo(nodeId));

        // Receive fresh update
        guard.ProcessGoodValue(nodeId, 42.0, DateTime.UtcNow);
        var freshRecords = guard.EvaluateStaleness(DateTime.UtcNow);
        Assert.That(freshRecords, Is.Empty);
    }
}
