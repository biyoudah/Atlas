using System.Collections;
using System.Reflection;
using Opc.Ua;
using Opc.Ua.Atlas.Models;

namespace Opc.Ua.Atlas.Decoding;

/// <summary>
/// Resolves and flattens complex OPC UA ExtensionObjects and structured DataTypes
/// into readable, dot-separated hierarchical member paths (e.g. "Axis[0].ActualPosition").
/// Supports both live value unpacking and static DataTypeDefinition schema reflection.
/// </summary>
public static class TypeDecoder
{
    private static readonly HashSet<string> s_ignoredPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "TypeId",
        "BinaryEncodingId",
        "XmlEncodingId",
        "JsonEncodingId"
    };

    /// <summary>
    /// Recursively flattens an ExtensionObject, IEncodeable, array, or complex structure into dotted paths.
    /// </summary>
    public static IReadOnlyList<FlattenedMember> Flatten(
        object? complexValue,
        IServiceMessageContext? messageContext = null,
        string prefix = "",
        int maxDepth = 10)
    {
        var results = new List<FlattenedMember>();
        FlattenInternal(complexValue, messageContext, prefix, results, currentDepth: 0, maxDepth);
        return results;
    }

    private static void FlattenInternal(
        object? value,
        IServiceMessageContext? messageContext,
        string prefix,
        List<FlattenedMember> results,
        int currentDepth,
        int maxDepth)
    {
        if (value == null || currentDepth >= maxDepth)
        {
            return;
        }

        // Unpack Variant
        if (value is Variant variant)
        {
            if (variant.IsNull)
            {
                return;
            }

            value = variant.AsBoxedObject(Variant.BoxingBehavior.Legacy);
            if (value == null)
            {
                return;
            }
        }

        // Unpack ExtensionObject
        if (value is ExtensionObject ext)
        {
            if (ext.TryGetValue(out IEncodeable? encodeable, messageContext) && encodeable != null)
            {
                value = encodeable;
            }
            else if (ext.TryGetAsJson(out string? json) && json != null)
            {
                value = json;
            }
            else if (ext.TryGetAsBinary(out ByteString binary) && !binary.IsNull)
            {
                value = binary.ToString();
            }
            else
            {
                return;
            }
        }

        // Handle collections / arrays (e.g. Axis[0], Axis[1])
        if (value is not string && value is IEnumerable enumerable)
        {
            int index = 0;
            foreach (object? item in enumerable)
            {
                string indexedPrefix = string.IsNullOrEmpty(prefix) ? $"[{index}]" : $"{prefix}[{index}]";
                FlattenInternal(item, messageContext, indexedPrefix, results, currentDepth + 1, maxDepth);
                index++;
            }
            return;
        }

        // Handle primitive / scalar leaves
        Type type = value.GetType();
        if (IsScalarType(type))
        {
            string leafPath = string.IsNullOrEmpty(prefix) ? "Value" : prefix;
            results.Add(new FlattenedMember(leafPath, GetFriendlyTypeName(type), value));
            return;
        }

        // Handle structured IEncodeable or complex object via reflection
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (PropertyInfo prop in properties)
        {
            if (s_ignoredPropertyNames.Contains(prop.Name))
            {
                continue;
            }

            if (!prop.CanRead || !prop.CanWrite)
            {
                continue;
            }

            object? propValue;
            try
            {
                propValue = prop.GetValue(value);
            }
            catch
            {
                continue;
            }

            string fieldPath = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";

            if (propValue == null)
            {
                results.Add(new FlattenedMember(fieldPath, GetFriendlyTypeName(prop.PropertyType), null));
            }
            else if (IsScalarType(propValue.GetType()))
            {
                results.Add(new FlattenedMember(fieldPath, GetFriendlyTypeName(propValue.GetType()), propValue));
            }
            else
            {
                FlattenInternal(propValue, messageContext, fieldPath, results, currentDepth + 1, maxDepth);
            }
        }
    }

    /// <summary>
    /// Flattens a static StructureDefinition (retrieved from DataTypeDefinition attribute)
    /// into schema member paths without needing a live runtime instance.
    /// </summary>
    public static IReadOnlyList<FlattenedMember> FlattenFromDefinition(
        StructureDefinition definition,
        NamespaceTable? namespaces = null,
        string prefix = "")
    {
        var results = new List<FlattenedMember>();
        if (definition.Fields.Count == 0)
        {
            return results;
        }

        foreach (StructureField field in definition.Fields)
        {
            string fieldName = field.Name ?? "Field";
            string fieldPath = string.IsNullOrEmpty(prefix) ? fieldName : $"{prefix}.{fieldName}";
            string typeName = namespaces != null
                ? StableNodeIdentifier.ToStableId(field.DataType, namespaces)
                : field.DataType.IdentifierAsString;

            string? desc = field.Description.Text;
            results.Add(new FlattenedMember(fieldPath, typeName, Value: null, Description: desc));
        }

        return results;
    }

    /// <summary>
    /// Reads the DataTypeDefinition attribute from a DataType node on the server.
    /// Used in OPC UA 1.04+ (Siemens S7-1500, Beckhoff) to resolve complex types dynamically.
    /// </summary>
    public static async Task<StructureDefinition?> ReadDataTypeDefinitionAsync(
        ISessionClient session,
        NodeId dataTypeId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (dataTypeId.IsNull)
        {
            return null;
        }

        try
        {
            ReadValueId nodeToRead = new()
            {
                NodeId = dataTypeId,
                AttributeId = Attributes.DataTypeDefinition
            };

            ReadResponse response = await session.ReadAsync(
                null,
                0,
                TimestampsToReturn.Neither,
                [nodeToRead],
                ct).ConfigureAwait(false);

            if (response.Results.Count > 0 && !StatusCode.IsBad(response.Results[0].StatusCode))
            {
                Variant val = response.Results[0].WrappedValue;
                if (val.TryGetValue(out ExtensionObject ext) &&
                    ext.TryGetValue(out StructureDefinition? structureDef, session.MessageContext))
                {
                    return structureDef;
                }

                if (val.AsBoxedObject(Variant.BoxingBehavior.Legacy) is StructureDefinition directDef)
                {
                    return directDef;
                }
            }
        }
        catch
        {
            // Fallback gracefully if server does not expose DataTypeDefinition
        }

        return null;
    }

    private static bool IsScalarType(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(DateTimeUtc) ||
               type == typeof(Guid) ||
               type == typeof(Uuid) ||
               type == typeof(NodeId) ||
               type == typeof(ExpandedNodeId) ||
               type == typeof(QualifiedName) ||
               type == typeof(LocalizedText) ||
               type == typeof(StatusCode);
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (type == typeof(double) || type == typeof(float)) return "Double";
        if (type == typeof(int) || type == typeof(short) || type == typeof(sbyte)) return "Int32";
        if (type == typeof(uint) || type == typeof(ushort) || type == typeof(byte)) return "UInt32";
        if (type == typeof(long)) return "Int64";
        if (type == typeof(ulong)) return "UInt64";
        if (type == typeof(bool)) return "Boolean";
        if (type == typeof(string)) return "String";
        if (type == typeof(DateTime) || type == typeof(DateTimeUtc)) return "DateTime";
        if (type == typeof(LocalizedText)) return "LocalizedText";
        if (type == typeof(QualifiedName)) return "QualifiedName";
        if (type == typeof(NodeId)) return "NodeId";
        return type.Name;
    }
}
