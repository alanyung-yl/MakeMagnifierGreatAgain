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
    private const string SigEcho1TemplateId = "6478641c19d732620e045e17";
    private const string UnvDlocIrdTemplateId = "5a1ead28fcdbcb001912fa9f";
    private const string FrontScopeSlotName = "mod_scope";

    // A universal rail carrier is normally present in many weapon/receiver
    // rail filters. Platform bridges such as the M14 DCSB and ASh-12 carry
    // handle appear in only a small family of filters even when that family
    // has colour or calibre variants. Keep those weapon-family parts out of
    // MMGA's child slots. Explicit trusted-riser IDs bypass this heuristic.
    private const int MinimumGeneralRailHostCount = 9;

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

    // EFT models weapon-mounted night/thermal sights as ordinary optics; only
    // helmet devices inherit the NightVision/ThermalVision categories. These
    // stable database/prefab-name tokens cover the weapon optics as well.
    private static readonly string[] VisionDeviceNameTokens =
    [
        "thermal",
        "nightvision",
        "night_vision",
        "nvg_",
        "reap-ir",
        "flir",
        "echo1",
        "t12w",
        "vulcan",
        "zeus",
        "digisight",
        "ir-hunter",
        "nspu",
        "1pn93",
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
            .SelectMany(item => GetScopeFilters(item)
                .Select(filter => (Owner: item, Filter: filter)))
            .Where(entry =>
                entry.Filter.Filter?.Overlaps(directRailAnchors) == true)
            .ToList();

        var railAcceptedItems = sightRailFilters
            .SelectMany(entry => entry.Filter.Filter ?? [])
            .ToHashSet();

        var railHostsByAcceptedItem = BuildRailHostIndex(sightRailFilters);
        var platformBoundCarrierIds = railAcceptedItems
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && IsScopeCarrier(item)
                && !trustedRiserIds.Contains(id.ToString())
                && IsPlatformBoundCarrier(id, railHostsByAcceptedItem))
            .ToHashSet();

        var onePowerOptics = onePowerCandidates
            .Where(railAcceptedItems.Contains)
            .ToHashSet();

        var g33CarrierCandidates = railAcceptedItems
            .Concat(templateTable.Items.Values
                .Where(item => trustedRiserIds.Contains(item.Id.ToString()))
                .Select(item => item.Id))
            .ToHashSet();

        // A G33 may directly receive a pure 1x sight, or a rail-facing riser
        // that has at least one recursive route to a pure 1x sight. A true
        // scope mount whose own scope slot accepts a magnified optic is not a
        // useful G33 child and is rejected outright. A riser is not rejected
        // merely because one of its nested choices is such a scope mount;
        // that child is blocked contextually by ConflictingItems below.
        var g33Carriers = g33CarrierCandidates
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && IsScopeCarrier(item)
                && !platformBoundCarrierIds.Contains(id)
                && !ExplicitlyExcludedMountIds.Contains(id)
                && !HasExcludedMountWording(item)
                && !AcceptsMagnifiedScope(item, templateTable)
                && HasOnePowerDescendant(
                    id,
                    templateTable,
                    onePowerCandidates,
                    platformBoundCarrierIds,
                    new HashSet<MongoId>())
            )
            .ToHashSet();

        var g33Discovered = onePowerOptics
            .Concat(g33Carriers)
            .Where(id =>
                !id.Equals(g33Id)
                && !id.Equals(epicHighG33Id)
                && !id.Equals(skeetIrId))
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
        // ordinary rail optic (including magnified scopes) or a compatible
        // rail carrier. NV/thermal sights are rejected directly and through
        // every recursive carrier route.
        var skeetSightIds = railAcceptedItems
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && IsSight(item)
                && !excludedVisionDeviceIds.Contains(id)
            )
            .ToHashSet();

        var skeetCarrierIds = railAcceptedItems
            .Where(id =>
                templateTable.Items.TryGetValue(id, out var item)
                && IsSkeetCarrierEligible(
                    item,
                    templateTable,
                    excludedVisionDeviceIds,
                    platformBoundCarrierIds)
            )
            .ToHashSet();

        var skeetDiscovered = skeetSightIds
            .Concat(skeetCarrierIds)
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

        // Carrier child filters remain owned by EFT/other mods. Conflicts are
        // therefore the context-sensitive guardrails for nested branches.
        // G33 accepts only pure 1x sights, even when the incompatible sight is
        // reachable through one or more mixed risers/scope mounts.
        var nonOnePowerSightIds = templateTable.Items.Values
            .Where(item => IsSight(item)
                && !onePowerCandidates.Contains(item.Id))
            .Select(item => item.Id)
            .Where(id =>
                !id.Equals(g33Id)
                && !id.Equals(epicHighG33Id)
                // Skeet is the valid outer parent of either G33. Putting it
                // in a G33 conflict list blocks Skeet -> G33 as well as the
                // reverse because EFT evaluates conflicts assembly-wide.
                && !id.Equals(skeetIrId))
            .ToHashSet();

        var g33NestedCarrierConflicts = new HashSet<MongoId>();
        foreach (var carrierId in g33Carriers)
        {
            CollectNestedG33CarrierConflicts(
                carrierId,
                templateTable,
                onePowerCandidates,
                platformBoundCarrierIds,
                g33NestedCarrierConflicts,
                new HashSet<MongoId>());
        }
        var skeetNestedCarrierConflicts = new HashSet<MongoId>();
        foreach (var carrierId in skeetCarrierIds)
        {
            CollectNestedSkeetCarrierConflicts(
                carrierId,
                templateTable,
                excludedVisionDeviceIds,
                platformBoundCarrierIds,
                skeetNestedCarrierConflicts,
                new HashSet<MongoId>());
        }
        // A Skeet may sit on a riser, but another Skeet cannot be installed
        // beneath that first Skeet through the riser's globally-owned filter.
        skeetNestedCarrierConflicts.Add(skeetIrId);

        g33Template.Properties!.ConflictingItems ??= [];
        g33Template.Properties.ConflictingItems.UnionWith(nonOnePowerSightIds);
        g33Template.Properties.ConflictingItems.UnionWith(g33NestedCarrierConflicts);
        g33Template.Properties.ConflictingItems.Remove(skeetIrId);
        epicHighG33Template.Properties!.ConflictingItems ??= [];
        epicHighG33Template.Properties.ConflictingItems.UnionWith(nonOnePowerSightIds);
        epicHighG33Template.Properties.ConflictingItems.UnionWith(g33NestedCarrierConflicts);
        epicHighG33Template.Properties.ConflictingItems.Remove(skeetIrId);

        if (skeetIrTemplate?.Properties != null)
        {
            skeetIrTemplate.Properties.ConflictingItems ??= [];
            skeetIrTemplate.Properties.ConflictingItems.UnionWith(excludedVisionDeviceIds);
            skeetIrTemplate.Properties.ConflictingItems.UnionWith(
                skeetNestedCarrierConflicts);
        }

        // WTT-style global slot registration cannot express "only while this
        // riser is nested". Add both G33 variants to every safe rail-facing
        // mount slot; the G33 conflicts above police incompatible branches.
        var railMountFilters = 0;
        var railMountCustomAdded = 0;
        var railMountEpicAdded = 0;
        foreach (var (owner, filter) in sightRailFilters)
        {
            if (!IsScopeCarrier(owner)
                || ExplicitlyExcludedMountIds.Contains(owner.Id)
                || HasExcludedMountWording(owner))
            {
                continue;
            }

            filter.Filter ??= [];
            railMountFilters++;
            if (filter.Filter.Add(g33Id))
            {
                railMountCustomAdded++;
            }
            if (filter.Filter.Add(epicHighG33Id))
            {
                railMountEpicAdded++;
            }
        }

        var parentSlotResult = AddToHhsCompatibleParentSlots(
            templateTable,
            g33Id
        );
        var epicHighParentSlotResult = AddToHhsCompatibleParentSlots(
            templateTable,
            epicHighG33Id
        );

        ValidateRootFilters(
            templateTable,
            g33Template,
            epicHighG33Template,
            skeetIrTemplate,
            targetFilter,
            epicHighTargetFilter,
            skeetIrTargetFilter,
            onePowerCandidates,
            excludedVisionDeviceIds,
            platformBoundCarrierIds);

        var rejectedG33ScopeMountCount = g33CarrierCandidates.Count(id =>
            templateTable.Items.TryGetValue(id, out var item)
                && IsScopeCarrier(item)
                && !platformBoundCarrierIds.Contains(id)
                && !ExplicitlyExcludedMountIds.Contains(id)
                && !HasExcludedMountWording(item)
                && AcceptsMagnifiedScope(item, templateTable)
                && HasOnePowerDescendant(
                    id,
                    templateTable,
                    onePowerCandidates,
                    platformBoundCarrierIds,
                    new HashSet<MongoId>()));
        var integratedIronCarrierCount = g33Carriers.Count(id =>
            templateTable.Items.TryGetValue(id, out var item)
                && string.Equals(
                    item.Properties?.SightModType,
                    "iron",
                    StringComparison.OrdinalIgnoreCase));

        logger.Success(
            $"MakeMagnifierGreatAgain: discovered {onePowerOptics.Count} pure 1x optic(s) "
                + $"and {g33Carriers.Count} G33-compatible sight carrier(s); "
                + $"both G33 mod_scope filters now contain {g33Discovered.Count} item(s). "
                + (skeetIrTargetFilter != null
                    ? $"The Skeet-IR filter contains {skeetSightIds.Count} sight(s), "
                        + $"{skeetCarrierIds.Count} carrier(s), and both G33 variants. "
                    : string.Empty)
                + $"Added {nonOnePowerSightIds.Count} non-1x-sight, "
                + $"{g33NestedCarrierConflicts.Count} G33 nested-carrier, and "
                + $"{skeetNestedCarrierConflicts.Count} Skeet nested-carrier conflict(s). "
                + $"Whitelisted both G33 variants in {railMountFilters} rail-facing mount filter(s) "
                + $"(new: {railMountCustomAdded}/{railMountEpicAdded}). "
                + $"Rejected {rejectedG33ScopeMountCount} G33 scope mount(s) with a magnified branch; retained "
                + $"{integratedIronCarrierCount} integrated-iron carrier(s). "
                + $"Excluded {platformBoundCarrierIds.Count} platform-bound carrier(s) "
                + $"seen on fewer than {MinimumGeneralRailHostCount} rail host(s). "
                + $"Verified {parentSlotResult.Matching} HHS-compatible parent filter(s); "
                + $"added the custom-bundle G33 to {parentSlotResult.Added} and "
                + $"the EpicAIO high clone to {epicHighParentSlotResult.Added}."
        );

        logger.Debug(
            "MakeMagnifierGreatAgain compatibility IDs: "
                + string.Join(", ", g33Discovered.OrderBy(id => id.ToString()))
        );

        logger.Debug(
            "MakeMagnifierGreatAgain platform-bound carrier exclusions: "
                + string.Join(", ", platformBoundCarrierIds
                    .OrderBy(id => id.ToString())
                    .Select(id =>
                    {
                        var hostCount = railHostsByAcceptedItem.TryGetValue(
                            id,
                            out var hosts)
                            ? hosts.Count
                            : 0;
                        var name = templateTable.Items.TryGetValue(id, out var item)
                            ? item.Name
                            : "missing";
                        return $"{id} ({name}; hosts={hostCount})";
                    }))
        );

        return Task.CompletedTask;
    }

    private static void ValidateRootFilters(
        TemplateTable templateTable,
        TemplateItem g33Template,
        TemplateItem epicHighG33Template,
        TemplateItem? skeetTemplate,
        SlotFilter g33Filter,
        SlotFilter epicHighG33Filter,
        SlotFilter? skeetFilter,
        HashSet<MongoId> onePowerOptics,
        HashSet<MongoId> excludedVisionDeviceIds,
        HashSet<MongoId> platformBoundCarrierIds)
    {
        ValidateG33RootFilter(
            "custom-bundle G33",
            templateTable,
            g33Filter,
            onePowerOptics,
            platformBoundCarrierIds);
        ValidateG33RootFilter(
            "EpicAIO G33",
            templateTable,
            epicHighG33Filter,
            onePowerOptics,
            platformBoundCarrierIds);

        if (skeetFilter?.Filter == null)
        {
            return;
        }

        MongoId g33Id = G33TemplateId;
        MongoId epicHighG33Id = EpicHighG33TemplateId;
        MongoId skeetId = SkeetIrTemplateId;
        var hierarchyIsInvalid = !skeetFilter.Filter.Contains(g33Id)
            || !skeetFilter.Filter.Contains(epicHighG33Id)
            || g33Filter.Filter?.Contains(skeetId) == true
            || epicHighG33Filter.Filter?.Contains(skeetId) == true
            || g33Template.Properties?.ConflictingItems?.Contains(skeetId) == true
            || epicHighG33Template.Properties?.ConflictingItems?.Contains(skeetId) == true
            || skeetTemplate?.Properties?.ConflictingItems?.Contains(g33Id) == true
            || skeetTemplate?.Properties?.ConflictingItems?.Contains(epicHighG33Id) == true;
        if (hierarchyIsInvalid)
        {
            throw new InvalidOperationException(
                "MakeMagnifierGreatAgain: directional hierarchy validation failed; expected Skeet -> either G33 -> 1x sight.");
        }

        var invalidSkeetIds = skeetFilter.Filter.Where(id =>
            excludedVisionDeviceIds.Contains(id)
            || (templateTable.Items.TryGetValue(id, out var item)
                && IsScopeCarrier(item)
                && (platformBoundCarrierIds.Contains(id)
                    || ExplicitlyExcludedMountIds.Contains(id)
                    || HasExcludedMountWording(item)
                    || HasDisqualifyingTacticalSlot(item, templateTable))))
            .ToArray();
        if (invalidSkeetIds.Length > 0)
        {
            throw new InvalidOperationException(
                "MakeMagnifierGreatAgain: Skeet root filter validation failed for: "
                    + string.Join(", ", invalidSkeetIds));
        }

        MongoId echo1Id = SigEcho1TemplateId;
        MongoId dlocIrdId = UnvDlocIrdTemplateId;
        if ((templateTable.Items.ContainsKey(echo1Id)
                && skeetFilter.Filter.Contains(echo1Id))
            || (templateTable.Items.ContainsKey(dlocIrdId)
                && skeetFilter.Filter.Contains(dlocIrdId)))
        {
            throw new InvalidOperationException(
                "MakeMagnifierGreatAgain: Skeet unexpectedly accepts ECHO1 or its UNV DLOC IRD base.");
        }
    }

    private static void ValidateG33RootFilter(
        string label,
        TemplateTable templateTable,
        SlotFilter filter,
        HashSet<MongoId> onePowerOptics,
        HashSet<MongoId> platformBoundCarrierIds)
    {
        var invalidIds = (filter.Filter ?? []).Where(id =>
            templateTable.Items.TryGetValue(id, out var item)
            && ((IsSight(item) && !onePowerOptics.Contains(id))
                || (IsScopeCarrier(item)
                    && (platformBoundCarrierIds.Contains(id)
                        || ExplicitlyExcludedMountIds.Contains(id)
                        || HasExcludedMountWording(item)
                        || AcceptsMagnifiedScope(item, templateTable)))))
            .ToArray();
        if (invalidIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"MakeMagnifierGreatAgain: {label} root filter validation failed for: "
                    + string.Join(", ", invalidIds));
        }
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
        var matching = 0;
        var added = 0;

        foreach (var template in templateTable.Items.Values)
        {
            // Offset/angled adapters are intentionally outside MMGA's
            // compatibility surface, even if another mod registered HHS-1 in
            // one of their filters.
            if (IsScopeCarrier(template)
                && (ExplicitlyExcludedMountIds.Contains(template.Id)
                    || HasExcludedMountWording(template)))
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

    private static bool IsScopeCarrier(TemplateItem item)
    {
        MongoId mountCategoryId = MountCategoryId;
        return item.Type == "Item"
            && GetScopeFilters(item).Any()
            && (item.Parent.Equals(mountCategoryId)
                || (string.Equals(
                        item.Properties?.SightModType,
                        "iron",
                        StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<MongoId, HashSet<MongoId>> BuildRailHostIndex(
        IEnumerable<(TemplateItem Owner, SlotFilter Filter)> sightRailFilters)
    {
        var result = new Dictionary<MongoId, HashSet<MongoId>>();

        foreach (var (owner, filter) in sightRailFilters)
        {
            foreach (var acceptedId in filter.Filter ?? [])
            {
                if (!result.TryGetValue(acceptedId, out var hosts))
                {
                    hosts = [];
                    result[acceptedId] = hosts;
                }

                hosts.Add(owner.Id);
            }
        }

        return result;
    }

    private static bool IsPlatformBoundCarrier(
        MongoId itemId,
        IReadOnlyDictionary<MongoId, HashSet<MongoId>> railHostsByAcceptedItem)
    {
        return !railHostsByAcceptedItem.TryGetValue(itemId, out var railHosts)
            || railHosts.Count < MinimumGeneralRailHostCount;
    }

    private static bool HasExcludedMountWording(TemplateItem item)
    {
        return ContainsExcludedMountWording(item.Name)
            || ContainsExcludedMountWording(item.Properties?.Name);
    }

    private static bool ContainsExcludedMountWording(string? value)
    {
        return value?.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0
            || value?.IndexOf("angled", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsSkeetCarrierEligible(
        TemplateItem item,
        TemplateTable templateTable,
        HashSet<MongoId> excludedVisionDeviceIds,
        HashSet<MongoId> platformBoundCarrierIds)
    {
        return IsScopeCarrier(item)
            && !platformBoundCarrierIds.Contains(item.Id)
            && !ExplicitlyExcludedMountIds.Contains(item.Id)
            && !HasExcludedMountWording(item)
            && !HasDisqualifyingTacticalSlot(item, templateTable)
            && HasAllowedSkeetSightDescendant(
                item.Id,
                templateTable,
                excludedVisionDeviceIds,
                platformBoundCarrierIds,
                new HashSet<MongoId>());
    }

    private static bool HasDisqualifyingTacticalSlot(
        TemplateItem item,
        TemplateTable templateTable)
    {
        var hasTacticalSlot = item.Properties?.Slots?.Any(slot =>
            slot.Name?.StartsWith("mod_tactical", StringComparison.OrdinalIgnoreCase)
                == true) == true;

        // A rail carrier with a tactical attachment slot but no recursive
        // magnified-sight route is a riser/accessory rail, not a scope mount.
        // Skeet excludes it; a true scope mount that also accepts a Vudu-class
        // optic remains valid.
        return hasTacticalSlot
            && !HasMagnifiedDescendant(
                item.Id,
                templateTable,
                new HashSet<MongoId>());
    }

    private static void CollectNestedG33CarrierConflicts(
        MongoId itemId,
        TemplateTable templateTable,
        HashSet<MongoId> onePowerOptics,
        HashSet<MongoId> platformBoundCarrierIds,
        HashSet<MongoId> conflicts,
        HashSet<MongoId> visited)
    {
        if (!visited.Add(itemId)
            || !templateTable.Items.TryGetValue(itemId, out var item))
        {
            return;
        }

        foreach (var filter in GetAllSlotFilters(item))
        {
            foreach (var childId in filter.Filter ?? [])
            {
                if (!templateTable.Items.TryGetValue(childId, out var child))
                {
                    continue;
                }

                if (IsScopeCarrier(child))
                {
                    var isEligible = !platformBoundCarrierIds.Contains(childId)
                        && !ExplicitlyExcludedMountIds.Contains(childId)
                        && !HasExcludedMountWording(child)
                        && !AcceptsMagnifiedScope(child, templateTable)
                        && HasOnePowerDescendant(
                            childId,
                            templateTable,
                            onePowerOptics,
                            platformBoundCarrierIds,
                            new HashSet<MongoId>());
                    if (!isEligible)
                    {
                        conflicts.Add(childId);
                        continue;
                    }
                }

                CollectNestedG33CarrierConflicts(
                    childId,
                    templateTable,
                    onePowerOptics,
                    platformBoundCarrierIds,
                    conflicts,
                    visited);
            }
        }
    }

    private static void CollectNestedSkeetCarrierConflicts(
        MongoId itemId,
        TemplateTable templateTable,
        HashSet<MongoId> excludedVisionDeviceIds,
        HashSet<MongoId> platformBoundCarrierIds,
        HashSet<MongoId> conflicts,
        HashSet<MongoId> visited)
    {
        if (!visited.Add(itemId)
            || !templateTable.Items.TryGetValue(itemId, out var item))
        {
            return;
        }

        foreach (var filter in GetAllSlotFilters(item))
        {
            foreach (var childId in filter.Filter ?? [])
            {
                if (!templateTable.Items.TryGetValue(childId, out var child))
                {
                    continue;
                }

                if (IsScopeCarrier(child)
                    && !IsSkeetCarrierEligible(
                        child,
                        templateTable,
                        excludedVisionDeviceIds,
                        platformBoundCarrierIds))
                {
                    conflicts.Add(childId);
                    continue;
                }

                CollectNestedSkeetCarrierConflicts(
                    childId,
                    templateTable,
                    excludedVisionDeviceIds,
                    platformBoundCarrierIds,
                    conflicts,
                    visited);
            }
        }
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
        HashSet<MongoId> platformBoundCarrierIds,
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

                if (platformBoundCarrierIds.Contains(childId))
                {
                    continue;
                }

                if (HasOnePowerDescendant(
                    childId,
                    templateTable,
                    onePowerOptics,
                    platformBoundCarrierIds,
                    visited))
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

    private static bool AcceptsMagnifiedScope(
        TemplateItem item,
        TemplateTable templateTable)
    {
        MongoId collimatorCategoryId = CollimatorCategoryId;
        MongoId compactCollimatorCategoryId = CompactCollimatorCategoryId;

        return GetScopeFilters(item).Any(filter =>
            (filter.Filter ?? []).Any(childId =>
                templateTable.Items.TryGetValue(childId, out var child)
                && !child.Parent.Equals(collimatorCategoryId)
                && !child.Parent.Equals(compactCollimatorCategoryId)
                && IsMagnifiedSight(child)));
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
        if (VisionDeviceNameTokens.Any(token =>
                ContainsToken(item.Name, token)
                || ContainsToken(item.Properties?.Name, token)
                || ContainsToken(item.Properties?.Description, token)))
        {
            return true;
        }

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

    private static bool ContainsToken(string? value, string token)
    {
        return value?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasAllowedSkeetSightDescendant(
        MongoId itemId,
        TemplateTable templateTable,
        HashSet<MongoId> excludedVisionDeviceIds,
        HashSet<MongoId> platformBoundCarrierIds,
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

                if (excludedVisionDeviceIds.Contains(childId))
                {
                    continue;
                }

                if (platformBoundCarrierIds.Contains(childId))
                {
                    continue;
                }

                if (IsScopeCarrier(child)
                    && (ExplicitlyExcludedMountIds.Contains(childId)
                        || HasExcludedMountWording(child)
                        || HasDisqualifyingTacticalSlot(child, templateTable)))
                {
                    continue;
                }

                if (HasAllowedSkeetSightDescendant(
                    childId,
                    templateTable,
                    excludedVisionDeviceIds,
                    platformBoundCarrierIds,
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
            slot.Name?.StartsWith(
                FrontScopeSlotName,
                StringComparison.OrdinalIgnoreCase) == true
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
