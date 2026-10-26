using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Locales;

namespace MakeMagnifierGreatAgain.Server;

// After JSOC/MMGA item registration, before SaveCallbacks loads profiles and
// validates their templates. The late compatibility pass fills our empty slot.
[Injectable(TypePriority = OnLoadOrder.Preload + 20)]
public sealed class JsocSkeetIrIntegrationMod(
    ISptLogger<JsocSkeetIrIntegrationMod> logger,
    TemplateTable templates,
    GlobalTable globals,
    TradersTable traders,
    LocaleTable locales,
    LocaleService localeService
) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!templates.Items.TryGetValue(SkeetIrIds.Template, out var skeet)
            || skeet.Properties == null)
        {
            throw new InvalidOperationException(
                $"MakeMagnifierGreatAgain: required JSOC SkeetIR {SkeetIrIds.Template} "
                + "was not registered before MMGA's integration pass. Install a supported JSOC version."
            );
        }

        if (templates.Items.ContainsKey(SkeetIrIds.LegacyTemplate))
        {
            throw new InvalidOperationException(
                "MakeMagnifierGreatAgain: the retired MMGA SkeetIR clone is still registered. "
                + "Remove the previous MMGA installation and reinstall the complete current package."
            );
        }

        ApplyTemplate(skeet);
        MigrateDatabaseReferences(templates, globals, traders);
        RemoveLegacyOffer(traders);

        foreach (var language in locales.Global.Keys)
        {
            var locale = localeService.GetLocaleDb(language);
            locale[$"{SkeetIrIds.Template} Name"] = "Trijicon SkeetIR-L thermal clip-on (MMGA)";
            locale[$"{SkeetIrIds.Template} ShortName"] = "SkeetIR MMGA";
            locale[$"{SkeetIrIds.Template} Description"] =
                "JSOC's Trijicon SkeetIR-L upgraded by MakeMagnifierGreatAgain: "
                + "a standalone 1x thermal clip-on with a separated mount base and a child optic slot.";
        }

        logger.Success(
            $"MakeMagnifierGreatAgain: upgraded JSOC SkeetIR {SkeetIrIds.Template} "
            + $"in place using {SkeetIrIds.Bundle}; no duplicate SkeetIR template was created."
        );
        return Task.CompletedTask;
    }

    internal static void ApplyTemplate(TemplateItem skeet)
    {
        skeet.Name = SkeetIrIds.PrefabName;
        var props = skeet.Properties
            ?? throw new InvalidOperationException("JSOC SkeetIR has no item properties.");
        props.Prefab = new Prefab { Path = SkeetIrIds.Bundle, Rcid = "" };
        props.ExaminedByDefault = true;
        props.Ergonomics = -2;
        props.ModesCount = new([2], default);
        props.Zooms = [[1d, 1d]];
        props.AimSensitivity = new(null, [[0.65f, 0.65f]]);
        props.ZoomSensitivity = 0;
        props.Slots =
        [
            new Slot
            {
                Id = new MongoId(SkeetIrIds.ChildSlot),
                Parent = new MongoId(SkeetIrIds.Template),
                Name = "mod_scope",
                Prototype = "55d30c4c4bdc2db4468b457e",
                Required = false,
                MergeSlotWithChildren = false,
                Properties = new SlotProperties
                {
                    Filters = [new SlotFilter { Filter = [], Shift = 0 }],
                },
            },
        ];
    }

    internal static void MigrateDatabaseReferences(
        TemplateTable templates, GlobalTable globals, TradersTable traders)
    {
        MongoId oldId = SkeetIrIds.LegacyTemplate;
        MongoId newId = SkeetIrIds.Template;
        foreach (var template in templates.Items.Values)
        {
            var props = template.Properties;
            if (props?.ConflictingItems?.Remove(oldId) == true)
            {
                props.ConflictingItems.Add(newId);
            }
            foreach (var slot in props?.Slots ?? [])
            {
                foreach (var filter in slot.Properties?.Filters ?? [])
                {
                    if (filter.Filter?.Remove(oldId) == true)
                    {
                        filter.Filter.Add(newId);
                    }
                }
            }
        }
        foreach (var preset in globals.ItemPresets.Values)
        {
            MigrateItems(preset.Items);
            if (preset.Encyclopedia == oldId)
            {
                preset.Encyclopedia = newId;
            }
        }
        foreach (var trader in traders.Values)
        {
            MigrateItems(trader.Assort?.Items);
        }
    }

    private static void MigrateItems(IEnumerable<Item>? items)
    {
        foreach (var item in items ?? [])
        {
            if (item.Template == new MongoId(SkeetIrIds.LegacyTemplate))
            {
                item.Template = SkeetIrIds.Template;
            }
        }
    }

    internal static void RemoveLegacyOffer(TradersTable traders)
    {
        // The old $100 test offer can use JSOC's template after migration. Match
        // its offer ID, never the shared template, to preserve JSOC's own sales.
        MongoId offerId = SkeetIrIds.LegacyOffer;
        foreach (var trader in traders.Values)
        {
            var assort = trader.Assort;
            if (assort == null)
            {
                continue;
            }
            assort.Items.RemoveAll(item => item.Id == offerId);
            assort.BarterScheme.Remove(offerId);
            assort.LoyalLevelItems.Remove(offerId);
        }
    }
}
