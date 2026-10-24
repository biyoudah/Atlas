using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Atlas.Exporters;
using Atlas.Models;

namespace Atlas.Serving;

/// <summary>
/// Combined HTTP and WebSocket server for Atlas.
/// Serves:
/// - Prometheus metrics at /metrics
/// - Machine catalog at /catalog
/// - Health probe at /health
/// - Real-time WebSocket multiplexed streaming at /ws
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
            if (path == "/ws")
            {
                if (context.Request.IsWebSocketRequest)
                {
                    string clientId = Guid.NewGuid().ToString("N")[..8];
                    var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                    await _gateway.HandleClientAsync(wsContext.WebSocket, clientId, ct).ConfigureAwait(false);
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                byte[] err = Encoding.UTF8.GetBytes("WebSocket upgrade required.");
                await context.Response.OutputStream.WriteAsync(err, ct).ConfigureAwait(false);
                context.Response.Close();
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

                context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            if (path == "/catalog")
            {
                string json = JsonCatalogExporter.ToJson(_catalog);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                context.Response.Close();
                return;
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
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            if (path is "/mes" or "/reconciliation")
            {
                var ofContext = WorkOrder ?? new Mes.WorkOrderContext();
                var oee = OeeCalculator.Calculate(ofContext, _multiplexer.GetSnapshot());
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(oee, new JsonSerializerOptions { WriteIndented = true });

                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            if (path is "/" or "/ui" or "/index.html" or "/cockpit")
            {
                byte[] html = EmbeddedUi.GetHtmlBytes();
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentLength64 = html.Length;
                await context.Response.OutputStream.WriteAsync(html, ct).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            // Default: 404
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            byte[] notFound = Encoding.UTF8.GetBytes("Endpoint not found. Valid endpoints: /, /ui, /metrics, /catalog, /health, /reconciliation, /ws");
            await context.Response.OutputStream.WriteAsync(notFound, ct).ConfigureAwait(false);
            context.Response.Close();
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
