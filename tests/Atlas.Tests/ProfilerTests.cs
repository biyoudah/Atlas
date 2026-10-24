using NUnit.Framework;
using Atlas.Profiling;

namespace Atlas.Tests;

[TestFixture]
public class ProfilerTests
{
    [Test]
    public void Classify_NeverChanged_ReturnsConstant()
    {
        var values = new object[] { 42.0, 42.0, 42.0, 42.0 };
        var numeric = new double[] { 42.0, 42.0, 42.0, 42.0 };

        SignalClassification result = ProfileAnalyzer.Classify(
            sampleCount: 4,
            changeCount: 0,
            observedValues: values,
            numericValues: numeric);

        Assert.That(result, Is.EqualTo(SignalClassification.Constant));
    }

    [Test]
    public void Classify_BinaryValues_ReturnsBoolean()
    {
        var values = new object[] { false, true, false, true, false };
        var numeric = Array.Empty<double>();

        SignalClassification result = ProfileAnalyzer.Classify(
            sampleCount: 5,
            changeCount: 4,
            observedValues: values,
            numericValues: numeric);

        Assert.That(result, Is.EqualTo(SignalClassification.Boolean));
    }

    [Test]
    public void Classify_MonotonicallyIncreasingNumbers_ReturnsCounter()
    {
        var values = new object[] { 101, 101, 102, 102, 103, 104 };
        var numeric = new double[] { 101, 101, 102, 102, 103, 104 };

        SignalClassification result = ProfileAnalyzer.Classify(
            sampleCount: 6,
            changeCount: 3,
            observedValues: values,
            numericValues: numeric);

        Assert.That(result, Is.EqualTo(SignalClassification.Counter));
    }

    [Test]
    public void Classify_FrequentFluctuatingFloats_ReturnsAnalogProcessValue()
    {
        var random = new Random(42);
        var values = new List<object>();
        var numeric = new List<double>();

        for (int i = 0; i < 50; i++)
        {
            double val = 10000.0 + random.NextDouble() * 50.0 - 25.0;
            values.Add(val);
            numeric.Add(val);
        }

        SignalClassification result = ProfileAnalyzer.Classify(
            sampleCount: 50,
            changeCount: 49,
            observedValues: values,
            numericValues: numeric);

        Assert.That(result, Is.EqualTo(SignalClassification.AnalogProcessValue));
    }

    [Test]
    public void Classify_RareStepChanges_ReturnsSetpoint()
    {
        // 50 samples, only 2 changes
        var values = new List<object>();
        var numeric = new List<double>();
        for (int i = 0; i < 30; i++) { values.Add(1500.0); numeric.Add(1500.0); }
        for (int i = 0; i < 20; i++) { values.Add(2000.0); numeric.Add(2000.0); }

        SignalClassification result = ProfileAnalyzer.Classify(
            sampleCount: 50,
            changeCount: 1,
            observedValues: values,
            numericValues: numeric);

        Assert.That(result, Is.EqualTo(SignalClassification.Setpoint));
    }

    [Test]
    public void PearsonCorrelation_CoVaryingSeries_ReturnsHighPositiveCorrelation()
    {
        var seriesA = new double[] { 10, 20, 30, 40, 50, 60, 70 };
        var seriesB = new double[] { 12, 19, 31, 38, 52, 61, 68 };

        double r = ProfileAnalyzer.ComputePearsonCorrelation(seriesA, seriesB);

        Assert.That(r, Is.GreaterThan(0.95));
    }

    [Test]
    public void DetectStateTracking_IdentifiesWhenAnalogRampsWithRunningState()
    {
        // State 0 -> speed 0; State 1 -> speed ~10,000
        var state = new object[] { 0, 0, 0, 1, 1, 1, 1, 0, 0 };
        var speed = new double[] { 0.0, 0.0, 0.0, 10020.0, 9980.0, 10040.0, 10010.0, 0.0, 0.0 };

        bool tracks = ProfileAnalyzer.DetectStateTracking(state, speed, out string relationship);

        Assert.That(tracks, Is.True);
        Assert.That(relationship, Does.Contain("tracks Running state"));
    }
}
