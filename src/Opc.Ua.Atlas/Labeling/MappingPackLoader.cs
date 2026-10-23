using System.Text.Json;

namespace Opc.Ua.Atlas.Labeling;

/// <summary>
/// Loads community and user-defined mapping packs from JSON files or embedded seed profiles.
/// </summary>
public static class MappingPackLoader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Loads a mapping pack from a specified JSON file path.
    /// </summary>
    public static UserMappingPack LoadFromFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Mapping pack file not found: {filePath}", filePath);
        }

        string json = File.ReadAllText(filePath);
        var pack = JsonSerializer.Deserialize<UserMappingPack>(json, s_jsonOptions);
        return pack ?? throw new InvalidOperationException($"Failed to deserialize mapping pack from {filePath}");
    }

    /// <summary>
    /// Loads a mapping pack from a file if it exists, or looks for it in the Packs/ folder.
    /// </summary>
    public static UserMappingPack? ResolvePack(string nameOrPath)
    {
        if (File.Exists(nameOrPath))
        {
            return LoadFromFile(nameOrPath);
        }

        string appDir = AppContext.BaseDirectory;
        string fileName = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameOrPath : $"{nameOrPath}.json";

        string[] candidateDirs =
        [
            Path.Combine(appDir, "Packs"),
            "Packs",
            Path.Combine("tools", "Opc.Ua.Atlas", "Packs"),
            Path.Combine(appDir, "..", "..", "..", "Packs")
        ];

        foreach (string dir in candidateDirs)
        {
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return LoadFromFile(candidate);
            }
        }

        return null;
    }
}
