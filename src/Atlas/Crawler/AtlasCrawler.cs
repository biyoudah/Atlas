using System.Diagnostics;
using Opc.Ua;
using Atlas.Models;
using Opc.Ua.Client;

namespace Atlas.Crawler;

/// <summary>
/// Configuration options for an address space crawl.
/// </summary>
public class CrawlerOptions
{
    /// <summary>
    /// Node to start the crawl from (defaults to ObjectsFolder).
    /// </summary>
    public NodeId RootNodeId { get; set; } = ObjectIds.ObjectsFolder;

    /// <summary>
    /// Root display path prefix.
    /// </summary>
    public string RootPath { get; set; } = "Objects";

    /// <summary>
    /// Maximum recursive browse depth to prevent runaway loops.
    /// </summary>
    public int MaxDepth { get; set; } = 30;

    /// <summary>
    /// Maximum items per read request (overrides server limit if specified).
    /// </summary>
    public uint? MaxNodesPerRead { get; set; }

    /// <summary>
    /// Convenience alias for MaxNodesPerRead.
    /// </summary>
    public int? BatchSize
    {
        get => (int?)MaxNodesPerRead;
        set => MaxNodesPerRead = value.HasValue ? (uint)value.Value : null;
    }

    /// <summary>
    /// Whether to recursively crawl inside the standard 'Server' diagnostics folder (defaults to false).
    /// </summary>
    public bool CrawlServerObject { get; set; } = false;

    /// <summary>
    /// Convenience alias for CrawlServerObject.
    /// </summary>
    public bool IncludeServerDiagnostics
    {
        get => CrawlServerObject;
        set => CrawlServerObject = value;
    }

    /// <summary>
    /// User identity description for catalog metadata (e.g. "Anonymous", "Admin").
    /// </summary>
    public string? IdentityType { get; set; }
}

/// <summary>
/// High-performance recursive address space crawler for OPC UA.
/// Discovers all reachable nodes, respects continuation points and server OperationLimits,
/// resolves property nodes (EURange, EngineeringUnits), and builds the AtlasCatalog.
/// </summary>
public class AtlasCrawler
{
    private readonly ISessionClient m_session;
    private readonly AttributeBatchReader m_attributeReader;
    private readonly CrawlerOptions m_options;

    public AtlasCrawler(ISessionClient session, CrawlerOptions? options = null)
    {
        m_session = session ?? throw new ArgumentNullException(nameof(session));
        m_options = options ?? new CrawlerOptions();
        m_attributeReader = new AttributeBatchReader(session, m_options.MaxNodesPerRead);
    }

    /// <summary>
    /// Executes the full crawl pipeline against the connected server.
    /// </summary>
    public async Task<AtlasCatalog> CrawlAsync(
        string? endpointUrl = null,
        string? identityType = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        NamespaceTable namespaces = (m_session as ISession)?.NamespaceUris
            ?? m_session.MessageContext?.NamespaceUris
            ?? new NamespaceTable();

        // Try reading server build info for metadata
        string? manufacturer = null;
        string? softwareVersion = null;
        string? serverName = null;
        try
        {
            (manufacturer, softwareVersion, serverName) = await TryReadServerBuildInfoAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Optional metadata read failure is non-fatal
        }

        // Phase 1: Recursive browse traversal
        var discoveredNodes = new List<DiscoveredNode>();
        var propertyLinks = new Dictionary<string, (NodeId? EuRangeId, NodeId? EngUnitsId, NodeId? EnumStringsId)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        int maxDepthReached = 0;
        int continuationPointsHandled = 0;

        var queue = new Queue<(NodeId NodeId, string Path, int Depth)>();
        queue.Enqueue((m_options.RootNodeId, m_options.RootPath, 0));
        visited.Add(StableNodeIdentifier.ToStableId(m_options.RootNodeId, namespaces));

        var browser = new Browser(m_session)
        {
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = 0
        };

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (currentNodeId, currentPath, currentDepth) = queue.Dequeue();
            if (currentDepth > maxDepthReached)
            {
                maxDepthReached = currentDepth;
            }

            if (currentDepth >= m_options.MaxDepth)
            {
                continue;
            }

            ArrayOf<ReferenceDescription> references;
            try
            {
                references = await browser.BrowseAsync(currentNodeId, ct).ConfigureAwait(false);
            }
            catch (ServiceResultException sre) when (StatusCode.IsBad(sre.StatusCode))
            {
                // Permission denied or unbrowsable branch: log and continue
                continue;
            }

            NodeId? euRangeNodeId = null;
            NodeId? engUnitsNodeId = null;
            NodeId? enumStringsNodeId = null;

            foreach (ReferenceDescription r in references)
            {
                if (r.NodeId.IsNull)
                {
                    continue;
                }

                NodeId childNodeId = ExpandedNodeId.ToNodeId(r.NodeId, namespaces);
                string stableChildId = StableNodeIdentifier.ToStableId(childNodeId, namespaces);
                string browseName = r.BrowseName.Name ?? childNodeId.IdentifierAsString;

                // Check for property nodes on the current node
                if (r.NodeClass == NodeClass.Variable)
                {
                    if (browseName == BrowseNames.EURange)
                    {
                        euRangeNodeId = childNodeId;
                    }
                    else if (browseName == BrowseNames.EngineeringUnits)
                    {
                        engUnitsNodeId = childNodeId;
                    }
                    else if (browseName == BrowseNames.EnumStrings || browseName == BrowseNames.EnumValues)
                    {
                        enumStringsNodeId = childNodeId;
                    }
                }

                // Filter out Objects/Server internal folder if disabled
                if (!m_options.CrawlServerObject && childNodeId == ObjectIds.Server)
                {
                    continue;
                }

                string childPath = $"{currentPath}/{browseName}";
                bool isTerminalProperty = browseName is BrowseNames.EURange or BrowseNames.EngineeringUnits or BrowseNames.EnumStrings or BrowseNames.EnumValues;

                if (!isTerminalProperty && visited.Add(stableChildId))
                {
                    discoveredNodes.Add(new DiscoveredNode(childNodeId, childPath, r.NodeClass));

                    // Enqueue containers and variables to discover child elements and properties
                    if (r.NodeClass is NodeClass.Object or NodeClass.View or NodeClass.Variable)
                    {
                        queue.Enqueue((childNodeId, childPath, currentDepth + 1));
                    }
                }
            }

            if (euRangeNodeId != null || engUnitsNodeId != null || enumStringsNodeId != null)
            {
                string parentStableId = StableNodeIdentifier.ToStableId(currentNodeId, namespaces);
                propertyLinks[parentStableId] = (euRangeNodeId, engUnitsNodeId, enumStringsNodeId);
            }
        }

        // Phase 2: Batch read intrinsic attributes
        IReadOnlyList<CatalogEntry> catalogEntries = await m_attributeReader
            .ReadAttributesAsync(discoveredNodes, namespaces, ct)
            .ConfigureAwait(false);

        // Phase 3: Batch read property values (EURange, EngineeringUnits) to enrich catalog entries
        await EnrichPropertiesAsync(catalogEntries, propertyLinks, namespaces, ct).ConfigureAwait(false);

        stopwatch.Stop();

        // Calculate statistics
        int varCount = 0, objCount = 0, methodCount = 0;
        foreach (CatalogEntry e in catalogEntries)
        {
            switch (e.NodeClass)
            {
                case "Variable":
                case "VariableType":
                    varCount++;
                    break;
                case "Object":
                    objCount++;
                    break;
                case "Method":
                    methodCount++;
                    break;
            }
        }

        var stats = new CrawlStatistics(
            TotalNodes: catalogEntries.Count,
            TotalVariables: varCount,
            TotalObjects: objCount,
            TotalMethods: methodCount,
            ContinuationPointsHandled: continuationPointsHandled,
            MaxDepthReached: maxDepthReached,
            DurationMilliseconds: stopwatch.ElapsedMilliseconds);

        var metadata = new CatalogMetadata
        {
            EndpointUrl = endpointUrl ?? string.Empty,
            ServerName = serverName,
            ManufacturerName = manufacturer,
            SoftwareVersion = softwareVersion,
            IdentityType = identityType ?? m_options.IdentityType ?? "Anonymous",
            CrawledAtUtc = DateTime.UtcNow,
            Namespaces = namespaces.ToArray(),
            Statistics = stats
        };

        return new AtlasCatalog
        {
            Metadata = metadata,
            Entries = catalogEntries
        };
    }

    private async Task EnrichPropertiesAsync(
        IReadOnlyList<CatalogEntry> entries,
        Dictionary<string, (NodeId? EuRangeId, NodeId? EngUnitsId, NodeId? EnumStringsId)> propertyLinks,
        NamespaceTable namespaces,
        CancellationToken ct)
    {
        if (propertyLinks.Count == 0)
        {
            return;
        }

        var entryLookup = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var readValueIds = new List<ReadValueId>();
        var targetMap = new List<(CatalogEntry Entry, string PropertyType)>();

        foreach (var (parentStableId, (euRangeId, engUnitsId, enumStringsId)) in propertyLinks)
        {
            if (!entryLookup.TryGetValue(parentStableId, out CatalogEntry? parentEntry))
            {
                continue;
            }

            if (euRangeId.HasValue)
            {
                readValueIds.Add(new ReadValueId { NodeId = euRangeId.Value, AttributeId = Attributes.Value });
                targetMap.Add((parentEntry, "EURange"));
            }

            if (engUnitsId.HasValue)
            {
                readValueIds.Add(new ReadValueId { NodeId = engUnitsId.Value, AttributeId = Attributes.Value });
                targetMap.Add((parentEntry, "EngineeringUnits"));
            }

            if (enumStringsId.HasValue)
            {
                readValueIds.Add(new ReadValueId { NodeId = enumStringsId.Value, AttributeId = Attributes.Value });
                targetMap.Add((parentEntry, "EnumStrings"));
            }
        }

        if (readValueIds.Count == 0)
        {
            return;
        }

        // Read all property values in one batched call
        try
        {
            ReadResponse response = await m_session.ReadAsync(
                requestHeader: null,
                maxAge: 0,
                timestampsToReturn: TimestampsToReturn.Neither,
                nodesToRead: readValueIds.ToArray(),
                ct: ct).ConfigureAwait(false);

            for (int i = 0; i < response.Results.Count && i < targetMap.Count; i++)
            {
                DataValue result = response.Results[i];
                if (StatusCode.IsBad(result.StatusCode))
                {
                    continue;
                }

                var (entry, propType) = targetMap[i];
                if (propType == "EURange")
                {
                    if (result.WrappedValue.TryGetValue(out ExtensionObject ext) && ext.TryGetValue(out Range? range, m_session.MessageContext) && range != null)
                    {
                        entry.Range = new EURangeInfo(range.Low, range.High);
                    }
                    else if (result.WrappedValue.AsBoxedObject(Variant.BoxingBehavior.Legacy) is Range directRange)
                    {
                        entry.Range = new EURangeInfo(directRange.Low, directRange.High);
                    }
                }
                else if (propType == "EngineeringUnits")
                {
                    if (result.WrappedValue.TryGetValue(out ExtensionObject ext) && ext.TryGetValue(out EUInformation? eu, m_session.MessageContext) && eu != null)
                    {
                        entry.Unit = eu.DisplayName.Text;
                    }
                    else if (result.WrappedValue.AsBoxedObject(Variant.BoxingBehavior.Legacy) is EUInformation directEu)
                    {
                        entry.Unit = directEu.DisplayName.Text;
                    }
                }
                else if (propType == "EnumStrings")
                {
                    if (result.WrappedValue.TryGetValue(out ArrayOf<LocalizedText> ltArr))
                    {
                        var list = new List<string>(ltArr.Count);
                        foreach (LocalizedText lt in ltArr)
                        {
                            list.Add(lt.Text ?? string.Empty);
                        }
                        entry.EnumStrings = list;
                    }
                }
            }
        }
        catch
        {
            // Failure to read properties is non-fatal; entries retain null properties
        }
    }

    private async Task<(string? Manufacturer, string? Version, string? ServerName)> TryReadServerBuildInfoAsync(
        CancellationToken ct)
    {
        var readValueIds = new ReadValueId[]
        {
            new() { NodeId = VariableIds.Server_ServerStatus_BuildInfo_ManufacturerName, AttributeId = Attributes.Value },
            new() { NodeId = VariableIds.Server_ServerStatus_BuildInfo_SoftwareVersion, AttributeId = Attributes.Value },
            new() { NodeId = VariableIds.Server_ServerStatus_BuildInfo_ProductName, AttributeId = Attributes.Value }
        };

        ReadResponse response = await m_session.ReadAsync(
            requestHeader: null,
            maxAge: 0,
            timestampsToReturn: TimestampsToReturn.Neither,
            nodesToRead: readValueIds,
            ct: ct).ConfigureAwait(false);

        string? manufacturer = null;
        string? version = null;
        string? productName = null;

        if (response.Results.Count > 0 && !StatusCode.IsBad(response.Results[0].StatusCode))
        {
            if (response.Results[0].WrappedValue.TryGetValue(out string m))
            {
                manufacturer = m;
            }
        }

        if (response.Results.Count > 1 && !StatusCode.IsBad(response.Results[1].StatusCode))
        {
            if (response.Results[1].WrappedValue.TryGetValue(out string v))
            {
                version = v;
            }
        }

        if (response.Results.Count > 2 && !StatusCode.IsBad(response.Results[2].StatusCode))
        {
            if (response.Results[2].WrappedValue.TryGetValue(out string p))
            {
                productName = p;
            }
        }

        return (manufacturer, version, productName);
    }
}
