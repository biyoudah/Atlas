using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Models;
using Atlas.Serving;
using NUnit.Framework;

namespace Atlas.Tests;

[TestFixture]
[NonParallelizable]
public class AtlasServerHttpTests
{
    private AtlasServer? _server;
    private HttpClient? _client;
    private string _baseUrl = "";

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [SetUp]
    public void SetUp()
    {
        int port = GetAvailablePort();
        _baseUrl = $"http://localhost:{port}/";

        var catalog = new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://127.0.0.1:4840/TestServer",
                CrawledAtUtc = DateTime.UtcNow,
                Namespaces = ["http://opcfoundation.org/UA/", "urn:test:atlas"]
            },
            Entries =
            [
                new CatalogEntry
                {
                    Id = "ns=1;s=SpindleSpeed",
                    BrowseName = "SpindleSpeed",
                    DisplayName = "Spindle Speed RPM",
                    Path = "Objects/Machines/TestCNC/SpindleSpeed",
                    DataType = "Double",
                    Unit = "RPM",
                    Category = SignalCategory.Sample,
                    Inferred = new InferredSemantics(
                        Label: "spindle_speed_rpm",
                        Confidence: 0.95,
                        Evidence: ["Test signal heuristic"],
                        Provenance: SignalProvenance.HeuristicInference)
                }
            ]
        };

        var multiplexer = new SubscriptionMultiplexer(catalog);
        _server = new AtlasServer(catalog, multiplexer, prefix: _baseUrl, machineName: "TestCNC");
        _server.Start();

        _client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    [TearDown]
    public async Task TearDown()
    {
        _client?.Dispose();
        if (_server != null)
        {
            await _server.DisposeAsync();
        }
    }

    [Test]
    public async Task GetRoot_ReturnsOk_AndHtml()
    {
        var response = await _client!.GetAsync("/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        string content = await response.Content.ReadAsStringAsync();
        Assert.That(content, Does.Contain("Atlas").Or.Contain("ATLAS"));
    }

    [Test]
    public async Task HeadRequest_ReturnsOk_WithoutResponseBodyException()
    {
        var request = new HttpRequestMessage(HttpMethod.Head, "/");
        var response = await _client!.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.That(body.Length, Is.EqualTo(0));
    }

    [Test]
    public async Task OptionsRequest_ReturnsNoContent_WithCorsHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/live");
        var response = await _client!.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.True);
    }

    [Test]
    public async Task GetMetrics_ReturnsOk_AndPrometheusFormat()
    {
        var response = await _client!.GetAsync("/metrics");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string metrics = await response.Content.ReadAsStringAsync();
        Assert.That(metrics, Does.Contain("# HELP"));
    }

    [Test]
    public async Task GetCatalog_ReturnsOk_AndValidCatalogJson()
    {
        var response = await _client!.GetAsync("/catalog");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("SpindleSpeed"));
        Assert.That(json, Does.Contain("opc.tcp://127.0.0.1:4840/TestServer"));
    }

    [Test]
    public async Task GetHealth_ReturnsOk_AndHealthStatus()
    {
        var response = await _client!.GetAsync("/health");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.That(doc, Is.Not.Null);
        Assert.That(doc!.RootElement.GetProperty("status").GetString(), Is.EqualTo("UP"));
        Assert.That(doc.RootElement.GetProperty("machine").GetProperty("catalogEntries").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetApiLive_ReturnsOk_AndSnapshotArray()
    {
        var response = await _client!.GetAsync("/api/live");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var array = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(array.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(array.GetArrayLength(), Is.EqualTo(1));

        var first = array[0];
        Assert.That(first.GetProperty("id").GetString(), Is.EqualTo("ns=1;s=SpindleSpeed"));
        Assert.That(first.GetProperty("browseName").GetString(), Is.EqualTo("SpindleSpeed"));
        Assert.That(first.GetProperty("label").GetString(), Is.EqualTo("spindle_speed_rpm"));
    }

    [Test]
    public async Task GetApiScan_WithoutUrl_ReturnsCurrentCatalog()
    {
        var response = await _client!.GetAsync("/api/scan");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("SpindleSpeed"));
    }

    [Test]
    public async Task GetApiScan_WithInvalidUrl_ReturnsBadRequest()
    {
        var response = await _client!.GetAsync("/api/scan?url=http://invalid-scheme");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.That(doc!.RootElement.GetProperty("error").GetBoolean(), Is.True);
    }

    [Test]
    public async Task UnknownEndpoint_ReturnsNotFound_WithHelpfulMessage()
    {
        var response = await _client!.GetAsync("/unknown_random_endpoint");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        string content = await response.Content.ReadAsStringAsync();
        Assert.That(content, Does.Contain("Valid endpoints"));
    }
}
