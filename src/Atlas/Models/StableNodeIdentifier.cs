using System.Text;
using Opc.Ua;

namespace Atlas.Models;

/// <summary>
/// Utilities to generate and parse stable, portable OPC UA Node identifiers
/// based on Namespace URIs (OPC UA Part 6 §5.1.12 "nsu=" format) rather than
/// volatile namespace indices (ns=X) which can shift upon server reboot or PLC download.
/// </summary>
public static class StableNodeIdentifier
{
    private const string NsuPrefix = "nsu=";

    /// <summary>
    /// Converts a <see cref="NodeId"/> to a stable string identifier using the namespace URI.
    /// Example output: "nsu=http://vendor.example/plc/;s=DB10.Var_27"
    /// </summary>
    public static string ToStableId(NodeId nodeId, NamespaceTable? namespaces, bool alwaysIncludeUri = false)
    {
        if (nodeId.IsNull)
        {
            return "i=0";
        }

        string? namespaceUri = null;
        if (nodeId.NamespaceIndex > 0 && namespaces != null)
        {
            namespaceUri = namespaces.GetString(nodeId.NamespaceIndex);
        }
        else if (nodeId.NamespaceIndex == 0 && alwaysIncludeUri)
        {
            namespaceUri = Namespaces.OpcUa;
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceUri))
        {
            sb.Append(NsuPrefix).Append(CoreUtils.EscapeUri(namespaceUri)).Append(';');
        }
        else if (nodeId.NamespaceIndex > 0)
        {
            sb.Append("ns=").Append(nodeId.NamespaceIndex).Append(';');
        }

        sb.Append(PrefixFor(nodeId.IdType)).Append(nodeId.IdentifierAsString);
        return sb.ToString();
    }

    /// <summary>
    /// Converts an <see cref="ExpandedNodeId"/> to a stable string identifier.
    /// </summary>
    public static string ToStableId(ExpandedNodeId expandedNodeId, NamespaceTable? namespaces)
    {
        if (expandedNodeId.IsNull)
        {
            return "i=0";
        }

        string? uri = expandedNodeId.NamespaceUri;
        if (string.IsNullOrEmpty(uri) && expandedNodeId.NamespaceIndex > 0 && namespaces != null)
        {
            uri = namespaces.GetString(expandedNodeId.NamespaceIndex);
        }

        if (!string.IsNullOrEmpty(uri))
        {
            var sb = new StringBuilder(NsuPrefix).Append(CoreUtils.EscapeUri(uri)).Append(';');
            sb.Append(PrefixFor(expandedNodeId.IdType)).Append(expandedNodeId.IdentifierAsString);
            return sb.ToString();
        }

        if (namespaces != null)
        {
            NodeId localId = ExpandedNodeId.ToNodeId(expandedNodeId, namespaces);
            return ToStableId(localId, namespaces);
        }

        return expandedNodeId.ToString();
    }

    /// <summary>
    /// Parses a stable string identifier (either "nsu=...;s=..." or "ns=...;s=...")
    /// into a target <see cref="NodeId"/> resolved against the provided <paramref name="namespaces"/>.
    /// </summary>
    public static NodeId ToNodeId(string stableId, NamespaceTable namespaces)
    {
        ArgumentNullException.ThrowIfNull(stableId);
        ArgumentNullException.ThrowIfNull(namespaces);

        if (stableId == "i=0" || string.IsNullOrWhiteSpace(stableId))
        {
            return NodeId.Null;
        }

        if (stableId.StartsWith(NsuPrefix, StringComparison.Ordinal))
        {
            ExpandedNodeId expanded = ExpandedNodeId.Parse(stableId);
            return ExpandedNodeId.ToNodeId(expanded, namespaces, updateNamespaceTable: true);
        }

        return NodeId.Parse(stableId);
    }

    /// <summary>
    /// Extracts the namespace URI and the identifier part from a stable ID.
    /// </summary>
    public static bool TryExtractNamespaceUri(string stableId, out string? namespaceUri, out string identifier)
    {
        if (stableId != null && stableId.StartsWith(NsuPrefix, StringComparison.Ordinal))
        {
            int semicolon = stableId.IndexOf(';', NsuPrefix.Length);
            if (semicolon > 0)
            {
                string escapedUri = stableId.Substring(NsuPrefix.Length, semicolon - NsuPrefix.Length);
                namespaceUri = CoreUtils.UnescapeUri(escapedUri);
                identifier = stableId.Substring(semicolon + 1);
                return true;
            }
        }

        namespaceUri = null;
        identifier = stableId ?? string.Empty;
        return false;
    }

    private static string PrefixFor(IdType idType) => idType switch
    {
        IdType.Numeric => "i=",
        IdType.String => "s=",
        IdType.Guid => "g=",
        IdType.Opaque => "b=",
        _ => "i="
    };
}
