using Moq;
using NUnit.Framework;
using Opc.Ua;
using Atlas.Crawler;
using Atlas.Models;
using Opc.Ua.Client;

namespace Atlas.Tests;

[TestFixture]
public class AttributeBatchReaderTests
{
    private NamespaceTable m_namespaces = null!;
    private Mock<ISessionClient> m_sessionMock = null!;

    [SetUp]
    public void SetUp()
    {
        m_namespaces = new NamespaceTable();
        m_namespaces.Append("urn:test:vendor");
        m_sessionMock = new Mock<ISessionClient>(MockBehavior.Strict);
    }

    [Test]
    public async Task ReadAttributesAsync_WhenEmptyList_ReturnsEmptyWithoutCallingServer()
    {
        var reader = new AttributeBatchReader(m_sessionMock.Object, maxNodesPerRead: 100);
        var entries = await reader.ReadAttributesAsync(Array.Empty<DiscoveredNode>(), m_namespaces);

        Assert.That(entries, Is.Empty);
        m_sessionMock.Verify(s => s.ReadAsync(
            It.IsAny<RequestHeader?>(),
            It.IsAny<double>(),
            It.IsAny<TimestampsToReturn>(),
            It.IsAny<ArrayOf<ReadValueId>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ReadAttributesAsync_BatchesRespectingLimit()
    {
        // 2 variable nodes, each requests 10 attributes = 20 total ReadValueIds.
        // With maxNodesPerRead = 12, it must split into 2 batches of 10 attributes each.
        var node1 = new DiscoveredNode(new NodeId("Speed", 1), "Objects/Speed", NodeClass.Variable);
        var node2 = new DiscoveredNode(new NodeId("Temp", 1), "Objects/Temp", NodeClass.Variable);

        int callCount = 0;
        m_sessionMock
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ArrayOf<ReadValueId>>(),
                It.IsAny<CancellationToken>()))
            .Returns<RequestHeader?, double, TimestampsToReturn, ArrayOf<ReadValueId>, CancellationToken>(
                (hdr, maxAge, ts, nodesToRead, ct) =>
                {
                    callCount++;
                    Assert.That(nodesToRead.Count, Is.LessThanOrEqualTo(12));

                    var results = new List<DataValue>(nodesToRead.Count);
                    for (int i = 0; i < nodesToRead.Count; i++)
                    {
                        results.Add(new DataValue(Variant.From("MockValue")));
                    }

                    return ValueTask.FromResult(new ReadResponse
                    {
                        ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                        Results = [.. results]
                    });
                });

        var reader = new AttributeBatchReader(m_sessionMock.Object, maxNodesPerRead: 12);
        var entries = await reader.ReadAttributesAsync([node1, node2], m_namespaces);

        Assert.That(callCount, Is.EqualTo(2));
        Assert.That(entries.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task ReadAttributesAsync_PopulatesVariableAttributesAndAccessLevels()
    {
        var node = new DiscoveredNode(new NodeId("Pressure", 1), "Objects/Machine/Pressure", NodeClass.Variable);

        m_sessionMock
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ArrayOf<ReadValueId>>(),
                It.IsAny<CancellationToken>()))
            .Returns<RequestHeader?, double, TimestampsToReturn, ArrayOf<ReadValueId>, CancellationToken>(
                (hdr, maxAge, ts, nodesToRead, ct) =>
                {
                    var results = new List<DataValue>(nodesToRead.Count);
                    foreach (ReadValueId r in nodesToRead)
                    {
                        DataValue val = r.AttributeId switch
                        {
                            Attributes.NodeClass => new DataValue(Variant.From((int)NodeClass.Variable)),
                            Attributes.BrowseName => new DataValue(Variant.From(new QualifiedName("Pressure", 1))),
                            Attributes.DisplayName => new DataValue(Variant.From(new LocalizedText("Main Pressure"))),
                            Attributes.Description => new DataValue(Variant.From(new LocalizedText("Main line pressure in bar"))),
                            Attributes.DataType => new DataValue(Variant.From(DataTypeIds.Double)),
                            Attributes.ValueRank => new DataValue(Variant.From(ValueRanks.Scalar)),
                            Attributes.AccessLevel => new DataValue(Variant.From((byte)(AccessLevels.CurrentRead | AccessLevels.CurrentWrite))),
                            Attributes.UserAccessLevel => new DataValue(Variant.From((byte)AccessLevels.CurrentRead)),
                            Attributes.MinimumSamplingInterval => new DataValue(Variant.From(50.0)),
                            Attributes.Historizing => new DataValue(Variant.From(true)),
                            _ => new DataValue(Variant.Null, StatusCodes.BadAttributeIdInvalid)
                        };
                        results.Add(val);
                    }

                    return ValueTask.FromResult(new ReadResponse
                    {
                        ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                        Results = [.. results]
                    });
                });

        var reader = new AttributeBatchReader(m_sessionMock.Object, maxNodesPerRead: 100);
        IReadOnlyList<CatalogEntry> entries = await reader.ReadAttributesAsync([node], m_namespaces);

        Assert.That(entries.Count, Is.EqualTo(1));
        CatalogEntry entry = entries[0];

        Assert.That(entry.Id, Is.EqualTo("nsu=urn:test:vendor;s=Pressure"));
        Assert.That(entry.Path, Is.EqualTo("Objects/Machine/Pressure"));
        Assert.That(entry.BrowseName, Is.EqualTo("Pressure"));
        Assert.That(entry.DisplayName, Is.EqualTo("Main Pressure"));
        Assert.That(entry.Description, Is.EqualTo("Main line pressure in bar"));
        Assert.That(entry.NodeClass, Is.EqualTo("Variable"));
        Assert.That(entry.DataType, Is.EqualTo("Double"));
        Assert.That(entry.ValueRank, Is.EqualTo(-1));
        Assert.That(entry.Access, Is.Not.Null);
        Assert.That(entry.Access!.Read, Is.True);
        Assert.That(entry.Access.Write, Is.True);
        Assert.That(entry.UserAccess, Is.Not.Null);
        Assert.That(entry.UserAccess!.Read, Is.True);
        Assert.That(entry.UserAccess.Write, Is.False); // Only read permission for current user
        Assert.That(entry.MinimumSamplingInterval, Is.EqualTo(50.0));
        Assert.That(entry.Historizing, Is.True);
    }

    [Test]
    public async Task ReadAttributesAsync_HandlesBadAttributeIdInvalidGracefully()
    {
        var node = new DiscoveredNode(new NodeId(1001u, 1), "Objects/LegacyTag", NodeClass.Variable);

        m_sessionMock
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ArrayOf<ReadValueId>>(),
                It.IsAny<CancellationToken>()))
            .Returns<RequestHeader?, double, TimestampsToReturn, ArrayOf<ReadValueId>, CancellationToken>(
                (hdr, maxAge, ts, nodesToRead, ct) =>
                {
                    var results = new List<DataValue>(nodesToRead.Count);
                    foreach (ReadValueId r in nodesToRead)
                    {
                        // Simulate older PLC that returns BadAttributeIdInvalid for SamplingInterval and Historizing
                        DataValue val = r.AttributeId switch
                        {
                            Attributes.NodeClass => new DataValue(Variant.From((int)NodeClass.Variable)),
                            Attributes.BrowseName => new DataValue(Variant.From(new QualifiedName("LegacyTag", 1))),
                            Attributes.DisplayName => new DataValue(Variant.From(new LocalizedText("Legacy Tag"))),
                            Attributes.DataType => new DataValue(Variant.From(DataTypeIds.Int32)),
                            Attributes.MinimumSamplingInterval => DataValue.FromStatusCode(StatusCodes.BadAttributeIdInvalid),
                            Attributes.Historizing => DataValue.FromStatusCode(StatusCodes.BadAttributeIdInvalid),
                            _ => new DataValue(Variant.Null, StatusCodes.Good)
                        };
                        results.Add(val);
                    }

                    return ValueTask.FromResult(new ReadResponse
                    {
                        ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                        Results = [.. results]
                    });
                });

        var reader = new AttributeBatchReader(m_sessionMock.Object, maxNodesPerRead: 100);
        IReadOnlyList<CatalogEntry> entries = await reader.ReadAttributesAsync([node], m_namespaces);

        Assert.That(entries.Count, Is.EqualTo(1));
        CatalogEntry entry = entries[0];
        Assert.That(entry.BrowseName, Is.EqualTo("LegacyTag"));
        Assert.That(entry.MinimumSamplingInterval, Is.Null);
        Assert.That(entry.Historizing, Is.Null);
    }
}
