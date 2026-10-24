using Opc.Ua;
using Atlas.Models;
using Opc.Ua.Client;

namespace Atlas.Crawler;

/// <summary>
/// A node discovered during browse to be enriched with attributes.
/// </summary>
public record DiscoveredNode(
    NodeId NodeId,
    string Path,
    NodeClass NodeClass);

/// <summary>
/// Batches attribute read operations respecting server OperationLimits.MaxNodesPerRead,
/// handling missing/unsupported attributes gracefully per node.
/// </summary>
public class AttributeBatchReader
{
    private readonly ISessionClient m_session;
    private readonly uint m_maxNodesPerRead;

    public AttributeBatchReader(ISessionClient session, uint? maxNodesPerRead = null)
    {
        m_session = session ?? throw new ArgumentNullException(nameof(session));

        uint serverLimit = 0;
        if (session is ISession fullSession && fullSession.OperationLimits != null)
        {
            serverLimit = fullSession.OperationLimits.MaxNodesPerRead;
        }

        // Conservative fallback to 200 items per read call if server does not specify
        m_maxNodesPerRead = maxNodesPerRead ?? (serverLimit > 0 ? serverLimit : 200u);
    }

    /// <summary>
    /// Reads attributes for all discovered nodes in batches, returning populated CatalogEntries.
    /// </summary>
    public async Task<IReadOnlyList<CatalogEntry>> ReadAttributesAsync(
        IReadOnlyList<DiscoveredNode> nodes,
        NamespaceTable namespaces,
        CancellationToken ct = default)
    {
        var entries = new List<CatalogEntry>(nodes.Count);
        if (nodes.Count == 0)
        {
            return entries;
        }

        // Plan read descriptors
        var plannedNodes = new List<(DiscoveredNode Node, List<uint> AttributeIds)>();
        foreach (DiscoveredNode node in nodes)
        {
            var attrs = new List<uint>
            {
                Attributes.NodeClass,
                Attributes.BrowseName,
                Attributes.DisplayName,
                Attributes.Description
            };

            if (node.NodeClass is NodeClass.Variable or NodeClass.VariableType)
            {
                attrs.Add(Attributes.DataType);
                attrs.Add(Attributes.ValueRank);
                attrs.Add(Attributes.AccessLevel);
                attrs.Add(Attributes.UserAccessLevel);
                attrs.Add(Attributes.MinimumSamplingInterval);
                attrs.Add(Attributes.Historizing);
            }
            else if (node.NodeClass == NodeClass.Method)
            {
                attrs.Add(Attributes.Executable);
                attrs.Add(Attributes.UserExecutable);
            }

            plannedNodes.Add((node, attrs));
        }

        // Chunk requests so total ReadValueId count per call <= m_maxNodesPerRead
        int currentIndex = 0;
        while (currentIndex < plannedNodes.Count)
        {
            ct.ThrowIfCancellationRequested();

            var currentBatch = new List<(DiscoveredNode Node, List<uint> AttributeIds)>();
            int readValuesCount = 0;

            while (currentIndex < plannedNodes.Count)
            {
                var next = plannedNodes[currentIndex];
                if (readValuesCount > 0 && (readValuesCount + next.AttributeIds.Count) > m_maxNodesPerRead)
                {
                    break;
                }

                currentBatch.Add(next);
                readValuesCount += next.AttributeIds.Count;
                currentIndex++;
            }

            // Build ReadValueId list
            var readValueIds = new List<ReadValueId>(readValuesCount);
            foreach (var (node, attrs) in currentBatch)
            {
                foreach (uint attrId in attrs)
                {
                    readValueIds.Add(new ReadValueId
                    {
                        NodeId = node.NodeId,
                        AttributeId = attrId
                    });
                }
            }

            // Execute batched read
            ReadResponse response = await m_session.ReadAsync(
                requestHeader: null,
                maxAge: 0,
                timestampsToReturn: TimestampsToReturn.Neither,
                nodesToRead: readValueIds.ToArray(),
                ct: ct).ConfigureAwait(false);

            ArrayOf<DataValue> results = response.Results;

            // Unpack results matching back to nodes
            int resultIndex = 0;
            foreach (var (node, attrs) in currentBatch)
            {
                var attrMap = new Dictionary<uint, DataValue>();
                foreach (uint attrId in attrs)
                {
                    if (resultIndex < results.Count)
                    {
                        attrMap[attrId] = results[resultIndex++];
                    }
                }

                CatalogEntry entry = BuildEntry(node, attrMap, namespaces);
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static CatalogEntry BuildEntry(
        DiscoveredNode node,
        Dictionary<uint, DataValue> attrs,
        NamespaceTable namespaces)
    {
        string stableId = StableNodeIdentifier.ToStableId(node.NodeId, namespaces);

        // BrowseName
        string? browseName = null;
        if (attrs.TryGetValue(Attributes.BrowseName, out DataValue bnDv) && !StatusCode.IsBad(bnDv.StatusCode))
        {
            if (bnDv.WrappedValue.TryGetValue(out QualifiedName qn))
            {
                browseName = qn.Name;
            }
        }

        // DisplayName
        string? displayName = null;
        if (attrs.TryGetValue(Attributes.DisplayName, out DataValue dnDv) && !StatusCode.IsBad(dnDv.StatusCode))
        {
            if (dnDv.WrappedValue.TryGetValue(out LocalizedText lt))
            {
                displayName = lt.Text;
            }
        }

        // Description
        string? description = null;
        if (attrs.TryGetValue(Attributes.Description, out DataValue descDv) && !StatusCode.IsBad(descDv.StatusCode))
        {
            if (descDv.WrappedValue.TryGetValue(out LocalizedText ltDesc) && !string.IsNullOrWhiteSpace(ltDesc.Text))
            {
                description = ltDesc.Text;
            }
        }

        // NodeClass
        string nodeClassStr = node.NodeClass.ToString();
        if (attrs.TryGetValue(Attributes.NodeClass, out DataValue ncDv) && !StatusCode.IsBad(ncDv.StatusCode))
        {
            if (ncDv.WrappedValue.TryGetValue(out int ncInt))
            {
                nodeClassStr = ((NodeClass)ncInt).ToString();
            }
        }

        // Variable specific attributes
        string? dataTypeName = null;
        string? dataTypeIdStr = null;
        int? valueRank = null;
        NodeAccessInfo? access = null;
        NodeAccessInfo? userAccess = null;
        double? minSamplingInterval = null;
        bool? historizing = null;

        if (attrs.TryGetValue(Attributes.DataType, out DataValue dtDv) && !StatusCode.IsBad(dtDv.StatusCode))
        {
            if (dtDv.WrappedValue.TryGetValue(out NodeId dtId))
            {
                dataTypeIdStr = StableNodeIdentifier.ToStableId(dtId, namespaces);
                BuiltInType builtIn = TypeInfo.GetBuiltInType(dtId);
                dataTypeName = builtIn != BuiltInType.Null ? builtIn.ToString() : dataTypeIdStr;
            }
        }

        if (attrs.TryGetValue(Attributes.ValueRank, out DataValue vrDv) && !StatusCode.IsBad(vrDv.StatusCode))
        {
            if (vrDv.WrappedValue.TryGetValue(out int vr))
            {
                valueRank = vr;
            }
        }

        if (attrs.TryGetValue(Attributes.AccessLevel, out DataValue alDv) && !StatusCode.IsBad(alDv.StatusCode))
        {
            if (alDv.WrappedValue.TryGetValue(out byte al))
            {
                access = new NodeAccessInfo(
                    Read: (al & AccessLevels.CurrentRead) != 0,
                    Write: (al & AccessLevels.CurrentWrite) != 0);
            }
        }

        if (attrs.TryGetValue(Attributes.UserAccessLevel, out DataValue ualDv) && !StatusCode.IsBad(ualDv.StatusCode))
        {
            if (ualDv.WrappedValue.TryGetValue(out byte ual))
            {
                userAccess = new NodeAccessInfo(
                    Read: (ual & AccessLevels.CurrentRead) != 0,
                    Write: (ual & AccessLevels.CurrentWrite) != 0);
            }
        }

        if (attrs.TryGetValue(Attributes.MinimumSamplingInterval, out DataValue msiDv) && !StatusCode.IsBad(msiDv.StatusCode))
        {
            if (msiDv.WrappedValue.TryGetValue(out double msi))
            {
                minSamplingInterval = msi;
            }
        }

        if (attrs.TryGetValue(Attributes.Historizing, out DataValue histDv) && !StatusCode.IsBad(histDv.StatusCode))
        {
            if (histDv.WrappedValue.TryGetValue(out bool hist))
            {
                historizing = hist;
            }
        }

        // Method specific attributes
        if (attrs.TryGetValue(Attributes.Executable, out DataValue execDv) && !StatusCode.IsBad(execDv.StatusCode))
        {
            if (execDv.WrappedValue.TryGetValue(out bool exec))
            {
                access = new NodeAccessInfo(Read: false, Write: false, Executable: exec);
            }
        }

        if (attrs.TryGetValue(Attributes.UserExecutable, out DataValue uexecDv) && !StatusCode.IsBad(uexecDv.StatusCode))
        {
            if (uexecDv.WrappedValue.TryGetValue(out bool uexec))
            {
                userAccess = new NodeAccessInfo(Read: false, Write: false, Executable: uexec);
            }
        }

        return new CatalogEntry
        {
            Id = stableId,
            Path = node.Path,
            BrowseName = browseName ?? node.NodeId.IdentifierAsString,
            DisplayName = displayName ?? browseName ?? node.NodeId.IdentifierAsString,
            Description = description,
            NodeClass = nodeClassStr,
            DataType = dataTypeName,
            DataTypeId = dataTypeIdStr,
            ValueRank = valueRank,
            Access = access,
            UserAccess = userAccess,
            MinimumSamplingInterval = minSamplingInterval,
            Historizing = historizing
        };
    }
}
