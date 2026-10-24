using System.Globalization;

namespace Atlas.Profiling;

/// <summary>
/// Core analytical algorithms for signal classification and transition correlation.
/// Operates on collected time-series telemetry independently of network IO for high testability.
/// </summary>
public static class ProfileAnalyzer
{
    /// <summary>
    /// Classifies an observed time series into a behavioral category.
    /// </summary>
    public static SignalClassification Classify(
        int sampleCount,
        int changeCount,
        IReadOnlyList<object?> observedValues,
        IReadOnlyList<double> numericValues)
    {
        if (changeCount == 0 || sampleCount <= 1)
        {
            return SignalClassification.Constant;
        }

        // Check for purely Boolean
        bool isBoolean = observedValues.All(v =>
            v is bool ||
            (v is int i && (i == 0 || i == 1)) ||
            (v is string s && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase))));

        var distinctSet = observedValues.Where(v => v != null).Distinct().ToList();

        if (isBoolean && distinctSet.Count <= 2)
        {
            return SignalClassification.Boolean;
        }

        // Check for Monotonic Counter
        if (numericValues.Count >= 2 && IsMonotonicCounter(numericValues))
        {
            return SignalClassification.Counter;
        }

        // Check for State Enum (small distinct set of integers or string states)
        bool isDiscreteEnum = observedValues.All(v => v is string || (v is int or short or byte or sbyte or ushort or uint));
        if (isDiscreteEnum && distinctSet.Count is >= 2 and <= 8 && (double)changeCount / sampleCount < 0.25)
        {
            return SignalClassification.StateEnum;
        }

        // Check for Setpoint (changes rarely, holds constant value)
        double changeRatio = (double)changeCount / sampleCount;
        if (changeRatio < 0.08)
        {
            return SignalClassification.Setpoint;
        }

        // Otherwise, high-frequency fluctuating process value
        return SignalClassification.AnalogProcessValue;
    }

    private static bool IsMonotonicCounter(IReadOnlyList<double> values)
    {
        int increments = 0;
        for (int i = 1; i < values.Count; i++)
        {
            double diff = values[i] - values[i - 1];
            if (diff < 0)
            {
                // Dropped backwards (not counter)
                return false;
            }
            if (diff > 0)
            {
                increments++;
                if (diff > 50.0)
                {
                    // Large step jumps (e.g. 1500 to 2000) are setpoint parameter adjustments, not counters
                    return false;
                }
            }
        }
        return increments >= 2;
    }

    /// <summary>
    /// Computes Pearson correlation coefficient between two numeric series.
    /// </summary>
    public static double ComputePearsonCorrelation(IReadOnlyList<double> seriesA, IReadOnlyList<double> seriesB)
    {
        int n = Math.Min(seriesA.Count, seriesB.Count);
        if (n < 3)
        {
            return 0.0;
        }

        double meanA = seriesA.Take(n).Average();
        double meanB = seriesB.Take(n).Average();

        double sumDiffProduct = 0.0;
        double sumSqA = 0.0;
        double sumSqB = 0.0;

        for (int i = 0; i < n; i++)
        {
            double diffA = seriesA[i] - meanA;
            double diffB = seriesB[i] - meanB;
            sumDiffProduct += diffA * diffB;
            sumSqA += diffA * diffA;
            sumSqB += diffB * diffB;
        }

        double denominator = Math.Sqrt(sumSqA * sumSqB);
        if (denominator < 1e-9)
        {
            return 0.0;
        }

        return Math.Clamp(sumDiffProduct / denominator, -1.0, 1.0);
    }

    /// <summary>
    /// Detects state-dependent activation (e.g. analog node reading near 0 when state is 0, and high when state is 1).
    /// </summary>
    public static bool DetectStateTracking(
        IReadOnlyList<object?> stateSeries,
        IReadOnlyList<double> analogSeries,
        out string relationship)
    {
        relationship = string.Empty;
        int count = Math.Min(stateSeries.Count, analogSeries.Count);
        if (count < 4)
        {
            return false;
        }

        var lowStateAnalog = new List<double>();
        var highStateAnalog = new List<double>();

        for (int i = 0; i < count; i++)
        {
            object? s = stateSeries[i];
            bool isHigh = s switch
            {
                bool b => b,
                int num => num > 0,
                string str => str.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                              str.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
                              str.Equals("1", StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if (isHigh)
            {
                highStateAnalog.Add(analogSeries[i]);
            }
            else
            {
                lowStateAnalog.Add(analogSeries[i]);
            }
        }

        if (lowStateAnalog.Count > 0 && highStateAnalog.Count > 0)
        {
            double avgLow = lowStateAnalog.Average();
            double avgHigh = highStateAnalog.Average();

            // When state is Active/High, analog is markedly higher (e.g. 0 vs 10000 RPM)
            if (avgHigh > 5.0 && (avgLow < 1.0 || (avgHigh / Math.Max(0.1, avgLow)) > 4.0))
            {
                relationship = "tracks Running state";
                return true;
            }
        }

        return false;
    }
}
