using System.Text.Json;
using NUnit.Framework;
using Atlas.Labeling;
using Atlas.Models;

namespace Atlas.Tests;

[TestFixture]
public class SignalCategoryAndSemanticsTests
{
    [Test]
    public void DeduceCategory_AssignsCorrectMTConnectCategories()
    {
        var speedEntry = new CatalogEntry
        {
            Id = "ns=2;s=SpindleSpeed",
            BrowseName = "ActualSpeed",
            Path = "Objects/CNC/ActualSpeed",
            Unit = "rpm",
            DataType = "Double"
        };

        var stateEntry = new CatalogEntry
        {
            Id = "ns=2;s=MachineState",
            BrowseName = "ExecutionState",
            Path = "Objects/CNC/ExecutionState",
            DataType = "String"
        };

        var estopEntry = new CatalogEntry
        {
            Id = "ns=2;s=EmergencyStop",
            BrowseName = "EmergencyStop",
            Path = "Objects/CNC/Safety/EmergencyStop",
            DataType = "Boolean"
        };

        var catalog = new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://localhost:4840",
                ServerUri = "urn:test",
                CrawledAtUtc = DateTime.UtcNow
            },
            Entries = [speedEntry, stateEntry, estopEntry]
        };

        SemanticLabeler.LabelCatalog(catalog);

        Assert.That(speedEntry.Category, Is.EqualTo(SignalCategory.Sample));
        Assert.That(stateEntry.Category, Is.EqualTo(SignalCategory.Event));
        Assert.That(estopEntry.Category, Is.EqualTo(SignalCategory.Condition));
    }

    [Test]
    public void MappingPack_CanOverrideSignalCategory()
    {
        var customTag = new CatalogEntry
        {
            Id = "ns=2;s=DB100.DBX0.0",
            BrowseName = "CustomInterlock",
            Path = "Objects/PLC/DB100.DBX0.0",
            DataType = "Boolean"
        };

        var catalog = new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://localhost:4840",
                ServerUri = "urn:test",
                CrawledAtUtc = DateTime.UtcNow
            },
            Entries = [customTag]
        };

        var pack = new UserMappingPack
        {
            Name = "custom-test-pack",
            Rules =
            [
                new UserMappingRule(
                    Pattern: "DB100\\.DBX0\\.0",
                    Label: "pallet_clamp_interlock",
                    Category: SignalCategory.Condition)
            ]
        };

        SemanticLabeler.LabelCatalog(catalog, mappingPack: pack);

        Assert.That(customTag.Category, Is.EqualTo(SignalCategory.Condition));
        Assert.That(customTag.Inferred?.Label, Is.EqualTo("pallet_clamp_interlock"));
        Assert.That(customTag.Inferred?.Confidence, Is.EqualTo(1.0));
    }

    [Test]
    public void Serialization_IncludesCategoryString()
    {
        var entry = new CatalogEntry
        {
            Id = "ns=2;s=Temp",
            Path = "Objects/Temp",
            Category = SignalCategory.Sample,
            Unit = "°C"
        };

        string json = JsonSerializer.Serialize(entry);
        Assert.That(json, Does.Contain("\"category\":\"Sample\""));
    }
}
