using NUnit.Framework;
using Atlas.Crawler;
using Atlas.Models;

namespace Atlas.Tests;

[TestFixture]
public class IdentityDifferTests
{
    [Test]
    public void Diff_IdentifiesNewlyVisibleNodesAndElevations()
    {
        // Anonymous base catalog: only sees public Root and Var1 (read-only)
        var baseCatalog = new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://s7-1500.local:4840",
                IdentityType = "Anonymous",
                Namespaces = ["http://opcfoundation.org/UA/", "urn:siemens:s7"]
            },
            Entries =
            [
                new CatalogEntry
                {
                    Id = "nsu=urn:siemens:s7;s=Root",
                    Path = "Objects/Root",
                    NodeClass = "Object"
                },
                new CatalogEntry
                {
                    Id = "nsu=urn:siemens:s7;s=\"DB1\".\"Var1\"",
                    Path = "Objects/Root/DB1/Var1",
                    NodeClass = "Variable",
                    UserAccess = new NodeAccessInfo(Read: true, Write: false)
                }
            ]
        };

        // Authenticated admin catalog: sees Root, Var1 (with write permission), and privileged DB99.SecretVar
        var targetCatalog = new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://s7-1500.local:4840",
                IdentityType = "Admin",
                Namespaces = ["http://opcfoundation.org/UA/", "urn:siemens:s7"]
            },
            Entries =
            [
                new CatalogEntry
                {
                    Id = "nsu=urn:siemens:s7;s=Root",
                    Path = "Objects/Root",
                    NodeClass = "Object"
                },
                new CatalogEntry
                {
                    Id = "nsu=urn:siemens:s7;s=\"DB1\".\"Var1\"",
                    Path = "Objects/Root/DB1/Var1",
                    NodeClass = "Variable",
                    UserAccess = new NodeAccessInfo(Read: true, Write: true) // Elevated to write!
                },
                new CatalogEntry
                {
                    Id = "nsu=urn:siemens:s7;s=\"DB99\".\"SecretVar\"",
                    Path = "Objects/Root/DB99/SecretVar",
                    NodeClass = "Variable",
                    DataType = "Int32",
                    UserAccess = new NodeAccessInfo(Read: true, Write: true) // Newly visible!
                }
            ]
        };

        CatalogDiff diff = IdentityDiffer.Diff(baseCatalog, targetCatalog);

        Assert.That(diff.BaseIdentity, Is.EqualTo("Anonymous"));
        Assert.That(diff.TargetIdentity, Is.EqualTo("Admin"));
        Assert.That(diff.Summary.NewlyVisibleNodes, Is.EqualTo(1));
        Assert.That(diff.Summary.ElevatedPermissions, Is.EqualTo(1));
        Assert.That(diff.Summary.HiddenNodes, Is.EqualTo(0));
        Assert.That(diff.Summary.UnchangedNodes, Is.EqualTo(1));

        Assert.That(diff.NewlyVisibleEntries.Count, Is.EqualTo(1));
        Assert.That(diff.NewlyVisibleEntries[0].Id, Is.EqualTo("nsu=urn:siemens:s7;s=\"DB99\".\"SecretVar\""));

        Assert.That(diff.Elevations.Count, Is.EqualTo(1));
        PermissionElevation elevation = diff.Elevations[0];
        Assert.That(elevation.Id, Is.EqualTo("nsu=urn:siemens:s7;s=\"DB1\".\"Var1\""));
        Assert.That(elevation.ReadGranted, Is.False);
        Assert.That(elevation.WriteGranted, Is.True);

        string md = diff.ToMarkdown();
        Assert.That(md, Does.Contain("Newly Visible Nodes"));
        Assert.That(md, Does.Contain("SecretVar"));
        Assert.That(md, Does.Contain("[Write]"));

        string json = diff.ToJson();
        Assert.That(json, Does.Contain("newlyVisibleNodes"));
        Assert.That(json, Does.Contain("SecretVar"));
    }

    [Test]
    public void Diff_IdentifiesHiddenNodesWhenRolesAreRestricted()
    {
        var baseCatalog = new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://test:4840",
                IdentityType = "FullAccess",
                Namespaces = ["http://opcfoundation.org/UA/"]
            },
            Entries =
            [
                new CatalogEntry { Id = "i=1001", Path = "Objects/Node1" },
                new CatalogEntry { Id = "i=1002", Path = "Objects/Node2" }
            ]
        };

        var targetCatalog = new AtlasCatalog
        {
            Metadata = new CatalogMetadata
            {
                EndpointUrl = "opc.tcp://test:4840",
                IdentityType = "Guest",
                Namespaces = ["http://opcfoundation.org/UA/"]
            },
            Entries =
            [
                new CatalogEntry { Id = "i=1001", Path = "Objects/Node1" }
            ]
        };

        CatalogDiff diff = IdentityDiffer.Diff(baseCatalog, targetCatalog);

        Assert.That(diff.Summary.HiddenNodes, Is.EqualTo(1));
        Assert.That(diff.HiddenEntries.Count, Is.EqualTo(1));
        Assert.That(diff.HiddenEntries[0].Id, Is.EqualTo("i=1002"));
    }
}
