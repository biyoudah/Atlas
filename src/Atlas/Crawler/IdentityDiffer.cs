using Atlas.Models;

namespace Atlas.Crawler;

/// <summary>
/// Compares two discovery catalogs collected under different user identities (e.g. Anonymous vs Authenticated)
/// to detect paywalled, hidden, or privileged tags on industrial PLCs.
/// </summary>
public static class IdentityDiffer
{
    /// <summary>
    /// Computes the delta between a base catalog and a target catalog.
    /// </summary>
    /// <param name="baseCatalog">Baseline catalog (e.g. anonymous crawl).</param>
    /// <param name="targetCatalog">Target catalog (e.g. operator or admin crawl).</param>
    /// <returns>A structured diff identifying newly visible nodes and permission elevations.</returns>
    public static CatalogDiff Diff(AtlasCatalog baseCatalog, AtlasCatalog targetCatalog)
    {
        ArgumentNullException.ThrowIfNull(baseCatalog);
        ArgumentNullException.ThrowIfNull(targetCatalog);

        var baseMap = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (CatalogEntry entry in baseCatalog.Entries)
        {
            baseMap[entry.Id] = entry;
        }

        var targetMap = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (CatalogEntry entry in targetCatalog.Entries)
        {
            targetMap[entry.Id] = entry;
        }

        var newlyVisible = new List<CatalogEntry>();
        var hidden = new List<CatalogEntry>();
        var elevations = new List<PermissionElevation>();
        int unchangedCount = 0;

        foreach (CatalogEntry targetEntry in targetCatalog.Entries)
        {
            if (!baseMap.TryGetValue(targetEntry.Id, out CatalogEntry? baseEntry))
            {
                newlyVisible.Add(targetEntry);
                continue;
            }

            // Node is in both; check permissions
            bool readGranted = (targetEntry.UserAccess?.Read == true) && (baseEntry.UserAccess?.Read != true);
            bool writeGranted = (targetEntry.UserAccess?.Write == true) && (baseEntry.UserAccess?.Write != true);

            if (readGranted || writeGranted)
            {
                elevations.Add(new PermissionElevation(
                    Id: targetEntry.Id,
                    Path: targetEntry.Path,
                    BaseUserAccess: baseEntry.UserAccess,
                    TargetUserAccess: targetEntry.UserAccess,
                    ReadGranted: readGranted,
                    WriteGranted: writeGranted));
            }
            else
            {
                unchangedCount++;
            }
        }

        foreach (CatalogEntry baseEntry in baseCatalog.Entries)
        {
            if (!targetMap.ContainsKey(baseEntry.Id))
            {
                hidden.Add(baseEntry);
            }
        }

        return new CatalogDiff
        {
            BaseIdentity = baseCatalog.Metadata.IdentityType ?? "Base",
            TargetIdentity = targetCatalog.Metadata.IdentityType ?? "Target",
            Summary = new DiffSummary(
                NewlyVisibleNodes: newlyVisible.Count,
                HiddenNodes: hidden.Count,
                ElevatedPermissions: elevations.Count,
                UnchangedNodes: unchangedCount),
            NewlyVisibleEntries = newlyVisible,
            HiddenEntries = hidden,
            Elevations = elevations
        };
    }
}
