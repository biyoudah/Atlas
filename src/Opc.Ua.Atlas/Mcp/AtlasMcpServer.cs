using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Opc.Ua.Atlas.Labeling;
using Opc.Ua.Atlas.Mes;
using Opc.Ua.Atlas.Models;
using Opc.Ua.Atlas.Serving;

namespace Opc.Ua.Atlas.Mcp;

/// <summary>
/// Native Model Context Protocol (MCP) Server for Atlas.
/// Exposes OPC UA address spaces, live machine telemetry, OEE metrics,
/// and mapping pack authoring directly to AI assistants and LLMs over JSON-RPC 2.0 stdio.
/// </summary>
public sealed class AtlasMcpServer
{
    private readonly AtlasCatalog _catalog;
    private readonly SubscriptionMultiplexer? _multiplexer;
    private readonly ILogger? _logger;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Mes.OeeCalculator _oeeCalculator = new();

    public AtlasMcpServer(
        AtlasCatalog catalog,
        SubscriptionMultiplexer? multiplexer = null,
        TextReader? input = null,
        TextWriter? output = null,
        ILogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _multiplexer = multiplexer;
        _logger = logger;
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
    }

    /// <summary>
    /// Runs the MCP stdio message loop until end of input.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null)
            {
                break; // EOF
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("method", out var methodProp))
                {
                    continue; // notification without method or malformed
                }

                string method = methodProp.GetString() ?? string.Empty;
                JsonNode? idNode = null;
                if (root.TryGetProperty("id", out var idProp))
                {
                    idNode = idProp.ValueKind switch
                    {
                        JsonValueKind.Number => JsonValue.Create(idProp.GetInt64()),
                        JsonValueKind.String => JsonValue.Create(idProp.GetString()),
                        _ => null
                    };
                }

                var paramsObj = root.TryGetProperty("params", out var p) ? p : default;

                // Handle requests
                JsonObject? response = await HandleMethodAsync(method, paramsObj, idNode).ConfigureAwait(false);
                if (response != null && idNode != null)
                {
                    string json = response.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                    await _output.WriteLineAsync(json).ConfigureAwait(false);
                    await _output.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error processing MCP request line.");
            }
        }
    }

    private Task<JsonObject?> HandleMethodAsync(string method, JsonElement @params, JsonNode? id)
    {
        switch (method)
        {
            case "initialize":
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject()
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "atlas-opcua",
                            ["version"] = "1.0.0"
                        }
                    }
                });

            case "notifications/initialized":
            case "initialized":
                return Task.FromResult<JsonObject?>(null);

            case "ping":
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["result"] = new JsonObject()
                });

            case "tools/list":
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["tools"] = GetToolsManifest()
                    }
                });

            case "tools/call":
                return Task.FromResult<JsonObject?>(HandleToolCall(@params, id));

            default:
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32601,
                        ["message"] = $"Method '{method}' not found."
                    }
                });
        }
    }

    private JsonObject HandleToolCall(JsonElement @params, JsonNode? id)
    {
        string toolName = @params.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var args = @params.TryGetProperty("arguments", out var a) ? a : default;

        string resultText;

        try
        {
            resultText = toolName switch
            {
                "search_address_space" => SearchAddressSpace(args),
                "read_node_value" => ReadNodeValue(args),
                "get_machine_health" => GetMachineHealth(),
                "calculate_oee" => CalculateOee(args),
                "generate_mapping_pack" => GenerateMappingPack(args),
                _ => throw new InvalidOperationException($"Unknown tool: '{toolName}'")
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["isError"] = true,
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"Error executing tool {toolName}: {ex.Message}"
                        }
                    }
                }
            };
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = resultText
                    }
                }
            }
        };
    }

    private string SearchAddressSpace(JsonElement args)
    {
        string query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        string? categoryFilter = args.TryGetProperty("category", out var c) ? c.GetString() : null;
        int limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 25;

        var matches = _catalog.Entries
            .Where(e =>
            {
                if (!string.IsNullOrEmpty(categoryFilter) &&
                    !string.Equals(e.Category.ToString(), categoryFilter, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(query))
                {
                    return true;
                }

                return e.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       e.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       (e.BrowseName != null && e.BrowseName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                       (e.Inferred?.Label != null && e.Inferred.Label.Contains(query, StringComparison.OrdinalIgnoreCase));
            })
            .Take(Math.Max(1, limit))
            .Select(e => new
            {
                id = e.Id,
                path = e.Path,
                browseName = e.BrowseName,
                dataType = e.DataType,
                category = e.Category.ToString(),
                inferredLabel = e.Inferred?.Label,
                confidence = e.Inferred?.Confidence,
                unit = e.Unit,
                access = e.Access
            })
            .ToList();

        return JsonSerializer.Serialize(new { count = matches.Count, results = matches }, new JsonSerializerOptions { WriteIndented = true });
    }

    private string ReadNodeValue(JsonElement args)
    {
        string nodeId = args.TryGetProperty("nodeId", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return "{\"error\":\"Missing required parameter 'nodeId'.\"}";
        }

        if (_multiplexer == null)
        {
            var staticEntry = _catalog.Entries.FirstOrDefault(e =>
                string.Equals(e.Id, nodeId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.BrowseName, nodeId, StringComparison.OrdinalIgnoreCase));

            return JsonSerializer.Serialize(new
            {
                nodeId,
                status = staticEntry != null ? "CONFIGURED" : "NOT_FOUND",
                note = "Multiplexer offline. Live values available when connected."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var snapshot = _multiplexer.GetSnapshot();
        if (snapshot.TryGetValue(nodeId, out var cached))
        {
            var qRec = _multiplexer.QualityGuard.GetRecord(nodeId);
            return JsonSerializer.Serialize(new
            {
                nodeId,
                path = cached.Entry.Path,
                category = cached.Entry.Category.ToString(),
                inferredLabel = cached.Entry.Inferred?.Label,
                value = cached.Value,
                unit = cached.Entry.Unit,
                quality = qRec?.State.ToString() ?? "Good",
                isStale = qRec?.IsStale ?? false,
                lastUpdateUtc = qRec?.LastUpdateUtc ?? DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        return JsonSerializer.Serialize(new
        {
            nodeId,
            error = "Node not found in live multiplexer snapshot."
        });
    }

    private string GetMachineHealth()
    {
        var diag = _multiplexer?.GetDiagnostics();
        var staleList = _multiplexer?.QualityGuard.EvaluateStaleness() ?? [];

        var conditions = new List<object>();
        if (_multiplexer != null)
        {
            foreach (var (_, (entry, val)) in _multiplexer.GetSnapshot())
            {
                if (entry.Category == SignalCategory.Condition && val != null)
                {
                    conditions.Add(new
                    {
                        nodeId = entry.Id,
                        browseName = entry.BrowseName,
                        label = entry.Inferred?.Label,
                        value = val
                    });
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            gateway = new
            {
                uptimeSeconds = diag?.UptimeSeconds ?? 0,
                scrapeLatencyMs = diag?.LastScrapeLatencyMs ?? 0,
                readErrorsTotal = diag?.ReadErrorsTotal ?? 0,
                reconnectsTotal = diag?.ReconnectsTotal ?? 0,
                monitoredItemsCount = diag?.ActiveMonitoredItemsCount ?? 0,
                machineConnected = diag?.IsConnected ?? false
            },
            catalog = new
            {
                totalNodes = _catalog.Entries.Count,
                staleNodesCount = staleList.Count
            },
            activeConditions = conditions
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private string CalculateOee(JsonElement args)
    {
        string orderId = args.TryGetProperty("orderId", out var o) ? o.GetString() ?? "OF-001" : "OF-001";
        string partNumber = args.TryGetProperty("partNumber", out var p) ? p.GetString() ?? "PART-001" : "PART-001";
        int targetQuantity = args.TryGetProperty("targetQuantity", out var tq) ? tq.GetInt32() : 500;
        double cycleSec = args.TryGetProperty("nominalCycleTimeSeconds", out var cs) ? cs.GetDouble() : 45.0;
        double plannedMin = args.TryGetProperty("plannedMinutes", out var pm) ? pm.GetDouble() : 480.0;
        int scrapParts = args.TryGetProperty("scrapParts", out var sp) ? sp.GetInt32() : 0;

        var workOrder = new WorkOrderContext
        {
            OrderId = orderId,
            PartNumber = partNumber,
            TargetQuantity = targetQuantity,
            NominalCycleTimeSeconds = cycleSec,
            PlannedProductionMinutes = plannedMin,
            DeclaredScrapParts = scrapParts
        };

        var snapshot = _multiplexer?.GetSnapshot() ?? new Dictionary<string, (CatalogEntry Entry, object? Value)>();
        var oee = _oeeCalculator.Calculate(workOrder, snapshot);

        return JsonSerializer.Serialize(oee, new JsonSerializerOptions { WriteIndented = true });
    }

    private string GenerateMappingPack(JsonElement args)
    {
        string name = args.TryGetProperty("packName", out var n) ? n.GetString() ?? "custom-pack" : "custom-pack";
        string? vendor = args.TryGetProperty("vendor", out var v) ? v.GetString() : null;

        var rulesList = new List<UserMappingRule>();
        if (args.TryGetProperty("rules", out var rulesArr) && rulesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rulesArr.EnumerateArray())
            {
                string pattern = item.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" : "";
                string label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                string? unit = item.TryGetProperty("unit", out var u) ? u.GetString() : null;
                string? desc = item.TryGetProperty("description", out var d) ? d.GetString() : null;
                SignalCategory? cat = null;
                if (item.TryGetProperty("category", out var c) && Enum.TryParse<SignalCategory>(c.GetString(), true, out var parsedCat))
                {
                    cat = parsedCat;
                }

                if (!string.IsNullOrEmpty(pattern) && !string.IsNullOrEmpty(label))
                {
                    rulesList.Add(new UserMappingRule(pattern, label, unit, null, desc, cat));
                }
            }
        }

        var pack = new UserMappingPack
        {
            Name = name,
            Vendor = vendor,
            Rules = rulesList
        };

        return JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray GetToolsManifest()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["name"] = "search_address_space",
                ["description"] = "Search through discovered OPC UA nodes by keyword, browse path, or MTConnect category (Sample, Event, Condition).",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search keyword in NodeId, BrowseName, or inferred label." },
                        ["category"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "Sample", "Event", "Condition" }, ["description"] = "Filter by MTConnect signal category." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum results to return (default 25)." }
                    }
                }
            },
            new JsonObject
            {
                ["name"] = "read_node_value",
                ["description"] = "Read current live telemetry value, quality status (Good, Uncertain, Bad), and staleness for a specific OPC UA node.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["nodeId"] = new JsonObject { ["type"] = "string", ["description"] = "OPC UA NodeId or stable identifier." }
                    },
                    ["required"] = new JsonArray { "nodeId" }
                }
            },
            new JsonObject
            {
                ["name"] = "get_machine_health",
                ["description"] = "Inspect overall edge gateway diagnostics, OPC UA connection health, and active alarms/interlocks.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            },
            new JsonObject
            {
                ["name"] = "calculate_oee",
                ["description"] = "Reconcile live OT production counters with an MES work order to calculate real-time Availability, Performance, Quality, and TRS/OEE.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["orderId"] = new JsonObject { ["type"] = "string", ["description"] = "Work order / OF identifier (e.g. OF-2026-9481)." },
                        ["partNumber"] = new JsonObject { ["type"] = "string", ["description"] = "Part number / SKU." },
                        ["targetQuantity"] = new JsonObject { ["type"] = "integer", ["description"] = "Total target production parts count." },
                        ["nominalCycleTimeSeconds"] = new JsonObject { ["type"] = "number", ["description"] = "Nominal ideal cycle time in seconds." },
                        ["plannedMinutes"] = new JsonObject { ["type"] = "number", ["description"] = "Planned shift production duration in minutes." },
                        ["scrapParts"] = new JsonObject { ["type"] = "integer", ["description"] = "Declared scrap parts count." }
                    }
                }
            },
            new JsonObject
            {
                ["name"] = "generate_mapping_pack",
                ["description"] = "Generate a standardized Atlas JSON mapping pack to map vendor-specific PLC addresses to standard semantics.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["packName"] = new JsonObject { ["type"] = "string", ["description"] = "Name of the mapping pack." },
                        ["vendor"] = new JsonObject { ["type"] = "string", ["description"] = "Machine vendor name." },
                        ["rules"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["pattern"] = new JsonObject { ["type"] = "string" },
                                    ["label"] = new JsonObject { ["type"] = "string" },
                                    ["unit"] = new JsonObject { ["type"] = "string" },
                                    ["category"] = new JsonObject { ["type"] = "string" },
                                    ["description"] = new JsonObject { ["type"] = "string" }
                                },
                                ["required"] = new JsonArray { "pattern", "label" }
                            }
                        }
                    },
                    ["required"] = new JsonArray { "packName" }
                }
            }
        };
    }
}
