using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Atlas.Mcp;
using Atlas.Models;
using Atlas.Serving;

namespace Atlas.Tests;

[TestFixture]
public class AtlasMcpServerTests
{
    private static AtlasCatalog CreateTestCatalog()
    {
        return new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://localhost:4840",
                ServerUri = "urn:test:mcp",
                CrawledAtUtc = DateTime.UtcNow
            },
            Entries =
            [
                new CatalogEntry
                {
                    Id = "ns=2;s=SpindleSpeed",
                    BrowseName = "ActualSpeed",
                    Path = "Objects/CNC/ActualSpeed",
                    Category = SignalCategory.Sample,
                    Unit = "rpm",
                    Inferred = new InferredSemantics("spindle_speed", 0.95, [])
                },
                new CatalogEntry
                {
                    Id = "ns=2;s=EmergencyStop",
                    BrowseName = "EmergencyStop",
                    Path = "Objects/CNC/Safety/EmergencyStop",
                    Category = SignalCategory.Condition,
                    Inferred = new InferredSemantics("emergency_stop", 1.0, [])
                },
                new CatalogEntry
                {
                    Id = "ns=2;s=PartsCount",
                    BrowseName = "PartCounter",
                    Path = "Objects/CNC/Parts",
                    Category = SignalCategory.Event,
                    Inferred = new InferredSemantics("parts_produced", 0.9, [])
                }
            ]
        };
    }

    [Test]
    public async Task McpServer_HandlesInitializeAndToolsList()
    {
        var catalog = CreateTestCatalog();
        var multiplexer = new SubscriptionMultiplexer(catalog);

        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
            {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
            """);

        var output = new StringWriter();
        var server = new AtlasMcpServer(catalog, multiplexer, input, output);

        await server.RunAsync();

        string[] responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(responses.Length, Is.EqualTo(2));

        // 1. initialize response
        var initJson = JsonNode.Parse(responses[0]);
        Assert.That(initJson?["id"]?.GetValue<int>(), Is.EqualTo(1));
        Assert.That(initJson?["result"]?["serverInfo"]?["name"]?.GetValue<string>(), Is.EqualTo("atlas-opcua"));

        // 2. tools/list response
        var listJson = JsonNode.Parse(responses[1]);
        Assert.That(listJson?["id"]?.GetValue<int>(), Is.EqualTo(2));
        var tools = listJson?["result"]?["tools"]?.AsArray();
        Assert.That(tools, Is.Not.Null);
        Assert.That(tools!.Count, Is.GreaterThanOrEqualTo(5));
    }

    [Test]
    public async Task McpServer_ExecutesSearchAddressSpaceTool()
    {
        var catalog = CreateTestCatalog();
        var multiplexer = new SubscriptionMultiplexer(catalog);

        var input = new StringReader("""
            {"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"search_address_space","arguments":{"query":"Spindle","category":"Sample"}}}
            """);

        var output = new StringWriter();
        var server = new AtlasMcpServer(catalog, multiplexer, input, output);

        await server.RunAsync();

        string line = output.ToString().Trim();
        var resp = JsonNode.Parse(line);
        Assert.That(resp?["id"]?.GetValue<int>(), Is.EqualTo(10));

        string text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Assert.That(text, Does.Contain("SpindleSpeed"));
        Assert.That(text, Does.Contain("Sample"));
    }

    [Test]
    public async Task McpServer_ExecutesCalculateOeeTool()
    {
        var catalog = CreateTestCatalog();
        var multiplexer = new SubscriptionMultiplexer(catalog);
        multiplexer.UpdateValue("ns=2;s=PartsCount", 200);

        var input = new StringReader("""
            {"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"calculate_oee","arguments":{"orderId":"OF-TEST-100","targetQuantity":500,"nominalCycleTimeSeconds":30,"plannedMinutes":120,"scrapParts":2}}}
            """);

        var output = new StringWriter();
        var server = new AtlasMcpServer(catalog, multiplexer, input, output);

        await server.RunAsync();

        string line = output.ToString().Trim();
        var resp = JsonNode.Parse(line);
        Assert.That(resp?["id"]?.GetValue<int>(), Is.EqualTo(20));

        string text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Assert.That(text, Does.Contain("OF-TEST-100"));
        Assert.That(text, Does.Contain("oeeOverall"));
        Assert.That(text, Does.Contain("totalPartsProduced"));
    }

    [Test]
    public async Task McpServer_ExecutesGenerateMappingPackTool()
    {
        var catalog = CreateTestCatalog();
        var multiplexer = new SubscriptionMultiplexer(catalog);

        var input = new StringReader("""
            {"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"generate_mapping_pack","arguments":{"packName":"fanuc-custom","vendor":"Fanuc","rules":[{"pattern":"PMC_D100","label":"spindle_speed","unit":"rpm","category":"Sample"}]}}}
            """);

        var output = new StringWriter();
        var server = new AtlasMcpServer(catalog, multiplexer, input, output);

        await server.RunAsync();

        string line = output.ToString().Trim();
        var resp = JsonNode.Parse(line);
        Assert.That(resp?["id"]?.GetValue<int>(), Is.EqualTo(30));

        string text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Assert.That(text, Does.Contain("fanuc-custom"));
        Assert.That(text, Does.Contain("PMC_D100"));
        Assert.That(text, Does.Contain("spindle_speed"));
    }

    [Test]
    public async Task McpServer_RejectsUnknownTools_OrWriteAttempts()
    {
        var catalog = CreateTestCatalog();
        var multiplexer = new SubscriptionMultiplexer(catalog);

        var input = new StringReader("""
            {"jsonrpc":"2.0","id":40,"method":"tools/call","params":{"name":"write_node_value","arguments":{"nodeId":"ns=2;s=SpindleSpeed","value":12000}}}
            """);

        var output = new StringWriter();
        var server = new AtlasMcpServer(catalog, multiplexer, input, output);

        await server.RunAsync();

        string line = output.ToString().Trim();
        var resp = JsonNode.Parse(line);
        Assert.That(resp?["id"]?.GetValue<int>(), Is.EqualTo(40));
        Assert.That(resp?["result"]?["isError"]?.GetValue<bool>(), Is.True);
        string err = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Assert.That(err, Does.Contain("Unknown tool"));
    }

    [Test]
    public async Task McpServer_ExecutesGetMachineHealthTool()
    {
        var catalog = CreateTestCatalog();
        var multiplexer = new SubscriptionMultiplexer(catalog);
        multiplexer.UpdateValue("ns=2;s=EmergencyStop", false);

        var input = new StringReader("""
            {"jsonrpc":"2.0","id":50,"method":"tools/call","params":{"name":"get_machine_health","arguments":{}}}
            """);

        var output = new StringWriter();
        var server = new AtlasMcpServer(catalog, multiplexer, input, output);

        await server.RunAsync();

        string line = output.ToString().Trim();
        var resp = JsonNode.Parse(line);
        Assert.That(resp?["id"]?.GetValue<int>(), Is.EqualTo(50));
        string text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Assert.That(text, Does.Contain("gateway"));
        Assert.That(text, Does.Contain("catalog"));
        Assert.That(text, Does.Contain("activeConditions"));
    }
}
