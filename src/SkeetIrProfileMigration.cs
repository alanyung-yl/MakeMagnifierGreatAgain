using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Migration;

namespace MakeMagnifierGreatAgain.Server;

// SPT runs these migrations on raw profile JSON before orphaned mod-item
// validation. This includes inventory, scav, mail, insurance, and saved builds.
[Injectable]
public sealed class SkeetIrProfileMigration : AbstractProfileMigration
{
    public override string MigrationName => "MMGA SkeetIR: reuse JSOC's item template";

    public override bool CanMigrate(JsonObject profile, IEnumerable<IProfileMigration> previouslyRanMigrations)
        => RewriteReferences(profile, false) > 0;

    public override JsonObject Migrate(JsonObject profile)
    {
        RewriteReferences(profile, true);
        return profile;
    }

    internal static int RewriteReferences(JsonNode? node, bool apply)
    {
        var count = 0;
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                // Template IDs are also dictionary keys (e.g. Encyclopedia).
                if (key == SkeetIrIds.LegacyTemplate)
                {
                    count++;
                    if (apply)
                    {
                        obj.Remove(key);
                        if (!obj.ContainsKey(SkeetIrIds.Template))
                        {
                            obj[SkeetIrIds.Template] = value;
                        }
                        else if (value is JsonValue legacy && legacy.TryGetValue<bool>(out var wasKnown)
                            && wasKnown && obj[SkeetIrIds.Template] is JsonValue current
                            && current.TryGetValue<bool>(out _))
                        {
                            // Preserve examination knowledge when both entries exist.
                            obj[SkeetIrIds.Template] = true;
                        }
                    }
                }

                // Never rewrite instance IDs, parent links, or free-form text.
                if (IsTemplateField(key) && IsLegacyId(value))
                {
                    count++;
                    if (apply)
                    {
                        obj[key] = SkeetIrIds.Template;
                    }
                }
                else
                {
                    count += RewriteReferences(value, apply);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                // Template-ID lists such as wishlists and favourite items.
                if (IsLegacyId(array[i]))
                {
                    count++;
                    if (apply)
                    {
                        array[i] = SkeetIrIds.Template;
                    }
                }
                else
                {
                    count += RewriteReferences(array[i], apply);
                }
            }
        }
        return count;
    }

    private static bool IsTemplateField(string key)
        => key is "_tpl" or "tpl" or "templateId" or "TemplateId" or "encyclopedia" or "Encyclopedia";

    private static bool IsLegacyId(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text)
            && text == SkeetIrIds.LegacyTemplate;
}
