using System;
using System.Collections;
using EFT.Animations;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace MakeMagnifierGreatAgain
{
    /// <summary>
    /// EFT normally exposes an attached sight and its parent magnifier as two
    /// independent aim points.  For the MMGA test item the child aim point must
    /// be folded into the G33 aim point so the G33 remains the active
    /// SightComponent and its OpticSight can start the global PiP camera.
    /// </summary>
    [HarmonyPatch(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.FindAimTransforms))]
    internal static class NestedSightAimPatch
    {
        private const string TestPrefabName = "scope_all_eotech_g33_mmga_test";
        private const string BackLensShaderName = "CW FX/BackLens";
        private const string OpticSightShaderName = "CW FX/OpticSight";

        private static bool _rebuildingAimState;
        private static bool _missingBackLensShaderWasLogged;
        private static bool _missingOpticSightShaderWasLogged;
        private static bool _opticSightShaderRebindWasLogged;
        private static bool _opticTextureConfigurationWasLogged;
        private static bool _mergeWasLogged;

        [HarmonyPostfix]
        private static void Postfix(ProceduralWeaponAnimation __instance)
        {
            if (_rebuildingAimState || __instance.ScopeAimTransforms == null)
            {
                return;
            }

            var magnifierAim = FindMagnifierAim(__instance);

            if (magnifierAim == null)
            {
                return;
            }

            var magnifierRoot = magnifierAim.ScopePrefabCache.transform;
            RestoreVanillaOpticSightShader(magnifierAim.ScopePrefabCache);
            RestoreVanillaBackLensShader(magnifierRoot);

            var removedAimPoints = 0;
            for (var index = __instance.ScopeAimTransforms.Count - 1; index >= 0; index--)
            {
                var candidate = __instance.ScopeAimTransforms[index];
                if (ReferenceEquals(candidate, magnifierAim)
                    || candidate.Bone == null
                    || !candidate.Bone.IsChildOf(magnifierRoot))
                {
                    continue;
                }

                __instance.ScopeAimTransforms.RemoveAt(index);
                removedAimPoints++;
            }

            if (removedAimPoints == 0)
            {
                return;
            }

            // FindAimTransforms already rebuilt its private optic list before
            // this postfix. Re-run the public finalization step so CurrentScope,
            // camera depth, PiP enablement, and scope-mode input all see the
            // collapsed list immediately.
            try
            {
                _rebuildingAimState = true;
                __instance.method_11();
            }
            finally
            {
                _rebuildingAimState = false;
            }

            if (!_mergeWasLogged)
            {
                _mergeWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    $"Merged {removedAimPoints} nested sight aim point(s) into the G33 magnifier aim point; "
                    + $"{__instance.ScopeAimTransforms.Count} weapon aim point(s) remain.");
            }
        }

        internal static ProceduralWeaponAnimation.SightNBone FindMagnifierAim(
            ProceduralWeaponAnimation animation)
        {
            if (animation.ScopeAimTransforms == null)
            {
                return null;
            }

            for (var index = 0; index < animation.ScopeAimTransforms.Count; index++)
            {
                var candidate = animation.ScopeAimTransforms[index];
                var cache = candidate.ScopePrefabCache;
                if (cache != null
                    && cache.transform.name.StartsWith(TestPrefabName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        internal static void RestoreVanillaOpticSightShader(ScopePrefabCache cache)
        {
            if (cache == null || !cache.HasOptics)
            {
                return;
            }

            var optic = cache.FirstOptic;
            var renderer = optic != null ? optic.LensRenderer : null;
            if (renderer == null)
            {
                MakeMagnifierGreatAgainPlugin.LogWarning(
                    "The G33 OpticSight has no LensRenderer; the PiP composite cannot be drawn.");
                return;
            }

            var shader = Shader.Find(OpticSightShaderName);
            if (shader == null)
            {
                if (!_missingOpticSightShaderWasLogged)
                {
                    _missingOpticSightShaderWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        $"Could not resolve runtime shader '{OpticSightShaderName}'.");
                }

                return;
            }

            var material = renderer.material;
            if (material.shader == shader)
            {
                LogOpticTextureConfiguration(material);
                return;
            }

            var previousShaderName = material.shader != null ? material.shader.name : "<missing>";
            var markTexture = GetTextureIfPresent(material, "_MarkTex");
            var maskTexture = GetTextureIfPresent(material, "_MaskTex");
            var maskTexture2 = GetTextureIfPresent(material, "_MaskTex2");
            var color = GetColorIfPresent(material, "_Color", Color.white);
            var shiftDirection = GetVectorIfPresent(material, "_ShiftDirection", new Vector4(0f, 1f, 0f, 0f));
            var shifts = GetVectorIfPresent(material, "_Shifts", Vector4.zero);
            var scales = GetVectorIfPresent(material, "_Scales", new Vector4(40f, 1.3f, 100f, 100f));
            var markLightness = GetFloatIfPresent(material, "_MarkLightness", 0.1f);
            var normalHideness = GetFloatIfPresent(material, "_NormalHideness", 32f);
            var hdr = GetFloatIfPresent(material, "_HDR", 4f);

            material.shader = shader;
            SetTextureIfPresent(material, "_MarkTex", markTexture);
            SetTextureIfPresent(material, "_MaskTex", maskTexture);
            SetTextureIfPresent(material, "_MaskTex2", maskTexture2);
            SetColorIfPresent(material, "_Color", color);
            SetVectorIfPresent(material, "_ShiftDirection", shiftDirection);
            SetVectorIfPresent(material, "_Shifts", shifts);
            SetVectorIfPresent(material, "_Scales", scales);
            SetFloatIfPresent(material, "_MarkLightness", markLightness);
            SetFloatIfPresent(material, "_NormalHideness", normalHideness);
            SetFloatIfPresent(material, "_HDR", hdr);
            material.renderQueue = -1;

            if (!_opticSightShaderRebindWasLogged)
            {
                _opticSightShaderRebindWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    $"Rebound G33 LensRenderer shader from '{previousShaderName}' to '{shader.name}' "
                    + "while preserving mark and mask textures.");
            }

            LogOpticTextureConfiguration(material);
        }

        private static void LogOpticTextureConfiguration(Material material)
        {
            if (_opticTextureConfigurationWasLogged)
            {
                return;
            }

            _opticTextureConfigurationWasLogged = true;
            var mark = GetTextureIfPresent(material, "_MarkTex");
            var windowMask = GetTextureIfPresent(material, "_MaskTex");
            var scopeMask = GetTextureIfPresent(material, "_MaskTex2");
            MakeMagnifierGreatAgainPlugin.LogInfo(
                "G33 optic textures: "
                + $"mark={DescribeTexture(mark)}, "
                + $"windowMask={DescribeTexture(windowMask)}, "
                + $"scopeMask={DescribeTexture(scopeMask)}.");
        }

        private static string DescribeTexture(Texture texture)
        {
            return texture == null
                ? "<missing>"
                : $"{texture.name} ({texture.width}x{texture.height})";
        }

        private static void RestoreVanillaBackLensShader(Transform magnifierRoot)
        {
            // The AssetRipper shader source compiles but does not reproduce the
            // shipped shader correctly. The bundle therefore carries a uniquely
            // named transparent placeholder; at runtime the declared `shaders`
            // dependency lets us bind the real game shader by name.
            var shader = Shader.Find(BackLensShaderName);
            if (shader == null)
            {
                if (!_missingBackLensShaderWasLogged)
                {
                    _missingBackLensShaderWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        $"Could not resolve runtime shader '{BackLensShaderName}'; "
                        + "the G33 rear lens will retain its transparent placeholder material.");
                }

                return;
            }

            var renderers = magnifierRoot.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var renderer in renderers)
            {
                if (!renderer.name.StartsWith("backLens", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var material = renderer.material;
                material.shader = shader;
                SetColorIfPresent(material, "_Color", new Color(0f, 0f, 0f, 0f));
                SetColorIfPresent(material, "_FresnelColor", new Color(0.12f, 0.20f, 0.22f, 0.12f));
                SetVectorIfPresent(material, "_Offset", Vector4.zero);
                SetFloatIfPresent(material, "_FresPow", 4f);
                SetFloatIfPresent(material, "_FresAdd", 0.3f);
                SetFloatIfPresent(material, "_NormalHideness", 6f);
                SetFloatIfPresent(material, "_SwitchToSightMultiplier", 0.1f);
                renderer.enabled = true;
            }
        }

        private static void SetColorIfPresent(Material material, string property, Color value)
        {
            if (material.HasProperty(property))
            {
                material.SetColor(property, value);
            }
        }

        private static Color GetColorIfPresent(Material material, string property, Color fallback)
        {
            return material.HasProperty(property) ? material.GetColor(property) : fallback;
        }

        private static Texture GetTextureIfPresent(Material material, string property)
        {
            return material.HasProperty(property) ? material.GetTexture(property) : null;
        }

        private static void SetTextureIfPresent(Material material, string property, Texture value)
        {
            if (material.HasProperty(property))
            {
                material.SetTexture(property, value);
            }
        }

        private static void SetVectorIfPresent(Material material, string property, Vector4 value)
        {
            if (material.HasProperty(property))
            {
                material.SetVector(property, value);
            }
        }

        private static Vector4 GetVectorIfPresent(Material material, string property, Vector4 fallback)
        {
            return material.HasProperty(property) ? material.GetVector(property) : fallback;
        }

        private static void SetFloatIfPresent(Material material, string property, float value)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }

        private static float GetFloatIfPresent(Material material, string property, float fallback)
        {
            return material.HasProperty(property) ? material.GetFloat(property) : fallback;
        }
    }

    [HarmonyPatch(typeof(ProceduralWeaponAnimation), "method_1")]
    internal static class MagnifierOpticActivationPatch
    {
        private static int _lastLoggedOpticInstanceId;
        private static bool _renderTextureDiagnosticScheduled;

        [HarmonyPostfix]
        private static void Postfix(ProceduralWeaponAnimation __instance)
        {
            var magnifierAim = NestedSightAimPatch.FindMagnifierAim(__instance);
            if (magnifierAim == null
                || !__instance.IsAiming
                || !__instance.FirstPersonPointOfView
                || !ReferenceEquals(__instance.CurrentScope, magnifierAim))
            {
                return;
            }

            var cache = magnifierAim.ScopePrefabCache;
            NestedSightAimPatch.RestoreVanillaOpticSightShader(cache);
            var optic = cache.CurrentModOpticSight;
            if (optic == null)
            {
                return;
            }

            var wasEnabled = optic.enabled;
            if (!wasEnabled)
            {
                optic.enabled = true;
            }

            var manager = CameraManager.Instance.OpticCameraManager;
            var wasCurrent = manager.CurrentOpticSight == optic;
            if (!wasCurrent)
            {
                manager.OnOpticSightEnabled(optic);
            }

            if (_lastLoggedOpticInstanceId == optic.GetInstanceID())
            {
                return;
            }

            _lastLoggedOpticInstanceId = optic.GetInstanceID();
            var cameraActive = manager.Camera != null && manager.Camera.gameObject.activeSelf;
            var lensShader = optic.LensRenderer != null
                && optic.LensRenderer.sharedMaterial != null
                && optic.LensRenderer.sharedMaterial.shader != null
                    ? optic.LensRenderer.sharedMaterial.shader.name
                    : "<missing>";
            MakeMagnifierGreatAgainPlugin.LogInfo(
                "G33 PiP bridge active: "
                + $"mode={cache.CurrentModeId}, wasEnabled={wasEnabled}, wasCurrent={wasCurrent}, "
                + $"cameraData={(optic.CameraData == null ? "missing" : optic.CameraData.GetType().Name)}, "
                + $"cameraActive={cameraActive}, lensShader={lensShader}.");

            if (!_renderTextureDiagnosticScheduled && MakeMagnifierGreatAgainPlugin.Instance != null)
            {
                _renderTextureDiagnosticScheduled = true;
                MakeMagnifierGreatAgainPlugin.Instance.StartCoroutine(LogRenderTextureAfterFrame(manager, optic));
            }
        }

        private static IEnumerator LogRenderTextureAfterFrame(
            OpticCameraManager manager,
            OpticSight optic)
        {
            yield return null;
            yield return new WaitForEndOfFrame();

            if (manager == null || manager.CurrentOpticSight != optic || manager._renderTexture == null)
            {
                _renderTextureDiagnosticScheduled = false;
                yield break;
            }

            var source = manager._renderTexture;
            var camera = manager.Camera;
            var previousActive = RenderTexture.active;
            var sampleTarget = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32);
            Texture2D sample = null;
            try
            {
                Graphics.Blit(source, sampleTarget);
                RenderTexture.active = sampleTarget;
                sample = new Texture2D(16, 16, TextureFormat.RGBA32, false, true);
                sample.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
                sample.Apply(false, false);

                var pixels = sample.GetPixels32();
                long luminanceSum = 0;
                var minimumLuminance = 255;
                var maximumLuminance = 0;
                var nonBlackPixels = 0;
                foreach (var pixel in pixels)
                {
                    var luminance = (pixel.r + pixel.g + pixel.b) / 3;
                    luminanceSum += luminance;
                    minimumLuminance = Math.Min(minimumLuminance, luminance);
                    maximumLuminance = Math.Max(maximumLuminance, luminance);
                    if (luminance > 2)
                    {
                        nonBlackPixels++;
                    }
                }

                var averageLuminance = pixels.Length == 0
                    ? 0f
                    : (float)luminanceSum / pixels.Length;
                var globalCameraTexture = Shader.GetGlobalTexture("_CamTex");
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "G33 PiP render diagnostic: "
                    + $"texture={source.name} {source.width}x{source.height}, "
                    + $"created={source.IsCreated()}, globalMatches={ReferenceEquals(globalCameraTexture, source)}, "
                    + $"sampleLuma(avg/min/max)={averageLuminance:F1}/{minimumLuminance}/{maximumLuminance}, "
                    + $"nonBlack={nonBlackPixels}/{pixels.Length}, "
                    + $"cameraEnabled={(camera != null && camera.enabled)}, "
                    + $"cameraActive={(camera != null && camera.gameObject.activeInHierarchy)}, "
                    + $"targetMatches={(camera != null && ReferenceEquals(camera.targetTexture, source))}, "
                    + $"cullingMask={(camera == null ? "<missing>" : $"0x{camera.cullingMask:X8}")}, "
                    + $"fov={(camera == null ? 0f : camera.fieldOfView):F1}.");
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (sample != null)
                {
                    UnityEngine.Object.Destroy(sample);
                }

                RenderTexture.ReleaseTemporary(sampleTarget);
            }
        }
    }
}
