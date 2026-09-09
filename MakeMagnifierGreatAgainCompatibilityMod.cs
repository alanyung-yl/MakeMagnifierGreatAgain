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
    private const string VanillaHhsTemplateId = "5c07dd120db834001c39092d";
    private const string FrontScopeSlotName = "mod_scope";

    // Stable EFT item categories, not item whitelist entries.
    private const string CollimatorCategoryId = "55818ad54bdc2ddc698b4569";
    private const string CompactCollimatorCategoryId = "55818acf4bdc2dde698b456b";
    private const string MountCategoryId = "55818b224bdc2dde698b456f";

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

        var onePowerOptics = templateTable.Items.Values
            .Where(IsPureOnePowerOptic)
            .Select(item => item.Id)
            .ToHashSet();

        // A mod_scope filter containing a real 1x optic represents an attachment
        // point from EFT's sight-rail compatibility graph. Mount-category entries
        // beside that optic are therefore suitable sight bases for the G33 slot.
        var sightRailFilters = templateTable.Items.Values
            .SelectMany(GetScopeFilters)
            .Where(filter => filter.Filter?.Overlaps(onePowerOptics) == true)
            .ToList();

        MongoId mountCategoryId = MountCategoryId;
        var railMounts = sightRailFilters
            .SelectMany(filter => filter.Filter ?? [])
            .Distinct()
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && item.Type == "Item"
                && item.Parent.Equals(mountCategoryId)
            )
            .ToHashSet();

        var discovered = onePowerOptics
            .Concat(railMounts)
            .Where(id => !id.Equals(g33Id))
            .ToHashSet();

        targetFilter.Filter ??= [];
        targetFilter.Filter.Clear();
        targetFilter.Filter.UnionWith(discovered);

        var parentSlotResult = AddToHhsCompatibleParentSlots(
            templateTable,
            g33Id
        );

        logger.Success(
            $"MakeMagnifierGreatAgain: discovered {onePowerOptics.Count} pure 1x optic(s) "
                + $"and {railMounts.Count} sight-rail mount(s); "
                + $"the G33 mod_scope filter now contains {discovered.Count} item(s). "
                + $"Verified {parentSlotResult.Matching} HHS-compatible parent filter(s) "
                + $"and added the G33 to {parentSlotResult.Added}."
        );

        logger.Debug(
            "MakeMagnifierGreatAgain compatibility IDs: "
                + string.Join(", ", discovered.OrderBy(id => id.ToString()))
        );

        return Task.CompletedTask;
    }

    private static (int Matching, int Added) AddToHhsCompatibleParentSlots(
        TemplateTable templateTable,
        MongoId g33Id
    )
    {
        MongoId vanillaHhsId = VanillaHhsTemplateId;
        var matching = 0;
        var added = 0;

        foreach (var template in templateTable.Items.Values)
        {
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
        )
        {
            return false;
        }

        var zooms = item.Properties?.Zooms?.SelectMany(group => group).ToArray();
        return zooms is { Length: > 0 }
            && zooms.All(zoom => Math.Abs(zoom - 1.0) <= OnePowerTolerance);
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
