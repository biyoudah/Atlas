using System.Net.WebSockets;
using System.Text.Json;
using NUnit.Framework;
using Opc.Ua.Atlas.Dashboard;
using Opc.Ua.Atlas.Labeling;
using Opc.Ua.Atlas.Models;
using Opc.Ua.Atlas.Serving;

namespace Opc.Ua.Atlas.Tests;

[TestFixture]
public class PrometheusAndDashboardTests
{
    private AtlasCatalog CreateSampleCatalog()
    {
        var entries = new List<CatalogEntry>
        {
            new()
            {
                Id = "http://vendor.example/plc/;s=DB10.SpindleSpeed",
                Path = "Machine/Spindle/Speed",
                BrowseName = "SpindleSpeed",
                DataType = "Double",
                Unit = "rpm",
                Range = new EURangeInfo(0, 15000),
                Inferred = new InferredSemantics("spindle_speed", 0.95, ["EURange matches", "unit is rpm"])
            },
            new()
            {
                Id = "http://vendor.example/plc/;s=DB100.PartCounter",
                Path = "Machine/Production/Parts",
                BrowseName = "PartsProduced",
                DataType = "Int32",
                Inferred = new InferredSemantics("parts_produced", 0.90, ["monotonic counter"])
            },
            new()
            {
                Id = "http://vendor.example/plc/;s=DB20.State",
                Path = "Machine/Controller/State",
                BrowseName = "ExecutionState",
                DataType = "String",
                Inferred = new InferredSemantics("machine_state", 0.85, ["companion spec match"])
            },
            new()
            {
                Id = "http://vendor.example/plc/;s=DB30.Coolant",
                Path = "Machine/Hydraulics/Pressure",
                BrowseName = "CoolantPressure",
                DataType = "Double",
                Unit = "bar",
                Range = new EURangeInfo(0, 100),
                Inferred = new InferredSemantics("coolant_pressure", 0.88, ["unit is bar"])
            },
            new()
            {
                Id = "http://vendor.example/plc/;s=DB40.SpindleTemp",
                Path = "Machine/Spindle/Temperature",
                BrowseName = "SpindleTemperature",
                DataType = "Double",
                Unit = "°C",
                Inferred = new InferredSemantics("spindle_temperature", 0.90, ["unit is °C"])
            },
            new()
            {
                Id = "http://vendor.example/plc/;s=DB50.Load",
                Path = "Machine/Spindle/Load",
                BrowseName = "SpindleLoad",
                DataType = "Double",
                Unit = "%",
                Inferred = new InferredSemantics("spindle_load", 0.80, ["spec match"])
            },
            new()
            {
                Id = "http://vendor.example/plc/;s=DB60.Feed",
                Path = "Machine/Axes/FeedRate",
                BrowseName = "FeedRate",
                DataType = "Double",
                Unit = "mm/min",
                Inferred = new InferredSemantics("feed_rate", 0.85, ["unit is mm/min"])
            },
            new()
            {
                Id = "http://vendor.example/plc/;s=DB70.CycleSec",
                Path = "Machine/Production/CycleTime",
                BrowseName = "CycleTime",
                DataType = "Double",
                Unit = "s",
                Inferred = new InferredSemantics("cycle_time", 0.82, ["unit is s"])
            }
        };

        return new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://cnc.local:4840",
                ServerUri = "urn:cnc:machine-01"
            },
            Entries = entries
        };
    }

    [Test]
    public void PrometheusExporter_SerializesMetrics_WithUnitsAndStateSets()
    {
        var catalog = CreateSampleCatalog();
        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>();

        foreach (var entry in catalog.Entries)
        {
            object? val = entry.Inferred?.Label switch
            {
                "spindle_speed" => 10250.5,
                "parts_produced" => 42L,
                "machine_state" => "RUNNING",
                "coolant_pressure" => 28.4,
                "spindle_temperature" => 48.2,
                "spindle_load" => 64.0,
                "feed_rate" => 1200.0,
                "cycle_time" => 45.8,
                _ => null
            };
            snapshot[entry.Id] = (entry, val);
        }

        string metrics = PrometheusExporter.SerializeMetrics(snapshot, "milling_cell_01");

        // Verify gauge with rpm unit
        Assert.That(metrics, Does.Contain("# TYPE opcua_spindle_speed_rpm gauge"));
        Assert.That(metrics, Does.Contain("opcua_spindle_speed_rpm{machine=\"milling_cell_01\",node_id=\"http://vendor.example/plc/;s=DB10.SpindleSpeed\"} 10250.5"));

        // Verify counter with _total
        Assert.That(metrics, Does.Contain("# TYPE opcua_parts_produced_total counter"));
        Assert.That(metrics, Does.Contain("opcua_parts_produced_total{machine=\"milling_cell_01\",node_id=\"http://vendor.example/plc/;s=DB100.PartCounter\"} 42"));

        // Verify state-set metric (discrete enum/state) avoiding series churn
        Assert.That(metrics, Does.Contain("opcua_machine_state{machine=\"milling_cell_01\",node_id=\"http://vendor.example/plc/;s=DB20.State\",state=\"RUNNING\"} 1"));

        // Verify bar pressure and celsius temperatures
        Assert.That(metrics, Does.Contain("opcua_coolant_pressure_bar"));
        Assert.That(metrics, Does.Contain("opcua_spindle_temperature_celsius"));
    }

    [Test]
    public void PrometheusExporter_LowCardinality_OmitsNodeIdLabel()
    {
        var catalog = CreateSampleCatalog();
        var snapshot = new Dictionary<string, (CatalogEntry Entry, object? Value)>();
        foreach (var entry in catalog.Entries)
        {
            if (entry.Inferred?.Label == "spindle_speed")
            {
                snapshot[entry.Id] = (entry, 8500.0);
            }
        }

        string lowCardMetrics = PrometheusExporter.SerializeMetrics(snapshot, "cnc_01", includeNodeIdLabel: false);
        Assert.That(lowCardMetrics, Does.Contain("opcua_spindle_speed_rpm{machine=\"cnc_01\"} 8500"));
        Assert.That(lowCardMetrics, Does.Not.Contain("node_id="));
    }

    [Test]
    public void GrafanaDashboardGenerator_GeneratesValidV10Dashboard()
    {
        var catalog = CreateSampleCatalog();
        string dashboardJson = GrafanaDashboardGenerator.Generate(
            catalog,
            title: "Haas VF-2 CNC Cell Telemetry",
            datasource: "Prometheus-Prod",
            machineName: "milling_cell_01");

        Assert.That(dashboardJson, Is.Not.Null.And.Not.Empty);

        using var doc = JsonDocument.Parse(dashboardJson);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("title").GetString(), Is.EqualTo("Haas VF-2 CNC Cell Telemetry"));
        Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(38));

        var panels = root.GetProperty("panels");
        Assert.That(panels.GetArrayLength(), Is.GreaterThanOrEqualTo(8));

        // Check panel types
        var panelTitles = new List<string>();
        foreach (var p in panels.EnumerateArray())
        {
            panelTitles.Add(p.GetProperty("title").GetString()!);
        }

        Assert.That(panelTitles, Does.Contain("Machine State"));
        Assert.That(panelTitles, Does.Contain("Parts Produced"));
        Assert.That(panelTitles, Does.Contain("Spindle Speed"));
        Assert.That(panelTitles, Does.Contain("Coolant Pressure"));
        Assert.That(panelTitles, Does.Contain("Spindle Temperature"));
        Assert.That(panelTitles, Does.Contain("Feed Rate History"));
        Assert.That(panelTitles, Does.Contain("Execution State Timeline"));
    }

    [Test]
    public async Task SubscriptionMultiplexer_EnforcesReadOnly_AndSafetyBlocks()
    {
        var catalog = CreateSampleCatalog();
        await using var multiplexer = new SubscriptionMultiplexer(catalog, session: null)
        {
            IsReadOnly = true
        };

        // Write while read-only must be rejected
        var (roSuccess, roMsg) = await multiplexer.WriteValueAsync("client1", "http://vendor.example/plc/;s=DB10.SpindleSpeed", 5000.0);
        Assert.That(roSuccess, Is.False);
        Assert.That(roMsg, Does.Contain("read-only"));

        // Disable read-only mode
        multiplexer.IsReadOnly = false;

        // Emergency Stop write MUST be permanently blocked
        var (esSuccess, esMsg) = await multiplexer.WriteValueAsync("client1", "ns=2;s=EmergencyStop", false);
        Assert.That(esSuccess, Is.False);
        Assert.That(esMsg, Does.Contain("Hard safety violation"));

        // Non-allowlisted node must be rejected
        var (alSuccess, alMsg) = await multiplexer.WriteValueAsync("client1", "http://vendor.example/plc/;s=DB10.SpindleSpeed", 5000.0);
        Assert.That(alSuccess, Is.False);
        Assert.That(alMsg, Does.Contain("not in the authorized write allowlist"));
    }

    [Test]
    public async Task SubscriptionMultiplexer_Allowlist_EnablesAuthorizedWrites()
    {
        var catalog = CreateSampleCatalog();
        string targetId = "http://vendor.example/plc/;s=DB10.SpindleSpeed";
        await using var multiplexer = new SubscriptionMultiplexer(
            catalog,
            session: null,
            writeAllowlist: [targetId])
        {
            IsReadOnly = false
        };

        var (success, msg) = await multiplexer.WriteValueAsync("client1", targetId, 4500.0);
        Assert.That(success, Is.True);
        Assert.That(msg, Does.Contain("Write successful"));

        var snapshot = multiplexer.GetSnapshot();
        Assert.That(snapshot[targetId].Value, Is.EqualTo(4500.0));
    }

    [Test]
    public async Task WebSocketGateway_ProcessesClientMessages_SubscribeAndCatalog()
    {
        var catalog = CreateSampleCatalog();
        await using var multiplexer = new SubscriptionMultiplexer(catalog, session: null);
        await using var gateway = new WebSocketGateway(multiplexer, catalog);

        multiplexer.UpdateValue("http://vendor.example/plc/;s=DB10.SpindleSpeed", 9800.0);

        // Verify catalog retrieval message
        string catalogMsg = """{"action":"catalog"}""";
        var fakeSession = new ClientSession("test_client", new FakeWebSocket());

        await gateway.ProcessClientMessageAsync(fakeSession, catalogMsg);

        // Verify subscribe message
        string subMsg = """{"action":"subscribe","nodes":["http://vendor.example/plc/;s=DB10.SpindleSpeed"]}""";
        await gateway.ProcessClientMessageAsync(fakeSession, subMsg);

        Assert.That(fakeSession.SubscribedNodes, Does.Contain("http://vendor.example/plc/;s=DB10.SpindleSpeed"));
    }

    [Test]
    public void MappingPackLoader_ResolvesSeedPacks()
    {
        string baseDir = TestContext.CurrentContext.TestDirectory;
        // Search upward for tools/Opc.Ua.Atlas/Packs
        string? cur = baseDir;
        string? packsDir = null;
        while (cur != null)
        {
            string candidate = Directory.Exists(Path.Combine(cur, "src", "Opc.Ua.Atlas", "Packs")) ? Path.Combine(cur, "src", "Opc.Ua.Atlas", "Packs") : Path.Combine(cur, "tools", "Opc.Ua.Atlas", "Packs");
            if (Directory.Exists(candidate))
            {
                packsDir = candidate;
                break;
            }
            cur = Directory.GetParent(cur)?.FullName;
        }

        Assert.That(packsDir, Is.Not.Null, "Packs directory should exist in repo");

        // Verify Siemens pack
        string siemensPath = Path.Combine(packsDir!, "siemens-s7-1500.json");
        Assert.That(File.Exists(siemensPath), Is.True);
        var siemensPack = MappingPackLoader.LoadFromFile(siemensPath);
        Assert.That(siemensPack.Name, Does.Contain("siemens"));
        Assert.That(siemensPack.Rules, Has.Count.GreaterThanOrEqualTo(5));

        // Verify Fanuc pack
        string fanucPath = Path.Combine(packsDir!, "fanuc-31i.json");
        Assert.That(File.Exists(fanucPath), Is.True);
        var fanucPack = MappingPackLoader.LoadFromFile(fanucPath);
        Assert.That(fanucPack.Name, Does.Contain("fanuc"));
        Assert.That(fanucPack.Rules, Has.Count.GreaterThanOrEqualTo(5));

        // Verify Mitsubishi pack
        string mitsuPath = Path.Combine(packsDir!, "mitsubishi-m800.json");
        Assert.That(File.Exists(mitsuPath), Is.True);
        var mitsuPack = MappingPackLoader.LoadFromFile(mitsuPath);
        Assert.That(mitsuPack.Name, Does.Contain("mitsubishi"));
        Assert.That(mitsuPack.Rules, Has.Count.GreaterThanOrEqualTo(4));
    }

    private sealed class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

