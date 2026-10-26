using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using WTTServerCommonLib;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace MakeMagnifierGreatAgain.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.alanyung-yl.makemagnifiergreatagain";
    public string Name { get; init; } = "MakeMagnifierGreatAgain";
    public string Author { get; init; } = "alanyung-yl";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("0.6.0");
    public Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        ["com.wtt.commonlib"] = new Range("~3.0.6"),
        ["com.epicrangetime.aio"] = new Range("~5.0.1"),
        ["com.c11.jsoc"] = new Range("~3.0.0"),
    };
    public string? Url { get; init; }
    public string License { get; init; } = "GPL-3.0-only";
}

// EpicAIO 5.0.1 creates its CommonLib items at Preload + 3. Register after
// that source template exists, while remaining safely before profile loading.
[Injectable(TypePriority = OnLoadOrder.Preload + 10)]
public sealed class MakeMagnifierGreatAgainServerMod(
    ISptLogger<MakeMagnifierGreatAgainServerMod> logger,
    WTTServerCommonLib.WTTServerCommonLib commonLib
) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await commonLib.CustomItemServiceExtended.CreateCustomItems(
            Assembly.GetExecutingAssembly()
        );

        logger.Success(
            "MakeMagnifierGreatAgain 0.6.0: handed custom items, traders, and locales " +
            "registration to WTT-CommonLib."
        );
    }
}
