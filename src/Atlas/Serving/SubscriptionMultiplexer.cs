using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Atlas.Models;
using Opc.Ua.Client;

namespace Atlas.Serving;

/// <summary>
/// Holds a single OPC UA session, multiplexes monitored item subscriptions across
/// multiple external viewers, and caches latest telemetry values.
/// Handles monitored-item limits by gracefully falling back to grouped batched Reads.
/// Enforces read-only safety by default with strict write allowlisting and audit logging.
/// </summary>
public sealed class SubscriptionMultiplexer : IAsyncDisposable
{
    private readonly ISession? _session;
    private readonly AtlasCatalog _catalog;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, (CatalogEntry Entry, object? Value, DateTime Timestamp)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _nodeSubscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writeAllowlist = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _subLock = new(1, 1);

    private Subscription? _opcSubscription;
    private readonly ConcurrentDictionary<string, MonitoredItem> _monitoredItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NodeId> _polledNodes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

    public bool IsReadOnly { get; set; } = true;
    public int MaxMonitoredItems { get; set; } = 500;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(1000);

    public QualityGuard QualityGuard { get; }
    public SyntheticMetricsEngine SyntheticEngine { get; } = new();
    public DateTime StartTimeUtc { get; } = DateTime.UtcNow;
    public double LastScrapeLatencyMs { get; private set; }
    public long ReadErrorsTotal => _readErrorsTotal;
    public long ReconnectsTotal => _reconnectsTotal;
    public int ActiveMonitoredItemsCount => _monitoredItems.Count;
    public bool IsSessionConnected => _session != null && _session.Connected;

    public void RecordReconnect() => Interlocked.Increment(ref _reconnectsTotal);
    public void RecordReadError() => Interlocked.Increment(ref _readErrorsTotal);

    private long _readErrorsTotal;
    private long _reconnectsTotal;

    public event Action<string, CatalogEntry, object?, DateTime>? OnValueChanged;

    public SubscriptionMultiplexer(
        AtlasCatalog catalog,
        ISession? session = null,
        ILogger? logger = null,
        IEnumerable<string>? writeAllowlist = null,
        QualityGuard? qualityGuard = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _session = session;
        _logger = logger;
        QualityGuard = qualityGuard ?? new QualityGuard(failureThreshold: 3, staleTimeout: TimeSpan.FromSeconds(10), logger: _logger);

        if (writeAllowlist != null)
        {
            foreach (string item in writeAllowlist)
            {
                _writeAllowlist.Add(item.Trim('\'', '"'));
            }

            if (_writeAllowlist.Count > 0)
            {
                IsReadOnly = false;
            }
        }

        // Initialize cache with known catalog entries
        foreach (CatalogEntry entry in catalog.Entries)
        {
            _cache[entry.Id] = (entry, null, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Gets self-supervision diagnostics for the collector gateway.
    /// </summary>
    public CollectorDiagnostics GetDiagnostics() => new(
        UptimeSeconds: Math.Round((DateTime.UtcNow - StartTimeUtc).TotalSeconds, 1),
        LastScrapeLatencyMs: Math.Round(LastScrapeLatencyMs, 2),
        ReadErrorsTotal: _readErrorsTotal,
        ReconnectsTotal: _reconnectsTotal,
        ActiveMonitoredItemsCount: _monitoredItems.Count,
        IsConnected: IsSessionConnected);

    /// <summary>
    /// Gets a thread-safe snapshot of all cached values.
    /// </summary>
    public IReadOnlyDictionary<string, (CatalogEntry Entry, object? Value)> GetSnapshot()
    {
        var dict = new Dictionary<string, (CatalogEntry Entry, object? Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _cache)
        {
            dict[k] = (v.Entry, v.Value);
        }
        return dict;
    }

    /// <summary>
    /// Updates a cached node value manually (used in simulations or batch reads).
    /// </summary>
    public void UpdateValue(string nodeIdOrUri, object? value, DateTime? timestamp = null)
    {
        DateTime ts = timestamp ?? DateTime.UtcNow;
        CatalogEntry? entry = null;

        if (_cache.TryGetValue(nodeIdOrUri, out var existing))
        {
            entry = existing.Entry;
        }
        else
        {
            // Lookup by BrowseName or Path in catalog
            entry = _catalog.Entries.FirstOrDefault(e =>
                string.Equals(e.Id, nodeIdOrUri, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.BrowseName, nodeIdOrUri, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                entry = new CatalogEntry
                {
                    Id = nodeIdOrUri,
                    NodeClass = "Variable",
                    BrowseName = nodeIdOrUri,
                    DisplayName = nodeIdOrUri,
                    DataType = "Double",
                    Access = new NodeAccessInfo(true, false),
                    Path = nodeIdOrUri
                };
            }
        }

        bool isCounter = entry.Inferred?.Label is "parts_produced" or "part_counter" ||
                         (entry.BrowseName != null && entry.BrowseName.Contains("part", StringComparison.OrdinalIgnoreCase));

        var record = QualityGuard.ProcessGoodValue(nodeIdOrUri, value, ts, isCounter);
        object? finalValue = record.Value;

        _cache[nodeIdOrUri] = (entry, finalValue, ts);
        OnValueChanged?.Invoke(nodeIdOrUri, entry, finalValue, ts);
    }

    /// <summary>
    /// Registers a client subscription to a set of nodes.
    /// </summary>
    public void SubscribeClient(string clientId, IEnumerable<string> nodeIds)
    {
        foreach (string rawId in nodeIds)
        {
            string id = rawId.Trim();
            var subscribers = _nodeSubscribers.GetOrAdd(id, _ => new HashSet<string>(StringComparer.Ordinal));
            lock (subscribers)
            {
                subscribers.Add(clientId);
            }

            _ = Task.Run(() => EnsureMonitoringAsync(id));
        }
    }

    /// <summary>
    /// Starts passive monitoring of all readable variables in the catalog to populate cache.
    /// </summary>
    public async Task StartAutoMonitoringAsync(CancellationToken ct = default)
    {
        if (_session == null || !_session.Connected)
        {
            return;
        }

        var readableVars = _catalog.Entries
            .Where(e => e.NodeClass == "Variable" && (e.Access == null || e.Access.Read))
            .Take(MaxMonitoredItems)
            .Select(e => e.Id)
            .ToList();

        SubscribeClient("internal_prometheus", readableVars);
        await Task.Delay(500, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unregisters a client subscription from a set of nodes.
    /// </summary>
    public void UnsubscribeClient(string clientId, IEnumerable<string>? nodeIds = null)
    {
        if (nodeIds == null)
        {
            // Unsubscribe client from all nodes
            foreach (var (_, subscribers) in _nodeSubscribers)
            {
                lock (subscribers)
                {
                    subscribers.Remove(clientId);
                }
            }
        }
        else
        {
            foreach (string rawId in nodeIds)
            {
                string id = rawId.Trim();
                if (_nodeSubscribers.TryGetValue(id, out var subscribers))
                {
                    lock (subscribers)
                    {
                        subscribers.Remove(clientId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Attempts to execute an authorized write with safety checks and audit logging.
    /// </summary>
    public async Task<(bool Success, string Message)> WriteValueAsync(
        string clientId,
        string nodeIdOrUri,
        object value,
        CancellationToken ct = default)
    {
        // 1. Read-only gate
        if (IsReadOnly)
        {
            _logger?.LogWarning("[AUDIT REJECTED] Client {ClientId} attempted write to {Node} while gateway is in read-only mode.", clientId, nodeIdOrUri);
            return (false, "Rejected: Gateway is operating in strict read-only mode.");
        }

        // 2. Emergency stop / safety protection
        if (nodeIdOrUri.Contains("EmergencyStop", StringComparison.OrdinalIgnoreCase) ||
            nodeIdOrUri.Contains("EStop", StringComparison.OrdinalIgnoreCase) ||
            nodeIdOrUri.Contains("Safety", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogCritical("[AUDIT BLOCKED] Safety violation: Write attempt to emergency stop / safety node {Node} by {ClientId} was permanently blocked.", nodeIdOrUri, clientId);
            return (false, "Rejected: Hard safety violation. Emergency stop and safety nodes cannot be written via network gateway.");
        }

        // 3. Allowlist check
        if (!_writeAllowlist.Contains(nodeIdOrUri))
        {
            _logger?.LogWarning("[AUDIT REJECTED] Client {ClientId} attempted write to non-allowlisted node {Node}.", clientId, nodeIdOrUri);
            return (false, $"Rejected: Node '{nodeIdOrUri}' is not in the authorized write allowlist.");
        }

        // 4. Audit Log Entry
        _logger?.LogInformation("[AUDIT WRITE] Client {ClientId} wrote value '{Value}' to node {Node} at {Timestamp:O}", clientId, value, nodeIdOrUri, DateTime.UtcNow);

        // 5. Execute write on OPC UA session if connected
        if (_session != null && _session.Connected)
        {
            try
            {
                ISession session = _session;
                NodeId targetId = session.NamespaceUris != null
                    ? StableNodeIdentifier.ToNodeId(nodeIdOrUri, session.NamespaceUris)
                    : NodeId.Parse(nodeIdOrUri);
                var writeVal = new WriteValue
                {
                    NodeId = targetId,
                    AttributeId = Attributes.Value,
                    Value = new DataValue(CreateVariant(value))
                };

                var response = await session.WriteAsync(
                    null,
                    [writeVal],
                    ct).ConfigureAwait(false);

                if (response.Results.Count > 0 && StatusCode.IsBad(response.Results[0]))
                {
                    return (false, $"OPC UA server rejected write: {response.Results[0]}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Write exception: {ex.Message}");
            }
        }

        // Update local cache
        UpdateValue(nodeIdOrUri, value);
        return (true, "Write successful.");
    }

    private async Task EnsureMonitoringAsync(string nodeIdStr)
    {
        if (_session == null || !_session.Connected)
        {
            return;
        }

        ISession session = _session;

        // Check if already monitored
        if (_monitoredItems.ContainsKey(nodeIdStr) || _polledNodes.ContainsKey(nodeIdStr))
        {
            return;
        }

        NodeId targetNodeId;
        try
        {
            targetNodeId = session.NamespaceUris != null
                ? StableNodeIdentifier.ToNodeId(nodeIdStr, session.NamespaceUris)
                : NodeId.Parse(nodeIdStr);
        }
        catch
        {
            // Try resolving by matching CatalogEntry
            var entry = _catalog.Entries.FirstOrDefault(e => e.Id == nodeIdStr);
            if (entry == null) return;
            try
            {
                targetNodeId = session.NamespaceUris != null
                    ? StableNodeIdentifier.ToNodeId(entry.Id, session.NamespaceUris)
                    : NodeId.Parse(entry.Id);
            }
            catch { return; }
        }

        await _subLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // If below limit, create MonitoredItem in subscription
            if (_monitoredItems.Count < MaxMonitoredItems)
            {
                try
                {
                    if (_opcSubscription == null)
                    {
                        _opcSubscription = new Subscription(session.DefaultSubscription)
                        {
                            PublishingInterval = 500,
                            PublishingEnabled = true
                        };
                        session.AddSubscription(_opcSubscription);
                        await _opcSubscription.CreateAsync().ConfigureAwait(false);
                    }

                    var item = new MonitoredItem(_opcSubscription.DefaultItem)
                    {
                        StartNodeId = targetNodeId,
                        AttributeId = Attributes.Value,
                        SamplingInterval = 250,
                        QueueSize = 1,
                        DiscardOldest = true
                    };

                    item.Notification += (mItem, e) =>
                    {
                        if (e.NotificationValue is MonitoredItemNotification min)
                        {
                            object? val = min.Value.WrappedValue.AsBoxedObject(Variant.BoxingBehavior.Legacy);
                            DateTime ts = (DateTime)min.Value.SourceTimestamp;
                            UpdateValue(nodeIdStr, val, ts);
                        }
                    };

                    _opcSubscription.AddItem(item);
                    await _opcSubscription.ApplyChangesAsync().ConfigureAwait(false);
                    _monitoredItems[nodeIdStr] = item;
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to create MonitoredItem for {NodeId}. Falling back to grouped polled Reads.", nodeIdStr);
                }
            }

            // Fallback: Grouped batched reads
            _polledNodes[nodeIdStr] = targetNodeId;
            EnsurePollingTaskRunning();
        }
        finally
        {
            _subLock.Release();
        }
    }

    private void EnsurePollingTaskRunning()
    {
        if (_pollingTask != null && !_pollingTask.IsCompleted)
        {
            return;
        }

        _pollingCts = new CancellationTokenSource();
        _pollingTask = Task.Run(() => PollingLoopAsync(_pollingCts.Token));
    }

    private async Task PollingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _session != null && _session.Connected)
        {
            try
            {
                var kvps = _polledNodes.ToArray();
                if (kvps.Length > 0)
                {
                    var readList = new List<ReadValueId>();
                    foreach (var kvp in kvps)
                    {
                        readList.Add(new ReadValueId
                        {
                            NodeId = kvp.Value,
                            AttributeId = Attributes.Value
                        });
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var response = await _session.ReadAsync(
                        null,
                        0,
                        TimestampsToReturn.Both,
                        readList.ToArray(),
                        ct).ConfigureAwait(false);
                    sw.Stop();
                    LastScrapeLatencyMs = sw.Elapsed.TotalMilliseconds;

                    for (int i = 0; i < response.Results.Count; i++)
                    {
                        var res = response.Results[i];
                        string idStr = kvps[i].Key;
                        if (StatusCode.IsGood(res.StatusCode))
                        {
                            object? val = res.WrappedValue.AsBoxedObject(Variant.BoxingBehavior.Legacy);
                            DateTime ts = (DateTime)res.SourceTimestamp;
                            UpdateValue(idStr, val, ts);
                        }
                        else
                        {
                            Interlocked.Increment(ref _readErrorsTotal);
                            QualityGuard.ProcessFailure(idStr, res.StatusCode.ToString());
                        }
                    }
                }

                await Task.Delay(PollingInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _readErrorsTotal);
                foreach (var kvp in _polledNodes.ToArray())
                {
                    QualityGuard.ProcessFailure(kvp.Key, ex.Message);
                }
                _logger?.LogDebug(ex, "Polled read cycle encountered an error.");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pollingCts?.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask.ConfigureAwait(false); } catch { }
        }

        if (_opcSubscription != null && _session != null && _session.Connected)
        {
            try
            {
                await _session.RemoveSubscriptionAsync(_opcSubscription).ConfigureAwait(false);
            }
            catch { }
        }

        _subLock.Dispose();
    }

    private static Variant CreateVariant(object value)
    {
        return value switch
        {
            double d => new Variant(d),
            float f => new Variant(f),
            int i => new Variant(i),
            uint u => new Variant(u),
            long l => new Variant(l),
            ulong ul => new Variant(ul),
            short s => new Variant(s),
            ushort us => new Variant(us),
            byte b => new Variant(b),
            sbyte sb => new Variant(sb),
            bool b => new Variant(b),
            string s => new Variant(s),
            _ => Variant.Null
        };
    }
}
