using Opc.Ua.Export;
using Opc.Ua.Atlas.Models;
using ExportLocalizedText = Opc.Ua.Export.LocalizedText;

namespace Opc.Ua.Atlas.Exporters;

/// <summary>
/// Exports the discovery catalog into an OPC UA Part 6 standard NodeSet2 XML document.
/// Enables importing the discovered address space into simulation tools, DCS/SCADA systems,
/// and OPC UA server mock environments.
/// </summary>
public static class NodeSet2XmlExporter
{
    private static readonly Dictionary<string, string> s_builtInTypeNodeIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Boolean"] = "i=1",
        ["SByte"] = "i=2",
        ["Byte"] = "i=3",
        ["Int16"] = "i=4",
        ["UInt16"] = "i=5",
        ["Int32"] = "i=6",
        ["UInt32"] = "i=7",
        ["Int64"] = "i=8",
        ["UInt64"] = "i=9",
        ["Float"] = "i=10",
        ["Double"] = "i=11",
        ["String"] = "i=12",
        ["DateTime"] = "i=13",
        ["Guid"] = "i=14",
        ["ByteString"] = "i=15",
        ["XmlElement"] = "i=16",
        ["NodeId"] = "i=17",
        ["ExpandedNodeId"] = "i=18",
        ["StatusCode"] = "i=19",
        ["QualifiedName"] = "i=20",
        ["LocalizedText"] = "i=21",
        ["Structure"] = "i=22",
        ["Number"] = "i=26",
        ["Integer"] = "i=27",
        ["UInteger"] = "i=28",
        ["Enumeration"] = "i=29",
        ["BaseDataType"] = "i=24",
    };

    /// <summary>
    /// Converts an <see cref="AtlasCatalog"/> into an OPC UA <see cref="UANodeSet"/> instance.
    /// </summary>
    public static UANodeSet ToNodeSet(AtlasCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var nsTable = new NamespaceTable();
        if (catalog.Metadata.Namespaces != null)
        {
            foreach (string uri in catalog.Metadata.Namespaces)
            {
                if (!string.Equals(uri, Namespaces.OpcUa, StringComparison.OrdinalIgnoreCase) &&
                    nsTable.GetIndex(uri) < 0)
                {
                    nsTable.Append(uri);
                }
            }
        }

        // Pre-scan entries to ensure any stable namespace URIs referenced in IDs are known
        foreach (CatalogEntry entry in catalog.Entries)
        {
            StableNodeIdentifier.ToNodeId(entry.Id, nsTable);
        }

        var nodeSet = new UANodeSet
        {
            LastModified = catalog.Metadata.CrawledAtUtc,
            LastModifiedSpecified = true,
        };

        // NodeSet2 XML excludes index 0 (standard OPC UA namespace) from NamespaceUris element
        string[] allUris = nsTable.ToArray();
        if (allUris.Length > 1)
        {
            nodeSet.NamespaceUris = allUris.Skip(1).ToArray();
        }

        var nodes = new List<UANode>(catalog.Entries.Count);

        foreach (CatalogEntry entry in catalog.Entries)
        {
            NodeId nodeId = StableNodeIdentifier.ToNodeId(entry.Id, nsTable);
            string nodeIdStr = nodeId.ToString();
            string browseName = entry.BrowseName ?? nodeId.IdentifierAsString;
            if (nodeId.NamespaceIndex > 0 && !browseName.Contains(':'))
            {
                browseName = $"{nodeId.NamespaceIndex}:{browseName}";
            }

            ExportLocalizedText[] displayName = [new ExportLocalizedText { Value = entry.DisplayName ?? entry.BrowseName ?? nodeId.IdentifierAsString }];
            ExportLocalizedText[]? description = !string.IsNullOrEmpty(entry.Description)
                ? [new ExportLocalizedText { Value = entry.Description }]
                : null;

            if (string.Equals(entry.NodeClass, "Object", StringComparison.OrdinalIgnoreCase))
            {
                nodes.Add(new UAObject
                {
                    NodeId = nodeIdStr,
                    BrowseName = browseName,
                    DisplayName = displayName,
                    Description = description,
                    References =
                    [
                        new Reference
                        {
                            ReferenceType = "i=40", // HasTypeDefinition
                            Value = "i=58" // BaseObjectType
                        }
                    ]
                });
            }
            else if (string.Equals(entry.NodeClass, "Method", StringComparison.OrdinalIgnoreCase))
            {
                nodes.Add(new UAMethod
                {
                    NodeId = nodeIdStr,
                    BrowseName = browseName,
                    DisplayName = displayName,
                    Description = description,
                    Executable = true,
                    UserExecutable = true
                });
            }
            else
            {
                // Default to UAVariable
                string dataTypeNodeId = ResolveDataTypeNodeId(entry.DataType, nsTable);
                byte accessLevel = 0;
                if (entry.Access?.Read == true) accessLevel |= AccessLevels.CurrentRead;
                if (entry.Access?.Write == true) accessLevel |= AccessLevels.CurrentWrite;
                if (accessLevel == 0) accessLevel = AccessLevels.CurrentRead;

                var variable = new UAVariable
                {
                    NodeId = nodeIdStr,
                    BrowseName = browseName,
                    DisplayName = displayName,
                    Description = description,
                    DataType = dataTypeNodeId,
                    ValueRank = entry.ValueRank ?? ValueRanks.Scalar,
                    AccessLevel = accessLevel,
                    References =
                    [
                        new Reference
                        {
                            ReferenceType = "i=40", // HasTypeDefinition
                            Value = "i=63" // BaseDataVariableType
                        }
                    ]
                };

                if (entry.UserAccess != null)
                {
                    byte userAccess = 0;
                    if (entry.UserAccess.Read) userAccess |= AccessLevels.CurrentRead;
                    if (entry.UserAccess.Write) userAccess |= AccessLevels.CurrentWrite;
                    variable.UserAccessLevel = userAccess;
                    variable.UserAccessLevelSpecified = true;
                }

                nodes.Add(variable);
            }
        }

        nodeSet.Items = nodes.ToArray();
        return nodeSet;
    }

    /// <summary>
    /// Exports the catalog to the given output stream in NodeSet2 XML format.
    /// </summary>
    public static void Export(AtlasCatalog catalog, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(stream);

        UANodeSet nodeSet = ToNodeSet(catalog);
        nodeSet.Write(stream);
    }

    /// <summary>
    /// Exports the catalog into a file at the specified path in NodeSet2 XML format.
    /// </summary>
    public static async Task ExportToFileAsync(AtlasCatalog catalog, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(filePath);

        using var ms = new MemoryStream();
        Export(catalog, ms);
        ms.Position = 0;

        using FileStream fs = File.Create(filePath);
        await ms.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    private static string ResolveDataTypeNodeId(string? dataType, NamespaceTable nsTable)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return "i=24"; // BaseDataType
        }

        if (s_builtInTypeNodeIds.TryGetValue(dataType, out string? builtInId))
        {
            return builtInId;
        }

        if (dataType.StartsWith("nsu=", StringComparison.Ordinal))
        {
            try
            {
                NodeId resolved = StableNodeIdentifier.ToNodeId(dataType, nsTable);
                return resolved.ToString();
            }
            catch
            {
                return "i=24";
            }
        }

        if (dataType.StartsWith("i=", StringComparison.Ordinal) || dataType.StartsWith("ns=", StringComparison.Ordinal))
        {
            return dataType;
        }

        return "i=24";
    }
}
