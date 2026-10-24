using NUnit.Framework;
using Atlas.Client;
using Atlas.Crawler;
using Atlas.Exporters;
using Atlas.Models;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Opc.Ua.Export;
using Atlas.FakeCnc;

namespace Atlas.Tests;

[TestFixture]
[NonParallelizable]
public class AtlasServerIntegrationTests
{
    private IApplicationInstance? m_app;
    private FakeCncServer? m_server;
    private int m_port;

    [OneTimeSetUp]
    public async Task StartServerAsync()
    {
        m_port = 61245;
        ITelemetryContext telemetry = DefaultTelemetry.Create(_ => { });
        var model = new CncSimulationModel();
        m_server = new FakeCncServer(telemetry, model, tickIntervalMs: 50);

        ApplicationConfiguration config = await FakeCncServer.CreateConfigurationAsync(
            port: m_port,
            telemetry: telemetry).ConfigureAwait(false);

        m_app = new ApplicationInstance(config, telemetry);
        await m_app.CheckApplicationInstanceCertificatesAsync(true).ConfigureAwait(false);
        await m_app.StartAsync(m_server).ConfigureAwait(false);
    }

    [OneTimeTearDown]
    public async Task StopServerAsync()
    {
        if (m_app != null)
        {
            await m_app.StopAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task CrawlLiveServer_DiscoversNodes_AndExportsAllFormats()
    {
        string endpointUrl = $"{Utils.UriSchemeOpcTcp}://localhost:{m_port}/FakeCncServer";

        await using ManagedSession session = await AtlasClientHelper.ConnectAsync(
            endpointUrl,
            timeoutMs: 15000).ConfigureAwait(false);

        Assert.That(session.Connected, Is.True, "Session should be connected to the loopback FakeCncServer.");

        var crawler = new AtlasCrawler(session, new CrawlerOptions
        {
            MaxDepth = 10,
            BatchSize = 100,
            IncludeServerDiagnostics = false,
            IdentityType = "Anonymous"
        });

        AtlasCatalog catalog = await crawler.CrawlAsync(endpointUrl: endpointUrl).ConfigureAwait(false);

        Assert.That(catalog, Is.Not.Null);
        Assert.That(catalog.Metadata, Is.Not.Null);
        Assert.That(catalog.Metadata.EndpointUrl, Is.EqualTo(endpointUrl));
        Assert.That(catalog.Metadata.Namespaces.Count, Is.GreaterThan(0));
        Assert.That(catalog.Entries.Count, Is.GreaterThan(10), "Should discover nodes in the address space.");

        // Check for specific entries
        CatalogEntry? variable = catalog.Entries.FirstOrDefault(e => e.NodeClass == "Variable");
        Assert.That(variable, Is.Not.Null, "At least one variable must be cataloged.");
        Assert.That(variable!.Id, Is.Not.Null.And.Not.Empty);
        Assert.That(variable.Path, Does.StartWith("Objects/"));
        Assert.That(variable.Access, Is.Not.Null);

        // Verify JSON export
        string json = JsonCatalogExporter.ToJson(catalog);
        Assert.That(json, Does.Contain(endpointUrl));
        Assert.That(json, Does.Contain(variable.BrowseName));

        // Verify CSV export
        using var sw = new StringWriter();
        CsvCatalogExporter.Export(catalog, sw);
        string csv = sw.ToString();
        Assert.That(csv, Does.Contain(variable.BrowseName));

        // Verify NodeSet2 XML export
        using var ms = new MemoryStream();
        NodeSet2XmlExporter.Export(catalog, ms);
        ms.Position = 0;
        UANodeSet? imported = UANodeSet.Read(ms);
        Assert.That(imported, Is.Not.Null);
        Assert.That(imported!.Items?.Length, Is.EqualTo(catalog.Entries.Count));
    }

    [Test]
    public async Task IdentityDiffer_WithLiveCatalogs_DetectsElevationsAndHiddenNodes()
    {
        string endpointUrl = $"{Utils.UriSchemeOpcTcp}://localhost:{m_port}/FakeCncServer";

        await using ManagedSession session = await AtlasClientHelper.ConnectAsync(
            endpointUrl,
            timeoutMs: 15000).ConfigureAwait(false);

        var crawler = new AtlasCrawler(session, new CrawlerOptions
        {
            MaxDepth = 5,
            BatchSize = 50
        });

        AtlasCatalog catalog1 = await crawler.CrawlAsync(endpointUrl, identityType: "Anonymous").ConfigureAwait(false);
        AtlasCatalog catalog2 = await crawler.CrawlAsync(endpointUrl, identityType: "Operator").ConfigureAwait(false);

        CatalogDiff diff = IdentityDiffer.Diff(catalog1, catalog2);
        Assert.That(diff.BaseIdentity, Is.EqualTo("Anonymous"));
        Assert.That(diff.TargetIdentity, Is.EqualTo("Operator"));
        Assert.That(diff.Summary.UnchangedNodes, Is.EqualTo(catalog1.Entries.Count));

        string report = diff.ToMarkdown();
        Assert.That(report, Does.Contain("Atlas Identity Diff"));
    }
}
