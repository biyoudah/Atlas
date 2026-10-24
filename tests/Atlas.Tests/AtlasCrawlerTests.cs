using Moq;
using NUnit.Framework;
using Opc.Ua;
using Atlas.Crawler;
using Atlas.Models;
using Opc.Ua.Client;

namespace Atlas.Tests;

[TestFixture]
public class AtlasCrawlerTests
{
    private NamespaceTable m_namespaces = null!;
    private Mock<ISessionClient> m_sessionMock = null!;

    [SetUp]
    public void SetUp()
    {
        m_namespaces = new NamespaceTable();
        m_namespaces.Append("urn:test:factory");
        m_sessionMock = new Mock<ISessionClient>(MockBehavior.Strict);

        var serviceMessageContext = ServiceMessageContext.Create(null);
        serviceMessageContext.NamespaceUris.Append("urn:test:factory");
        m_sessionMock.SetupGet(s => s.MessageContext).Returns(serviceMessageContext);
    }

    [Test]
    public async Task CrawlAsync_TraversesHierarchicalTreeAndRecordsPaths()
    {
        var machineNodeId = new NodeId("CNC_Machine", 1);
        var spindleNodeId = new NodeId("Spindle_Speed", 1);

        // Browse from ObjectsFolder
        m_sessionMock
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<ViewDescription?>(),
                It.IsAny<uint>(),
                It.Is<ArrayOf<BrowseDescription>>(b => b[0].NodeId == ObjectIds.ObjectsFolder),
                It.IsAny<CancellationToken>()))
            .Returns<RequestHeader?, ViewDescription?, uint, ArrayOf<BrowseDescription>, CancellationToken>(
                (hdr, view, maxRefs, b, ct) =>
                {
                    var refs = new ReferenceDescription[]
                    {
                        new()
                        {
                            NodeId = new ExpandedNodeId(machineNodeId),
                            NodeClass = NodeClass.Object,
                            BrowseName = new QualifiedName("CNC_Machine", 1),
                            DisplayName = new LocalizedText("CNC Machine"),
                            ReferenceTypeId = ReferenceTypeIds.Organizes
                        }
                    };

                    return ValueTask.FromResult(new BrowseResponse
                    {
                        ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                        Results = [new BrowseResult { References = refs, StatusCode = StatusCodes.Good }]
                    });
                });

        // Browse from CNC_Machine
        m_sessionMock
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<ViewDescription?>(),
                It.IsAny<uint>(),
                It.Is<ArrayOf<BrowseDescription>>(b => b[0].NodeId == machineNodeId),
                It.IsAny<CancellationToken>()))
            .Returns<RequestHeader?, ViewDescription?, uint, ArrayOf<BrowseDescription>, CancellationToken>(
                (hdr, view, maxRefs, b, ct) =>
                {
                    var refs = new ReferenceDescription[]
                    {
                        new()
                        {
                            NodeId = new ExpandedNodeId(spindleNodeId),
                            NodeClass = NodeClass.Variable,
                            BrowseName = new QualifiedName("Spindle_Speed", 1),
                            DisplayName = new LocalizedText("Spindle Speed"),
                            ReferenceTypeId = ReferenceTypeIds.HasComponent
                        }
                    };

                    return ValueTask.FromResult(new BrowseResponse
                    {
                        ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                        Results = [new BrowseResult { References = refs, StatusCode = StatusCodes.Good }]
                    });
                });

        // Browse from Spindle_Speed (leaf variable)
        m_sessionMock
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<ViewDescription?>(),
                It.IsAny<uint>(),
                It.Is<ArrayOf<BrowseDescription>>(b => b[0].NodeId == spindleNodeId),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new BrowseResponse
            {
                ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                Results = [new BrowseResult { References = [], StatusCode = StatusCodes.Good }]
            }));

        // Attribute reads
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
                    var results = new List<DataValue>();
                    foreach (ReadValueId r in nodesToRead)
                    {
                        if (r.NodeId == spindleNodeId && r.AttributeId == Attributes.DataType)
                        {
                            results.Add(new DataValue(Variant.From(DataTypeIds.Double)));
                        }
                        else if (r.NodeId == spindleNodeId && r.AttributeId == Attributes.AccessLevel)
                        {
                            results.Add(new DataValue(Variant.From((byte)AccessLevels.CurrentRead)));
                        }
                        else if (r.AttributeId == Attributes.BrowseName)
                        {
                            results.Add(new DataValue(Variant.From(new QualifiedName(r.NodeId.IdentifierAsString, 1))));
                        }
                        else if (r.AttributeId == Attributes.DisplayName)
                        {
                            results.Add(new DataValue(Variant.From(new LocalizedText(r.NodeId.IdentifierAsString))));
                        }
                        else if (r.AttributeId == Attributes.NodeClass)
                        {
                            results.Add(new DataValue(Variant.From(r.NodeId == spindleNodeId ? (int)NodeClass.Variable : (int)NodeClass.Object)));
                        }
                        else
                        {
                            results.Add(new DataValue(Variant.Null, StatusCodes.Good));
                        }
                    }

                    return ValueTask.FromResult(new ReadResponse
                    {
                        ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                        Results = [.. results]
                    });
                });

        var crawler = new AtlasCrawler(m_sessionMock.Object);
        AtlasCatalog catalog = await crawler.CrawlAsync("opc.tcp://localhost:4840", "Anonymous");

        Assert.That(catalog.Entries.Count, Is.EqualTo(2));

        CatalogEntry machineEntry = catalog.Entries[0];
        Assert.That(machineEntry.BrowseName, Is.EqualTo("CNC_Machine"));
        Assert.That(machineEntry.Path, Is.EqualTo("Objects/CNC_Machine"));
        Assert.That(machineEntry.NodeClass, Is.EqualTo("Object"));

        CatalogEntry spindleEntry = catalog.Entries[1];
        Assert.That(spindleEntry.BrowseName, Is.EqualTo("Spindle_Speed"));
        Assert.That(spindleEntry.Path, Is.EqualTo("Objects/CNC_Machine/Spindle_Speed"));
        Assert.That(spindleEntry.NodeClass, Is.EqualTo("Variable"));
        Assert.That(spindleEntry.DataType, Is.EqualTo("Double"));
    }

    [Test]
    public async Task CrawlAsync_CycleDetection_PreventsInfiniteLoops()
    {
        var nodeA = new NodeId("NodeA", 1);
        var nodeB = new NodeId("NodeB", 1);

        // Root -> NodeA
        m_sessionMock
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<ViewDescription?>(),
                It.IsAny<uint>(),
                It.Is<ArrayOf<BrowseDescription>>(b => b[0].NodeId == ObjectIds.ObjectsFolder),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new BrowseResponse
            {
                ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                Results = [new BrowseResult
                {
                    References = [new ReferenceDescription { NodeId = new ExpandedNodeId(nodeA), NodeClass = NodeClass.Object, BrowseName = new QualifiedName("NodeA", 1) }],
                    StatusCode = StatusCodes.Good
                }]
            }));

        // NodeA -> NodeB
        m_sessionMock
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<ViewDescription?>(),
                It.IsAny<uint>(),
                It.Is<ArrayOf<BrowseDescription>>(b => b[0].NodeId == nodeA),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new BrowseResponse
            {
                ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                Results = [new BrowseResult
                {
                    References = [new ReferenceDescription { NodeId = new ExpandedNodeId(nodeB), NodeClass = NodeClass.Object, BrowseName = new QualifiedName("NodeB", 1) }],
                    StatusCode = StatusCodes.Good
                }]
            }));

        // NodeB -> NodeA (Circular reference loop!)
        m_sessionMock
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader?>(),
                It.IsAny<ViewDescription?>(),
                It.IsAny<uint>(),
                It.Is<ArrayOf<BrowseDescription>>(b => b[0].NodeId == nodeB),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new BrowseResponse
            {
                ResponseHeader = new ResponseHeader { ServiceResult = StatusCodes.Good },
                Results = [new BrowseResult
                {
                    References = [new ReferenceDescription { NodeId = new ExpandedNodeId(nodeA), NodeClass = NodeClass.Object, BrowseName = new QualifiedName("NodeA", 1) }],
                    StatusCode = StatusCodes.Good
                }]
            }));

        // Attributes dummy read
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
                    var results = new List<DataValue>();
                    for (int i = 0; i < nodesToRead.Count; i++)
                    {
                        results.Add(new DataValue(Variant.Null, StatusCodes.Good));
                    }
                    return ValueTask.FromResult(new ReadResponse { Results = [.. results] });
                });

        var crawler = new AtlasCrawler(m_sessionMock.Object);
        AtlasCatalog catalog = await crawler.CrawlAsync("opc.tcp://localhost:4840", "Anonymous");

        // Exactly 2 nodes should be visited despite circular loop
        Assert.That(catalog.Entries.Count, Is.EqualTo(2));
        Assert.That(catalog.Entries.Select(e => e.BrowseName), Is.EquivalentTo(new[] { "NodeA", "NodeB" }));
    }
}
