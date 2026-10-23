using NUnit.Framework;
using Opc.Ua.Atlas.Labeling;
using Opc.Ua.Atlas.Models;
using Opc.Ua.Atlas.Profiling;

namespace Opc.Ua.Atlas.Tests;

[TestFixture]
public class SemanticLabelerTests
{
    private static AtlasCatalog CreateCatalog(params CatalogEntry[] entries) => new()
    {
        Metadata = new CatalogMetadata
        {
            EndpointUrl = "opc.tcp://localhost:4840",
            CrawledAtUtc = DateTime.UtcNow
        },
        Entries = entries.ToList()
    };

    [Test]
    public void UserMappingPack_AlwaysOverridesWithFullConfidence()
    {
        var catalog = CreateCatalog(new CatalogEntry
        {
            Id = "nsu=http://siemens.com/simatic;s=\"DB10\".\"Var_27\"",
            Path = "Objects/PLC/DB10/Var_27",
            BrowseName = "Var_27",
            NodeClass = "Variable",
            DataType = "Double"
        });

        var mappingPack = new UserMappingPack
        {
            Name = "Siemens-S7-1500-CNC",
            Rules =
            [
                new UserMappingRule(
                    Pattern: @".*DB10.*Var_27",
                    Label: "spindle_speed",
                    Unit: "rpm",
                    Description: "Main spindle actual speed")
            ]
        };

        SemanticLabeler.LabelCatalog(catalog, mappingPack: mappingPack);

        CatalogEntry entry = catalog.Entries[0];
        Assert.That(entry.Inferred, Is.Not.Null);
        Assert.That(entry.Inferred!.Label, Is.EqualTo("spindle_speed"));
        Assert.That(entry.Inferred.Confidence, Is.EqualTo(1.0));
        Assert.That(entry.Unit, Is.EqualTo("rpm"));
        Assert.That(entry.Inferred.Evidence[0], Does.Contain("Siemens-S7-1500-CNC"));
    }

    [Test]
    public void MultiFactorInference_CombinesCompanionSpecUnitAndRange()
    {
        var catalog = CreateCatalog(new CatalogEntry
        {
            Id = "nsu=http://opcfoundation.org/UA/CNC/;s=ActualSpeed",
            Path = "Objects/CNC/Spindle/ActualSpeed",
            BrowseName = "ActualSpeed",
            NodeClass = "Variable",
            DataType = "Double",
            Unit = "rpm",
            Range = new EURangeInfo(0.0, 18000.0)
        });

        SemanticLabeler.LabelCatalog(catalog);

        CatalogEntry entry = catalog.Entries[0];
        Assert.That(entry.Inferred, Is.Not.Null);
        Assert.That(entry.Inferred!.Label, Is.EqualTo("spindle_speed"));
        Assert.That(entry.Inferred.Confidence, Is.GreaterThanOrEqualTo(0.95));
        Assert.That(entry.Inferred.Evidence.Count, Is.GreaterThanOrEqualTo(3));
        Assert.That(entry.Inferred.Evidence.Any(e => e.Contains("rpm")), Is.True);
        Assert.That(entry.Inferred.Evidence.Any(e => e.Contains("EURange")), Is.True);
    }

    [Test]
    public void HeuristicInference_InfersCrypticPlcNodeFromProfilingCorrelations()
    {
        // Siemens S7-1500 cryptic tag DB10.Var_27 without units or semantic names
        var catalog = CreateCatalog(new CatalogEntry
        {
            Id = "nsu=http://siemens.com/simatic;s=\"DB10\".\"Var_27\"",
            Path = "Objects/PLC/DB10/Var_27",
            BrowseName = "Var_27",
            NodeClass = "Variable",
            DataType = "Double"
        });

        var profileReport = new ProfileReport
        {
            Profiles = new Dictionary<string, NodeProfile>
            {
                ["nsu=http://siemens.com/simatic;s=\"DB10\".\"Var_27\""] = new(
                    Id: "nsu=http://siemens.com/simatic;s=\"DB10\".\"Var_27\"",
                    BrowseName: "Var_27",
                    Classification: SignalClassification.AnalogProcessValue,
                    SampleCount: 100,
                    ChangeCount: 95,
                    ChangeRatePerMinute: 60.0,
                    ObservedMin: 0.0,
                    ObservedMax: 10042.0,
                    LastObservedValue: 9980.0,
                    DistinctValuesCount: 90)
            },
            Correlations =
            [
                new CorrelatedTransition(
                    SourceNodeId: "nsu=http://siemens.com/simatic;s=StateWord",
                    TargetNodeId: "nsu=http://siemens.com/simatic;s=\"DB10\".\"Var_27\"",
                    CorrelationCoefficient: 0.95,
                    Relationship: "tracks Running state",
                    Confidence: 0.90)
            ]
        };

        SemanticLabeler.LabelCatalog(catalog, profileReport: profileReport);

        CatalogEntry entry = catalog.Entries[0];
        Assert.That(entry.Inferred, Is.Not.Null);
        Assert.That(entry.Inferred!.Label, Is.EqualTo("spindle_speed"));
        Assert.That(entry.Inferred.Confidence, Is.GreaterThanOrEqualTo(0.80));
        Assert.That(entry.Inferred.Evidence.Any(e => e.Contains("tracks Running state")), Is.True);
    }
}
