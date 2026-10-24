using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Models;

namespace Atlas.Dashboard;

/// <summary>
/// Generates production-ready Grafana (v10+) JSON dashboards automatically
/// from an Atlas discovered and labeled catalog.
/// Creates gauges with EURange limits, KPI stat cards, time-series graphs,
/// and state timelines without manual metric configuration.
/// </summary>
public static class GrafanaDashboardGenerator
{
    /// <summary>
    /// Generates a Grafana dashboard JSON string for the specified catalog.
    /// </summary>
    public static string Generate(
        AtlasCatalog catalog,
        string title = "Atlas CNC Machine Overview",
        string datasource = "Prometheus",
        string machineName = "default_machine")
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var panels = new JsonArray();
        int panelId = 1;

        // Group entries by inferred label or BrowseName
        var byLabel = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Entries)
        {
            if (entry.Inferred?.Label != null)
            {
                byLabel.TryAdd(entry.Inferred.Label, entry);
            }
            if (entry.BrowseName != null)
            {
                byLabel.TryAdd(entry.BrowseName, entry);
            }
        }

        int currentY = 0;

        // --- ROW 0: MTConnect Conditions & Safety Interlocks (y=currentY, h=3) ---
        bool hasConditions = false;
        int condX = 0;

        if (byLabel.TryGetValue("emergency_stop", out _))
        {
            panels.Add(CreateStatPanel(
                id: panelId++,
                title: "Emergency Stop",
                expr: $"opcua_emergency_stop{{machine=\"{machineName}\"}}",
                x: condX, y: currentY, w: 6, h: 3,
                datasource: datasource,
                colorMode: "background",
                mappings:
                [
                    new { type = "value", options = new Dictionary<string, object> { ["1"] = new { text = "TRIPPED", color = "red" }, ["0"] = new { text = "OK", color = "green" } } }
                ]));
            condX += 6;
            hasConditions = true;
        }

        if (byLabel.TryGetValue("door_interlock", out _))
        {
            panels.Add(CreateStatPanel(
                id: panelId++,
                title: "Door Interlock",
                expr: $"opcua_door_interlock{{machine=\"{machineName}\"}}",
                x: condX, y: currentY, w: 6, h: 3,
                datasource: datasource,
                colorMode: "background",
                mappings:
                [
                    new { type = "value", options = new Dictionary<string, object> { ["1"] = new { text = "LOCKED", color = "green" }, ["0"] = new { text = "OPEN", color = "yellow" } } }
                ]));
            condX += 6;
            hasConditions = true;
        }

        if (hasConditions)
        {
            currentY += 3;
        }

        // --- ROW 1: MTConnect Events & Operational KPIs (y=currentY, h=4) ---
        // 1. Machine State / Operating Mode
        if (byLabel.TryGetValue("machine_state", out var stateEntry) || byLabel.TryGetValue("operating_mode", out stateEntry))
        {
            panels.Add(CreateStatPanel(
                id: panelId++,
                title: "Machine State / Operating Mode",
                expr: $"opcua_operating_mode{{machine=\"{machineName}\"}} or opcua_machine_state{{machine=\"{machineName}\"}}",
                x: 0, y: currentY, w: 6, h: 4,
                datasource: datasource,
                colorMode: "background",
                mappings:
                [
                    new { type = "value", options = new Dictionary<string, object> { ["1"] = new { text = "AUTOMATIC / RUNNING", color = "green" }, ["0"] = new { text = "MANUAL / STOPPED", color = "yellow" }, ["2"] = new { text = "SETUP", color = "blue" } } }
                ]));
        }

        // 2. Parts Produced
        if (byLabel.TryGetValue("parts_produced", out var partsEntry))
        {
            panels.Add(CreateStatPanel(
                id: panelId++,
                title: "Parts Produced",
                expr: $"opcua_parts_produced_total{{machine=\"{machineName}\"}}",
                x: 6, y: currentY, w: 6, h: 4,
                datasource: datasource,
                colorMode: "value",
                unit: "none",
                fixedColor: "blue"));
        }

        // 3. Cycle Time
        if (byLabel.TryGetValue("cycle_time", out var cycleEntry))
        {
            panels.Add(CreateStatPanel(
                id: panelId++,
                title: "Cycle Time",
                expr: $"opcua_cycle_time_seconds{{machine=\"{machineName}\"}}",
                x: 12, y: currentY, w: 6, h: 4,
                datasource: datasource,
                colorMode: "value",
                unit: "s",
                fixedColor: "purple"));
        }

        // 4. Spindle Load / Feed Override
        if (byLabel.TryGetValue("spindle_load", out var loadEntry))
        {
            panels.Add(CreateGaugePanel(
                id: panelId++,
                title: "Spindle Load",
                expr: $"opcua_spindle_load{{machine=\"{machineName}\"}}",
                x: 18, y: currentY, w: 6, h: 4,
                datasource: datasource,
                unit: "percent",
                min: 0,
                max: 100,
                thresholds:
                [
                    new { value = (double?)null, color = "green" },
                    new { value = (double?)75.0, color = "yellow" },
                    new { value = (double?)90.0, color = "red" }
                ]));
        }
        else if (byLabel.TryGetValue("feed_override", out var feedOvrEntry))
        {
            panels.Add(CreateGaugePanel(
                id: panelId++,
                title: "Feed Override",
                expr: $"opcua_feed_override{{machine=\"{machineName}\"}}",
                x: 18, y: currentY, w: 6, h: 4,
                datasource: datasource,
                unit: "percent",
                min: 0,
                max: 150,
                thresholds:
                [
                    new { value = (double?)null, color = "green" },
                    new { value = (double?)105.0, color = "yellow" },
                    new { value = (double?)125.0, color = "red" }
                ]));
        }

        currentY += 4;

        // --- ROW 2: MTConnect Process Samples & Gauges (y=currentY, h=6) ---
        // 5. Spindle Speed Gauge
        if (byLabel.TryGetValue("spindle_speed", out var speedEntry))
        {
            double maxSpeed = speedEntry.Range?.High ?? 12000.0;
            panels.Add(CreateGaugePanel(
                id: panelId++,
                title: "Spindle Speed",
                expr: $"opcua_spindle_speed_rpm{{machine=\"{machineName}\"}}",
                x: 0, y: currentY, w: 8, h: 6,
                datasource: datasource,
                unit: "rpm",
                min: speedEntry.Range?.Low ?? 0.0,
                max: maxSpeed,
                thresholds:
                [
                    new { value = (double?)null, color = "green" },
                    new { value = (double?)(maxSpeed * 0.85), color = "yellow" },
                    new { value = (double?)(maxSpeed * 0.95), color = "red" }
                ]));
        }

        // 6. Coolant Pressure Gauge
        if (byLabel.TryGetValue("coolant_pressure", out var pressureEntry))
        {
            double maxPressure = pressureEntry.Range?.High ?? 80.0;
            panels.Add(CreateGaugePanel(
                id: panelId++,
                title: "Coolant Pressure",
                expr: $"opcua_coolant_pressure_bar{{machine=\"{machineName}\"}}",
                x: 8, y: currentY, w: 8, h: 6,
                datasource: datasource,
                unit: "bar",
                min: pressureEntry.Range?.Low ?? 0.0,
                max: maxPressure,
                thresholds:
                [
                    new { value = (double?)null, color = "red" },
                    new { value = (double?)10.0, color = "green" },
                    new { value = (double?)(maxPressure * 0.9), color = "yellow" }
                ]));
        }

        // 7. Spindle Temperature Gauge
        if (byLabel.TryGetValue("spindle_temperature", out var tempEntry))
        {
            panels.Add(CreateGaugePanel(
                id: panelId++,
                title: "Spindle Temperature",
                expr: $"opcua_spindle_temperature_celsius{{machine=\"{machineName}\"}}",
                x: 16, y: currentY, w: 8, h: 6,
                datasource: datasource,
                unit: "celsius",
                min: 0,
                max: 120,
                thresholds:
                [
                    new { value = (double?)null, color = "green" },
                    new { value = (double?)60.0, color = "yellow" },
                    new { value = (double?)85.0, color = "red" }
                ]));
        }

        currentY += 6;

        // --- ROW 3: Time Series (y=currentY, h=7) ---
        // 8. Feed Rate Time Series
        if (byLabel.TryGetValue("feed_rate", out var feedEntry))
        {
            panels.Add(CreateTimeSeriesPanel(
                id: panelId++,
                title: "Feed Rate History",
                expr: $"opcua_feed_rate_mm_per_min{{machine=\"{machineName}\"}}",
                x: 0, y: currentY, w: 12, h: 7,
                datasource: datasource,
                unit: "mm/min"));
        }

        // 9. Temperature Trends
        if (byLabel.TryGetValue("spindle_temperature", out _))
        {
            panels.Add(CreateTimeSeriesPanel(
                id: panelId++,
                title: "Temperature Trends",
                expr: $"{{__name__=~\"opcua_.*temperature.*\", machine=\"{machineName}\"}}",
                x: 12, y: currentY, w: 12, h: 7,
                datasource: datasource,
                unit: "celsius"));
        }

        currentY += 7;

        // --- ROW 4: State Timeline (y=currentY, h=5) ---
        if (byLabel.TryGetValue("machine_state", out _))
        {
            panels.Add(CreateStateTimelinePanel(
                id: panelId++,
                title: "Execution State Timeline",
                expr: $"opcua_machine_state{{machine=\"{machineName}\"}}",
                x: 0, y: currentY, w: 24, h: 5,
                datasource: datasource));
        }

        var root = new JsonObject
        {
            ["id"] = null,
            ["uid"] = $"atlas-{SanitizeUid(catalog.Metadata.ServerUri ?? catalog.Metadata.EndpointUrl)}",
            ["title"] = title,
            ["tags"] = new JsonArray { "opcua", "atlas", "cnc", "industry-4.0" },
            ["style"] = "dark",
            ["timezone"] = "browser",
            ["editable"] = true,
            ["graphTooltip"] = 1,
            ["time"] = new JsonObject { ["from"] = "now-15m", ["to"] = "now" },
            ["refresh"] = "1s",
            ["schemaVersion"] = 38,
            ["version"] = 1,
            ["panels"] = panels
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject CreateStatPanel(
        int id, string title, string expr, int x, int y, int w, int h,
        string datasource, string colorMode = "value", string? unit = null,
        string? fixedColor = null, object[]? mappings = null)
    {
        var panel = new JsonObject
        {
            ["id"] = id,
            ["type"] = "stat",
            ["title"] = title,
            ["gridPos"] = new JsonObject { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h },
            ["datasource"] = new JsonObject { ["type"] = "prometheus", ["uid"] = datasource },
            ["targets"] = new JsonArray
            {
                new JsonObject { ["expr"] = expr, ["refId"] = "A", ["legendFormat"] = "{{state}}" }
            },
            ["options"] = new JsonObject
            {
                ["colorMode"] = colorMode,
                ["graphMode"] = "area",
                ["justifyMode"] = "auto",
                ["reduceOptions"] = new JsonObject
                {
                    ["values"] = false,
                    ["calcs"] = new JsonArray { "lastNotNull" },
                    ["fields"] = ""
                }
            },
            ["fieldConfig"] = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["unit"] = unit ?? "none",
                    ["color"] = fixedColor != null
                        ? new JsonObject { ["mode"] = "fixed", ["fixedColor"] = fixedColor }
                        : new JsonObject { ["mode"] = "thresholds" }
                }
            }
        };

        if (mappings != null)
        {
            panel["fieldConfig"]!["defaults"]!["mappings"] = JsonSerializer.SerializeToNode(mappings);
        }

        return panel;
    }

    private static JsonObject CreateGaugePanel(
        int id, string title, string expr, int x, int y, int w, int h,
        string datasource, string unit, double min, double max, object[] thresholds)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "gauge",
            ["title"] = title,
            ["gridPos"] = new JsonObject { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h },
            ["datasource"] = new JsonObject { ["type"] = "prometheus", ["uid"] = datasource },
            ["targets"] = new JsonArray
            {
                new JsonObject { ["expr"] = expr, ["refId"] = "A" }
            },
            ["fieldConfig"] = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["unit"] = unit,
                    ["min"] = min,
                    ["max"] = max,
                    ["thresholds"] = new JsonObject
                    {
                        ["mode"] = "absolute",
                        ["steps"] = JsonSerializer.SerializeToNode(thresholds)
                    }
                }
            },
            ["options"] = new JsonObject
            {
                ["showThresholdLabels"] = false,
                ["showThresholdMarkers"] = true
            }
        };
    }

    private static JsonObject CreateTimeSeriesPanel(
        int id, string title, string expr, int x, int y, int w, int h,
        string datasource, string unit)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "timeseries",
            ["title"] = title,
            ["gridPos"] = new JsonObject { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h },
            ["datasource"] = new JsonObject { ["type"] = "prometheus", ["uid"] = datasource },
            ["targets"] = new JsonArray
            {
                new JsonObject { ["expr"] = expr, ["refId"] = "A" }
            },
            ["fieldConfig"] = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["unit"] = unit,
                    ["custom"] = new JsonObject
                    {
                        ["drawStyle"] = "line",
                        ["lineInterpolation"] = "smooth",
                        ["lineWidth"] = 2,
                        ["fillOpacity"] = 15
                    }
                }
            }
        };
    }

    private static JsonObject CreateStateTimelinePanel(
        int id, string title, string expr, int x, int y, int w, int h,
        string datasource)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "state-timeline",
            ["title"] = title,
            ["gridPos"] = new JsonObject { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h },
            ["datasource"] = new JsonObject { ["type"] = "prometheus", ["uid"] = datasource },
            ["targets"] = new JsonArray
            {
                new JsonObject { ["expr"] = expr, ["refId"] = "A", ["legendFormat"] = "{{state}}" }
            },
            ["fieldConfig"] = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["color"] = new JsonObject { ["mode"] = "thresholds" }
                }
            }
        };
    }

    private static string SanitizeUid(string input)
    {
        var chars = input.Where(char.IsLetterOrDigit).Take(20).ToArray();
        return chars.Length > 0 ? new string(chars).ToLowerInvariant() : "catalog";
    }
}
