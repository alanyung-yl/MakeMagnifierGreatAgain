using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace MakeMagnifierGreatAgain
{
    /// <summary>
    /// EFT can render into a widescreen target while applying a different
    /// user-selected camera aspect. Forward that exact camera aspect to the
    /// SkeetIR custom compositor without polling or replacing its shader.
    /// </summary>
    [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.SetAspectRatio))]
    internal static class SkeetIrAspectRatioPatch
    {
        private static readonly int EftInverseAspectProperty =
            Shader.PropertyToID("_MMGA_EftInverseAspect");
        private static float _lastDivision;

        [HarmonyPostfix]
        private static void Postfix(float division)
        {
            if (division > 0.0001f)
            {
                var inverseAspect = 1f / division;
                Shader.SetGlobalFloat(EftInverseAspectProperty, inverseAspect);

                if (!Mathf.Approximately(_lastDivision, division))
                {
                    _lastDivision = division;
                    MakeMagnifierGreatAgainPlugin.LogInfo(
                        $"EFT display aspect {division:F4}:1 applied to SkeetIR "
                        + $"(vertical PiP UV scale {inverseAspect:F4}).");
                }
            }
        }
    }
}
