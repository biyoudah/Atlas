using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Opc.Ua.Atlas.Models;

namespace Opc.Ua.Atlas.Serving;

/// <summary>
/// High-rate WebSocket streaming gateway for live visualization dashboards.
/// Protocol messages are strictly message-based JSON (never NodeIds in query strings).
/// Multiplexes client subscriptions over a single <see cref="SubscriptionMultiplexer"/>.
/// </summary>
public sealed class WebSocketGateway : IAsyncDisposable
{
    private readonly SubscriptionMultiplexer _multiplexer;
    private readonly AtlasCatalog _catalog;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new(StringComparer.Ordinal);

    public int ConnectedClientsCount => _clients.Count;

    public WebSocketGateway(
        SubscriptionMultiplexer multiplexer,
        AtlasCatalog catalog,
        ILogger? logger = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger;

        _multiplexer.OnValueChanged += HandleValueChanged;
    }

    /// <summary>
    /// Handles an incoming upgraded WebSocket connection.
    /// Runs until the connection is closed or aborted.
    /// </summary>
    public async Task HandleClientAsync(WebSocket webSocket, string clientId, CancellationToken ct = default)
    {
        var session = new ClientSession(clientId, webSocket);
        _clients[clientId] = session;
        _logger?.LogInformation("WebSocket client {ClientId} connected.", clientId);

        var buffer = new byte[8192];
        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                string messageText = Encoding.UTF8.GetString(ms.ToArray());
                await ProcessClientMessageAsync(session, messageText, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger?.LogDebug(ex, "WebSocket client {ClientId} disconnected unexpectedly.", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _multiplexer.UnsubscribeClient(clientId);
            _logger?.LogInformation("WebSocket client {ClientId} disconnected.", clientId);
        }
    }

    /// <summary>
    /// Parses and handles an incoming JSON message from a client.
    /// </summary>
    public async Task ProcessClientMessageAsync(ClientSession session, string messageText, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageText);
            var root = doc.RootElement;
            if (!root.TryGetProperty("action", out var actionProp))
            {
                await SendJsonAsync(session, new { @event = "error", message = "Missing 'action' property" }, ct).ConfigureAwait(false);
                return;
            }

            string action = actionProp.GetString()?.ToLowerInvariant() ?? string.Empty;

            switch (action)
            {
                case "subscribe":
                {
                    var nodesToSubscribe = new List<string>();
                    if (root.TryGetProperty("nodes", out var nodesProp) && nodesProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in nodesProp.EnumerateArray())
                        {
                            string? node = el.GetString();
                            if (!string.IsNullOrEmpty(node))
                            {
                                nodesToSubscribe.Add(node);
                                session.SubscribedNodes.Add(node);
                            }
                        }
                    }

                    _multiplexer.SubscribeClient(session.ClientId, nodesToSubscribe);

                    // Send immediate acknowledgment
                    await SendJsonAsync(session, new
                    {
                        @event = "subscribed",
                        count = nodesToSubscribe.Count,
                        nodes = nodesToSubscribe
                    }, ct).ConfigureAwait(false);

                    // Immediately stream current cached values for newly subscribed nodes
                    var snapshot = _multiplexer.GetSnapshot();
                    foreach (string node in nodesToSubscribe)
                    {
                        if (snapshot.TryGetValue(node, out var snap) && snap.Value != null)
                        {
                            await SendValueUpdateAsync(session, snap.Entry, snap.Value, DateTime.UtcNow, ct).ConfigureAwait(false);
                        }
                    }
                    break;
                }

                case "unsubscribe":
                {
                    var nodesToUnsub = new List<string>();
                    if (root.TryGetProperty("nodes", out var nodesProp) && nodesProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in nodesProp.EnumerateArray())
                        {
                            string? node = el.GetString();
                            if (!string.IsNullOrEmpty(node))
                            {
                                nodesToUnsub.Add(node);
                                session.SubscribedNodes.Remove(node);
                            }
                        }
                        _multiplexer.UnsubscribeClient(session.ClientId, nodesToUnsub);
                    }
                    else
                    {
                        // Unsubscribe all
                        session.SubscribedNodes.Clear();
                        _multiplexer.UnsubscribeClient(session.ClientId);
                    }

                    await SendJsonAsync(session, new { @event = "unsubscribed", count = nodesToUnsub.Count }, ct).ConfigureAwait(false);
                    break;
                }

                case "read":
                {
                    if (root.TryGetProperty("node", out var nodeProp) && nodeProp.GetString() is string targetNode)
                    {
                        var snapshot = _multiplexer.GetSnapshot();
                        if (snapshot.TryGetValue(targetNode, out var snap))
                        {
                            await SendValueUpdateAsync(session, snap.Entry, snap.Value, DateTime.UtcNow, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await SendJsonAsync(session, new { @event = "error", message = $"Node '{targetNode}' not found in catalog." }, ct).ConfigureAwait(false);
                        }
                    }
                    break;
                }

                case "write":
                {
                    string? targetNode = root.TryGetProperty("node", out var nProp) ? nProp.GetString() : (root.TryGetProperty("nodeId", out var nidProp) ? nidProp.GetString() : null);
                    if (!string.IsNullOrEmpty(targetNode) && root.TryGetProperty("value", out var valProp))
                    {
                        object? val = valProp.ValueKind switch
                        {
                            JsonValueKind.Number when valProp.TryGetInt64(out long l) => l,
                            JsonValueKind.Number when valProp.TryGetDouble(out double d) => d,
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String => valProp.GetString(),
                            _ => valProp.ToString()
                        };

                        if (val != null)
                        {
                            var (success, msg) = await _multiplexer.WriteValueAsync(session.ClientId, targetNode, val, ct).ConfigureAwait(false);
                            await SendJsonAsync(session, new
                            {
                                @event = "write_result",
                                node = targetNode,
                                success,
                                message = msg
                            }, ct).ConfigureAwait(false);
                        }
                    }
                    break;
                }

                case "catalog":
                {
                    var entries = _catalog.Entries.Select(e => new
                    {
                        id = e.Id,
                        path = e.Path,
                        browseName = e.BrowseName,
                        dataType = e.DataType,
                        unit = e.Unit,
                        range = e.Range,
                        inferred = e.Inferred != null ? new
                        {
                            label = e.Inferred.Label,
                            confidence = e.Inferred.Confidence,
                            evidence = e.Inferred.Evidence
                        } : null
                    });

                    await SendJsonAsync(session, new
                    {
                        @event = "catalog",
                        serverUri = _catalog.Metadata.ServerUri ?? _catalog.Metadata.EndpointUrl,
                        count = _catalog.Entries.Count,
                        entries
                    }, ct).ConfigureAwait(false);
                    break;
                }

                default:
                    await SendJsonAsync(session, new { @event = "error", message = $"Unknown action '{action}'" }, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (JsonException ex)
        {
            await SendJsonAsync(session, new { @event = "error", message = $"Malformed JSON: {ex.Message}" }, ct).ConfigureAwait(false);
        }
    }

    private void HandleValueChanged(string nodeId, CatalogEntry entry, object? value, DateTime timestamp)
    {
        foreach (var session in _clients.Values)
        {
            if (session.SubscribedNodes.Contains(nodeId) ||
                (entry.BrowseName != null && session.SubscribedNodes.Contains(entry.BrowseName)) ||
                session.SubscribedNodes.Contains("*"))
            {
                _ = SendValueUpdateAsync(session, entry, value, timestamp, CancellationToken.None);
            }
        }
    }

    private static async Task SendValueUpdateAsync(
        ClientSession session,
        CatalogEntry entry,
        object? value,
        DateTime timestamp,
        CancellationToken ct)
    {
        var payload = new
        {
            @event = "value",
            id = entry.Id,
            browseName = entry.BrowseName,
            label = entry.Inferred?.Label,
            confidence = entry.Inferred?.Confidence,
            value,
            unit = entry.Unit,
            timestamp = timestamp.ToString("o")
        };

        await SendJsonAsync(session, payload, ct).ConfigureAwait(false);
    }

    private static async Task SendJsonAsync(ClientSession session, object data, CancellationToken ct)
    {
        if (session.Socket.State != WebSocketState.Open)
        {
            return;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await session.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.Socket.State == WebSocketState.Open)
            {
                await session.Socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    ct).ConfigureAwait(false);
            }
        }
        finally
        {
            session.SendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _multiplexer.OnValueChanged -= HandleValueChanged;

        foreach (var session in _clients.Values)
        {
            try
            {
                if (session.Socket.State == WebSocketState.Open)
                {
                    await session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { }
        }
        _clients.Clear();
    }
}

public sealed class ClientSession
{
    public string ClientId { get; }
    public WebSocket Socket { get; }
    public HashSet<string> SubscribedNodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    public ClientSession(string clientId, WebSocket socket)
    {
        ClientId = clientId;
        Socket = socket;
    }
}
