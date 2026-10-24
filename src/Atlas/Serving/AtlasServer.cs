using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Atlas.Client;
using Atlas.Crawler;
using Atlas.Exporters;
using Atlas.Labeling;
using Atlas.Models;
using Opc.Ua;
using Opc.Ua.Client;

namespace Atlas.Serving;

/// <summary>
/// Combined HTTP and WebSocket server for Atlas.
/// Serves:
/// - Prometheus metrics at /metrics
/// - Machine catalog at /catalog
/// - Health probe at /health
/// - Live values snapshot at /api/live
/// - Address-space crawl/scan at /api/scan
/// - Real-time WebSocket multiplexed streaming at /ws
/// - Interactive Cockpit UI at / or /ui
/// </summary>
public sealed class AtlasServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly SubscriptionMultiplexer _multiplexer;
    private readonly WebSocketGateway _gateway;
    private readonly AtlasCatalog _catalog;
    private readonly string _prefix;
    private readonly string _machineName;
    private readonly ILogger? _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public WebSocketGateway Gateway => _gateway;
    public SubscriptionMultiplexer Multiplexer => _multiplexer;
    public bool IsListening => _listener.IsListening;
    public bool IncludeNodeIdInMetrics { get; set; } = true;
    public Mes.WorkOrderContext? WorkOrder { get; set; }
    public Mes.OeeCalculator OeeCalculator { get; } = new();

    public AtlasServer(
        AtlasCatalog catalog,
        SubscriptionMultiplexer multiplexer,
        string prefix = "http://localhost:9090/",
        string machineName = "default_machine",
        ILogger? logger = null,
        Mes.WorkOrderContext? workOrder = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _prefix = prefix.EndsWith('/') ? prefix : prefix + "/";
        _machineName = machineName;
        _logger = logger;
        WorkOrder = workOrder;

        _gateway = new WebSocketGateway(_multiplexer, _catalog, _logger);
    }

    /// <summary>
    /// Starts the HTTP and WebSocket listener.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _logger?.LogInformation("Atlas Server listening on {Prefix}", _prefix);

        _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Stops the server.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch { }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error accepting HTTP connection.");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        string path = context.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "/";

        try
        {
            // Pre-flight CORS support
            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS, HEAD";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Accept, Authorization";
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }

            if (path == "/ws")
            {
                if (context.Request.IsWebSocketRequest)
                {
                    string clientId = Guid.NewGuid().ToString("N")[..8];
                    var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                    await _gateway.HandleClientAsync(wsContext.WebSocket, clientId, ct).ConfigureAwait(false);
                    return;
                }

                byte[] err = Encoding.UTF8.GetBytes("WebSocket upgrade required.");
                await SendResponseAsync(context, err, "text/plain; charset=utf-8", HttpStatusCode.BadRequest, ct).ConfigureAwait(false);
                return;
            }

            if (path == "/metrics")
            {
                var diag = _multiplexer.GetDiagnostics();
                var oee = WorkOrder != null ? OeeCalculator.Calculate(WorkOrder, _multiplexer.GetSnapshot()) : null;
                string metrics = PrometheusExporter.SerializeMetrics(
                    _multiplexer.GetSnapshot(),
                    _machineName,
                    _multiplexer.QualityGuard,
                    _multiplexer.SyntheticEngine,
                    oee,
                    diag,
                    includeNodeIdLabel: IncludeNodeIdInMetrics);
                byte[] bytes = Encoding.UTF8.GetBytes(metrics);

                await SendResponseAsync(context, bytes, "text/plain; version=0.0.4; charset=utf-8", HttpStatusCode.OK, ct).ConfigureAwait(false);
                return;
            }

            if (path == "/catalog")
            {
                string json = JsonCatalogExporter.ToJson(_catalog);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                await SendResponseAsync(context, bytes, "application/json; charset=utf-8", HttpStatusCode.OK, ct).ConfigureAwait(false);
                return;
            }

            if (path == "/api/live")
            {
                var snapshot = _multiplexer.GetSnapshot();
                var canonicals = _multiplexer.GetCanonicalSignals();
                var list = new List<object>();

                foreach (var entry in _catalog.Entries)
                {
                    object? val = null;
                    double? numVal = null;
                    string quality = "Good";
                    string? ts = null;
                    bool isStale = false;

                    if (canonicals.TryGetValue(entry.Id, out var sig))
                    {
                        val = sig.RawValue;
                        numVal = sig.NumericValue;
                        quality = sig.Quality.ToString();
                        ts = sig.SourceTimestampUtc.ToString("o");
                        isStale = sig.IsStale;
                    }
                    else if (snapshot.TryGetValue(entry.Id, out var snap))
                    {
                        val = snap.Value;
                        if (QualityGuard.TryConvertToDouble(snap.Value, out double d))
                        {
                            numVal = d;
                        }
                    }

                    string dev = "Main";
                    string chan = "";
                    if (!string.IsNullOrEmpty(entry.Path))
                    {
                        var matchMachine = System.Text.RegularExpressions.Regex.Match(entry.Path, @"Machines/([^/]+)");
                        if (matchMachine.Success)
                        {
                            dev = matchMachine.Groups[1].Value;
                        }
                        var matchChan = System.Text.RegularExpressions.Regex.Match(entry.Path, @"(Channel\s*\d+)");
                        if (matchChan.Success)
                        {
                            chan = matchChan.Groups[1].Value;
                        }
                    }

                    list.Add(new
                    {
                        id = entry.Id,
                        browseName = entry.BrowseName,
                        displayName = entry.DisplayName,
                        path = entry.Path,
                        device = dev,
                        channel = chan,
                        label = entry.Inferred?.Label,
                        category = entry.Category,
                        dataType = entry.DataType,
                        unit = entry.Unit,
                        value = val,
                        numericValue = numVal,
                        quality,
                        isStale,
                        timestamp = ts ?? DateTime.UtcNow.ToString("o")
                    });
                }

                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions { WriteIndented = true });
                await SendResponseAsync(context, bytes, "application/json; charset=utf-8", HttpStatusCode.OK, ct).ConfigureAwait(false);
                return;
            }

            if (path == "/api/scan")
            {
                string? url = context.Request.QueryString["url"];
                int depth = 6;
                if (int.TryParse(context.Request.QueryString["depth"], out int d) && d is >= 1 and <= 30)
                {
                    depth = d;
                }

                if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) && context.Request.HasEntityBody)
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                        string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("url", out var urlProp))
                            {
                                url = urlProp.GetString();
                            }
                            if (doc.RootElement.TryGetProperty("depth", out var depthProp) && depthProp.TryGetInt32(out int depthVal) && depthVal is >= 1 and <= 30)
                            {
                                depth = depthVal;
                            }
                        }
                    }
                    catch { }
                }

                // If empty or identical to currently served catalog endpoint, return current catalog
                if (string.IsNullOrWhiteSpace(url) ||
                    string.Equals(url.Trim(), _catalog.Metadata.EndpointUrl, StringComparison.OrdinalIgnoreCase))
                {
                    string json = JsonCatalogExporter.ToJson(_catalog);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await SendResponseAsync(context, bytes, "application/json; charset=utf-8", HttpStatusCode.OK, ct).ConfigureAwait(false);
                    return;
                }

                url = url.Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUri) || parsedUri.Scheme != "opc.tcp")
                {
                    var errObj = new
                    {
                        error = true,
                        message = $"Invalid OPC UA endpoint URL: '{url}'. Expected format 'opc.tcp://<host>:<port>'."
                    };
                    byte[] errBytes = JsonSerializer.SerializeToUtf8Bytes(errObj);
                    await SendResponseAsync(context, errBytes, "application/json; charset=utf-8", HttpStatusCode.BadRequest, ct).ConfigureAwait(false);
                    return;
                }

                try
                {
                    using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    scanCts.CancelAfter(TimeSpan.FromSeconds(10));

                    await using ManagedSession session = await AtlasClientHelper.ConnectAsync(
                        url,
                        timeoutMs: 8000,
                        logger: _logger,
                        ct: scanCts.Token).ConfigureAwait(false);

                    var crawler = new AtlasCrawler(session, new CrawlerOptions
                    {
                        MaxDepth = depth,
                        RootNodeId = ObjectIds.ObjectsFolder
                    });

                    AtlasCatalog catalog = await crawler.CrawlAsync(url, ct: scanCts.Token).ConfigureAwait(false);
                    SemanticLabeler.LabelCatalog(catalog);

                    string json = JsonCatalogExporter.ToJson(catalog);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await SendResponseAsync(context, bytes, "application/json; charset=utf-8", HttpStatusCode.OK, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Live scan failed for endpoint {Url}", url);
                    var errObj = new
                    {
                        error = true,
                        message = $"Failed to scan endpoint '{url}': {ex.Message}"
                    };
                    byte[] errBytes = JsonSerializer.SerializeToUtf8Bytes(errObj);
                    await SendResponseAsync(context, errBytes, "application/json; charset=utf-8", HttpStatusCode.BadGateway, ct).ConfigureAwait(false);
                    return;
                }
            }

            if (path == "/health")
            {
                var diag = _multiplexer.GetDiagnostics();
                var staleRecords = _multiplexer.QualityGuard.EvaluateStaleness();
                var health = new
                {
                    status = diag.IsConnected || _catalog.Entries.Count > 0 ? "UP" : "DEGRADED",
                    serverUri = _catalog.Metadata.ServerUri ?? _catalog.Metadata.EndpointUrl,
                    collector = new
                    {
                        uptimeSeconds = diag.UptimeSeconds,
                        scrapeLatencyMs = diag.LastScrapeLatencyMs,
                        readErrorsTotal = diag.ReadErrorsTotal,
                        reconnectsTotal = diag.ReconnectsTotal,
                        monitoredItemsActive = diag.ActiveMonitoredItemsCount
                    },
                    machine = new
                    {
                        connected = diag.IsConnected,
                        catalogEntries = _catalog.Entries.Count,
                        staleSignalsCount = staleRecords.Count
                    },
                    connectedWsClients = _gateway.ConnectedClientsCount,
                    readOnly = _multiplexer.IsReadOnly,
                    timestamp = DateTime.UtcNow.ToString("o")
                };

                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(health, new JsonSerializerOptions { WriteIndented = true });
                await SendResponseAsync(context, bytes, "application/json; charset=utf-8", HttpStatusCode.OK, ct).ConfigureAwait(false);
                return;
            }

            if (path is "/mes" or "/reconciliation")
            {
                var ofContext = WorkOrder ?? new Mes.WorkOrderContext();
                var oee = OeeCalculator.Calculate(ofContext, _multiplexer.GetSnapshot());
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(oee, new JsonSerializerOptions { WriteIndented = true });

                await SendResponseAsync(context, bytes, "application/json; charset=utf-8", HttpStatusCode.OK, ct).ConfigureAwait(false);
                return;
            }

            if (path is "/" or "/ui" or "/index.html" or "/cockpit")
            {
                byte[] html = EmbeddedUi.GetHtmlBytes();
                await SendResponseAsync(context, html, "text/html; charset=utf-8", HttpStatusCode.OK, ct).ConfigureAwait(false);
                return;
            }

            // Default: 404
            byte[] notFound = Encoding.UTF8.GetBytes("Endpoint not found. Valid endpoints: /, /ui, /metrics, /catalog, /health, /api/live, /api/scan, /reconciliation, /ws");
            await SendResponseAsync(context, notFound, "text/plain; charset=utf-8", HttpStatusCode.NotFound, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error handling request to {Path}", path);
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch { }
        }
    }

    private static async Task SendResponseAsync(
        HttpListenerContext context,
        byte[] bytes,
        string contentType,
        HttpStatusCode statusCode,
        CancellationToken ct)
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS, HEAD";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Accept, Authorization";
        context.Response.ContentType = contentType;
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentLength64 = bytes.Length;

        if (!string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_listenerTask != null)
        {
            try { await _listenerTask.ConfigureAwait(false); } catch { }
        }

        await _gateway.DisposeAsync().ConfigureAwait(false);
        await _multiplexer.DisposeAsync().ConfigureAwait(false);
        _listener.Close();
    }
}
