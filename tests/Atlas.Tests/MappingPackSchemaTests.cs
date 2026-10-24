using System.IO;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;

namespace Atlas.Tests;

[TestFixture]
public class MappingPackSchemaTests
{
    private string m_schemaPath = null!;
    private string m_packsDir = null!;

    [SetUp]
    public void SetUp()
    {
        // Traverse up to find repo root
        string current = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(current, "Atlas.slnx")) && Directory.GetParent(current) != null)
        {
            current = Directory.GetParent(current)!.FullName;
        }

        m_schemaPath = Path.Combine(current, "schema", "mapping-pack.schema.json");
        m_packsDir = Path.Combine(current, "src", "Atlas", "Packs");
    }

    [Test]
    public void SchemaFile_ExistsAndIsNonEmpty()
    {
        Assert.That(File.Exists(m_schemaPath), Is.True, $"Schema not found at {m_schemaPath}");
        var schemaText = File.ReadAllText(m_schemaPath);
        Assert.That(schemaText.Length, Is.GreaterThan(100));
    }

    [Test]
    public void AllBuiltInPacks_ValidateAgainstMappingPackSchema()
    {
        Assert.That(File.Exists(m_schemaPath), Is.True);
        var schemaText = File.ReadAllText(m_schemaPath);
        var schema = JsonSchema.FromText(schemaText);

        var packFiles = Directory.GetFiles(m_packsDir, "*.json");
        Assert.That(packFiles.Length, Is.GreaterThanOrEqualTo(3), "Expected at least 3 built-in packs");

        foreach (var packFile in packFiles)
        {
            var jsonContent = File.ReadAllText(packFile);
            var jsonNode = JsonNode.Parse(jsonContent);
            Assert.That(jsonNode, Is.Not.Null, $"Failed to parse JSON for {Path.GetFileName(packFile)}");

            var result = schema.Evaluate(jsonNode, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.That(result.IsValid, Is.True,
                $"Pack '{Path.GetFileName(packFile)}' failed schema validation. Errors: {string.Join("; ", result.Details.Where(d => d.HasErrors).Select(d => $"{d.InstanceLocation}: {d.Errors?.Values.FirstOrDefault()}"))}");
        }
    }
}
