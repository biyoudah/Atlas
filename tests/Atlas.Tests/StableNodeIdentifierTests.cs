using NUnit.Framework;
using Opc.Ua;
using Atlas.Models;

namespace Atlas.Tests;

[TestFixture]
public class StableNodeIdentifierTests
{
    private NamespaceTable m_namespaceTable = null!;

    [SetUp]
    public void SetUp()
    {
        // NamespaceTable constructor already initializes index 0 with http://opcfoundation.org/UA/
        m_namespaceTable = new NamespaceTable();
        m_namespaceTable.Append("urn:siemens:s7-1500:plc1");       // index 1
        m_namespaceTable.Append("http://fanuc.co.jp/robotics/v1");  // index 2
    }

    [Test]
    public void ToStableId_WhenNullNodeId_ReturnsZeroNumeric()
    {
        string stable = StableNodeIdentifier.ToStableId(NodeId.Null, m_namespaceTable);
        Assert.That(stable, Is.EqualTo("i=0"));
    }

    [Test]
    public void ToStableId_WhenNamespace0Numeric_ReturnsStandardNumeric()
    {
        var node = new NodeId(2258u); // ServerStatus
        string stable = StableNodeIdentifier.ToStableId(node, m_namespaceTable);
        Assert.That(stable, Is.EqualTo("i=2258"));
    }

    [Test]
    public void ToStableId_WhenStringIdWithNamespaceUri_FormatsNsuPrefixedString()
    {
        ushort nsIdx = (ushort)m_namespaceTable.GetIndex("urn:siemens:s7-1500:plc1");
        var node = new NodeId("DB10.Var_27", nsIdx);

        string stable = StableNodeIdentifier.ToStableId(node, m_namespaceTable);
        Assert.That(stable, Is.EqualTo("nsu=urn:siemens:s7-1500:plc1;s=DB10.Var_27"));
    }

    [Test]
    public void ToStableId_WhenGuidIdWithNamespaceUri_FormatsNsuPrefixedGuid()
    {
        ushort nsIdx = (ushort)m_namespaceTable.GetIndex("http://fanuc.co.jp/robotics/v1");
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var node = new NodeId(guid, nsIdx);

        string stable = StableNodeIdentifier.ToStableId(node, m_namespaceTable);
        Assert.That(stable, Is.EqualTo($"nsu=http://fanuc.co.jp/robotics/v1;g={guid}"));
    }

    [Test]
    public void ToNodeId_WhenGivenNsuString_ResolvesToCorrectLocalNodeId()
    {
        string stable = "nsu=urn:siemens:s7-1500:plc1;s=DB10.Var_27";
        NodeId resolved = StableNodeIdentifier.ToNodeId(stable, m_namespaceTable);

        Assert.That(resolved.NamespaceIndex, Is.EqualTo(1));
        Assert.That(resolved.IdentifierAsString, Is.EqualTo("DB10.Var_27"));
        Assert.That(resolved.IdType, Is.EqualTo(IdType.String));
    }

    [Test]
    public void StableId_SurvivesNamespaceTableIndexShift()
    {
        // In the original server table, "urn:siemens:s7-1500:plc1" was at index 1
        ushort oldIndex = (ushort)m_namespaceTable.GetIndex("urn:siemens:s7-1500:plc1");
        Assert.That(oldIndex, Is.EqualTo(1));

        var originalNode = new NodeId("SpeedSetpoint", oldIndex);
        string stableId = StableNodeIdentifier.ToStableId(originalNode, m_namespaceTable);
        Assert.That(stableId, Is.EqualTo("nsu=urn:siemens:s7-1500:plc1;s=SpeedSetpoint"));

        // Later, new NodeSets were loaded or server rebooted, and the URI shifted to index 4
        var reorderedTable = new NamespaceTable(); // index 0 is OPC UA
        reorderedTable.Append("http://opcfoundation.org/UA/DI/");        // index 1
        reorderedTable.Append("http://opcfoundation.org/UA/Machinery/"); // index 2
        reorderedTable.Append("http://opcfoundation.org/UA/Robotics/");  // index 3
        reorderedTable.Append("urn:siemens:s7-1500:plc1");               // index 4

        NodeId remappedNode = StableNodeIdentifier.ToNodeId(stableId, reorderedTable);

        Assert.That(remappedNode.NamespaceIndex, Is.EqualTo(4));
        Assert.That(remappedNode.IdentifierAsString, Is.EqualTo("SpeedSetpoint"));
    }

    [Test]
    public void TryExtractNamespaceUri_ExtractsUriAndLocalIdentifier()
    {
        string stable = "nsu=http://vendor.com/model/;s=Motor.Rpm";
        bool extracted = StableNodeIdentifier.TryExtractNamespaceUri(stable, out string? uri, out string identifier);

        Assert.That(extracted, Is.True);
        Assert.That(uri, Is.EqualTo("http://vendor.com/model/"));
        Assert.That(identifier, Is.EqualTo("s=Motor.Rpm"));
    }
}
