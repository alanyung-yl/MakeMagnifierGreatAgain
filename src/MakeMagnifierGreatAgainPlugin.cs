using BepInEx;
using HarmonyLib;

namespace MakeMagnifierGreatAgain
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class MakeMagnifierGreatAgainPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.alanyung-yl.makemagnifiergreatagain";
        public const string PluginName = "MakeMagnifierGreatAgain";
        public const string PluginVersion = "0.7.1";

        internal static MakeMagnifierGreatAgainPlugin Instance { get; private set; }

        internal static void LogInfo(string message)
        {
            Instance?.Logger.LogInfo(message);
        }

        internal static void LogWarning(string message)
        {
            Instance?.Logger.LogWarning(message);
        }

        public const string G33TestTemplateId = "31d49a6fcf9e8c1864d7e401";
        public const string EpicHighG33TemplateId = "31d49a6fcf9e8c1864d7e420";
        public const string EpicUh1HighG33TemplateId = "31d49a6fcf9e8c1864d7e430";
        public const string EpicExps3LowG33TemplateId = "31d49a6fcf9e8c1864d7e440";

        private void Awake()
        {
            Instance = this;
            new Harmony(PluginGuid).PatchAll();
            Logger.LogInfo(
                $"{PluginName} {PluginVersion} loaded. " +
                $"G33 proof-of-concept item {G33TestTemplateId} is enabled; " +
                "nested sight aim points are merged into the magnifier, and " +
                "SkeetIR uses its aspect-aware custom optic compositor.");
        }
    }
}
