using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace MakeMagnifierGreatAgain.Server;

/// <summary>
/// Builds the magnifier's front-slot filter from the fully merged item database.
/// This intentionally runs at PostLoad so vanilla items and server-mod items are
/// evaluated by the same rules.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 50_000)]
public sealed class MakeMagnifierGreatAgainCompatibilityMod(
    ISptLogger<MakeMagnifierGreatAgainCompatibilityMod> logger,
    TemplateTable templateTable
) : IOnLoad
{
    private const string G33TemplateId = "31d49a6fcf9e8c1864d7e401";
    private const string EpicHighG33TemplateId = "31d49a6fcf9e8c1864d7e420";
    private const string SkeetIrTemplateId = "31d49a6fcf9e8c1864d7e410";
    private const string VanillaHhsTemplateId = "5c07dd120db834001c39092d";
    private const string FrontScopeSlotName = "mod_scope";

    // Stable EFT item categories, not item whitelist entries.
    private const string CollimatorCategoryId = "55818ad54bdc2ddc698b4569";
    private const string CompactCollimatorCategoryId = "55818acf4bdc2dde698b456b";
    private const string MountCategoryId = "55818b224bdc2dde698b456f";
    private const string NightVisionCategoryId = "5a2c3a9486f774688b05e574";
    private const string ThermalVisionCategoryId = "5d21f59b6dbe99052b54ef83";

    // Native sights that mount directly to a standard Picatinny/Weaver rail.
    // A mod_scope filter accepting any of these is treated as a rail-facing
    // slot. This avoids confusing optic-specific adapter slots with rails.
    private static readonly MongoId[] DirectRailAnchorIds =
    [
        "58491f3324597764bc48fa02", // EOTech XPS3-0
        "59f9d81586f7744c7506ee62", // Vortex Razor AMG UH-1
        "60a23797a37c940de7062d02", // SIG Sauer ROMEO8T
    ];

    private static readonly HashSet<MongoId> ExplicitlyExcludedMountIds =
    [
        "5649a2464bdc2d91118b45a8", // NcSTAR MPR45 45-degree backup mount
    ];

    private const double OnePowerTolerance = 0.001;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MongoId g33Id = G33TemplateId;
        if (!templateTable.Items.TryGetValue(g33Id, out var g33Template))
        {
            throw new InvalidOperationException(
                $"MakeMagnifierGreatAgain: G33 template {G33TemplateId} was not registered."
            );
        }

        var targetFilter = g33Template
            .Properties?.Slots?.FirstOrDefault(slot =>
                string.Equals(slot.Name, FrontScopeSlotName, StringComparison.Ordinal)
            )
            ?.Properties?.Filters?.FirstOrDefault();

        if (targetFilter == null)
        {
            throw new InvalidOperationException(
                "MakeMagnifierGreatAgain: the G33 mod_scope filter was not found."
            );
        }

        MongoId epicHighG33Id = EpicHighG33TemplateId;
        if (!templateTable.Items.TryGetValue(epicHighG33Id, out var epicHighG33Template))
        {
            throw new InvalidOperationException(
                $"MakeMagnifierGreatAgain: EpicAIO high G33 clone {EpicHighG33TemplateId} was not registered."
            );
        }

        var epicHighTargetFilter = epicHighG33Template
            .Properties?.Slots?.FirstOrDefault(slot =>
                string.Equals(slot.Name, FrontScopeSlotName, StringComparison.Ordinal)
            )
            ?.Properties?.Filters?.FirstOrDefault();

        if (epicHighTargetFilter == null)
        {
            throw new InvalidOperationException(
                "MakeMagnifierGreatAgain: the EpicAIO high G33 clone mod_scope filter was not found."
            );
        }

        MongoId skeetIrId = SkeetIrTemplateId;
        templateTable.Items.TryGetValue(skeetIrId, out var skeetIrTemplate);
        var skeetIrTargetFilter = skeetIrTemplate
            ?.Properties?.Slots?.FirstOrDefault(slot =>
                string.Equals(slot.Name, FrontScopeSlotName, StringComparison.Ordinal)
            )
            ?.Properties?.Filters?.FirstOrDefault();

        if (skeetIrTemplate != null && skeetIrTargetFilter == null)
        {
            logger.Warning(
                "MakeMagnifierGreatAgain: Skeet-IR is registered, but its mod_scope filter was not found."
            );
        }

        var onePowerCandidates = templateTable.Items.Values
            .Where(IsPureOnePowerOptic)
            .Select(item => item.Id)
            .ToHashSet();

        MongoId mountCategoryId = MountCategoryId;
        var trustedRiserIds = LoadTrustedRiserIds(logger);
        var directRailAnchors = DirectRailAnchorIds
            .Where(templateTable.Items.ContainsKey)
            .ToHashSet();

        // Discover actual rail-facing slots from the merged database. Adapter
        // slots (RMR plates, ROMEO4 bases, etc.) do not accept these anchors and
        // are therefore not mistaken for a standard rail. Exclude the G33's own
        // generated slot so this discovery cannot become self-referential.
        var sightRailFilters = templateTable.Items.Values
            .Where(item =>
                !item.Id.Equals(g33Id)
                && !item.Id.Equals(epicHighG33Id))
            .SelectMany(GetScopeFilters)
            .Where(filter => filter.Filter?.Overlaps(directRailAnchors) == true)
            .ToList();

        var railAcceptedItems = sightRailFilters
            .SelectMany(filter => filter.Filter ?? [])
            .ToHashSet();

        var onePowerOptics = onePowerCandidates
            .Where(railAcceptedItems.Contains)
            .ToHashSet();

        var railMounts = railAcceptedItems
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && item.Type == "Item"
                && item.Parent.Equals(mountCategoryId)
                && !ExplicitlyExcludedMountIds.Contains(id)
                && (
                    trustedRiserIds.Contains(id.ToString())
                    || (
                        HasOnePowerDescendant(
                            id,
                            templateTable,
                            onePowerCandidates,
                            new HashSet<MongoId>()
                        )
                        && !HasMagnifiedDescendant(
                            id,
                            templateTable,
                            new HashSet<MongoId>()
                        )
                    )
                )
            )
            .ToHashSet();

        var g33Discovered = onePowerOptics
            .Concat(railMounts)
            .Where(id =>
                !id.Equals(g33Id)
                && !id.Equals(epicHighG33Id))
            .ToHashSet();

        targetFilter.Filter ??= [];
        targetFilter.Filter.Clear();
        targetFilter.Filter.UnionWith(g33Discovered);
        epicHighTargetFilter.Filter ??= [];
        epicHighTargetFilter.Filter.Clear();
        epicHighTargetFilter.Filter.UnionWith(g33Discovered);

        var excludedVisionDeviceIds = templateTable.Items.Values
            .Where(item =>
                item.Type == "Item"
                && IsNightVisionOrThermal(item, templateTable))
            .Select(item => item.Id)
            .ToHashSet();

        // Skeet is the rail-facing outer clip-on. Its direct child can be any
        // ordinary rail optic (including magnified scopes) or a rail mount
        // that eventually carries one. NV and thermal devices are rejected at
        // the Skeet root, so they also cannot enter through a nested mount.
        var skeetSightIds = railAcceptedItems
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && IsSight(item)
                && !excludedVisionDeviceIds.Contains(id)
            )
            .ToHashSet();

        var skeetMountIds = railAcceptedItems
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && item.Type == "Item"
                && item.Parent.Equals(mountCategoryId)
                && item.Name?.StartsWith(
                    "mount_all_",
                    StringComparison.OrdinalIgnoreCase
                ) == true
                && !ExplicitlyExcludedMountIds.Contains(id)
                && HasAllowedSkeetSightDescendant(
                    id,
                    templateTable,
                    excludedVisionDeviceIds,
                    new HashSet<MongoId>()
                )
            )
            .ToHashSet();

        var skeetDiscovered = skeetSightIds
            .Concat(skeetMountIds)
            .Where(id => !id.Equals(skeetIrId))
            .ToHashSet();
        skeetDiscovered.Add(g33Id);
        skeetDiscovered.Add(epicHighG33Id);

        if (skeetIrTargetFilter != null)
        {
            skeetIrTargetFilter.Filter ??= [];
            skeetIrTargetFilter.Filter.Clear();
            skeetIrTargetFilter.Filter.UnionWith(skeetDiscovered);
        }

        // Trusted risers retain their normal global child filters. Prevent a
        // magnified optic from coexisting with the G33 anywhere in the same
        // weapon assembly, closing the otherwise unavoidable nested loophole.
        var magnifiedSightIds = templateTable.Items.Values
            .Where(IsMagnifiedSight)
            .Select(item => item.Id)
            .Where(id =>
                !id.Equals(g33Id)
                && !id.Equals(epicHighG33Id))
            .ToHashSet();

        // Rail-facing mounts that failed the safe-mount test would otherwise
        // remain installable through a trusted riser and lead to a dead-end
        // slot. Conflict them at the G33 level along with magnified sights.
        var rejectedRailMountIds = railAcceptedItems
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && item.Type == "Item"
                && item.Parent.Equals(mountCategoryId)
                && !railMounts.Contains(id)
            )
            .ToHashSet();

        g33Template.Properties!.ConflictingItems ??= [];
        g33Template.Properties.ConflictingItems.UnionWith(magnifiedSightIds);
        g33Template.Properties.ConflictingItems.UnionWith(rejectedRailMountIds);
        epicHighG33Template.Properties!.ConflictingItems ??= [];
        epicHighG33Template.Properties.ConflictingItems.UnionWith(magnifiedSightIds);
        epicHighG33Template.Properties.ConflictingItems.UnionWith(rejectedRailMountIds);

        if (skeetIrTemplate?.Properties != null)
        {
            skeetIrTemplate.Properties.ConflictingItems ??= [];
            skeetIrTemplate.Properties.ConflictingItems.UnionWith(excludedVisionDeviceIds);
        }

        var parentSlotResult = AddToHhsCompatibleParentSlots(
            templateTable,
            g33Id
        );
        var epicHighParentSlotResult = AddToHhsCompatibleParentSlots(
            templateTable,
            epicHighG33Id
        );

        logger.Success(
            $"MakeMagnifierGreatAgain: discovered {onePowerOptics.Count} pure 1x optic(s) "
                + $"and {railMounts.Count} sight-rail mount(s); "
                + $"both G33 mod_scope filters now contain {g33Discovered.Count} item(s). "
                + (skeetIrTargetFilter != null
                    ? $"The Skeet-IR filter contains {skeetSightIds.Count} sight(s), "
                        + $"{skeetMountIds.Count} mount(s), and both G33 variants. "
                    : string.Empty)
                + $"Added {magnifiedSightIds.Count} magnified-sight and "
                + $"{rejectedRailMountIds.Count} rejected-mount conflict(s). "
                + $"Verified {parentSlotResult.Matching} HHS-compatible parent filter(s); "
                + $"added the custom-bundle G33 to {parentSlotResult.Added} and "
                + $"the EpicAIO high clone to {epicHighParentSlotResult.Added}."
        );

        logger.Debug(
            "MakeMagnifierGreatAgain compatibility IDs: "
                + string.Join(", ", g33Discovered.OrderBy(id => id.ToString()))
        );

        return Task.CompletedTask;
    }

    private static HashSet<string> LoadTrustedRiserIds(
        ISptLogger<MakeMagnifierGreatAgainCompatibilityMod> logger
    )
    {
        var assemblyDirectory = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var configPath = System.IO.Path.Combine(
            assemblyDirectory ?? AppContext.BaseDirectory,
            "config.json"
        );

        try
        {
            if (!File.Exists(configPath))
            {
                logger.Warning(
                    $"MakeMagnifierGreatAgain: compatibility config was not found at {configPath}; "
                        + "no trusted-riser exceptions will be applied."
                );
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var config = JsonSerializer.Deserialize<CompatibilityConfig>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            return (config?.TrustedRiserIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            logger.Error(
                $"MakeMagnifierGreatAgain: failed to read {configPath}; "
                    + $"no trusted-riser exceptions will be applied. {exception.Message}"
            );
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record CompatibilityConfig
    {
        public List<string> TrustedRiserIds { get; init; } = [];
    }

    private static (int Matching, int Added) AddToHhsCompatibleParentSlots(
        TemplateTable templateTable,
        MongoId g33Id
    )
    {
        MongoId vanillaHhsId = VanillaHhsTemplateId;
        MongoId mountCategoryId = MountCategoryId;
        var matching = 0;
        var added = 0;

        foreach (var template in templateTable.Items.Values)
        {
            // The G33 is a rail-facing magnifier, not a child optic for another
            // mount/riser. This prevents G33 -> riser -> G33 recursion.
            if (template.Parent.Equals(mountCategoryId))
            {
                continue;
            }

            foreach (var filter in GetAllSlotFilters(template))
            {
                if (filter.Filter?.Contains(vanillaHhsId) != true)
                {
                    continue;
                }

                matching++;
                if (filter.Filter.Add(g33Id))
                {
                    added++;
                }
            }
        }

        if (matching == 0)
        {
            throw new InvalidOperationException(
                "MakeMagnifierGreatAgain: no parent attachment filters accept the vanilla HHS-1."
            );
        }

        return (matching, added);
    }

    private static bool IsPureOnePowerOptic(TemplateItem item)
    {
        MongoId collimatorCategoryId = CollimatorCategoryId;
        MongoId compactCollimatorCategoryId = CompactCollimatorCategoryId;

        if (
            item.Type != "Item"
            || (
                !item.Parent.Equals(collimatorCategoryId)
                && !item.Parent.Equals(compactCollimatorCategoryId)
            )
            || string.Equals(item.Properties?.SightModType, "iron", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        var zooms = item.Properties?.Zooms?.SelectMany(group => group).ToArray();
        return zooms is { Length: > 0 }
            && zooms.All(zoom => Math.Abs(zoom - 1.0) <= OnePowerTolerance);
    }

    private static bool HasOnePowerDescendant(
        MongoId itemId,
        TemplateTable templateTable,
        HashSet<MongoId> onePowerOptics,
        HashSet<MongoId> visited
    )
    {
        if (!visited.Add(itemId) || !templateTable.Items.TryGetValue(itemId, out var item))
        {
            return false;
        }

        foreach (var filter in GetAllSlotFilters(item))
        {
            foreach (var childId in filter.Filter ?? [])
            {
                if (onePowerOptics.Contains(childId))
                {
                    return true;
                }

                if (HasOnePowerDescendant(childId, templateTable, onePowerOptics, visited))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasMagnifiedDescendant(
        MongoId itemId,
        TemplateTable templateTable,
        HashSet<MongoId> visited
    )
    {
        if (!visited.Add(itemId) || !templateTable.Items.TryGetValue(itemId, out var item))
        {
            return false;
        }

        foreach (var filter in GetAllSlotFilters(item))
        {
            foreach (var childId in filter.Filter ?? [])
            {
                if (!templateTable.Items.TryGetValue(childId, out var child))
                {
                    continue;
                }

                if (IsMagnifiedSight(child))
                {
                    return true;
                }

                if (HasMagnifiedDescendant(childId, templateTable, visited))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsMagnifiedSight(TemplateItem item)
    {
        var zooms = item.Properties?.Zooms?.SelectMany(group => group).ToArray();
        return zooms is { Length: > 0 }
            && zooms.Any(zoom => Math.Abs(zoom - 1.0) > OnePowerTolerance);
    }

    private static bool IsSight(TemplateItem item)
    {
        var zooms = item.Properties?.Zooms?.SelectMany(group => group).ToArray();
        return item.Type == "Item"
            && zooms is { Length: > 0 }
            && !string.Equals(
                item.Properties?.SightModType,
                "iron",
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static bool IsNightVisionOrThermal(
        TemplateItem item,
        TemplateTable templateTable)
    {
        MongoId nightVisionCategoryId = NightVisionCategoryId;
        MongoId thermalVisionCategoryId = ThermalVisionCategoryId;
        var visited = new HashSet<MongoId>();
        var parentId = item.Parent;

        while (visited.Add(parentId))
        {
            if (parentId.Equals(nightVisionCategoryId)
                || parentId.Equals(thermalVisionCategoryId))
            {
                return true;
            }

            if (!templateTable.Items.TryGetValue(parentId, out var parent))
            {
                break;
            }

            parentId = parent.Parent;
        }

        return false;
    }

    private static bool HasAllowedSkeetSightDescendant(
        MongoId itemId,
        TemplateTable templateTable,
        HashSet<MongoId> excludedVisionDeviceIds,
        HashSet<MongoId> visited
    )
    {
        if (!visited.Add(itemId) || !templateTable.Items.TryGetValue(itemId, out var item))
        {
            return false;
        }

        foreach (var filter in GetAllSlotFilters(item))
        {
            foreach (var childId in filter.Filter ?? [])
            {
                if (!templateTable.Items.TryGetValue(childId, out var child))
                {
                    continue;
                }

                if (IsSight(child) && !excludedVisionDeviceIds.Contains(childId))
                {
                    return true;
                }

                if (HasAllowedSkeetSightDescendant(
                    childId,
                    templateTable,
                    excludedVisionDeviceIds,
                    visited
                ))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<SlotFilter> GetScopeFilters(TemplateItem item)
    {
        var scopeSlots = item.Properties?.Slots?.Where(slot =>
            string.Equals(slot.Name, FrontScopeSlotName, StringComparison.Ordinal)
        );

        return scopeSlots?.SelectMany(slot => slot.Properties?.Filters ?? []) ?? [];
    }

    private static IEnumerable<SlotFilter> GetAllSlotFilters(TemplateItem item)
    {
        return item.Properties?.Slots?.SelectMany(slot =>
                slot.Properties?.Filters ?? []
            ) ?? [];
    }
}
