using Atlas.Labeling;
using Atlas.Models;
using Atlas.Standard;
using NUnit.Framework;

namespace Atlas.Tests;

[TestFixture]
public class MachineToolStandardTests
{
    private static CatalogMetadata CreateTestMetadata() => new()
    {
        EndpointUrl = "opc.tcp://localhost:4840",
        ServerName = "MachineToolStandardTestServer",
        Statistics = new CrawlStatistics(10, 5, 5, 0, 0, 2, 10)
    };

    [Test]
    public void StandardMachineToolNodes_AreClassifiedAsTier1StandardCompanionSpec()
    {
        var catalog = new AtlasCatalog
        {
            Metadata = CreateTestMetadata(),
            Entries = new List<CatalogEntry>
            {
                new()
                {
                    Id = "nsu=http://opcfoundation.org/UA/MachineTool/;s=Production/PartCounters/PartsProduced",
                    BrowseName = "PartsProduced",
                    Path = "Objects/FullMachineTool/Production/PartCounters/PartsProduced",
                    NodeClass = "Variable",
                    DataType = "UInt32"
                },
                new()
                {
                    Id = "nsu=http://opcfoundation.org/UA/MachineTool/;s=Equipment/Spindles/Spindle1/ActualSpeed",
                    BrowseName = "ActualSpeed",
                    Path = "Objects/FullMachineTool/Equipment/Spindles/Spindle1/ActualSpeed",
                    NodeClass = "Variable",
                    DataType = "Double"
                },
                new()
                {
                    Id = "nsu=http://opcfoundation.org/UA/Machinery/;s=MachineryItemState",
                    BrowseName = "MachineryItemState",
                    Path = "Objects/FullMachineTool/Monitoring/MachineryItemState",
                    NodeClass = "Variable",
                    DataType = "Int32"
                }
            }
        };

        SemanticLabeler.LabelCatalog(catalog);

        // Check PartsProduced
        var partsEntry = catalog.Entries[0];
        Assert.That(partsEntry.Inferred, Is.Not.Null);
        Assert.That(partsEntry.Inferred!.Label, Is.EqualTo("parts_produced"));
        Assert.That(partsEntry.Inferred.Provenance, Is.EqualTo(SignalProvenance.StandardCompanionSpec));
        Assert.That(partsEntry.Inferred.Confidence, Is.EqualTo(1.0));
        Assert.That(partsEntry.Category, Is.EqualTo(SignalCategory.Event));

        // Check ActualSpeed
        var speedEntry = catalog.Entries[1];
        Assert.That(speedEntry.Inferred, Is.Not.Null);
        Assert.That(speedEntry.Inferred!.Label, Is.EqualTo("spindle_speed"));
        Assert.That(speedEntry.Inferred.Provenance, Is.EqualTo(SignalProvenance.StandardCompanionSpec));
        Assert.That(speedEntry.Inferred.Confidence, Is.EqualTo(1.0));
        Assert.That(speedEntry.Unit, Is.EqualTo("rpm"));
        Assert.That(speedEntry.Category, Is.EqualTo(SignalCategory.Sample));

        // Check MachineryItemState
        var stateEntry = catalog.Entries[2];
        Assert.That(stateEntry.Inferred, Is.Not.Null);
        Assert.That(stateEntry.Inferred!.Label, Is.EqualTo("machine_state"));
        Assert.That(stateEntry.Inferred.Provenance, Is.EqualTo(SignalProvenance.StandardCompanionSpec));
        Assert.That(stateEntry.Category, Is.EqualTo(SignalCategory.Event));
    }

    [Test]
    public void MachineToolState_PreservesMultiStateFidelity()
    {
        // Must preserve distinct states (Active, Interrupted, FeedHold, Ready, Fault, Manual)
        Assert.That(MachineToolStandards.ParseStandardState("ACTIVE"), Is.EqualTo(MachineToolStandards.MachineToolState.Active));
        Assert.That(MachineToolStandards.ParseStandardState("FEED_HOLD"), Is.EqualTo(MachineToolStandards.MachineToolState.FeedHold));
        Assert.That(MachineToolStandards.ParseStandardState("INTERRUPTED"), Is.EqualTo(MachineToolStandards.MachineToolState.Interrupted));
        Assert.That(MachineToolStandards.ParseStandardState("READY"), Is.EqualTo(MachineToolStandards.MachineToolState.Ready));
        Assert.That(MachineToolStandards.ParseStandardState("FAULT"), Is.EqualTo(MachineToolStandards.MachineToolState.Fault));
        Assert.That(MachineToolStandards.ParseStandardState("MANUAL"), Is.EqualTo(MachineToolStandards.MachineToolState.Manual));
        Assert.That(MachineToolStandards.ParseStandardState(2), Is.EqualTo(MachineToolStandards.MachineToolState.Active));
        Assert.That(MachineToolStandards.ParseStandardState(5), Is.EqualTo(MachineToolStandards.MachineToolState.FeedHold));
    }

    [Test]
    public void PrecedenceHierarchy_StandardTrumpsVendorAndVendorTrumpsHeuristic()
    {
        // 1. Standard Companion Spec node
        var stdEntry = new CatalogEntry
        {
            Id = "nsu=http://opcfoundation.org/UA/MachineTool/;s=PartsProduced",
            BrowseName = "PartsProduced",
            Path = "Objects/MachineTool/Production/PartsProduced",
            NodeClass = "Variable"
        };

        // 2. Proprietary PLC tag matching vendor pack
        var vendorEntry = new CatalogEntry
        {
            Id = "ns=3;s=\"DB10_Spindle\".\"Var_27\"",
            BrowseName = "Var_27",
            Path = "Objects/DB10/Var_27",
            NodeClass = "Variable"
        };

        // 3. Unrecognized tag requiring heuristic inference
        var heuristicEntry = new CatalogEntry
        {
            Id = "ns=2;s=SpindleBearingTemp",
            BrowseName = "BearingTemperature",
            Path = "Objects/Sensors/BearingTemperature",
            NodeClass = "Variable",
            Unit = "°C"
        };

        var catalog = new AtlasCatalog
        {
            Metadata = CreateTestMetadata(),
            Entries = [stdEntry, vendorEntry, heuristicEntry]
        };

        var vendorPack = new UserMappingPack
        {
            Name = "siemens-s7-1500",
            Rules = [new UserMappingRule(".*DB10.*Var_27.*", "spindle_speed", "rpm")]
        };

        SemanticLabeler.LabelCatalog(catalog, mappingPack: vendorPack);

        // Tier 1: Standard Companion Spec (1.0 confidence)
        Assert.That(stdEntry.Inferred!.Provenance, Is.EqualTo(SignalProvenance.StandardCompanionSpec));
        Assert.That(stdEntry.Inferred.Confidence, Is.EqualTo(1.0));

        // Tier 2: Vendor Pack (1.0 confidence for explicit pack rules)
        Assert.That(vendorEntry.Inferred!.Provenance, Is.EqualTo(SignalProvenance.VendorMappingPack));
        Assert.That(vendorEntry.Inferred.Confidence, Is.EqualTo(1.0));

        // Tier 3: Heuristic Inference (0.75-0.85 confidence)
        Assert.That(heuristicEntry.Inferred!.Provenance, Is.EqualTo(SignalProvenance.HeuristicInference));
        Assert.That(heuristicEntry.Inferred.Confidence, Is.GreaterThanOrEqualTo(0.70));
    }
}
