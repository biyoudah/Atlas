using Microsoft.Extensions.Logging;
using Opc.Ua;
using Atlas.Models;
using Opc.Ua.Client;

namespace Atlas.Profiling;

/// <summary>
/// Options controlling a passive live profiling observation run.
/// </summary>
public class ProfilerOptions
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(10);
    public int SamplingIntervalMs { get; set; } = 250;
    public int MaxMonitoredItems { get; set; } = 500;
}

/// <summary>
/// Subscribes passively to target nodes on a live OPC UA server for a specified duration,
/// monitoring rates of change, signal classifications, and cross-node correlations.
/// </summary>
public class LiveProfiler
{
    private readonly ISessionClient m_session;
    private readonly ILogger? m_logger;

    public LiveProfiler(ISessionClient session, ILogger? logger = null)
    {
        m_session = session ?? throw new ArgumentNullException(nameof(session));
        m_logger = logger;
    }

    /// <summary>
    /// Executes a passive profiling run on the given catalog entries.
    /// </summary>
    public async Task<ProfileReport> ProfileAsync(
        IReadOnlyList<CatalogEntry> targetEntries,
        ProfilerOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ProfilerOptions();
        DateTime startTime = DateTime.UtcNow;

        var variableEntries = targetEntries
            .Where(e => e.NodeClass == "Variable" && (e.Access == null || e.Access.Read))
            .Take(options.MaxMonitoredItems)
            .ToList();

        var stateAccumulators = new Dictionary<string, NodeTelemetryAccumulator>(StringComparer.Ordinal);
        foreach (CatalogEntry e in variableEntries)
        {
            stateAccumulators[e.Id] = new NodeTelemetryAccumulator(e.Id, e.BrowseName ?? e.Path);
        }

        // Run sampling loop via batched reads or subscription polling
        // Using high-speed batched reads allows broad compatibility across all PLC tiers
        DateTime endTime = startTime + options.Duration;
        int delayMs = Math.Max(50, options.SamplingIntervalMs);

        NamespaceTable namespaces = m_session.MessageContext.NamespaceUris;
        List<ReadValueId> readIds = variableEntries.Select(e => new ReadValueId
        {
            NodeId = StableNodeIdentifier.ToNodeId(e.Id, namespaces),
            AttributeId = Attributes.Value
        }).ToList();

        m_logger?.LogInformation("Starting passive profiling of {Count} nodes for {Duration}s...", variableEntries.Count, options.Duration.TotalSeconds);

        while (DateTime.UtcNow < endTime && !ct.IsCancellationRequested)
        {
            try
            {
                ReadResponse response = await m_session.ReadAsync(
                    null,
                    0,
                    TimestampsToReturn.Neither,
                    readIds.ToArray(),
                    ct).ConfigureAwait(false);

                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < response.Results.Count && i < variableEntries.Count; i++)
                {
                    DataValue dv = response.Results[i];
                    if (!StatusCode.IsBad(dv.StatusCode) && !dv.WrappedValue.IsNull)
                    {
                        object? boxed = dv.WrappedValue.AsBoxedObject(Variant.BoxingBehavior.Legacy);
                        stateAccumulators[variableEntries[i].Id].Record(now, boxed);
                    }
                }
            }
            catch (Exception ex)
            {
                m_logger?.LogDebug(ex, "Transient error during profile sampling tick; continuing.");
            }

            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        DateTime completedTime = DateTime.UtcNow;
        double durationMinutes = Math.Max(0.01, (completedTime - startTime).TotalMinutes);

        // Analyze each node's behavior
        var profileResults = new Dictionary<string, NodeProfile>(StringComparer.Ordinal);
        foreach (var (id, acc) in stateAccumulators)
        {
            var classification = ProfileAnalyzer.Classify(
                acc.SampleCount,
                acc.ChangeCount,
                acc.ValuesHistory,
                acc.NumericHistory);

            double ratePerMin = acc.ChangeCount / durationMinutes;

            var profile = new NodeProfile(
                Id: id,
                BrowseName: acc.BrowseName,
                Classification: classification,
                SampleCount: acc.SampleCount,
                ChangeCount: acc.ChangeCount,
                ChangeRatePerMinute: Math.Round(ratePerMin, 2),
                ObservedMin: acc.NumericHistory.Count > 0 ? acc.NumericHistory.Min() : null,
                ObservedMax: acc.NumericHistory.Count > 0 ? acc.NumericHistory.Max() : null,
                LastObservedValue: acc.ValuesHistory.LastOrDefault(),
                DistinctValuesCount: acc.ValuesHistory.Distinct().Count())
            {
                ChangeTimestamps = acc.ChangeTimestamps
            };

            profileResults[id] = profile;
        }

        // Analyze cross-correlations
        var correlations = new List<CorrelatedTransition>();
        var profileList = profileResults.Values.ToList();

        for (int i = 0; i < profileList.Count; i++)
        {
            for (int j = i + 1; j < profileList.Count; j++)
            {
                NodeProfile a = profileList[i];
                NodeProfile b = profileList[j];

                // Correlation check: State/Boolean vs Analog
                if ((a.Classification is SignalClassification.Boolean or SignalClassification.StateEnum) &&
                    b.Classification == SignalClassification.AnalogProcessValue)
                {
                    var accA = stateAccumulators[a.Id];
                    var accB = stateAccumulators[b.Id];
                    if (ProfileAnalyzer.DetectStateTracking(accA.ValuesHistory, accB.NumericHistory, out string rel))
                    {
                        correlations.Add(new CorrelatedTransition(a.Id, b.Id, 0.95, rel, Confidence: 0.90));
                    }
                }
                else if (a.Classification == SignalClassification.AnalogProcessValue &&
                         b.Classification == SignalClassification.AnalogProcessValue)
                {
                    var accA = stateAccumulators[a.Id];
                    var accB = stateAccumulators[b.Id];
                    double r = ProfileAnalyzer.ComputePearsonCorrelation(accA.NumericHistory, accB.NumericHistory);
                    if (Math.Abs(r) >= 0.85)
                    {
                        string rel = r > 0 ? "Strongly co-varying positive correlation" : "Strongly co-varying negative correlation";
                        correlations.Add(new CorrelatedTransition(a.Id, b.Id, r, rel, Confidence: Math.Abs(r)));
                    }
                }
            }
        }

        return new ProfileReport
        {
            StartedAtUtc = startTime,
            CompletedAtUtc = completedTime,
            Profiles = profileResults,
            Correlations = correlations
        };
    }

    private sealed class NodeTelemetryAccumulator
    {
        public string Id { get; }
        public string BrowseName { get; }
        public int SampleCount { get; private set; }
        public int ChangeCount { get; private set; }
        public List<object?> ValuesHistory { get; } = [];
        public List<double> NumericHistory { get; } = [];
        public List<DateTime> ChangeTimestamps { get; } = [];
        private object? m_lastValue;

        public NodeTelemetryAccumulator(string id, string browseName)
        {
            Id = id;
            BrowseName = browseName;
        }

        public void Record(DateTime timestamp, object? val)
        {
            SampleCount++;
            ValuesHistory.Add(val);

            if (val is double d) NumericHistory.Add(d);
            else if (val is float f) NumericHistory.Add(f);
            else if (val is int i) NumericHistory.Add(i);
            else if (val is uint u) NumericHistory.Add(u);
            else if (val is short s) NumericHistory.Add(s);
            else if (val is ushort us) NumericHistory.Add(us);
            else if (val is long l) NumericHistory.Add(l);

            if (SampleCount == 1)
            {
                m_lastValue = val;
                return;
            }

            bool changed = !Equals(m_lastValue, val);
            if (changed)
            {
                ChangeCount++;
                ChangeTimestamps.Add(timestamp);
                m_lastValue = val;
            }
        }
    }
}
