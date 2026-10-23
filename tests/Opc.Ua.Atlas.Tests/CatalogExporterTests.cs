using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Opc.Ua.Atlas.Exporters;
using Opc.Ua.Atlas.Models;
using Opc.Ua.Export;

namespace Opc.Ua.Atlas.Tests;

[TestFixture]
public class CatalogExporterTests
{
    private AtlasCatalog CreateSampleCatalog()
    {
        return new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://plc01.plant.local:4840",
                ManufacturerName = "Siemens",
                SoftwareVersion = "V2.9.2",
                CrawledAtUtc = new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc),
                Namespaces = ["http://opcfoundation.org/UA/", "http://siemens.com/simatic-s7-1500", "urn:custom:machine"],
                IdentityType = "Anonymous",
                Statistics = new CrawlStatistics(
                    TotalNodes: 2,
                    TotalVariables: 1,
                    TotalObjects: 1,
                    TotalMethods: 0,
                    ContinuationPointsHandled: 0,
                    MaxDepthReached: 3,
                    DurationMilliseconds: 120)
            },
            Entries =
            [
                new CatalogEntry
                {
                    Id = "nsu=urn:custom:machine;s=Machine1",
                    Path = "Objects/Machine1",
                    BrowseName = "Machine1",
                    DisplayName = "Machine 1 Cell",
                    NodeClass = "Object",
                    Description = "Root cell object"
                },
                new CatalogEntry
                {
                    Id = "nsu=http://siemens.com/simatic-s7-1500;s=\"DB10\".\"Var_27\"",
                    Path = "Objects/Machine1/DB10/Var_27",
                    BrowseName = "Var_27",
                    DisplayName = "Spindle Actual Speed",
                    Description = "Live spindle speed reading",
                    NodeClass = "Variable",
                    DataType = "Double",
                    ValueRank = -1,
                    Access = new NodeAccessInfo(Read: true, Write: false),
                    UserAccess = new NodeAccessInfo(Read: true, Write: false),
                    Unit = "rpm",
                    Range = new EURangeInfo(0.0, 3200.0),
                    Inferred = new InferredSemantics(
                        Label: "spindle_speed",
                        Confidence: 0.85,
                        Evidence: ["tracks Running state", "range 0-3200", "unit rpm"])
                }
            ]
        };
    }

    [Test]
    public void JsonCatalogExporter_SerializesAndRoundTrips()
    {
        AtlasCatalog catalog = CreateSampleCatalog();

        string json = JsonCatalogExporter.ToJson(catalog);
        Assert.That(json, Is.Not.Null.And.Not.Empty);
        Assert.That(json, Does.Contain("spindle_speed"));
        Assert.That(json, Does.Contain("rpm"));
        Assert.That(json, Does.Contain("0.85"));

        AtlasCatalog? deserialized = JsonSerializer.Deserialize<AtlasCatalog>(json);
        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Metadata.ManufacturerName, Is.EqualTo("Siemens"));
        Assert.That(deserialized.Entries.Count, Is.EqualTo(2));
        Assert.That(deserialized.Entries[1].Inferred?.Label, Is.EqualTo("spindle_speed"));
        Assert.That(deserialized.Entries[1].Range?.High, Is.EqualTo(3200.0));
    }

    [Test]
    public async Task JsonCatalogExporter_ExportsToFile()
    {
        AtlasCatalog catalog = CreateSampleCatalog();
        string tempFile = Path.Combine(Path.GetTempPath(), $"atlas_test_{Guid.NewGuid():N}.json");

        try
        {
            await JsonCatalogExporter.ExportToFileAsync(catalog, tempFile);
            Assert.That(File.Exists(tempFile), Is.True);

            string content = await File.ReadAllTextAsync(tempFile);
            Assert.That(content, Does.Contain("opc.tcp://plc01.plant.local:4840"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Test]
    public void CsvCatalogExporter_ExportsFormattedCsv()
    {
        AtlasCatalog catalog = CreateSampleCatalog();
        using var sw = new StringWriter();
        CsvCatalogExporter.Export(catalog, sw);

        string csv = sw.ToString();
        Assert.That(csv, Is.Not.Null.And.Not.Empty);

        string[] lines = csv.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Length, Is.EqualTo(3)); // 1 header + 2 entries

        Assert.That(lines[0], Does.StartWith("Id,Path,BrowseName,DisplayName"));
        Assert.That(lines[1], Does.Contain("Machine1"));
        Assert.That(lines[2], Does.Contain("rpm"));
        Assert.That(lines[2], Does.Contain("3200"));
        Assert.That(lines[2], Does.Contain("Double"));
    }

    [Test]
    public async Task CsvCatalogExporter_ExportsToFile()
    {
        AtlasCatalog catalog = CreateSampleCatalog();
        string tempFile = Path.Combine(Path.GetTempPath(), $"atlas_test_{Guid.NewGuid():N}.csv");

        try
        {
            await CsvCatalogExporter.ExportToFileAsync(catalog, tempFile);
            Assert.That(File.Exists(tempFile), Is.True);

            string content = await File.ReadAllTextAsync(tempFile);
            Assert.That(content, Does.Contain("Var_27"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Test]
    public void NodeSet2XmlExporter_ProducesValidUANodeSet()
    {
        AtlasCatalog catalog = CreateSampleCatalog();

        using var ms = new MemoryStream();
        NodeSet2XmlExporter.Export(catalog, ms);
        ms.Position = 0;

        string xml = Encoding.UTF8.GetString(ms.ToArray());
        Assert.That(xml, Does.Contain("<UANodeSet"));
        Assert.That(xml, Does.Contain("http://siemens.com/simatic-s7-1500"));
        Assert.That(xml, Does.Contain("<UAObject"));
        Assert.That(xml, Does.Contain("<UAVariable"));

        // Roundtrip through OPC UA UANodeSet reader
        ms.Position = 0;
        UANodeSet? imported = UANodeSet.Read(ms);
        Assert.That(imported, Is.Not.Null);
        Assert.That(imported!.NamespaceUris, Is.Not.Null);
        Assert.That(imported.NamespaceUris, Does.Contain("http://siemens.com/simatic-s7-1500"));
        Assert.That(imported.Items, Is.Not.Null);
        Assert.That(imported.Items!.Length, Is.EqualTo(2));

        UAVariable? varNode = imported.Items.OfType<UAVariable>().FirstOrDefault();
        Assert.That(varNode, Is.Not.Null);
        Assert.That(varNode!.BrowseName, Does.EndWith("Var_27"));
        Assert.That(varNode.DataType, Is.EqualTo("i=11")); // Double built-in
    }

    [Test]
    public async Task NodeSet2XmlExporter_ExportsToFile()
    {
        AtlasCatalog catalog = CreateSampleCatalog();
        string tempFile = Path.Combine(Path.GetTempPath(), $"atlas_test_{Guid.NewGuid():N}.xml");

        try
        {
            await NodeSet2XmlExporter.ExportToFileAsync(catalog, tempFile);
            Assert.That(File.Exists(tempFile), Is.True);

            using var fs = File.OpenRead(tempFile);
            UANodeSet? imported = UANodeSet.Read(fs);
            Assert.That(imported, Is.Not.Null);
            Assert.That(imported!.Items?.Length, Is.EqualTo(2));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
