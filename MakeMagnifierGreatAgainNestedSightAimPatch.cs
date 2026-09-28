using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.Animations;
using EFT.CameraControl;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

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
        private const string G33PrefabName = "scope_all_eotech_g33_mmga_test";
        private const string WilcoxBossPrefabName =
            "scope_all_wilcox_boss_xe_hp";
        private const string Romeo8TPrefabName =
            "scope_all_sig_romeo_8t";
        private const string Romeo7PrefabName =
            "scope_all_sig_sauer_romeo7_1x30";
        private const string Romeo4PrefabName =
            "scope_base_sig_romeo_4";
        private const string AimpointH2PrefabName =
            "scope_base_aimpoint_micro_h2";
        private const string HolosunHs401G5PrefabName =
            "scope_all_holosun_hs401g5";
        private const string AimpointProPrefabName =
            "scope_base_aimpoint_pro";
        private const string AimpointCompM4PrefabName =
            "scope_base_aimpooint_comp_m4";
        private const string TrijiconMroPrefabName =
            "scope_base_trijicon_mro";
        private const string TrijiconSroPrefabName =
            "scope_base_trijicon_sro";
        private const string TrijiconRmrPrefabName =
            "scope_base_trijicon_rmr";
        private const string SkeetIrDatabasePrefabName = "scope_eotech_skeet_ir_mmga_test";
        private const string SkeetIrBundleRootName = "scope_eotech_Skeet_working";
        private const string BackLensShaderName = "CW FX/BackLens";
        private const string OpticSightShaderName = "CW FX/OpticSight";

        private static bool _rebuildingAimState;
        private static bool _missingBackLensShaderWasLogged;
        private static bool _missingOpticSightShaderWasLogged;
        private static bool _opticSightShaderRebindWasLogged;
        private static bool _mergeWasLogged;
        private static readonly Dictionary<int, ProceduralWeaponAnimation.SightNBone>
            CollapsedG33ChildAims =
                new Dictionary<int, ProceduralWeaponAnimation.SightNBone>();

        [HarmonyPostfix]
        private static void Postfix(ProceduralWeaponAnimation __instance)
        {
            if (_rebuildingAimState || __instance.ScopeAimTransforms == null)
            {
                return;
            }

            CollapsedG33ChildAims.Remove(__instance.GetInstanceID());
            var magnifierAim = FindMagnifierAim(__instance);
            var removedAimPoints = 0;
            if (magnifierAim != null)
            {
                var magnifierRoot = magnifierAim.ScopePrefabCache.transform;
                var immediateChild = FindImmediateChildScope(magnifierRoot);
                var collapseBossSelectors = immediateChild != null
                    && IsWilcoxBossRootName(immediateChild.transform.name)
                    && IsWithinSkeetIr(magnifierRoot);
                PreserveAimBone(magnifierAim.Bone);
                RestoreVanillaOpticSightShader(magnifierAim.ScopePrefabCache);
                RestoreVanillaBackLensShader(magnifierRoot);

                for (var index = __instance.ScopeAimTransforms.Count - 1; index >= 0; index--)
                {
                    var candidate = __instance.ScopeAimTransforms[index];
                    if (ReferenceEquals(candidate, magnifierAim)
                        || candidate.Bone == null
                        || !candidate.Bone.IsChildOf(magnifierRoot))
                    {
                        continue;
                    }

                    if (immediateChild != null
                        && ReferenceEquals(candidate.ScopePrefabCache, immediateChild))
                    {
                        PreserveAimBone(candidate.Bone);
                        ProceduralWeaponAnimation.SightNBone retainedAim;
                        if (!collapseBossSelectors
                            || !CollapsedG33ChildAims.TryGetValue(
                                __instance.GetInstanceID(),
                                out retainedAim)
                            || ShouldPreferNestedOneXAim(retainedAim, candidate))
                        {
                            CollapsedG33ChildAims[__instance.GetInstanceID()] =
                                candidate;
                        }

                        // BOSS exposes separate reflex and integrated-iron
                        // aim entries. Collapse both from EFT's public Switch
                        // Sight list, while retaining camera 001 privately as
                        // the folded-G33 1x donor.
                    }

                    __instance.ScopeAimTransforms.RemoveAt(index);
                    removedAimPoints++;
                }
            }

            var updatedSkeetAim =
                SkeetIrSightSelectionPatch.ConfigureFrontClipOn(__instance);
            if (removedAimPoints == 0 && !updatedSkeetAim)
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

            if (removedAimPoints > 0 && !_mergeWasLogged)
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
                if (cache != null && IsG33RootName(cache.transform.name))
                {
                    return candidate;
                }
            }

            return null;
        }

        internal static ProceduralWeaponAnimation.SightNBone FindSkeetIrAim(
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
                    && IsSkeetIrRootName(cache.transform.name))
                {
                    return candidate;
                }
            }

            return null;
        }

        internal static ScopePrefabCache FindImmediateChildScope(Transform hostRoot)
        {
            if (hostRoot == null)
            {
                return null;
            }

            Transform scopeSlot = null;
            for (var index = 0; index < hostRoot.childCount; index++)
            {
                var child = hostRoot.GetChild(index);
                if (string.Equals(child.name, "mod_scope", StringComparison.OrdinalIgnoreCase))
                {
                    scopeSlot = child;
                    break;
                }
            }

            if (scopeSlot == null)
            {
                return null;
            }

            ScopePrefabCache closest = null;
            var closestDepth = int.MaxValue;
            var caches = scopeSlot.GetComponentsInChildren<ScopePrefabCache>(true);
            foreach (var cache in caches)
            {
                var depth = 0;
                var cursor = cache.transform;
                while (cursor != null && cursor != scopeSlot)
                {
                    depth++;
                    cursor = cursor.parent;
                }

                if (cursor == scopeSlot && depth < closestDepth)
                {
                    closest = cache;
                    closestDepth = depth;
                }
            }

            return closest;
        }

        internal static bool IsG33RootName(string name)
        {
            return name != null
                && name.StartsWith(G33PrefabName, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWilcoxBossRootName(string name)
        {
            return name != null
                && name.StartsWith(
                    WilcoxBossPrefabName,
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsTargetedCompactReflexRootName(string name)
        {
            return StartsWithPrefabName(name, Romeo8TPrefabName)
                || StartsWithPrefabName(name, Romeo7PrefabName)
                || ContainsPrefabToken(name, "romeo7")
                || ContainsPrefabToken(name, "romeo_7")
                || StartsWithPrefabName(name, Romeo4PrefabName)
                || StartsWithPrefabName(name, AimpointH2PrefabName)
                || StartsWithPrefabName(name, HolosunHs401G5PrefabName)
                || StartsWithPrefabName(name, AimpointProPrefabName)
                || StartsWithPrefabName(name, AimpointCompM4PrefabName)
                || StartsWithPrefabName(name, TrijiconMroPrefabName)
                || StartsWithPrefabName(name, TrijiconSroPrefabName);
        }

        internal static bool RequiresCompactReticleOverlay(
            CollimatorSight collimator)
        {
            var cache = collimator != null
                ? collimator.GetComponentInParent<ScopePrefabCache>()
                : null;
            var name = cache != null ? cache.transform.name : null;
            return StartsWithPrefabName(name, Romeo4PrefabName)
                || StartsWithPrefabName(name, AimpointH2PrefabName)
                || StartsWithPrefabName(name, HolosunHs401G5PrefabName)
                || StartsWithPrefabName(name, AimpointProPrefabName)
                || StartsWithPrefabName(name, AimpointCompM4PrefabName)
                || StartsWithPrefabName(name, TrijiconMroPrefabName)
                || StartsWithPrefabName(name, TrijiconSroPrefabName)
                || StartsWithPrefabName(name, TrijiconRmrPrefabName);
        }

        internal static bool IsTargetedCompactCollimator(
            CollimatorSight collimator)
        {
            var cache = collimator != null
                ? collimator.GetComponentInParent<ScopePrefabCache>()
                : null;
            return cache != null
                && IsTargetedCompactReflexRootName(cache.transform.name);
        }

        private static bool StartsWithPrefabName(string name, string prefabName)
        {
            return name != null
                && name.StartsWith(prefabName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsPrefabToken(string name, string token)
        {
            return name != null
                && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShouldPreferNestedOneXAim(
            ProceduralWeaponAnimation.SightNBone current,
            ProceduralWeaponAnimation.SightNBone candidate)
        {
            if (candidate == null || candidate.Bone == null)
            {
                return false;
            }
            if (current == null || current.Bone == null)
            {
                return true;
            }

            // BOSS camera 001 is its native reflex axis; camera 002 belongs
            // to the lower integrated irons. Folded G33 1x must use 001.
            var candidateIsPrimary = string.Equals(
                candidate.Bone.name,
                "mod_aim_camera_001",
                StringComparison.OrdinalIgnoreCase);
            var currentIsPrimary = string.Equals(
                current.Bone.name,
                "mod_aim_camera_001",
                StringComparison.OrdinalIgnoreCase);
            return candidateIsPrimary && !currentIsPrimary;
        }

        internal static void PreserveAimBone(Transform bone)
        {
            if (bone == null)
            {
                return;
            }

            var guard = bone.GetComponent<SkeetIrAimBonePoseGuard>();
            if (guard == null)
            {
                guard = bone.gameObject.AddComponent<SkeetIrAimBonePoseGuard>();
            }
            guard.Capture();
        }

        internal static bool RestoreAimBone(Transform bone)
        {
            var guard = bone != null
                ? bone.GetComponent<SkeetIrAimBonePoseGuard>()
                : null;
            if (guard != null)
            {
                return guard.Restore();
            }

            return false;
        }

        internal static ProceduralWeaponAnimation.SightNBone FindCollapsedG33ChildAim(
            ProceduralWeaponAnimation animation,
            ScopePrefabCache g33Cache)
        {
            ProceduralWeaponAnimation.SightNBone childAim;
            if (animation == null
                || g33Cache == null
                || !IsG33RootName(g33Cache.transform.name)
                || !CollapsedG33ChildAims.TryGetValue(
                    animation.GetInstanceID(),
                    out childAim)
                || childAim == null
                || childAim.Bone == null
                || !childAim.Bone.IsChildOf(g33Cache.transform))
            {
                return null;
            }

            return childAim;
        }

        internal static bool IsSkeetIrRootName(string name)
        {
            return name != null
                && (
                    name.StartsWith(
                        SkeetIrBundleRootName,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || name.StartsWith(
                        SkeetIrDatabasePrefabName,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
        }

        internal static bool IsWithinSkeetIr(Transform transform)
        {
            for (var cursor = transform; cursor != null; cursor = cursor.parent)
            {
                if (IsSkeetIrRootName(cursor.name))
                {
                    return true;
                }
            }

            return false;
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

    /// <summary>
    /// The original Skeet-IR bundle was authored against an older EFT build.
    /// Its folded-mode CollimatorSight lives on mode_001 while its serialized
    /// renderer lives on the child linza_mode_001 object. EFT 4.1.3's Awake
    /// replaces that valid serialized renderer with GetComponent on mode_001,
    /// producing a null reference and interrupting every mode transition.
    /// Preserve the serialized child renderer for this legacy layout.
    /// </summary>
    [HarmonyPatch(typeof(CollimatorSight), nameof(CollimatorSight.Awake))]
    internal static class LegacySkeetCollimatorAwakePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CollimatorSight __instance)
        {
            if (__instance == null
                || __instance.GetComponent<MeshRenderer>() != null
                || __instance.CollimatorMeshRenderer == null
                || !NestedSightAimPatch.IsWithinSkeetIr(__instance.transform))
            {
                return true;
            }

            __instance.CollimatorMaterial =
                __instance.CollimatorMeshRenderer.sharedMaterial;
            MakeMagnifierGreatAgainPlugin.LogInfo(
                "Preserved the Skeet-IR folded-mode child collimator renderer for EFT 4.1.3 compatibility.");
            return false;
        }
    }

    /// <summary>
    /// Treats Skeet-IR as the outer clip-on selector. Selecting Skeet enables
    /// its thermal mode; selecting its immediate child folds Skeet aside and
    /// leaves that child sight's native modes and zoom controls intact.
    /// </summary>
    [HarmonyPatch]
    internal static class SkeetIrSightSelectionPatch
    {
        internal sealed class FrontClipOnState
        {
            internal ProceduralWeaponAnimation Animation;
            internal ProceduralWeaponAnimation.SightNBone SkeetAim;
            internal ProceduralWeaponAnimation.SightNBone OriginalSkeetAim;
            internal SightComponent SkeetMod;
            internal ScopePrefabCache SkeetCache;
            internal ProceduralWeaponAnimation.SightNBone ChildAim;
            internal ScopePrefabCache ChildCache;
            internal ScopeThermalVisionData ThermalData;
            internal ProceduralWeaponAnimation.SightNBone OriginalChildAim;
            internal ProceduralWeaponAnimation.SightNBone G33OneXChildAim;
            internal bool UsesWilcoxBossDonor;
            internal bool UsesNativeReticleProjection;
            internal bool SuppressNestedDonorGlass;
            internal Renderer ProjectedCollimatorRenderer;
            internal Mesh ProjectedCollimatorMesh;
            internal Material ProjectedCollimatorMaterial;
            internal Texture2D ProjectedCollimatorMarkTexture;
            internal readonly Dictionary<OpticSight, ScopeThermalVisionData>
                OriginalChildThermals =
                    new Dictionary<OpticSight, ScopeThermalVisionData>();
            internal readonly Dictionary<OpticSight, bool> OriginalSkeetOpticStates =
                new Dictionary<OpticSight, bool>();
            internal readonly Dictionary<Renderer, bool> OriginalSkeetLensStates =
                new Dictionary<Renderer, bool>();
            internal bool Initialized;
            internal bool ThermalActive;
            internal bool ThermalUsesSkeetOptic;
            internal bool CameraRefreshPending;
            internal int LateRefreshFrames;
            internal int LastChildMode = -1;
            internal OpticSight LastChildOptic;
            internal bool HybridOneXPoseCorrectionWasLogged;
        }

        internal sealed class FixedOneXState
        {
            internal ProceduralWeaponAnimation Animation;
            internal ProceduralWeaponAnimation.SightNBone SkeetAim;
            internal ProceduralWeaponAnimation.SightNBone OriginalSkeetAim;
            internal ScopePrefabCache SkeetCache;
            internal ProceduralWeaponAnimation.SightNBone ChildAim;
            internal ScopePrefabCache ChildCache;
            internal bool PoseCorrectionWasLogged;
        }

        private static readonly Dictionary<int, FrontClipOnState> FrontClipOnStates =
            new Dictionary<int, FrontClipOnState>();
        private static readonly Dictionary<int, FixedOneXState> FixedOneXStates =
            new Dictionary<int, FixedOneXState>();
        private static readonly FieldInfo OpticUpdaterInstanceIdField =
            AccessTools.Field(typeof(OpticComponentUpdater), "cachedOpticId")
            ?? AccessTools.Field(typeof(OpticComponentUpdater), "int_0");
        private static readonly MethodInfo ApplyLiveThermalStateMethod =
            AccessTools.Method(
                typeof(ThermalVision),
                "method_1",
                new[] { typeof(bool) });

        private static int _lastLoggedSkeetInstanceId;
        private static int _lastLoggedMode = -1;
        private static readonly HashSet<string> LoggedChildRoutes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoggedReticleSources =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> LoggedProjectedCollimatorSources =
            new HashSet<int>();
        private static bool _frontClipOnConfiguredWasLogged;
        private static bool _oneXChildRouteWasLogged;
        private static bool _hybridOneXRouteWasLogged;
        private static bool _g33OneXAimRouteWasLogged;
        private static bool _missingG33ReticleShaderWasLogged;
        private const string CorrectMarkOrientationProperty =
            "_MMGA_CorrectMarkOrientation";
        private static bool _missingUpdaterFieldWasLogged;
        private static bool _missingLiveThermalMethodWasLogged;

        /// <summary>
        /// A magnified optic behind a clip-on must remain EFT's selected optic.
        /// It owns eye relief, the optic-camera pivot, native reticle, and (for
        /// an LPVO) ScopeZoomHandler. Keep Skeet's list entry only as a Switch
        /// Sight selector, but make that entry a proxy for the immediate child
        /// optic. Both selector positions consequently use the same child aim
        /// point; only Skeet's physical mode and injected thermal data differ.
        /// </summary>
        internal static bool ConfigureFrontClipOn(
            ProceduralWeaponAnimation animation)
        {
            if (animation == null || animation.ScopeAimTransforms == null)
            {
                return false;
            }

            var animationId = animation.GetInstanceID();
            var restoredPreviousState = false;
            FixedOneXState previousFixedOneX;
            if (FixedOneXStates.TryGetValue(animationId, out previousFixedOneX))
            {
                var previousRuntime = previousFixedOneX.SkeetCache != null
                    ? previousFixedOneX.SkeetCache.GetComponent<SkeetIrFixedOneXAimRuntime>()
                    : null;
                if (previousRuntime != null)
                {
                    previousRuntime.enabled = false;
                }

                if (previousFixedOneX.SkeetAim != null
                    && previousFixedOneX.OriginalSkeetAim != null)
                {
                    CopyAimPoint(
                        previousFixedOneX.OriginalSkeetAim,
                        previousFixedOneX.SkeetAim);
                }

                FixedOneXStates.Remove(animationId);
                restoredPreviousState = true;
            }

            FrontClipOnState previousState;
            if (FrontClipOnStates.TryGetValue(animationId, out previousState))
            {
                var previousRuntime = previousState.SkeetCache != null
                    ? previousState.SkeetCache.GetComponent<SkeetIrFrontClipOnRuntime>()
                    : null;
                if (previousRuntime != null)
                {
                    previousRuntime.enabled = false;
                }

                if (previousState.SkeetAim != null
                    && previousState.OriginalSkeetAim != null)
                {
                    CopyAimPoint(previousState.OriginalSkeetAim, previousState.SkeetAim);
                }
                if (previousState.ChildAim != null
                    && previousState.OriginalChildAim != null)
                {
                    CopyAimPoint(previousState.OriginalChildAim, previousState.ChildAim);
                }
                if (previousState.G33OneXChildAim != null)
                {
                    // FindAimTransforms can run while the returning weapon is
                    // still carrying the last shared optic-camera alignment.
                    // Restore the retained folded-sight bone before rebuilding
                    // the replacement front-clip-on state around it.
                    NestedSightAimPatch.RestoreAimBone(
                        previousState.G33OneXChildAim.Bone);
                }

                RestoreChildThermals(previousState);
                DestroyProjectedCollimatorMaterial(previousState);
                RestoreSkeetOptics(previousState);
                FrontClipOnStates.Remove(animationId);
                restoredPreviousState = true;
            }

            var skeetAim = NestedSightAimPatch.FindSkeetIrAim(animation);
            if (skeetAim == null || skeetAim.ScopePrefabCache == null)
            {
                return restoredPreviousState;
            }

            var skeetCache = skeetAim.ScopePrefabCache;
            var lensRuntime = skeetCache.GetComponent<SkeetIrLensVisibilityRuntime>();
            if (lensRuntime == null)
            {
                lensRuntime = skeetCache.gameObject.AddComponent<SkeetIrLensVisibilityRuntime>();
            }
            lensRuntime.Initialize(animation, skeetAim);

            var childCache = NestedSightAimPatch.FindImmediateChildScope(
                skeetCache.transform);

            if (childCache == null)
            {
                var missingChildKey = skeetCache.GetInstanceID() + ":<no child cache>";
                if (LoggedChildRoutes.Add(missingChildKey))
                {
                    MakeMagnifierGreatAgainPlugin.LogInfo(
                        "Skeet-IR child route diagnostic: no immediate child "
                        + "ScopePrefabCache under '"
                        + skeetCache.transform.name + "'.");
                }
                return restoredPreviousState;
            }

            ProceduralWeaponAnimation.SightNBone childAim = null;
            for (var index = 0; index < animation.ScopeAimTransforms.Count; index++)
            {
                var candidate = animation.ScopeAimTransforms[index];
                if (!ReferenceEquals(candidate, skeetAim)
                    && ReferenceEquals(candidate.ScopePrefabCache, childCache))
                {
                    childAim = candidate;
                    break;
                }
            }

            var nestedChild = NestedSightAimPatch.FindImmediateChildScope(
                childCache.transform);
            var nestedName = nestedChild != null
                ? nestedChild.transform.name
                : "<none>";
            var routeKey = childCache.transform.name + "|" + nestedName
                + "|" + (childAim != null && childAim.Bone != null
                    ? childAim.Bone.name : "<no aim bone>");
            if (LoggedChildRoutes.Add(routeKey))
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Skeet-IR child route diagnostic: child='"
                    + childCache.transform.name + "', nested='" + nestedName
                    + "', hasOptics=" + childCache.HasOptics
                    + ", hasCollimators=" + childCache.HasCollimators
                    + ", modes=" + childCache.ModesCount
                    + ", aimBone='"
                    + (childAim != null && childAim.Bone != null
                        ? childAim.Bone.name : "<missing>")
                    + "', magnified="
                    + (childAim != null && IsMagnifiedChild(
                        childAim.Mod, childCache))
                    + DescribeAimSelection(
                        animation,
                        skeetAim,
                        childAim)
                    + ".");
            }

            if (childAim == null || childAim.Bone == null)
            {
                return restoredPreviousState;
            }

            NestedSightAimPatch.PreserveAimBone(skeetAim.Bone);
            NestedSightAimPatch.PreserveAimBone(childAim.Bone);

            if (!childCache.HasOptics
                || !IsMagnifiedChild(childAim.Mod, childCache))
            {
                // A returning weapon can reach this rebuild after the shared
                // optic camera has already written into the retained child
                // bone. Restore the preserved prefab pose before creating the
                // replacement fixed-1x state around that transform.
                NestedSightAimPatch.RestoreAimBone(childAim.Bone);

                // Keep Skeet's SightComponent and ScopePrefabCache so its
                // established thermal PiP still owns the optic camera. Only
                // take the child's ADS eye position and rotation.
                var fixedOneX = new FixedOneXState
                {
                    Animation = animation,
                    SkeetAim = skeetAim,
                    OriginalSkeetAim = new ProceduralWeaponAnimation.SightNBone(),
                    SkeetCache = skeetCache,
                    ChildAim = childAim,
                    ChildCache = childCache
                };
                CopyAimPoint(skeetAim, fixedOneX.OriginalSkeetAim);
                FixedOneXStates[animationId] = fixedOneX;
                RestoreFixedOneXAim(fixedOneX);

                var fixedOneXRuntime =
                    skeetCache.GetComponent<SkeetIrFixedOneXAimRuntime>();
                if (fixedOneXRuntime == null)
                {
                    fixedOneXRuntime =
                        skeetCache.gameObject.AddComponent<SkeetIrFixedOneXAimRuntime>();
                }
                fixedOneXRuntime.Initialize(animation, fixedOneX);
                fixedOneXRuntime.enabled = true;

                if (!_oneXChildRouteWasLogged)
                {
                    _oneXChildRouteWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogInfo(
                        "Using the Skeet-IR 1x child path for '"
                        + childCache.transform.name
                        + "'; ADS uses the child's mod_aim_camera while Skeet retains its thermal PiP.");
                }

                return true;
            }

            var thermalData = FindSkeetThermalData(skeetCache);
            if (thermalData == null)
            {
                return restoredPreviousState;
            }

            var nestedG33Child = NestedSightAimPatch.IsG33RootName(
                childCache.transform.name)
                ? NestedSightAimPatch.FindImmediateChildScope(
                    childCache.transform)
                : null;
            var usesWilcoxBossDonor = nestedG33Child != null
                && NestedSightAimPatch.IsWilcoxBossRootName(
                    nestedG33Child.transform.name);
            var usesTargetedCompactReflex = nestedG33Child != null
                && NestedSightAimPatch.IsTargetedCompactReflexRootName(
                    nestedG33Child.transform.name);
            var state = new FrontClipOnState
            {
                Animation = animation,
                SkeetAim = skeetAim,
                SkeetMod = skeetAim.Mod,
                SkeetCache = skeetCache,
                ChildAim = childAim,
                ChildCache = childCache,
                ThermalData = thermalData,
                OriginalChildAim = new ProceduralWeaponAnimation.SightNBone(),
                G33OneXChildAim = NestedSightAimPatch.FindCollapsedG33ChildAim(
                    animation,
                    childCache),
                UsesWilcoxBossDonor = usesWilcoxBossDonor,
                // Every custom-G33 3x overlay must preserve the donor's
                // native screen projection. The older non-BOSS path combined
                // a render-target-flipped projection matrix with a vertically
                // mirrored mark texture. That restored mark orientation but
                // mirrored the projected glass position around screen center,
                // shifting thermal-on reticles away from their thermal-off
                // point of aim. BOSS already used this native pair and was the
                // aligned control case.
                UsesNativeReticleProjection = nestedG33Child != null,
                SuppressNestedDonorGlass = usesTargetedCompactReflex
            };

            CaptureChildThermals(state);
            if (state.G33OneXChildAim != null)
            {
                NestedSightAimPatch.RestoreAimBone(
                    state.G33OneXChildAim.Bone);
                CaptureProjectedCollimator(state);
            }
            CaptureSkeetOptics(state);
            CopyAimPoint(childAim, state.OriginalChildAim);
            state.OriginalSkeetAim =
                new ProceduralWeaponAnimation.SightNBone();
            CopyAimPoint(skeetAim, state.OriginalSkeetAim);
            CopyAimPoint(childAim, skeetAim);
            FrontClipOnStates[animationId] = state;

            var runtime = skeetCache.GetComponent<SkeetIrFrontClipOnRuntime>();
            if (runtime == null)
            {
                runtime = skeetCache.gameObject.AddComponent<SkeetIrFrontClipOnRuntime>();
            }
            runtime.Initialize(animation, state);
            runtime.enabled = true;

            var currentIndex = animation.ScopeAimTransforms.Count > 0
                ? animation.AimIndex % animation.ScopeAimTransforms.Count
                : -1;
            var thermalActive = currentIndex >= 0
                && ReferenceEquals(
                    animation.ScopeAimTransforms[currentIndex],
                    skeetAim);
            ApplyFrontClipOnState(state, thermalActive, true);

            if (!_frontClipOnConfiguredWasLogged)
            {
                _frontClipOnConfiguredWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Configured Skeet-IR as a front clip-on for direct magnified child "
                    + $"'{childCache.transform.name}'; optic modes use the child PiP, "
                    + "while hybrid collimator modes use Skeet's thermal PiP.");
            }

            return true;
        }

        private static string DescribeAimSelection(
            ProceduralWeaponAnimation animation,
            ProceduralWeaponAnimation.SightNBone skeetAim,
            ProceduralWeaponAnimation.SightNBone childAim)
        {
            if (animation == null || animation.ScopeAimTransforms == null)
            {
                return ", aim selection unavailable";
            }

            var aims = animation.ScopeAimTransforms;
            var selectedIndex = aims.Count > 0
                ? animation.AimIndex % aims.Count
                : -1;
            var skeetIndex = -1;
            var childIndex = -1;
            var descriptions = new List<string>();

            for (var index = 0; index < aims.Count; index++)
            {
                var aim = aims[index];
                if (ReferenceEquals(aim, skeetAim))
                {
                    skeetIndex = index;
                }
                if (ReferenceEquals(aim, childAim))
                {
                    childIndex = index;
                }

                var cacheName = aim != null && aim.ScopePrefabCache != null
                    ? aim.ScopePrefabCache.transform.name
                    : "<no cache>";
                var boneName = aim != null && aim.Bone != null
                    ? aim.Bone.name
                    : "<no bone>";
                descriptions.Add(index + ":" + cacheName + "/" + boneName);
            }

            return ", aimIndex=" + animation.AimIndex
                + ", selectedIndex=" + selectedIndex
                + ", skeetIndex=" + skeetIndex
                + ", childIndex=" + childIndex
                + ", aims=[" + string.Join(", ", descriptions.ToArray()) + "]";
        }

        internal static bool TryGetFrontClipOnState(
            ProceduralWeaponAnimation animation,
            out FrontClipOnState state)
        {
            state = null;
            return animation != null
                && FrontClipOnStates.TryGetValue(animation.GetInstanceID(), out state)
                && state != null
                && state.SkeetAim != null
                && state.SkeetCache != null
                && state.ChildCache != null;
        }

        internal static bool TryGetFixedOneXState(
            ProceduralWeaponAnimation animation,
            out FixedOneXState state)
        {
            state = null;
            return animation != null
                && FixedOneXStates.TryGetValue(animation.GetInstanceID(), out state)
                && state != null
                && state.SkeetAim != null
                && state.ChildAim != null
                && state.ChildAim.Bone != null;
        }

        internal static void RestoreFixedOneXAim(FixedOneXState state)
        {
            if (state == null
                || state.ChildAim == null
                || state.ChildAim.Bone == null
                || state.SkeetAim == null)
            {
                return;
            }

            // The player animation and shared optic camera survive a weapon
            // swap. A zoom change on the other weapon can therefore leave the
            // retained 1x mod_aim_camera at an optic-camera position. Restore
            // the prefab-local pose before rebinding Skeet's selector entry.
            var corrected = NestedSightAimPatch.RestoreAimBone(
                state.ChildAim.Bone);
            CopyAimPose(state.ChildAim, state.SkeetAim);
            // Bone alone is insufficient after a weapon rebuild. EFT also
            // consults these selector fields when deciding whether and how to
            // align the borrowed aim transform to an optic camera.
            state.SkeetAim.Depth = state.ChildAim.Depth;
            state.SkeetAim.AlignIfAvailable = state.ChildAim.AlignIfAvailable;
            state.SkeetAim.BoneRelatesToOptics =
                state.ChildAim.BoneRelatesToOptics;
            if (corrected && !state.PoseCorrectionWasLogged)
            {
                state.PoseCorrectionWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Restored fixed 1x mod_aim_camera '"
                    + state.ChildAim.Bone.name
                    + "' after shared optic state changed its local position.");
            }
        }

        internal static bool UsesHybridOneXAim(FrontClipOnState state)
        {
            if (state == null
                || state.G33OneXChildAim == null
                || state.G33OneXChildAim.Bone == null
                || state.ChildCache == null)
            {
                return false;
            }

            var mode = state.ChildCache.CurrentModeId;
            if (mode < 0 || mode >= state.ChildCache.ModesCount)
            {
                return false;
            }

            var modeInfo = state.ChildCache.GetScopeModeInfo(mode);
            return modeInfo != null && modeInfo.OpticSight == null;
        }

        internal static void RestoreHybridOneXAim(FrontClipOnState state)
        {
            if (!UsesHybridOneXAim(state)
                || state.ChildAim == null
                || state.SkeetAim == null)
            {
                return;
            }

            var corrected = NestedSightAimPatch.RestoreAimBone(
                state.G33OneXChildAim.Bone);
            CopyHybridAimSource(
                state,
                state.G33OneXChildAim,
                state.ChildAim,
                true);
            CopyAimPoint(state.ChildAim, state.SkeetAim);

            if (corrected && !state.HybridOneXPoseCorrectionWasLogged)
            {
                state.HybridOneXPoseCorrectionWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Restored hybrid 1x mod_aim_camera '"
                    + state.G33OneXChildAim.Bone.name
                    + "' after weapon switching changed its local pose.");
            }
        }

        internal static void PrepareBossZoomTransition(
            FrontClipOnState state,
            int targetChildMode)
        {
            if (state == null
                || !state.UsesWilcoxBossDonor
                || state.ChildCache == null
                || state.ChildAim == null
                || state.SkeetAim == null
                || targetChildMode < 0
                || targetChildMode >= state.ChildCache.ModesCount)
            {
                return;
            }

            var targetModeInfo = state.ChildCache.GetScopeModeInfo(
                targetChildMode);
            var usesNestedOneXAim = targetModeInfo != null
                && targetModeInfo.OpticSight == null;
            var source = usesNestedOneXAim
                ? state.G33OneXChildAim
                : state.OriginalChildAim;
            if (source == null || source.Bone == null)
            {
                return;
            }

            if (usesNestedOneXAim)
            {
                NestedSightAimPatch.RestoreAimBone(source.Bone);
            }

            // SetScopeMode calls UpdateSensitivity before the later weapon and
            // visual refresh. Install the destination owner now so sensitivity,
            // calibration, and alignment read BOSS at 1x and G33 at 3x.
            CopyHybridAimSource(
                state,
                source,
                state.ChildAim,
                usesNestedOneXAim);
            CopyAimPoint(state.ChildAim, state.SkeetAim);
        }

        internal static bool IsActiveFrontClipOnState(
            ProceduralWeaponAnimation animation,
            FrontClipOnState state)
        {
            FrontClipOnState registeredState;
            if (animation == null
                || state == null
                || !TryGetFrontClipOnState(animation, out registeredState)
                || !ReferenceEquals(registeredState, state))
            {
                return false;
            }

            var localPlayer = EFT.GamePlayerOwner.MyPlayer;
            return localPlayer == null
                || ReferenceEquals(
                    localPlayer.ProceduralWeaponAnimation,
                    animation);
        }

        private static void CopyAimPoint(
            ProceduralWeaponAnimation.SightNBone source,
            ProceduralWeaponAnimation.SightNBone destination)
        {
            destination.Mod = source.Mod;
            destination.Bone = source.Bone;
            destination.ScopePrefabCache = source.ScopePrefabCache;
            destination.Depth = source.Depth;
            destination.Rotation = source.Rotation;
            destination.LeftStanceRotation = source.LeftStanceRotation;
            destination.AlignIfAvailable = source.AlignIfAvailable;
            destination.BoneRelatesToOptics = source.BoneRelatesToOptics;
            destination.EyeGuardShift = source.EyeGuardShift;
        }

        private static bool IsMagnifiedChild(
            SightComponent childSight,
            ScopePrefabCache childCache)
        {
            // Look at the sight's available zoom, not its current setting:
            // a Razor at 1x must still use the magnified-optic proxy.
            if (childSight != null && childSight.HasCurrentZoomGreaterThenOne())
            {
                return true;
            }

            for (var mode = 0; mode < childCache.ModesCount; mode++)
            {
                var optic = childCache.GetOpticSight(mode);
                if (optic != null
                    && optic.CameraData != null
                    && optic.CameraData.IsAdjustableOptic)
                {
                    return true;
                }
            }

            return false;
        }

        private static ScopeThermalVisionData FindSkeetThermalData(
            ScopePrefabCache skeetCache)
        {
            for (var mode = 0; mode < skeetCache.ModesCount; mode++)
            {
                var optic = skeetCache.GetOpticSight(mode);
                if (optic != null
                    && optic.ScopeData != null
                    && optic.ScopeData.ThermalVisionData != null)
                {
                    return optic.ScopeData.ThermalVisionData;
                }
            }

            return null;
        }

        private static void CaptureChildThermals(FrontClipOnState state)
        {
            for (var mode = 0; mode < state.ChildCache.ModesCount; mode++)
            {
                var optic = state.ChildCache.GetOpticSight(mode);
                if (optic == null
                    || optic.ScopeData == null
                    || state.OriginalChildThermals.ContainsKey(optic))
                {
                    continue;
                }

                state.OriginalChildThermals.Add(
                    optic,
                    optic.ScopeData.ThermalVisionData);
            }
        }

        private static void CaptureProjectedCollimator(FrontClipOnState state)
        {
            CreateProjectedCollimatorMaterial(
                state.ChildCache,
                state.G33OneXChildAim != null,
                out state.ProjectedCollimatorRenderer,
                out state.ProjectedCollimatorMesh,
                out state.ProjectedCollimatorMaterial);

            if (state.ProjectedCollimatorMaterial == null)
            {
                return;
            }

            // The custom-G33 post-effect uses an unflipped projection matrix
            // (see SkeetIrG33ReticlePostProcess), so the native mark texture
            // must also stay unflipped. Keeping this pair together preserves
            // both the mark orientation and the original glass-plane screen
            // position. The legacy fallback remains only for a malformed G33
            // route where no immediate donor cache was discovered.
            if (!state.UsesNativeReticleProjection)
            {
                var sourceMark = state.ProjectedCollimatorMaterial.HasProperty(
                    "_MarkTex")
                    ? state.ProjectedCollimatorMaterial.GetTexture("_MarkTex")
                    : null;
                state.ProjectedCollimatorMarkTexture =
                    CreateCenteredVerticalMarkFlip(sourceMark);
                if (state.ProjectedCollimatorMarkTexture != null)
                {
                    state.ProjectedCollimatorMaterial.SetTexture(
                        "_MarkTex",
                        state.ProjectedCollimatorMarkTexture);
                }
            }

            // The selected texture/projection path owns the orientation
            // correction. Keep the shared shader on its native branch;
            // changing the UV equation here moved some donor marks.
            if (state.ProjectedCollimatorMaterial.HasProperty(
                CorrectMarkOrientationProperty))
            {
                state.ProjectedCollimatorMaterial.SetFloat(
                    CorrectMarkOrientationProperty,
                    0f);
            }
        }

        private static Texture2D CreateCenteredVerticalMarkFlip(
            Texture markTexture)
        {
            if (markTexture == null)
            {
                return null;
            }

            var width = Mathf.Max(1, markTexture.width);
            var height = Mathf.Max(1, markTexture.height);
            var temporary = RenderTexture.GetTemporary(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Texture2D readable = null;
            Texture2D flipped = null;
            try
            {
                Graphics.Blit(markTexture, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(
                    width,
                    height,
                    TextureFormat.RGBA32,
                    false,
                    true);
                readable.ReadPixels(
                    new Rect(0f, 0f, width, height),
                    0,
                    0,
                    false);
                readable.Apply(false, false);

                var sourcePixels = readable.GetPixels32();
                var minimumY = height;
                var maximumY = -1;
                for (var y = 0; y < height; y++)
                {
                    var row = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        if (sourcePixels[row + x].a == 0)
                        {
                            continue;
                        }

                        minimumY = Mathf.Min(minimumY, y);
                        maximumY = Mathf.Max(maximumY, y);
                    }
                }

                if (maximumY < minimumY)
                {
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        "Could not flip custom G33 thermal mark texture '"
                        + markTexture.name
                        + "' because its readable copy contained no alpha; "
                        + "retaining the native orientation.");
                    return null;
                }

                var flippedPixels = new Color32[sourcePixels.Length];
                for (var sourceY = 0; sourceY < height; sourceY++)
                {
                    // Row N maps to height-1-N, an exact reflection around
                    // normalized UV Y=0.5. Do not use the alpha centroid or
                    // alpha bounds: asymmetric ballistic marks intentionally
                    // place more artwork on one side of their aiming point.
                    var destinationY = height - 1 - sourceY;
                    var sourceRow = sourceY * width;
                    var destinationRow = destinationY * width;
                    for (var x = 0; x < width; x++)
                    {
                        flippedPixels[destinationRow + x] =
                            sourcePixels[sourceRow + x];
                    }
                }

                var mipChain = markTexture.mipmapCount > 1;
                flipped = new Texture2D(
                    width,
                    height,
                    TextureFormat.RGBA32,
                    mipChain,
                    true)
                {
                    name = markTexture.name + "_MMGA_G33_ThermalVerticalFlip",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = markTexture.filterMode,
                    wrapMode = markTexture.wrapMode,
                    anisoLevel = markTexture.anisoLevel
                };
                flipped.SetPixels32(flippedPixels);
                flipped.Apply(mipChain, false);

                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Vertically mirrored custom G33 thermal mark '"
                    + markTexture.name
                    + "' around authored UV center 0.5 (source alpha rows "
                    + minimumY
                    + ".."
                    + maximumY
                    + "); native projection and point of aim are preserved.");
                return flipped;
            }
            catch (Exception exception)
            {
                if (flipped != null)
                {
                    UnityEngine.Object.Destroy(flipped);
                }
                MakeMagnifierGreatAgainPlugin.LogWarning(
                    "Could not create the custom G33 thermal mark texture "
                    + "orientation copy for '"
                    + markTexture.name
                    + "'; retaining the native orientation. "
                    + exception.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null)
                {
                    UnityEngine.Object.Destroy(readable);
                }
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        internal static void CreateProjectedCollimatorMaterial(
            ScopePrefabCache childCache,
            bool preferNestedChild,
            out Renderer renderer,
            out Mesh projectedMesh,
            out Material projectedMaterial)
        {
            renderer = null;
            projectedMesh = null;
            projectedMaterial = null;

            var collimator = FindProjectedCollimator(
                childCache,
                preferNestedChild);
            renderer = collimator != null
                ? collimator.CollimatorMeshRenderer
                : null;
            var liveRendererMaterial = renderer != null
                ? renderer.sharedMaterial
                : null;
            var preferLiveRendererMaterial =
                NestedSightAimPatch.IsTargetedCompactCollimator(collimator);
            var sourceMaterial = preferLiveRendererMaterial
                ? liveRendererMaterial
                : collimator != null ? collimator.CollimatorMaterial : null;
            if (sourceMaterial == null)
            {
                sourceMaterial = preferLiveRendererMaterial
                    && collimator != null
                    ? collimator.CollimatorMaterial
                    : liveRendererMaterial;
            }

            var sourceKey = collimator != null
                ? collimator.GetInstanceID()
                : childCache != null ? childCache.GetInstanceID() : 0;
            if (LoggedProjectedCollimatorSources.Add(sourceKey))
            {
                var sourceMark = sourceMaterial != null
                    && sourceMaterial.HasProperty("_MarkTex")
                    ? sourceMaterial.GetTexture("_MarkTex")
                    : null;
                var rendererMark = liveRendererMaterial != null
                    && liveRendererMaterial.HasProperty("_MarkTex")
                    ? liveRendererMaterial.GetTexture("_MarkTex")
                    : null;
                var collimatorMaterial = collimator != null
                    ? collimator.CollimatorMaterial
                    : null;
                var collimatorMark = collimatorMaterial != null
                    && collimatorMaterial.HasProperty("_MarkTex")
                    ? collimatorMaterial.GetTexture("_MarkTex")
                    : null;
                var root = collimator != null
                    ? collimator.GetComponentInParent<ScopePrefabCache>()
                    : null;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Skeet-IR projected mark diagnostic: root='"
                    + (root != null ? root.transform.name : "<missing>")
                    + "', collimator='"
                    + (collimator != null ? collimator.transform.name : "<missing>")
                    + "', renderer='"
                    + (renderer != null ? renderer.name : "<missing>")
                    + "', preferRenderer=" + preferLiveRendererMaterial
                    + ", sourceMaterial='"
                    + (sourceMaterial != null ? sourceMaterial.name : "<missing>")
                    + "', sourceMark='"
                    + (sourceMark != null ? sourceMark.name : "<missing>")
                    + "', rendererMark='"
                    + (rendererMark != null ? rendererMark.name : "<missing>")
                    + "', collimatorMark='"
                    + (collimatorMark != null
                        ? collimatorMark.name : "<missing>")
                    + "', drawOnly="
                    + NestedSightAimPatch.RequiresCompactReticleOverlay(
                        collimator) + ".");
            }

            var skinnedRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedRenderer != null)
            {
                projectedMesh = skinnedRenderer.sharedMesh;
            }
            else if (renderer != null)
            {
                var meshFilter = renderer.GetComponent<MeshFilter>();
                projectedMesh = meshFilter != null ? meshFilter.sharedMesh : null;
            }

            if (renderer == null || projectedMesh == null || sourceMaterial == null)
            {
                return;
            }

            var shader = FindG33ReticleShader();
            if (shader == null)
            {
                if (!_missingG33ReticleShaderWasLogged)
                {
                    _missingG33ReticleShaderWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        "SkeetIR/CollimatorMarkOnly was not found; thermal "
                        + "views cannot draw the projected child reticle.");
                }
                renderer = null;
                return;
            }

            projectedMaterial = new Material(shader);
            projectedMaterial.CopyPropertiesFromMaterial(sourceMaterial);
            if (NestedSightAimPatch.RequiresCompactReticleOverlay(collimator))
            {
                // Selected compact donors use neutral attenuation in their
                // private thermal-only reticle copy. Keep the native source
                // material and its normal, nonthermal rendering untouched.
                if (projectedMaterial.HasProperty("_NoiseTex"))
                {
                    projectedMaterial.SetTexture(
                        "_NoiseTex",
                        Texture2D.whiteTexture);
                }
                if (projectedMaterial.HasProperty("_FadeTex"))
                {
                    projectedMaterial.SetTexture(
                        "_FadeTex",
                        Texture2D.whiteTexture);
                }
            }
            if (projectedMaterial.HasProperty("_Color"))
            {
                // CollimatorSight can retain an inactive black material while
                // the renderer holds the live reticle color. Prefer the live
                // renderer value and fall back to the EXPS3 red when neither
                // serialized value contains a usable color.
                var markColor = sourceMaterial.HasProperty("_Color")
                    ? sourceMaterial.GetColor("_Color")
                    : Color.clear;
                var rendererMaterial = renderer.sharedMaterial;
                var visibleColor = Mathf.Max(
                    markColor.r,
                    Mathf.Max(markColor.g, markColor.b));
                if (visibleColor <= 0.01f
                    && rendererMaterial != null
                    && rendererMaterial.HasProperty("_Color"))
                {
                    markColor = rendererMaterial.GetColor("_Color");
                    visibleColor = Mathf.Max(
                        markColor.r,
                        Mathf.Max(markColor.g, markColor.b));
                }
                if (visibleColor <= 0.01f)
                {
                    markColor = new Color(1f, 0.08f, 0.07f, 1f);
                }
                projectedMaterial.SetColor("_Color", markColor);
            }
            projectedMaterial.name = sourceMaterial.name + "_MMGA_ThermalProjection";
            projectedMaterial.hideFlags = HideFlags.HideAndDontSave;
            projectedMaterial.renderQueue = sourceMaterial.renderQueue;
        }

        internal static CollimatorSight FindProjectedCollimator(
            ScopePrefabCache childCache,
            bool preferNestedChild)
        {
            var reticleCache = preferNestedChild
                ? NestedSightAimPatch.FindImmediateChildScope(childCache.transform)
                : childCache;
            if ((reticleCache == null || !reticleCache.HasCollimators)
                && childCache.HasCollimators)
            {
                reticleCache = childCache;
            }

            var collimator = reticleCache != null && reticleCache.HasCollimators
                ? reticleCache.FirstCollimator
                : null;
            return collimator;
        }

        private static Shader FindG33ReticleShader()
        {
            const string shaderName = "SkeetIR/CollimatorMarkOnly";
            var shader = Shader.Find(shaderName);
            if (shader != null)
            {
                return shader;
            }

            // The shader is an explicit asset in skeet-ir.bundle rather than
            // a prefab material dependency. Load it once from the already
            // mounted bundle so Shader.Find can resolve it thereafter.
            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                var assetNames = bundle.GetAllAssetNames();
                for (var index = 0; index < assetNames.Length; index++)
                {
                    var assetName = assetNames[index];
                    if (assetName.EndsWith(
                        "skeetircollimatormarkonly.shader",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        shader = bundle.LoadAsset<Shader>(assetName);
                        if (shader != null)
                        {
                            return shader;
                        }
                    }
                }
            }

            return null;
        }

        private static void DestroyProjectedCollimatorMaterial(
            FrontClipOnState state)
        {
            if (state.ProjectedCollimatorMaterial != null)
            {
                UnityEngine.Object.Destroy(state.ProjectedCollimatorMaterial);
            }
            if (state.ProjectedCollimatorMarkTexture != null)
            {
                UnityEngine.Object.Destroy(
                    state.ProjectedCollimatorMarkTexture);
            }

            state.ProjectedCollimatorRenderer = null;
            state.ProjectedCollimatorMesh = null;
            state.ProjectedCollimatorMaterial = null;
            state.ProjectedCollimatorMarkTexture = null;
        }

        private static void CaptureSkeetOptics(FrontClipOnState state)
        {
            for (var mode = 0; mode < state.SkeetCache.ModesCount; mode++)
            {
                var optic = state.SkeetCache.GetOpticSight(mode);
                if (optic == null || state.OriginalSkeetOpticStates.ContainsKey(optic))
                {
                    continue;
                }

                state.OriginalSkeetOpticStates.Add(optic, optic.enabled);
                if (optic.LensRenderer != null
                    && !state.OriginalSkeetLensStates.ContainsKey(optic.LensRenderer))
                {
                    state.OriginalSkeetLensStates.Add(
                        optic.LensRenderer,
                        optic.LensRenderer.enabled);
                }
            }
        }

        private static void RestoreSkeetOptics(FrontClipOnState state)
        {
            foreach (var pair in state.OriginalSkeetOpticStates)
            {
                if (pair.Key != null)
                {
                    pair.Key.enabled = pair.Value;
                    if (pair.Key.LensRenderer != null)
                    {
                        pair.Key.LensFade(!pair.Value);
                    }
                }
            }

            foreach (var pair in state.OriginalSkeetLensStates)
            {
                if (pair.Key != null)
                {
                    pair.Key.enabled = pair.Value;
                }
            }
        }

        private static void RestoreChildThermals(FrontClipOnState state)
        {
            foreach (var pair in state.OriginalChildThermals)
            {
                if (pair.Key != null && pair.Key.ScopeData != null)
                {
                    pair.Key.ScopeData.ThermalVisionData = pair.Value;
                }
            }
        }

        private static void ApplyFrontClipOnState(
            FrontClipOnState state,
            bool thermalActive,
            bool force = false)
        {
            var childMode = state.ChildCache.CurrentModeId;
            var childModeInfo = childMode >= 0
                && childMode < state.ChildCache.ModesCount
                ? state.ChildCache.GetScopeModeInfo(childMode)
                : null;
            if (state.G33OneXChildAim != null)
            {
                var usesNestedOneXAim = childModeInfo != null
                    && childModeInfo.OpticSight == null;
                if (usesNestedOneXAim)
                {
                    NestedSightAimPatch.RestoreAimBone(
                        state.G33OneXChildAim.Bone);
                }
                var childAimSource = usesNestedOneXAim
                    ? state.G33OneXChildAim
                    : state.OriginalChildAim;
                CopyHybridAimSource(
                    state,
                    childAimSource,
                    state.ChildAim,
                    usesNestedOneXAim);

                if (usesNestedOneXAim && !_g33OneXAimRouteWasLogged)
                {
                    _g33OneXAimRouteWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogInfo(
                        "Custom G33 1x ADS now uses nested child mod_aim_camera '"
                        + state.G33OneXChildAim.Bone.name + "'.");
                }
            }
            var thermalUsesSkeetOptic = thermalActive
                && childModeInfo != null
                && childModeInfo.OpticSight == null;
            if (childModeInfo != null && childModeInfo.OpticSight == null)
            {
                NestedSightAimPatch.RestoreAimBone(state.ChildAim.Bone);
            }
            var changed = force
                || !state.Initialized
                || state.ThermalActive != thermalActive
                || state.ThermalUsesSkeetOptic != thermalUsesSkeetOptic
                || state.LastChildMode != childMode;

            state.Initialized = true;
            state.ThermalActive = thermalActive;
            state.ThermalUsesSkeetOptic = thermalUsesSkeetOptic;
            state.LastChildMode = childMode;
            ApplyMode(state.SkeetCache, thermalActive ? 0 : 1);

            // Ordinary hybrids keep the selector metadata stable and change
            // only pose. BOSS is the exception: its reflex cache owns the
            // collimator calibration that makes POA match POI at 50 m, so its
            // folded 1x proxy carries the complete BOSS aim entry.
            CopyAimPoint(state.ChildAim, state.SkeetAim);
            RestoreHybridOneXAim(state);

            if (thermalUsesSkeetOptic)
            {
                // The hybrid has no optic camera in this mode. Leave the aim
                // entry owned by the hybrid and activate Skeet's established
                // thermal optic directly; this is the same PiP source used by
                // the confirmed fixed-1x path without changing ADS ownership.
                ConfigureThermalReticle(state.SkeetCache);

                if (!_hybridOneXRouteWasLogged)
                {
                    _hybridOneXRouteWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogInfo(
                        "Using Skeet-IR's thermal optic for hybrid 1x mode on '"
                        + state.ChildCache.transform.name
                        + "'; ADS ownership remains with the hybrid sight.");
                }
            }

            foreach (var pair in state.OriginalChildThermals)
            {
                if (pair.Key != null && pair.Key.ScopeData != null)
                {
                    pair.Key.ScopeData.ThermalVisionData = thermalActive
                        && !thermalUsesSkeetOptic
                        ? state.ThermalData
                        : pair.Value;
                }
            }

            SetSkeetOpticLensVisibility(state);
            if (changed)
            {
                state.CameraRefreshPending = true;
                // The AimIndex callback can be followed by another native
                // optic visual update in the same frame. Refresh again from
                // LateUpdate (and once on the following frame) so the final
                // child optic state, rather than the intermediate state, wins.
                state.LateRefreshFrames = 2;
            }

        }

        private static void CopyAimPose(
            ProceduralWeaponAnimation.SightNBone source,
            ProceduralWeaponAnimation.SightNBone destination)
        {
            destination.Bone = source.Bone;
            destination.Rotation = source.Rotation;
            destination.LeftStanceRotation = source.LeftStanceRotation;
            destination.EyeGuardShift = source.EyeGuardShift;
        }

        private static void CopyHybridAimSource(
            FrontClipOnState state,
            ProceduralWeaponAnimation.SightNBone source,
            ProceduralWeaponAnimation.SightNBone destination,
            bool usesNestedOneXAim)
        {
            if (state.UsesWilcoxBossDonor)
            {
                CopyAimPoint(source, destination);
                if (usesNestedOneXAim
                    && source.Mod != null
                    && (SkeetIrChildZoomForwardPatch.IsSelectedAim(
                            state.Animation,
                            state.SkeetAim)
                        || SkeetIrChildZoomForwardPatch.IsSelectedAim(
                            state.Animation,
                            state.ChildAim)))
                {
                    // mod_aim_camera_001 is BOSS scope index zero. Keep its
                    // selected reticle mode, but do not let the collapsed
                    // Skeet/G33 proxy select the discarded iron camera.
                    source.Mod.SelectedScopeIndex = 0;
                }
                return;
            }

            CopyAimPose(source, destination);
        }

        private static void SetSkeetOpticLensVisibility(FrontClipOnState state)
        {
            foreach (var pair in state.OriginalSkeetOpticStates)
            {
                var optic = pair.Key;
                if (optic == null)
                {
                    continue;
                }

                var skeetOwnsThermalPip = state.ThermalUsesSkeetOptic
                    && state.Animation != null
                    && state.Animation.IsAiming
                    && state.Animation.FirstPersonPointOfView
                    && ReferenceEquals(
                        state.SkeetCache.CurrentModOpticSight,
                        optic);
                optic.enabled = skeetOwnsThermalPip;
                optic.LensFade(!skeetOwnsThermalPip && state.ThermalActive);
                if (optic.LensRenderer != null)
                {
                    bool originallyEnabled;
                    var wasOriginallyEnabled =
                        state.OriginalSkeetLensStates.TryGetValue(
                            optic.LensRenderer,
                            out originallyEnabled)
                        && originallyEnabled;
                    optic.LensRenderer.enabled = skeetOwnsThermalPip
                        || (!state.ThermalActive && wasOriginallyEnabled);
                }
            }
        }

        private static void EnsureChildOwnsOpticCamera(
            ProceduralWeaponAnimation animation,
            FrontClipOnState state,
            bool forceRefresh = false)
        {
            if (state.ThermalUsesSkeetOptic)
            {
                EnsureSkeetOwnsOpticCamera(animation, state, forceRefresh);
                return;
            }

            if (!animation.IsAiming
                || !animation.FirstPersonPointOfView
                || (!SkeetIrChildZoomForwardPatch.IsSelectedAim(
                        animation,
                        state.SkeetAim)
                    && !SkeetIrChildZoomForwardPatch.IsSelectedAim(
                        animation,
                        state.ChildAim)))
            {
                return;
            }

            var childOptic = state.ChildCache.CurrentModOpticSight;
            if (childOptic == null
                || childOptic.ScopeData == null
                || childOptic.CameraData == null)
            {
                return;
            }

            ScopeThermalVisionData originalThermal;
            if (!state.OriginalChildThermals.TryGetValue(
                childOptic,
                out originalThermal))
            {
                originalThermal = childOptic.ScopeData.ThermalVisionData;
                state.OriginalChildThermals.Add(childOptic, originalThermal);
            }

            childOptic.ScopeData.ThermalVisionData = state.ThermalActive
                ? state.ThermalData
                : originalThermal;

            if (childOptic.LensRenderer != null)
            {
                childOptic.LensRenderer.enabled = true;
            }

            var cameraClass = CameraManager.Instance;
            if (cameraClass == null)
            {
                return;
            }

            var manager = cameraClass.OpticCameraManager;
            var needsRefresh = state.CameraRefreshPending
                || forceRefresh
                || !ReferenceEquals(state.LastChildOptic, childOptic)
                || !ReferenceEquals(manager.CurrentOpticSight, childOptic);
            if (!needsRefresh)
            {
                SynchronizeLiveThermalState(
                    manager.Camera != null
                        ? manager.Camera.GetComponent<ThermalVision>()
                        : null,
                    state.ThermalActive);
                childOptic.LensFade(false);
                return;
            }

            // CopyComponentFromOptic caches by OpticSight instance id. Thermal
            // changes on that same Razor would otherwise be ignored, which is
            // exactly the stale 1x thermal view seen after returning inline.
            if (manager.Updater != null && OpticUpdaterInstanceIdField != null)
            {
                OpticUpdaterInstanceIdField.SetValue(manager.Updater, int.MinValue);
            }
            else if (OpticUpdaterInstanceIdField == null
                && !_missingUpdaterFieldWasLogged)
            {
                _missingUpdaterFieldWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogWarning(
                    "Could not reset the optic-camera component cache before applying "
                    + "Skeet-IR thermal state; same-optic thermal refresh may be stale.");
            }

            if (!childOptic.enabled)
            {
                childOptic.enabled = true;
            }

            if (!ReferenceEquals(manager.CurrentOpticSight, childOptic)
                || state.CameraRefreshPending)
            {
                manager.OnOpticSightEnabled(childOptic);
            }

            // OpticComponentUpdater only disables the ThermalVision behaviour
            // when ThermalVisionData becomes null. It does not clear the
            // behaviour's public On flag or the companion components toggled
            // by ThermalVision itself. That leaves the previous thermal frame
            // in the child's PiP even though Skeet has already folded aside.
            // Complete the live effect transition after EFT has copied the
            // child's new data to the shared optic camera.
            var liveThermal = SynchronizeLiveThermalState(
                manager.Camera != null
                    ? manager.Camera.GetComponent<ThermalVision>()
                    : null,
                state.ThermalActive,
                true);

            childOptic.LensFade(false);
            state.LastChildOptic = childOptic;
            state.CameraRefreshPending = false;

            MakeMagnifierGreatAgainPlugin.LogInfo(
                "Refreshed Skeet-IR front clip-on through child optic "
                + $"'{state.ChildCache.transform.name}' "
                + $"(thermal={(state.ThermalActive ? "on" : "off")}, "
                + $"liveEnabled={(liveThermal != null && liveThermal.enabled)}, "
                + $"liveOn={(liveThermal != null && liveThermal.On)}, "
                + $"adjustable={childOptic.CameraData.IsAdjustableOptic}).");
        }

        private static void EnsureSkeetOwnsOpticCamera(
            ProceduralWeaponAnimation animation,
            FrontClipOnState state,
            bool forceRefresh)
        {
            if (!animation.IsAiming || !animation.FirstPersonPointOfView)
            {
                SetSkeetOpticLensVisibility(state);
                return;
            }

            var skeetOptic = state.SkeetCache.CurrentModOpticSight;
            if (skeetOptic == null
                || skeetOptic.ScopeData == null
                || skeetOptic.CameraData == null)
            {
                return;
            }

            var cameraClass = CameraManager.Instance;
            if (cameraClass == null)
            {
                return;
            }

            var manager = cameraClass.OpticCameraManager;
            var needsRefresh = state.CameraRefreshPending
                || forceRefresh
                || !ReferenceEquals(state.LastChildOptic, skeetOptic)
                || !ReferenceEquals(manager.CurrentOpticSight, skeetOptic);

            if (needsRefresh
                && manager.Updater != null
                && OpticUpdaterInstanceIdField != null)
            {
                OpticUpdaterInstanceIdField.SetValue(manager.Updater, int.MinValue);
            }

            if (!skeetOptic.enabled)
            {
                skeetOptic.enabled = true;
            }

            if (needsRefresh)
            {
                manager.OnOpticSightEnabled(skeetOptic);
            }

            var liveThermal = SynchronizeLiveThermalState(
                manager.Camera != null
                    ? manager.Camera.GetComponent<ThermalVision>()
                    : null,
                true,
                needsRefresh);

            skeetOptic.LensFade(false);
            state.LastChildOptic = skeetOptic;
            state.CameraRefreshPending = false;

            if (needsRefresh)
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Refreshed hybrid 1x thermal PiP through Skeet-IR's optic "
                    + $"for '{state.ChildCache.transform.name}' "
                    + $"(liveEnabled={(liveThermal != null && liveThermal.enabled)}, "
                    + $"liveOn={(liveThermal != null && liveThermal.On)}).");
            }
        }

        private static ThermalVision SynchronizeLiveThermalState(
            ThermalVision liveThermal,
            bool thermalActive,
            bool forceImmediateApply = false)
        {
            if (liveThermal == null)
            {
                return null;
            }

            var alreadySynchronized = liveThermal.On == thermalActive
                && liveThermal.enabled == thermalActive;
            if (!forceImmediateApply && alreadySynchronized)
            {
                return liveThermal;
            }

            if (ApplyLiveThermalStateMethod != null)
            {
                ApplyLiveThermalStateMethod.Invoke(
                    liveThermal,
                    new object[] { thermalActive });
            }
            else
            {
                // Keep the essential state correct if a future publicized EFT
                // assembly renames the internal immediate-apply method.
                liveThermal.On = thermalActive;
                if (!_missingLiveThermalMethodWasLogged)
                {
                    _missingLiveThermalMethodWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        "Could not find ThermalVision.method_1(bool); applied only "
                        + "the live On/enabled state for Skeet-IR.");
                }
            }

            // Disable only after the immediate switch has restored its
            // dependent effects; enable after it has configured them.
            liveThermal.enabled = thermalActive;
            return liveThermal;
        }

        internal static void LateUpdateFrontClipOn(
            ProceduralWeaponAnimation animation,
            FrontClipOnState state)
        {
            if (!IsActiveFrontClipOnState(animation, state))
            {
                return;
            }

            RestoreHybridOneXAim(state);
            var forceRefresh = state.LateRefreshFrames > 0;
            EnsureChildOwnsOpticCamera(animation, state, forceRefresh);
            if (forceRefresh)
            {
                state.LateRefreshFrames--;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.CycleScopes))]
        private static bool CycleScopesPrefix(
            ProceduralWeaponAnimation __instance,
            int currentScopeIndex)
        {
            // AimIndex has already changed when EFT invokes CycleScopes. Put
            // the outer Skeet prefab into the destination mode before
            // EFT calculates alignment, camera depth, aim plane, and FOV.
            // Changing the mode later from method_1 leaves those calculations
            // based on the previously active hierarchy and produces the
            // partially restored camera position seen after returning inline.
            if (currentScopeIndex < 0
                || currentScopeIndex >= __instance.ScopeAimTransforms.Count)
            {
                return true;
            }

            FrontClipOnState frontClipOn;
            if (TryGetFrontClipOnState(__instance, out frontClipOn))
            {
                var selectedThermal = ReferenceEquals(
                    __instance.ScopeAimTransforms[currentScopeIndex],
                    frontClipOn.SkeetAim);
                ApplyFrontClipOnState(frontClipOn, selectedThermal);
                return true;
            }

            FixedOneXState fixedOneX;
            if (TryGetFixedOneXState(__instance, out fixedOneX))
            {
                RestoreFixedOneXAim(fixedOneX);
            }

            var skeetAim = NestedSightAimPatch.FindSkeetIrAim(__instance);
            if (skeetAim == null)
            {
                return true;
            }

            var skeetCache = skeetAim.ScopePrefabCache;
            var destinationAim = __instance.ScopeAimTransforms[currentScopeIndex];
            var childCache = NestedSightAimPatch.FindImmediateChildScope(
                skeetCache.transform);

            if (ReferenceEquals(destinationAim, skeetAim))
            {
                ConfigureThermalReticle(skeetCache);
                ApplyMode(skeetCache, 0);
            }
            else if (childCache != null
                && ReferenceEquals(destinationAim.ScopePrefabCache, childCache))
            {
                ApplyMode(skeetCache, 1);
            }

            // EFT must still perform its complete native aim-point transition.
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProceduralWeaponAnimation), "method_1")]
        private static void WeaponUpdatePrefix(ProceduralWeaponAnimation __instance)
        {
            if (__instance.ScopeAimTransforms == null)
            {
                return;
            }

            FrontClipOnState frontClipOn;
            if (TryGetFrontClipOnState(__instance, out frontClipOn))
            {
                ApplyFrontClipOnState(
                    frontClipOn,
                    SkeetIrChildZoomForwardPatch.IsSelectedAim(
                        __instance,
                        frontClipOn.SkeetAim));
                return;
            }

            FixedOneXState fixedOneX;
            if (TryGetFixedOneXState(__instance, out fixedOneX))
            {
                RestoreFixedOneXAim(fixedOneX);
            }

            var skeetAim = NestedSightAimPatch.FindSkeetIrAim(__instance);
            if (skeetAim == null)
            {
                return;
            }

            var skeetCache = skeetAim.ScopePrefabCache;

            // Let EFT change AimIndex normally. method_1 runs after that state
            // has changed, so it remains the last known working place to match
            // the physical Skeet mode to the selected aim point.
            var selectedSkeet = SkeetIrChildZoomForwardPatch.IsSelectedAim(
                __instance,
                skeetAim);
            if (selectedSkeet)
            {
                ConfigureThermalReticle(skeetCache);
            }

            ApplyMode(skeetCache, selectedSkeet ? 0 : 1);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProceduralWeaponAnimation), "method_1")]
        private static void WeaponUpdatePostfix(ProceduralWeaponAnimation __instance)
        {
            FrontClipOnState frontClipOn;
            if (TryGetFrontClipOnState(__instance, out frontClipOn)
                && IsActiveFrontClipOnState(__instance, frontClipOn))
            {
                SetSkeetOpticLensVisibility(frontClipOn);
                EnsureChildOwnsOpticCamera(__instance, frontClipOn);
            }
        }

        private static void ConfigureThermalReticle(ScopePrefabCache skeetCache)
        {
            if (skeetCache == null || !skeetCache.HasOptics)
            {
                return;
            }

            var optic = skeetCache.FirstOptic;
            var targetRenderer = optic != null ? optic.LensRenderer : null;
            if (targetRenderer == null)
            {
                return;
            }

            var targetMaterial = targetRenderer.material;
            if (targetMaterial == null || !targetMaterial.HasProperty("_MarkTex"))
            {
                return;
            }

            // Use Skeet's immediate child as the sight source. The sole extra
            // hop is G33 -> its immediate 1x child because the G33's reticle is
            // a live physical capture, not a baked _MarkTex. This remains
            // bounded and cannot wander into an LPVO/ACOG piggyback slot.
            var childCache = NestedSightAimPatch.FindImmediateChildScope(skeetCache.transform);
            var reticleCache = childCache;
            if (reticleCache != null
                && NestedSightAimPatch.IsG33RootName(reticleCache.transform.name))
            {
                reticleCache = NestedSightAimPatch.FindImmediateChildScope(
                    reticleCache.transform);
            }

            var modeInfo = GetCurrentModeInfo(reticleCache);
            var sourceOptic = modeInfo != null ? modeInfo.OpticSight : null;
            // The proven direct 1x path uses the immediate collimator's mark,
            // even when that sight also exposes an OpticSight component.
            var directOneXCollimator = childCache != null
                && childCache.HasCollimators
                && !NestedSightAimPatch.IsG33RootName(childCache.transform.name);
            var sourceReticle = !directOneXCollimator
                && sourceOptic != null
                && sourceOptic.ScopeData != null
                ? sourceOptic.ScopeData.Reticle
                : null;

            // Magnified optics use EFT's ScopeReticle mesh/material rather
            // than a baked collimator texture. Hand the active child reticle
            // to Skeet's OpticSight so EFT draws it with its native geometry.
            if (optic.ScopeData != null)
            {
                optic.ScopeData.Reticle = sourceReticle;
            }

            if (sourceReticle != null && sourceReticle.Mesh != null)
            {
                targetMaterial.SetTexture("_MarkTex", Texture2D.blackTexture);
                LogThermalReticleSource(
                    skeetCache,
                    childCache,
                    reticleCache,
                    $"scope-reticle mesh={sourceReticle.Mesh.name}");
                return;
            }

            var collimator = directOneXCollimator
                ? childCache.FirstCollimator
                : modeInfo != null ? modeInfo.CollimatorSight : null;
            if (collimator != null)
            {
                // Keep the mark on its original collimator mesh. The main
                // camera runtime renders that projection into an independent
                // screen-space texture which Skeet's final lens pass samples.
                // This preserves the donor projection without relocating
                // _MarkTex onto Skeet's lens or relying on final draw order.
                DisableSkeetThermalReticle(skeetCache);
                KeepOneXChildCollimatorActive(childCache);
                LogThermalReticleSource(
                    skeetCache,
                    childCache,
                    reticleCache,
                    "native child collimator=" + collimator.transform.name);
                return;
            }

            // A 1x sight without a live collimator has no native mark that can
            // remain on the child. Do not fall back to copying its texture to
            // Skeet, because that recreates the position/scale mismatch this
            // path is specifically intended to avoid.
            DisableSkeetThermalReticle(skeetCache);
            LogThermalReticleSource(
                skeetCache,
                childCache,
                reticleCache,
                "no live collimator or scope reticle; thermal mark disabled");
        }

        internal static void DisableSkeetThermalReticle(
            ScopePrefabCache skeetCache)
        {
            if (skeetCache == null || !skeetCache.HasOptics)
            {
                return;
            }

            var optic = skeetCache.FirstOptic;
            if (optic == null)
            {
                return;
            }

            if (optic.ScopeData != null)
            {
                optic.ScopeData.Reticle = null;
            }

            var renderer = optic.LensRenderer;
            var material = renderer != null ? renderer.material : null;
            if (material != null && material.HasProperty("_MarkTex"))
            {
                material.SetTexture("_MarkTex", Texture2D.blackTexture);
            }
        }

        internal static void KeepOneXChildCollimatorActive(
            ScopePrefabCache childCache)
        {
            var reticleCache = childCache;
            var customG33 = reticleCache != null
                && NestedSightAimPatch.IsG33RootName(reticleCache.transform.name);
            if (customG33)
            {
                reticleCache = NestedSightAimPatch.FindImmediateChildScope(
                    reticleCache.transform);
            }

            var modeInfo = GetCurrentModeInfo(reticleCache);
            var collimator = modeInfo != null
                ? modeInfo.CollimatorSight
                : null;
            if (collimator == null
                && reticleCache != null
                && reticleCache.HasCollimators)
            {
                collimator = reticleCache.FirstCollimator;
            }

            if (collimator == null)
            {
                return;
            }

            // A conventional hybrid such as HHS-1 owns its 1x collimator
            // through the active ScopeModeInfo.ModeGameObject. Do not force
            // that component or renderer on from a second lifecycle: EFT's
            // native OnEnable/OnDisable events also register and unregister
            // the collimator with its mask/SSAA pipeline. The custom G33 is
            // different because its donor is a collapsed nested sight rather
            // than the outer mode object.
            if (!customG33
                && childCache != null
                && childCache.ModesCount > 1
                && modeInfo != null
                && ReferenceEquals(modeInfo.CollimatorSight, collimator))
            {
                return;
            }

            if (!collimator.gameObject.activeSelf)
            {
                collimator.gameObject.SetActive(true);
            }
            if (!collimator.enabled)
            {
                collimator.enabled = true;
            }
            if (collimator.CollimatorMeshRenderer != null
                && !collimator.CollimatorMeshRenderer.enabled)
            {
                collimator.CollimatorMeshRenderer.enabled = true;
            }

            // Keep the original component alive so EFT's existing LookAt and
            // collimator-update lifecycle continues to own its projection.
            // Do not redraw or relocate the mark through Skeet's material.
        }

        private static ScopePrefabCache.ScopeModeInfo GetCurrentModeInfo(
            ScopePrefabCache cache)
        {
            if (cache == null || cache.ModesCount == 0)
            {
                return null;
            }

            var mode = cache.CurrentModeId;
            if (mode < 0 || mode >= cache.ModesCount)
            {
                mode = 0;
            }

            return cache.GetScopeModeInfo(mode);
        }

        private static void LogThermalReticleSource(
            ScopePrefabCache skeetCache,
            ScopePrefabCache childCache,
            ScopePrefabCache reticleCache,
            string sourceDescription)
        {
            var childName = childCache != null ? childCache.transform.name : "<missing>";
            var reticleName = reticleCache != null ? reticleCache.transform.name : "<missing>";
            var key = skeetCache.GetInstanceID() + "|" + childName
                + "|" + reticleName + "|" + sourceDescription;
            if (!LoggedReticleSources.Add(key))
            {
                return;
            }

            MakeMagnifierGreatAgainPlugin.LogInfo(
                "Skeet-IR thermal reticle configured from direct child "
                + $"'{childName}' (reticle source '{reticleName}'): {sourceDescription}.");
        }

        private static void ApplyMode(ScopePrefabCache skeetCache, int mode)
        {
            if (skeetCache == null || skeetCache.ModesCount <= mode)
            {
                return;
            }

            if (skeetCache.CurrentModeId != mode)
            {
                skeetCache.SetMode(mode);
            }

            var instanceId = skeetCache.GetInstanceID();
            if (_lastLoggedSkeetInstanceId != instanceId || _lastLoggedMode != mode)
            {
                _lastLoggedSkeetInstanceId = instanceId;
                _lastLoggedMode = mode;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    $"Skeet-IR sight selection applied bundle mode_{mode:000}.");
            }
        }

    }

    internal sealed class SkeetIrAimBonePoseGuard : MonoBehaviour
    {
        private bool _captured;
        private Vector3 _localPosition;
        private Quaternion _localRotation;

        internal void Capture()
        {
            if (_captured)
            {
                return;
            }

            _localPosition = transform.localPosition;
            _localRotation = transform.localRotation;
            _captured = true;
        }

        internal bool Restore()
        {
            if (!_captured)
            {
                return false;
            }

            var changed = (transform.localPosition - _localPosition).sqrMagnitude
                > 0.0000000001f
                || Quaternion.Angle(transform.localRotation, _localRotation)
                > 0.0001f;
            transform.localPosition = _localPosition;
            transform.localRotation = _localRotation;
            return changed;
        }
    }

    /// <summary>
    /// EFT aligns an optic by writing its calculated optic-camera position
    /// directly into the selected aim bone. That is invalid when a collimator
    /// mode has no active optic, or when Skeet borrows a child's aim bone.
    /// Restore the prefab pose and skip that write in those two cases.
    /// </summary>
    [HarmonyPatch(
        typeof(ProceduralWeaponAnimation),
        nameof(ProceduralWeaponAnimation.OpticLookAtGlobal))]
    internal static class SkeetIrAimBoneAlignmentPatch
    {
        private static readonly HashSet<int> LoggedCaches = new HashSet<int>();

        [HarmonyPrefix]
        private static bool Prefix(ProceduralWeaponAnimation __instance)
        {
            SkeetIrSightSelectionPatch.FrontClipOnState frontClipOn;
            if (SkeetIrSightSelectionPatch.TryGetFrontClipOnState(
                    __instance,
                    out frontClipOn)
                && SkeetIrSightSelectionPatch.UsesHybridOneXAim(frontClipOn)
                && SkeetIrChildZoomForwardPatch.IsSelectedAim(
                    __instance,
                    frontClipOn.SkeetAim))
            {
                // The folded custom G33 retains a nested 1x aim point outside
                // EFT's rebuilt aim list. Protect it explicitly when Skeet's
                // thermal selector is active after a weapon swap.
                SkeetIrSightSelectionPatch.RestoreHybridOneXAim(frontClipOn);
                return false;
            }

            SkeetIrSightSelectionPatch.FixedOneXState fixedOneX;
            if (SkeetIrSightSelectionPatch.TryGetFixedOneXState(
                    __instance,
                    out fixedOneX)
                && (SkeetIrChildZoomForwardPatch.IsSelectedAim(
                        __instance,
                        fixedOneX.SkeetAim)
                    || SkeetIrChildZoomForwardPatch.IsSelectedAim(
                        __instance,
                        fixedOneX.ChildAim)))
            {
                // Both fixed-1x selector entries use the child's original
                // mod_aim_camera. Do not let either entry write an
                // optic_camera pose into that shared transform.
                SkeetIrSightSelectionPatch.RestoreFixedOneXAim(fixedOneX);
                return false;
            }

            var aim = __instance != null ? __instance.CurrentScope : null;
            var guard = aim != null && aim.Bone != null
                ? aim.Bone.GetComponent<SkeetIrAimBonePoseGuard>()
                : null;
            var cache = aim != null ? aim.ScopePrefabCache : null;
            if (guard == null || cache == null)
            {
                return true;
            }

            var boneOwner = aim.Bone.GetComponentInParent<ScopePrefabCache>();
            if (cache.CurrentModHasOptics && ReferenceEquals(cache, boneOwner))
            {
                return true;
            }

            guard.Restore();
            if (LoggedCaches.Add(cache.GetInstanceID()))
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Protected mod_aim_camera pose for '" + cache.transform.name
                    + $"' (mode={cache.CurrentModeId}, borrowed={boneOwner != cache}).");
            }
            return false;
        }
    }

    /// <summary>
    /// The Skeet lens shader composites the shared optic texture, which still
    /// contains its last frame after ADS ends. Gate only that display surface;
    /// the physical clip-on body remains visible when folded aside.
    /// </summary>
    internal sealed class SkeetIrLensVisibilityRuntime : MonoBehaviour
    {
        private const string VisibleProperty = "_MMGA_ThermalVisible";
        private const string OpticalMaskProperty = "_MMGA_OpticalMask";
        private const string OpticalMaskEnabledProperty =
            "_MMGA_OpticalMaskEnabled";
        private const string ReticleProjectionProperty =
            "_MMGA_ReticleProjection";
        private const string ReticleProjectionEnabledProperty =
            "_MMGA_ReticleProjectionEnabled";
        private const string OpticalMaskShaderName =
            "SkeetIR/OpticalWindowMask";
        private const string OpticalMaskMaxViewDepthProperty =
            "_MMGA_MaxViewDepth";
        private static readonly HashSet<int> LoggedForegroundRoots =
            new HashSet<int>();
        private static bool _missingOpticalMaskShaderWasLogged;
        private ProceduralWeaponAnimation _animation;
        private ProceduralWeaponAnimation.SightNBone _skeetAim;
        private Material _lensMaterial;
        private Renderer _skeetLensRenderer;
        private Material _opticalMaskMaterial;
        private Material _skeetFrameMaskMaterial;
        private RenderTexture _opticalMaskTexture;
        private CommandBuffer _opticalMaskBuffer;
        private Renderer _oneXReticleRenderer;
        private Mesh _oneXReticleMesh;
        private CollimatorSight _oneXReticleSourceCollimator;
        private Material _oneXReticleSourceMaterial;
        private Material _oneXReticleProjectionMaterial;
        private RenderTexture _oneXReticleProjectionTexture;
        private CommandBuffer _oneXReticleProjectionBuffer;
        private SkeetIrDrawOnlyReticleOverlay _oneXReticleOverlay;
        private bool _oneXUsesDrawOnlyOverlay;
        private int _oneXReticleRootId;
        private static readonly HashSet<int> LoggedReticleProjectionRoots =
            new HashSet<int>();
        private static readonly HashSet<int> LoggedOneXReticleSources =
            new HashSet<int>();
        private static readonly HashSet<int> LoggedDrawOnlyReticleOverlayRoots =
            new HashSet<int>();

        internal void Initialize(
            ProceduralWeaponAnimation animation,
            ProceduralWeaponAnimation.SightNBone skeetAim)
        {
            DisableOpticalMask();
            ReleaseOpticalMaskResources();
            DisableOneXReticleProjection();
            ReleaseOneXReticleResources();
            _animation = animation;
            _skeetAim = skeetAim;
            var optic = skeetAim.ScopePrefabCache.FirstOptic;
            var renderer = optic != null ? optic.LensRenderer : null;
            _skeetLensRenderer = renderer;
            _lensMaterial = renderer != null ? renderer.material : null;
            SetVisible(false);
        }

        private void OnEnable()
        {
            Camera.onPreCull += OnCameraPreCull;
        }

        private void LateUpdate()
        {
            var thermalSelected = _animation != null
                && _skeetAim != null
                && SkeetIrChildZoomForwardPatch.IsSelectedAim(
                    _animation,
                    _skeetAim);
            var oneXChild = thermalSelected
                ? FindActiveOneXForegroundRoot()
                : null;
            if (oneXChild != null)
            {
                // Front-clip-on setup turns the public Skeet selector entry
                // into a stable proxy for the child sight. Its
                // ScopePrefabCache therefore points at HHS/G33 here, not at
                // the physical Skeet cache. Clearing the reticle through that
                // proxy permanently blackened the HHS magnified _MarkTex and
                // nulled its ScopeData.Reticle after the first 3x -> 1x
                // transition. Always resolve the separately retained real
                // Skeet cache before mutating the thermal lens.
                var skeetCache = FindRegisteredSkeetCache();
                SkeetIrSightSelectionPatch.DisableSkeetThermalReticle(
                    skeetCache);
                SkeetIrSightSelectionPatch.KeepOneXChildCollimatorActive(
                    oneXChild);
            }

            var visible = thermalSelected
                && _animation.IsAiming
                && _animation.FirstPersonPointOfView;
            SetVisible(visible);
            if (!visible)
            {
                DisableOpticalMask();
            }
        }

        private void OnDisable()
        {
            Camera.onPreCull -= OnCameraPreCull;
            SetVisible(false);
            DisableOpticalMask();
            ReleaseOpticalMaskResources();
            DisableOneXReticleProjection();
            ReleaseOneXReticleResources();
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            SetVisible(false);
            DisableOpticalMask();
            ReleaseOpticalMaskResources();
            DisableOneXReticleProjection();
            ReleaseOneXReticleResources();
        }

        private void OnCameraPreCull(Camera camera)
        {
            var cameraManager = CameraManager.Instance;
            if (camera == null
                || cameraManager == null
                || !ReferenceEquals(cameraManager.Camera, camera))
            {
                return;
            }

            var foregroundRoot = IsThermalDisplayVisible()
                ? FindActiveOneXForegroundRoot()
                : null;
            if (foregroundRoot == null)
            {
                DisableOpticalMask();
                DisableOneXReticleProjection();
                ReleaseOneXReticleResources();
                return;
            }

            if (!RenderOneXReticleProjection(camera, foregroundRoot))
            {
                DisableOneXReticleProjection();
            }

            int occluderCount;
            int skeetFrameOccluderCount;
            if (!RenderForegroundOcclusionMask(
                    camera,
                    foregroundRoot,
                    out occluderCount,
                    out skeetFrameOccluderCount))
            {
                DisableOpticalMask();
            }
            else
            {
                if (_lensMaterial != null)
                {
                    _lensMaterial.SetTexture(
                        OpticalMaskProperty,
                        _opticalMaskTexture);
                    _lensMaterial.SetFloat(OpticalMaskEnabledProperty, 1f);
                }

                if (LoggedForegroundRoots.Add(foregroundRoot.GetInstanceID()))
                {
                    MakeMagnifierGreatAgainPlugin.LogInfo(
                        "Skeet-IR 1x thermal view now preserves "
                        + occluderCount
                        + " foreground housing/mount renderer pass(es) from '"
                        + foregroundRoot.transform.name
                        + "' plus "
                        + skeetFrameOccluderCount
                        + " solid Skeet frame renderer pass(es); all optical "
                        + "surfaces remain excluded.");
                }
            }
        }

        private void PrepareOneXReticleProjection(
            ScopePrefabCache foregroundRoot)
        {
            var rootId = foregroundRoot.GetInstanceID();
            var preferNestedChild = NestedSightAimPatch.IsG33RootName(
                foregroundRoot.transform.name);
            var selectedCollimator =
                SkeetIrSightSelectionPatch.FindProjectedCollimator(
                    foregroundRoot,
                    preferNestedChild);
            if (_oneXReticleRootId != rootId
                || !ReferenceEquals(
                    _oneXReticleSourceCollimator,
                    selectedCollimator)
                || _oneXReticleRenderer == null
                || _oneXReticleMesh == null
                || _oneXReticleProjectionMaterial == null)
            {
                ReleaseOneXReticleResources();
                _oneXReticleSourceCollimator = selectedCollimator;
                SkeetIrSightSelectionPatch.CreateProjectedCollimatorMaterial(
                    foregroundRoot,
                    preferNestedChild,
                    out _oneXReticleRenderer,
                    out _oneXReticleMesh,
                    out _oneXReticleProjectionMaterial);
                _oneXReticleRootId = rootId;
                _oneXUsesDrawOnlyOverlay =
                    NestedSightAimPatch.RequiresCompactReticleOverlay(
                        _oneXReticleSourceCollimator);
                var rendererMaterial = _oneXReticleRenderer != null
                    ? _oneXReticleRenderer.sharedMaterial
                    : null;
                _oneXReticleSourceMaterial = _oneXUsesDrawOnlyOverlay
                    ? rendererMaterial
                    : _oneXReticleSourceCollimator != null
                        ? _oneXReticleSourceCollimator.CollimatorMaterial
                        : null;
                if (_oneXReticleSourceMaterial == null
                    && _oneXReticleSourceCollimator != null)
                {
                    _oneXReticleSourceMaterial =
                        _oneXReticleSourceCollimator.CollimatorMaterial;
                }
                if (_oneXReticleSourceMaterial == null)
                {
                    _oneXReticleSourceMaterial = rendererMaterial;
                }
            }
        }

        private bool RenderOneXReticleProjection(
            Camera camera,
            ScopePrefabCache foregroundRoot)
        {
            PrepareOneXReticleProjection(foregroundRoot);
            if (_oneXReticleRenderer == null
                || _oneXReticleMesh == null
                || _oneXReticleSourceMaterial == null
                || _oneXReticleProjectionMaterial == null)
            {
                return false;
            }

            SyncOneXReticleProjectionProperties();
            var sourceId = _oneXReticleSourceCollimator != null
                ? _oneXReticleSourceCollimator.GetInstanceID()
                : _oneXReticleRootId;
            if (LoggedOneXReticleSources.Add(sourceId))
            {
                var sourceMark = _oneXReticleSourceMaterial.HasProperty(
                    "_MarkTex")
                    ? _oneXReticleSourceMaterial.GetTexture("_MarkTex")
                    : null;
                var projectedMark = _oneXReticleProjectionMaterial.HasProperty(
                    "_MarkTex")
                    ? _oneXReticleProjectionMaterial.GetTexture("_MarkTex")
                    : null;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Skeet-IR 1x live mark diagnostic: root='"
                    + foregroundRoot.transform.name
                    + "', sourceMaterial='"
                    + _oneXReticleSourceMaterial.name
                    + "', sourceMark='"
                    + (sourceMark != null ? sourceMark.name : "<missing>")
                    + "', projectedMark='"
                    + (projectedMark != null
                        ? projectedMark.name : "<missing>")
                    + "', drawOnly=" + _oneXUsesDrawOnlyOverlay + ".");
            }
            if (_oneXUsesDrawOnlyOverlay)
            {
                DisableLensReticleProjection();
                EnableOneXDrawOnlyReticleOverlay(camera);
                return true;
            }

            DisableOneXDrawOnlyReticleOverlay();
            EnsureOneXReticleProjectionTexture(camera);
            if (_oneXReticleProjectionTexture == null)
            {
                return false;
            }

            if (_oneXReticleProjectionBuffer == null)
            {
                _oneXReticleProjectionBuffer = new CommandBuffer
                {
                    name = "MMGA Skeet-IR child reticle projection"
                };
            }

            _oneXReticleProjectionBuffer.Clear();
            _oneXReticleProjectionBuffer.SetRenderTarget(
                _oneXReticleProjectionTexture);
            _oneXReticleProjectionBuffer.ClearRenderTarget(
                false,
                true,
                Color.clear);
            _oneXReticleProjectionBuffer.SetViewProjectionMatrices(
                camera.worldToCameraMatrix,
                GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));
            var cameraPosition = camera.transform.position;
            Shader.SetGlobalVector(
                "_WorldSpaceCameraPos",
                new Vector4(
                    cameraPosition.x,
                    cameraPosition.y,
                    cameraPosition.z,
                    1f));
            _oneXReticleProjectionBuffer.DrawMesh(
                _oneXReticleMesh,
                _oneXReticleRenderer.localToWorldMatrix,
                _oneXReticleProjectionMaterial);
            Graphics.ExecuteCommandBuffer(_oneXReticleProjectionBuffer);

            if (_lensMaterial == null
                || !_lensMaterial.HasProperty(ReticleProjectionProperty)
                || !_lensMaterial.HasProperty(
                    ReticleProjectionEnabledProperty))
            {
                return false;
            }
            _lensMaterial.SetTexture(
                ReticleProjectionProperty,
                _oneXReticleProjectionTexture);
            _lensMaterial.SetFloat(ReticleProjectionEnabledProperty, 1f);

            var rootId = _oneXReticleRootId;
            if (LoggedReticleProjectionRoots.Add(rootId))
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Skeet-IR 1x thermal now composites a screen-space texture "
                    + "rendered from the child's original collimator mesh and "
                    + "projection; no main-camera redraw or lens cutout is used.");
            }
            return true;
        }

        private void SyncOneXReticleProjectionProperties()
        {
            // Match EFT's ScopeMaskRenderer: CollimatorMaterial is the
            // authoritative projected-mark source. HHS mode transitions can
            // replace or temporarily invalidate the renderer material while
            // the CollimatorSight retains the correct mark properties.
            var liveMaterial = _oneXUsesDrawOnlyOverlay
                && _oneXReticleRenderer != null
                ? _oneXReticleRenderer.sharedMaterial
                : _oneXReticleSourceCollimator != null
                    ? _oneXReticleSourceCollimator.CollimatorMaterial
                    : null;
            if (liveMaterial == null)
            {
                liveMaterial = _oneXReticleRenderer.sharedMaterial;
            }
            if (liveMaterial != null)
            {
                _oneXReticleSourceMaterial = liveMaterial;
            }

            if (_oneXReticleSourceMaterial == null)
            {
                return;
            }

            CopyTextureProperty("_MarkTex");
            if (_oneXUsesDrawOnlyOverlay)
            {
                if (_oneXReticleProjectionMaterial.HasProperty("_NoiseTex"))
                {
                    _oneXReticleProjectionMaterial.SetTexture(
                        "_NoiseTex",
                        Texture2D.whiteTexture);
                }
                if (_oneXReticleProjectionMaterial.HasProperty("_FadeTex"))
                {
                    _oneXReticleProjectionMaterial.SetTexture(
                        "_FadeTex",
                        Texture2D.whiteTexture);
                }
            }
            else
            {
                CopyTextureProperty("_NoiseTex");
                CopyTextureProperty("_FadeTex");
            }
            CopyVectorProperty("_MarkShift");
            CopyFloatProperty("_MarkScale");
            CopyFloatProperty("_HDR");

            if (_oneXReticleSourceMaterial.HasProperty("_Color")
                && _oneXReticleProjectionMaterial.HasProperty("_Color"))
            {
                var color = _oneXReticleSourceMaterial.GetColor("_Color");
                if (Mathf.Max(color.r, Mathf.Max(color.g, color.b)) > 0.01f)
                {
                    _oneXReticleProjectionMaterial.SetColor("_Color", color);
                }
            }
        }

        private void CopyTextureProperty(string propertyName)
        {
            if (_oneXReticleSourceMaterial.HasProperty(propertyName)
                && _oneXReticleProjectionMaterial.HasProperty(propertyName))
            {
                _oneXReticleProjectionMaterial.SetTexture(
                    propertyName,
                    _oneXReticleSourceMaterial.GetTexture(propertyName));
            }
        }

        private void CopyVectorProperty(string propertyName)
        {
            if (_oneXReticleSourceMaterial.HasProperty(propertyName)
                && _oneXReticleProjectionMaterial.HasProperty(propertyName))
            {
                _oneXReticleProjectionMaterial.SetVector(
                    propertyName,
                    _oneXReticleSourceMaterial.GetVector(propertyName));
            }
        }

        private void CopyFloatProperty(string propertyName)
        {
            if (_oneXReticleSourceMaterial.HasProperty(propertyName)
                && _oneXReticleProjectionMaterial.HasProperty(propertyName))
            {
                _oneXReticleProjectionMaterial.SetFloat(
                    propertyName,
                    _oneXReticleSourceMaterial.GetFloat(propertyName));
            }
        }

        private void EnsureOneXReticleProjectionTexture(Camera camera)
        {
            var width = Mathf.Max(1, camera.pixelWidth);
            var height = Mathf.Max(1, camera.pixelHeight);
            if (_oneXReticleProjectionTexture != null
                && _oneXReticleProjectionTexture.width == width
                && _oneXReticleProjectionTexture.height == height)
            {
                return;
            }

            ReleaseOneXReticleProjectionTexture();
            var format = SystemInfo.SupportsRenderTextureFormat(
                RenderTextureFormat.ARGBHalf)
                ? RenderTextureFormat.ARGBHalf
                : RenderTextureFormat.ARGB32;
            _oneXReticleProjectionTexture = new RenderTexture(
                width,
                height,
                0,
                format,
                RenderTextureReadWrite.Linear)
            {
                name = "MMGA Skeet-IR child reticle projection",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _oneXReticleProjectionTexture.Create();
        }

        private void DisableOneXReticleProjection()
        {
            DisableLensReticleProjection();
            DisableOneXDrawOnlyReticleOverlay();
        }

        private void DisableLensReticleProjection()
        {
            if (_lensMaterial != null
                && _lensMaterial.HasProperty(
                    ReticleProjectionEnabledProperty))
            {
                _lensMaterial.SetFloat(ReticleProjectionEnabledProperty, 0f);
            }
        }

        private void EnableOneXDrawOnlyReticleOverlay(Camera camera)
        {
            if (camera == null
                || _oneXReticleRenderer == null
                || _oneXReticleMesh == null
                || _oneXReticleProjectionMaterial == null)
            {
                DisableOneXDrawOnlyReticleOverlay();
                return;
            }

            _oneXReticleOverlay =
                camera.GetComponent<SkeetIrDrawOnlyReticleOverlay>();
            if (_oneXReticleOverlay == null)
            {
                _oneXReticleOverlay = camera.gameObject
                    .AddComponent<SkeetIrDrawOnlyReticleOverlay>();
            }

            _oneXReticleOverlay.Configure(
                this,
                _oneXReticleRenderer,
                _oneXReticleMesh,
                _oneXReticleProjectionMaterial);

            if (LoggedDrawOnlyReticleOverlayRoots.Add(_oneXReticleRootId))
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Skeet-IR 1x thermal will redraw the selected compact donor mark "
                    + "through a draw-only AfterEverything command buffer; "
                    + "the main-camera image is never copied or blitted.");
            }
        }

        private void DisableOneXDrawOnlyReticleOverlay()
        {
            if (_oneXReticleOverlay != null)
            {
                _oneXReticleOverlay.Disable(this);
            }
        }

        private void ReleaseOneXReticleProjectionTexture()
        {
            if (_oneXReticleProjectionTexture == null)
            {
                return;
            }

            _oneXReticleProjectionTexture.Release();
            UnityEngine.Object.Destroy(_oneXReticleProjectionTexture);
            _oneXReticleProjectionTexture = null;
        }

        private void ReleaseOneXReticleResources()
        {
            DisableOneXDrawOnlyReticleOverlay();
            if (_oneXReticleProjectionBuffer != null)
            {
                _oneXReticleProjectionBuffer.Release();
                _oneXReticleProjectionBuffer = null;
            }
            ReleaseOneXReticleProjectionTexture();
            if (_oneXReticleProjectionMaterial != null)
            {
                UnityEngine.Object.Destroy(_oneXReticleProjectionMaterial);
            }

            _oneXReticleRenderer = null;
            _oneXReticleMesh = null;
            _oneXReticleSourceCollimator = null;
            _oneXReticleSourceMaterial = null;
            _oneXReticleProjectionMaterial = null;
            _oneXReticleOverlay = null;
            _oneXUsesDrawOnlyOverlay = false;
            _oneXReticleRootId = 0;
        }

        private void SetVisible(bool visible)
        {
            if (_lensMaterial != null && _lensMaterial.HasProperty(VisibleProperty))
            {
                _lensMaterial.SetFloat(VisibleProperty, visible ? 1f : 0f);
            }
        }

        private bool IsThermalDisplayVisible()
        {
            if (_animation == null
                || _skeetAim == null
                || !_animation.IsAiming
                || !_animation.FirstPersonPointOfView
                || !SkeetIrChildZoomForwardPatch.IsSelectedAim(
                    _animation,
                    _skeetAim))
            {
                return false;
            }

            var localPlayer = EFT.GamePlayerOwner.MyPlayer;
            return localPlayer == null
                || ReferenceEquals(
                    localPlayer.ProceduralWeaponAnimation,
                    _animation);
        }

        private ScopePrefabCache FindActiveOneXForegroundRoot()
        {
            ScopePrefabCache foregroundRoot = null;
            SkeetIrSightSelectionPatch.FixedOneXState fixedOneX;
            if (SkeetIrSightSelectionPatch.TryGetFixedOneXState(
                    _animation,
                    out fixedOneX)
                && ReferenceEquals(fixedOneX.SkeetAim, _skeetAim))
            {
                foregroundRoot = fixedOneX.ChildCache;
            }
            else
            {
                SkeetIrSightSelectionPatch.FrontClipOnState frontClipOn;
                if (!SkeetIrSightSelectionPatch.TryGetFrontClipOnState(
                        _animation,
                        out frontClipOn)
                    || !ReferenceEquals(frontClipOn.SkeetAim, _skeetAim)
                    || !frontClipOn.ThermalUsesSkeetOptic)
                {
                    return null;
                }

                // Keep the complete immediate-child hierarchy. For the custom
                // G33 1x route this includes both the flipped magnifier/mount
                // and its nested collimator, so every solid object between the
                // player camera and Skeet's display can occlude that display.
                foregroundRoot = frontClipOn.ChildCache;
            }

            return foregroundRoot;
        }

        private bool RenderForegroundOcclusionMask(
            Camera camera,
            ScopePrefabCache foregroundRoot,
            out int occluderCount,
            out int skeetFrameOccluderCount)
        {
            occluderCount = 0;
            skeetFrameOccluderCount = 0;
            if (_lensMaterial == null
                || !_lensMaterial.HasProperty(OpticalMaskProperty)
                || !_lensMaterial.HasProperty(OpticalMaskEnabledProperty))
            {
                return false;
            }

            if (_opticalMaskMaterial == null)
            {
                var shader = FindOpticalMaskShader();
                if (shader == null)
                {
                    if (!_missingOpticalMaskShaderWasLogged)
                    {
                        _missingOpticalMaskShaderWasLogged = true;
                        MakeMagnifierGreatAgainPlugin.LogWarning(
                            "SkeetIR/OpticalWindowMask was not found; foreground sight geometry cannot occlude the Skeet-IR 1x thermal display.");
                    }
                    return false;
                }

                _opticalMaskMaterial = new Material(shader)
                {
                    name = "MMGA Skeet-IR foreground occlusion mask",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _skeetFrameMaskMaterial = new Material(shader)
                {
                    name = "MMGA Skeet-IR forward-frame occlusion mask",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            EnsureOpticalMaskTexture(camera);
            if (_opticalMaskTexture == null)
            {
                return false;
            }

            if (_opticalMaskBuffer == null)
            {
                _opticalMaskBuffer = new CommandBuffer
                {
                    name = "MMGA Skeet-IR foreground occlusion"
                };
            }

            _opticalMaskBuffer.Clear();
            _opticalMaskBuffer.SetRenderTarget(_opticalMaskTexture);
            _opticalMaskBuffer.ClearRenderTarget(false, true, Color.black);
            _opticalMaskBuffer.SetViewProjectionMatrices(
                camera.worldToCameraMatrix,
                GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));

            occluderCount = DrawSolidOccluders(
                foregroundRoot,
                null,
                _opticalMaskMaterial);

            var skeetCache = FindRegisteredSkeetCache();
            if (skeetCache != null
                && !ReferenceEquals(skeetCache, foregroundRoot)
                && _skeetLensRenderer != null
                && _skeetFrameMaskMaterial != null)
            {
                // Include Skeet's own solid housing/model in the mask while
                // excluding the already-processed child subtree and every
                // Skeet lens, glass, collimator, and optical material. Limit
                // the replacement-material draw to geometry in front of the
                // active Skeet lens; rear body geometry otherwise fills the
                // aperture because the off-screen mask has no scene depth.
                var lensInView = camera.worldToCameraMatrix.MultiplyPoint(
                    _skeetLensRenderer.bounds.center);
                var lensViewDepth = Mathf.Max(0.0001f, -lensInView.z);
                if (_skeetFrameMaskMaterial.HasProperty(
                        OpticalMaskMaxViewDepthProperty))
                {
                    _skeetFrameMaskMaterial.SetFloat(
                        OpticalMaskMaxViewDepthProperty,
                        Mathf.Max(0.0001f, lensViewDepth - 0.0001f));
                }
                skeetFrameOccluderCount = DrawSolidOccluders(
                    skeetCache,
                    foregroundRoot.transform,
                    _skeetFrameMaskMaterial);
            }

            if (occluderCount == 0 && skeetFrameOccluderCount == 0)
            {
                return false;
            }

            Graphics.ExecuteCommandBuffer(_opticalMaskBuffer);
            return true;
        }

        private int DrawSolidOccluders(
            ScopePrefabCache root,
            Transform excludedSubtree,
            Material maskMaterial)
        {
            var count = 0;
            if (root == null || maskMaterial == null)
            {
                return count;
            }

            var opticalSurfaces = FindOpticalSurfaces(root);
            var nonFirstPersonLodRenderers =
                FindNonFirstPersonLodRenderers(root);
            var renderers = root.transform
                .GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null
                    || !renderer.enabled
                    || !renderer.gameObject.activeInHierarchy
                    || (excludedSubtree != null
                        && (ReferenceEquals(
                                renderer.transform,
                                excludedSubtree)
                            || renderer.transform.IsChildOf(
                                excludedSubtree)))
                    || nonFirstPersonLodRenderers.Contains(renderer)
                    || opticalSurfaces.Contains(renderer)
                    || IsNamedOpticalSurface(renderer.name))
                {
                    continue;
                }

                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                {
                    _opticalMaskBuffer.DrawRenderer(
                        renderer,
                        maskMaterial);
                    count++;
                    continue;
                }

                for (var materialIndex = 0;
                    materialIndex < materials.Length;
                    materialIndex++)
                {
                    if (IsOpticalSurfaceMaterial(materials[materialIndex]))
                    {
                        continue;
                    }

                    _opticalMaskBuffer.DrawRenderer(
                        renderer,
                        maskMaterial,
                        materialIndex);
                    count++;
                }
            }

            return count;
        }

        private ScopePrefabCache FindRegisteredSkeetCache()
        {
            SkeetIrSightSelectionPatch.FixedOneXState fixedOneX;
            if (SkeetIrSightSelectionPatch.TryGetFixedOneXState(
                    _animation,
                    out fixedOneX)
                && ReferenceEquals(fixedOneX.SkeetAim, _skeetAim))
            {
                return fixedOneX.SkeetCache;
            }

            SkeetIrSightSelectionPatch.FrontClipOnState frontClipOn;
            if (SkeetIrSightSelectionPatch.TryGetFrontClipOnState(
                    _animation,
                    out frontClipOn)
                && ReferenceEquals(frontClipOn.SkeetAim, _skeetAim))
            {
                return frontClipOn.SkeetCache;
            }

            return _skeetAim != null
                ? _skeetAim.ScopePrefabCache
                : null;
        }

        private static HashSet<Renderer> FindNonFirstPersonLodRenderers(
            ScopePrefabCache foregroundRoot)
        {
            var firstPersonRenderers = new HashSet<Renderer>();
            var nonFirstPersonRenderers = new HashSet<Renderer>();
            var lodGroups = foregroundRoot.transform
                .GetComponentsInChildren<LODGroup>(true);

            foreach (var lodGroup in lodGroups)
            {
                if (lodGroup == null)
                {
                    continue;
                }

                var lods = lodGroup.GetLODs();
                for (var lodIndex = 0; lodIndex < lods.Length; lodIndex++)
                {
                    var renderers = lods[lodIndex].renderers;
                    if (renderers == null)
                    {
                        continue;
                    }

                    foreach (var renderer in renderers)
                    {
                        if (renderer == null)
                        {
                            continue;
                        }

                        // EFT builds its first-person optic renderer list from
                        // LOD0. CommandBuffer.DrawRenderer bypasses Unity's LOD
                        // selection, so explicitly exclude every lower LOD.
                        if (lodIndex == 0)
                        {
                            firstPersonRenderers.Add(renderer);
                        }
                        else
                        {
                            nonFirstPersonRenderers.Add(renderer);
                        }
                    }
                }
            }

            nonFirstPersonRenderers.ExceptWith(firstPersonRenderers);
            return nonFirstPersonRenderers;
        }

        private static HashSet<Renderer> FindOpticalSurfaces(
            ScopePrefabCache foregroundRoot)
        {
            var surfaces = new HashSet<Renderer>();

            // A replacement-material draw would paint the complete
            // collimator glass mesh into the occlusion mask, not just the
            // projected mark pixels. Exclude every live collimator directly,
            // including ones omitted from a prefab's current mode table.
            var allCollimators = foregroundRoot.transform
                .GetComponentsInChildren<CollimatorSight>(true);
            foreach (var collimator in allCollimators)
            {
                if (collimator != null
                    && collimator.CollimatorMeshRenderer != null)
                {
                    surfaces.Add(collimator.CollimatorMeshRenderer);
                }
            }

            var caches = foregroundRoot.transform
                .GetComponentsInChildren<ScopePrefabCache>(true);
            foreach (var cache in caches)
            {
                if (cache == null)
                {
                    continue;
                }

                for (var mode = 0; mode < cache.ModesCount; mode++)
                {
                    var modeInfo = cache.GetScopeModeInfo(mode);
                    if (modeInfo == null)
                    {
                        continue;
                    }

                    var collimator = modeInfo.CollimatorSight;
                    if (collimator != null
                        && collimator.CollimatorMeshRenderer != null)
                    {
                        surfaces.Add(collimator.CollimatorMeshRenderer);
                    }

                    var optic = modeInfo.OpticSight;
                    if (optic != null && optic.LensRenderer != null)
                    {
                        surfaces.Add(optic.LensRenderer);
                    }
                }
            }

            return surfaces;
        }

        private static bool IsNamedOpticalSurface(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName))
            {
                return false;
            }

            var normalized = rendererName.ToLowerInvariant();
            return normalized.Contains("linza")
                || normalized.Contains("lens")
                || normalized.Contains("glass");
        }

        private static bool IsOpticalSurfaceMaterial(Material material)
        {
            if (material == null)
            {
                return false;
            }

            var shaderName = material.shader != null
                ? material.shader.name
                : string.Empty;
            var normalized = (material.name + " " + shaderName)
                .ToLowerInvariant();
            return normalized.Contains("collimator")
                || normalized.Contains("opticsight")
                || normalized.Contains("backlens")
                || normalized.Contains("glass")
                || normalized.Contains("lens");
        }

        private void EnsureOpticalMaskTexture(Camera camera)
        {
            var width = Mathf.Max(1, camera.pixelWidth);
            var height = Mathf.Max(1, camera.pixelHeight);
            if (_opticalMaskTexture != null
                && _opticalMaskTexture.width == width
                && _opticalMaskTexture.height == height)
            {
                return;
            }

            ReleaseOpticalMaskTexture();
            var format = SystemInfo.SupportsRenderTextureFormat(
                RenderTextureFormat.R8)
                ? RenderTextureFormat.R8
                : RenderTextureFormat.ARGB32;
            _opticalMaskTexture = new RenderTexture(
                width,
                height,
                0,
                format,
                RenderTextureReadWrite.Linear)
            {
                name = "MMGA Skeet-IR foreground occlusion",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _opticalMaskTexture.Create();
        }

        private static Shader FindOpticalMaskShader()
        {
            var shader = Shader.Find(OpticalMaskShaderName);
            if (shader != null)
            {
                return shader;
            }

            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                var assetNames = bundle.GetAllAssetNames();
                for (var index = 0; index < assetNames.Length; index++)
                {
                    var assetName = assetNames[index];
                    if (assetName.EndsWith(
                        "skeetiropticalwindowmask.shader",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        shader = bundle.LoadAsset<Shader>(assetName);
                        if (shader != null)
                        {
                            return shader;
                        }
                    }
                }
            }

            return null;
        }

        private void DisableOpticalMask()
        {
            if (_lensMaterial != null
                && _lensMaterial.HasProperty(OpticalMaskEnabledProperty))
            {
                _lensMaterial.SetFloat(OpticalMaskEnabledProperty, 0f);
            }
        }

        private void ReleaseOpticalMaskResources()
        {
            if (_opticalMaskBuffer != null)
            {
                _opticalMaskBuffer.Release();
                _opticalMaskBuffer = null;
            }

            ReleaseOpticalMaskTexture();
            if (_opticalMaskMaterial != null)
            {
                UnityEngine.Object.Destroy(_opticalMaskMaterial);
                _opticalMaskMaterial = null;
            }
            if (_skeetFrameMaskMaterial != null)
            {
                UnityEngine.Object.Destroy(_skeetFrameMaskMaterial);
                _skeetFrameMaskMaterial = null;
            }
        }

        private void ReleaseOpticalMaskTexture()
        {
            if (_opticalMaskTexture == null)
            {
                return;
            }

            _opticalMaskTexture.Release();
            UnityEngine.Object.Destroy(_opticalMaskTexture);
            _opticalMaskTexture = null;
        }
    }

    /// <summary>
    /// Keeps a direct fixed-1x child's prefab aim-camera pose authoritative.
    /// ProceduralWeaponAnimation and the optic camera are shared across weapon
    /// swaps, so a zoom update on another weapon can otherwise leak its last
    /// optic-camera alignment into this retained transform.
    /// </summary>
    internal sealed class SkeetIrFixedOneXAimRuntime : MonoBehaviour
    {
        private ProceduralWeaponAnimation _animation;
        private SkeetIrSightSelectionPatch.FixedOneXState _state;

        internal void Initialize(
            ProceduralWeaponAnimation animation,
            SkeetIrSightSelectionPatch.FixedOneXState state)
        {
            _animation = animation;
            _state = state;
            Restore();
        }

        private void OnEnable()
        {
            Camera.onPreCull += OnCameraPreCull;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= OnCameraPreCull;
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
        }

        private void Update()
        {
            Restore();
        }

        private void LateUpdate()
        {
            Restore();
        }

        private void OnCameraPreCull(Camera camera)
        {
            var cameraManager = CameraManager.Instance;
            if (camera != null
                && cameraManager != null
                && ReferenceEquals(cameraManager.Camera, camera)
                && _animation != null
                && _animation.IsAiming
                && _animation.FirstPersonPointOfView
                && (SkeetIrChildZoomForwardPatch.IsSelectedAim(
                        _animation,
                        _state.SkeetAim)
                    || SkeetIrChildZoomForwardPatch.IsSelectedAim(
                        _animation,
                        _state.ChildAim)))
            {
                // Weapon switching can update the shared optic state after
                // LateUpdate. Restore once more at the last main-camera hook.
                Restore();
            }
        }

        private void Restore()
        {
            SkeetIrSightSelectionPatch.FixedOneXState activeState;
            if (_animation != null
                && SkeetIrSightSelectionPatch.TryGetFixedOneXState(
                    _animation,
                    out activeState)
                && ReferenceEquals(activeState, _state))
            {
                SkeetIrSightSelectionPatch.RestoreFixedOneXAim(_state);
            }
        }
    }

    /// <summary>
    /// Finishes the clip-on transition after EFT's same-frame optic updates
    /// and prevents the child optic camera from photographing the physical
    /// clip-on that sits in front of it. Renderer suppression is scoped only
    /// to the optic-camera render; the Skeet model remains visible normally.
    /// </summary>
    internal sealed class SkeetIrFrontClipOnRuntime : MonoBehaviour
    {
        private const string TransparentOpticalShaderName =
            "SkeetIR/TransparentOpticGlass";

        private sealed class DonorOpticalRendererState
        {
            internal Renderer Renderer;
            internal Material[] SavedMaterials;
        }

        private static bool _g33ProjectedReticleWasLogged;
        private static bool _missingTransparentOpticalShaderWasLogged;
        private static readonly HashSet<int> LoggedDonorOpticalRoots =
            new HashSet<int>();
        private ProceduralWeaponAnimation _animation;
        private SkeetIrSightSelectionPatch.FrontClipOnState _state;
        private Renderer[] _occludingRenderers = new Renderer[0];
        private bool[] _rendererEnabledStates = new bool[0];
        private bool _renderersHidden;
        private bool _collimatorWasEnabled;
        private bool _collimatorHidden;
        private SkeetIrG33ReticlePostProcess _reticlePostProcess;
        private DonorOpticalRendererState[] _donorOpticalRenderers =
            new DonorOpticalRendererState[0];
        private Material _transparentOpticalMaterial;
        private bool _donorOpticalMaterialsSuppressed;

        internal void Initialize(
            ProceduralWeaponAnimation animation,
            SkeetIrSightSelectionPatch.FrontClipOnState state)
        {
            DisableReticleOverlay();
            RestoreCollimator();
            RestoreRenderers();
            RestoreDonorOpticalMaterials();
            ReleaseDonorOpticalSuppression();
            _animation = animation;
            _state = state;

            var cameraClass = CameraManager.Instance;
            var opticCamera = cameraClass != null
                ? cameraClass.OpticCameraManager.Camera
                : null;
            if (state.G33OneXChildAim != null && opticCamera != null)
            {
                _reticlePostProcess =
                    opticCamera.GetComponent<SkeetIrG33ReticlePostProcess>();
                if (_reticlePostProcess == null)
                {
                    _reticlePostProcess = opticCamera.gameObject
                        .AddComponent<SkeetIrG33ReticlePostProcess>();
                }
                _reticlePostProcess.Disable(state);
            }

            var renderers = new List<Renderer>();
            var allRenderers = state.SkeetCache.transform
                .GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in allRenderers)
            {
                if (renderer == null
                    || renderer.transform == state.ChildCache.transform
                    || renderer.transform.IsChildOf(state.ChildCache.transform))
                {
                    continue;
                }

                renderers.Add(renderer);
            }

            _occludingRenderers = renderers.ToArray();
            _rendererEnabledStates = new bool[_occludingRenderers.Length];
            ConfigureDonorOpticalSuppression();
        }

        private void OnEnable()
        {
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            DisableReticleOverlay();
            RestoreRenderers();
            RestoreCollimator();
            RestoreDonorOpticalMaterials();
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            DisableReticleOverlay();
            RestoreRenderers();
            RestoreCollimator();
            RestoreDonorOpticalMaterials();
            ReleaseDonorOpticalSuppression();
        }

        private void LateUpdate()
        {
            SkeetIrSightSelectionPatch.LateUpdateFrontClipOn(
                _animation,
                _state);
        }

        private void OnCameraPreCull(Camera camera)
        {
            var preCullCameraManager = CameraManager.Instance;
            if (_state != null
                && camera != null
                && preCullCameraManager != null
                && ReferenceEquals(preCullCameraManager.Camera, camera))
            {
                SkeetIrSightSelectionPatch.RestoreHybridOneXAim(_state);
            }

            if (_state == null
                || camera == null
                || !_state.ThermalActive)
            {
                DisableReticleOverlay();
                RestoreRenderers();
                RestoreCollimator();
                RestoreDonorOpticalMaterials();
                return;
            }

            if (!SkeetIrSightSelectionPatch.IsActiveFrontClipOnState(
                _animation,
                _state))
            {
                DisableReticleOverlay();
                RestoreRenderers();
                RestoreCollimator();
                RestoreDonorOpticalMaterials();
                return;
            }

            var cameraClass = CameraManager.Instance;
            if (cameraClass == null)
            {
                DisableReticleOverlay();
                RestoreRenderers();
                RestoreCollimator();
                RestoreDonorOpticalMaterials();
                return;
            }

            if (_state.ThermalUsesSkeetOptic)
            {
                if (!ReferenceEquals(cameraClass.Camera, camera))
                {
                    return;
                }

                DisableReticleOverlay();
                RestoreRenderers();
                RestoreCollimator();
                RestoreDonorOpticalMaterials();
                return;
            }

            if (!ReferenceEquals(
                cameraClass.OpticCameraManager.Camera,
                camera))
            {
                return;
            }

            var currentOptic = cameraClass.OpticCameraManager.CurrentOpticSight;
            if (currentOptic == null
                || !currentOptic.transform.IsChildOf(
                    _state.ChildCache.transform))
            {
                DisableReticleOverlay();
                RestoreRenderers();
                RestoreCollimator();
                RestoreDonorOpticalMaterials();
                return;
            }

            RestoreRenderers();
            RestoreCollimator();
            RestoreDonorOpticalMaterials();
            for (var index = 0; index < _occludingRenderers.Length; index++)
            {
                var renderer = _occludingRenderers[index];
                if (renderer == null)
                {
                    continue;
                }

                _rendererEnabledStates[index] = renderer.enabled;
                renderer.enabled = false;
            }

            _renderersHidden = true;
            if (_state.G33OneXChildAim != null)
            {
                SuppressDonorOpticalMaterials();
                HideCollimator();
                EnableReticleOverlay(camera);
            }
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            var cameraClass = CameraManager.Instance;
            if (cameraClass != null
                && ReferenceEquals(cameraClass.OpticCameraManager.Camera, camera))
            {
                RestoreRenderers();
                RestoreCollimator();
                RestoreDonorOpticalMaterials();
            }
        }

        private void EnableReticleOverlay(Camera camera)
        {
            if (_state == null
                || camera == null
                || _state.ProjectedCollimatorRenderer == null
                || _state.ProjectedCollimatorMesh == null
                || _state.ProjectedCollimatorMaterial == null)
            {
                DisableReticleOverlay();
                return;
            }

            _reticlePostProcess =
                camera.GetComponent<SkeetIrG33ReticlePostProcess>();
            if (_reticlePostProcess == null)
            {
                _reticlePostProcess =
                    camera.gameObject.AddComponent<SkeetIrG33ReticlePostProcess>();
            }

            _reticlePostProcess.Configure(
                _state,
                _state.ProjectedCollimatorRenderer,
                _state.ProjectedCollimatorMesh,
                _state.ProjectedCollimatorMaterial,
                _state.UsesNativeReticleProjection);

            if (!_g33ProjectedReticleWasLogged)
            {
                _g33ProjectedReticleWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Custom G33 thermal 3x now redraws the donor collimator "
                    + "after the optic-camera thermal effect; its native mesh, "
                    + "projection scale, position, and red color are preserved.");
            }
        }

        private void DisableReticleOverlay()
        {
            if (_reticlePostProcess != null)
            {
                _reticlePostProcess.Disable(_state);
            }
        }

        private void ConfigureDonorOpticalSuppression()
        {
            _donorOpticalRenderers = new DonorOpticalRendererState[0];
            if (_state == null
                || !_state.SuppressNestedDonorGlass
                || _state.ChildCache == null)
            {
                return;
            }

            var donorCache = NestedSightAimPatch.FindImmediateChildScope(
                _state.ChildCache.transform);
            if (donorCache == null)
            {
                return;
            }

            var shader = FindTransparentOpticalShader();
            if (shader == null)
            {
                if (!_missingTransparentOpticalShaderWasLogged)
                {
                    _missingTransparentOpticalShaderWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        TransparentOpticalShaderName
                        + " was not found; mixed donor glass cannot be "
                        + "removed from the G33 thermal camera pass.");
                }
                return;
            }

            _transparentOpticalMaterial = new Material(shader)
            {
                name = "MMGA G33 thermal transparent donor glass",
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 3010
            };
            if (_transparentOpticalMaterial.HasProperty("_Color"))
            {
                _transparentOpticalMaterial.SetColor("_Color", Color.clear);
            }
            if (_transparentOpticalMaterial.HasProperty("_FresnelColor"))
            {
                _transparentOpticalMaterial.SetColor(
                    "_FresnelColor",
                    Color.clear);
            }

            var states = new List<DonorOpticalRendererState>();
            var renderers = donorCache.transform
                .GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null
                    || ReferenceEquals(
                        renderer,
                        _state.ProjectedCollimatorRenderer))
                {
                    continue;
                }

                var materials = renderer.sharedMaterials;
                var namedOptical = IsNamedDonorOpticalRenderer(renderer.name);
                var hasOpticalMaterial = false;
                for (var index = 0;
                    materials != null && index < materials.Length;
                    index++)
                {
                    if (IsDonorOpticalMaterial(materials[index]))
                    {
                        hasOpticalMaterial = true;
                        break;
                    }
                }

                if (namedOptical || hasOpticalMaterial)
                {
                    states.Add(new DonorOpticalRendererState
                    {
                        Renderer = renderer
                    });
                }
            }

            _donorOpticalRenderers = states.ToArray();
            if (LoggedDonorOpticalRoots.Add(donorCache.GetInstanceID()))
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Custom G33 thermal pass will suppress optical material "
                    + "slots on "
                    + _donorOpticalRenderers.Length
                    + " renderer(s) from '"
                    + donorCache.transform.name
                    + "' while retaining their body/frame materials.");
            }
        }

        private Shader FindTransparentOpticalShader()
        {
            var shader = Shader.Find(TransparentOpticalShaderName);
            if (shader != null)
            {
                return shader;
            }

            // Unity may not register a dependency-only shader with
            // Shader.Find even though Skeet's prepared prefab already has an
            // instantiated material that uses it. Resolve that live shader
            // directly before attempting a bundle asset lookup.
            if (_state != null && _state.SkeetCache != null)
            {
                var renderers = _state.SkeetCache.transform
                    .GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    var materials = renderer != null
                        ? renderer.sharedMaterials
                        : null;
                    foreach (var material in materials ?? new Material[0])
                    {
                        if (material != null
                            && material.shader != null
                            && string.Equals(
                                material.shader.name,
                                TransparentOpticalShaderName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return material.shader;
                        }
                    }
                }
            }

            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                var assetNames = bundle.GetAllAssetNames();
                for (var index = 0; index < assetNames.Length; index++)
                {
                    var assetName = assetNames[index];
                    if (!assetName.EndsWith(
                            "skeetirtransparentopticglass.shader",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    shader = bundle.LoadAsset<Shader>(assetName);
                    if (shader != null)
                    {
                        return shader;
                    }
                }
            }

            return null;
        }

        private void SuppressDonorOpticalMaterials()
        {
            if (_donorOpticalMaterialsSuppressed
                || _transparentOpticalMaterial == null)
            {
                return;
            }

            foreach (var state in _donorOpticalRenderers)
            {
                var renderer = state != null ? state.Renderer : null;
                if (renderer == null)
                {
                    continue;
                }

                var materials = renderer.sharedMaterials;
                state.SavedMaterials = materials;
                if (materials == null || materials.Length == 0)
                {
                    continue;
                }

                var replacements = (Material[])materials.Clone();
                var namedOptical = IsNamedDonorOpticalRenderer(renderer.name);
                var replaced = false;
                for (var index = 0; index < replacements.Length; index++)
                {
                    if (namedOptical
                        || IsDonorOpticalMaterial(replacements[index]))
                    {
                        replacements[index] = _transparentOpticalMaterial;
                        replaced = true;
                    }
                }

                if (replaced)
                {
                    renderer.sharedMaterials = replacements;
                }
            }

            _donorOpticalMaterialsSuppressed = true;
        }

        private void RestoreDonorOpticalMaterials()
        {
            if (!_donorOpticalMaterialsSuppressed)
            {
                return;
            }

            foreach (var state in _donorOpticalRenderers)
            {
                if (state != null
                    && state.Renderer != null
                    && state.SavedMaterials != null)
                {
                    state.Renderer.sharedMaterials = state.SavedMaterials;
                    state.SavedMaterials = null;
                }
            }
            _donorOpticalMaterialsSuppressed = false;
        }

        private void ReleaseDonorOpticalSuppression()
        {
            RestoreDonorOpticalMaterials();
            _donorOpticalRenderers = new DonorOpticalRendererState[0];
            if (_transparentOpticalMaterial != null)
            {
                UnityEngine.Object.Destroy(_transparentOpticalMaterial);
                _transparentOpticalMaterial = null;
            }
        }

        private static bool IsNamedDonorOpticalRenderer(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName))
            {
                return false;
            }

            var normalized = rendererName.ToLowerInvariant();
            return normalized.Contains("linza")
                || normalized.Contains("lens")
                || normalized.Contains("glass");
        }

        private static bool IsDonorOpticalMaterial(Material material)
        {
            if (material == null)
            {
                return false;
            }

            var shaderName = material.shader != null
                ? material.shader.name
                : string.Empty;
            var normalized = (material.name + " " + shaderName)
                .ToLowerInvariant();
            return normalized.Contains("collimator")
                || normalized.Contains("opticsight")
                || normalized.Contains("backlens")
                || normalized.Contains("glass")
                || normalized.Contains("lens")
                || normalized.Contains("linza");
        }

        private void HideCollimator()
        {
            var renderer = _state != null
                ? _state.ProjectedCollimatorRenderer
                : null;
            if (_collimatorHidden || renderer == null)
            {
                return;
            }

            _collimatorWasEnabled = renderer.enabled;
            renderer.enabled = false;
            _collimatorHidden = true;
        }

        private void RestoreCollimator()
        {
            if (!_collimatorHidden)
            {
                return;
            }

            var renderer = _state != null
                ? _state.ProjectedCollimatorRenderer
                : null;
            if (renderer != null)
            {
                renderer.enabled = _collimatorWasEnabled;
            }
            _collimatorHidden = false;
        }

        private void RestoreRenderers()
        {
            if (!_renderersHidden)
            {
                return;
            }

            for (var index = 0; index < _occludingRenderers.Length; index++)
            {
                var renderer = _occludingRenderers[index];
                if (renderer != null)
                {
                    renderer.enabled = _rendererEnabledStates[index];
                }
            }

            _renderersHidden = false;
        }
    }

    /// <summary>
    /// Draw-only main-camera reticle overlay for the compact donors whose mark
    /// is otherwise hidden behind foreground optical geometry. Unlike the
    /// rejected main-camera OnRenderImage experiment, this never samples,
    /// copies, or blits the camera image.
    /// </summary>
    internal sealed class SkeetIrDrawOnlyReticleOverlay : MonoBehaviour
    {
        private object _owner;
        private Camera _camera;
        private CommandBuffer _drawBuffer;

        internal void Configure(
            object owner,
            Renderer renderer,
            Mesh mesh,
            Material material)
        {
            _owner = owner;
            _camera = GetComponent<Camera>();
            if (_camera == null
                || renderer == null
                || mesh == null
                || material == null)
            {
                Clear();
                return;
            }

            if (_drawBuffer == null)
            {
                _drawBuffer = new CommandBuffer
                {
                    name = "MMGA Skeet-IR draw-only 1x reticle"
                };
                _camera.AddCommandBuffer(
                    CameraEvent.AfterEverything,
                    _drawBuffer);
            }

            _drawBuffer.Clear();
            _drawBuffer.SetRenderTarget(
                BuiltinRenderTextureType.CameraTarget);
            _drawBuffer.SetViewProjectionMatrices(
                _camera.worldToCameraMatrix,
                GL.GetGPUProjectionMatrix(
                    _camera.projectionMatrix,
                    false));
            var cameraPosition = _camera.transform.position;
            _drawBuffer.SetGlobalVector(
                "_WorldSpaceCameraPos",
                new Vector4(
                    cameraPosition.x,
                    cameraPosition.y,
                    cameraPosition.z,
                    1f));
            _drawBuffer.DrawMesh(
                mesh,
                renderer.localToWorldMatrix,
                material);
        }

        internal void Disable(object owner)
        {
            if (_owner == null || ReferenceEquals(_owner, owner))
            {
                _owner = null;
                Clear();
            }
        }

        private void Clear()
        {
            if (_drawBuffer != null)
            {
                _drawBuffer.Clear();
            }
        }

        private void OnDestroy()
        {
            if (_camera != null && _drawBuffer != null)
            {
                _camera.RemoveCommandBuffer(
                    CameraEvent.AfterEverything,
                    _drawBuffer);
            }
            if (_drawBuffer != null)
            {
                _drawBuffer.Release();
                _drawBuffer = null;
            }
        }
    }

    /// <summary>
    /// Final optic-camera image effect used only by the custom G33 3x thermal
    /// route. The donor collimator is omitted from the thermal scene pass, then
    /// redrawn over the processed image with its original transform and shader
    /// projection parameters. This keeps its native size and prevents thermal
    /// vision from converting the red mark to black.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    internal sealed class SkeetIrG33ReticlePostProcess : MonoBehaviour
    {
        private object _owner;
        private Camera _camera;
        private Renderer _renderer;
        private Mesh _mesh;
        private Material _material;
        private bool _useNativeScreenProjection;
        private CommandBuffer _drawBuffer;

        internal void Configure(
            object owner,
            Renderer renderer,
            Mesh mesh,
            Material material,
            bool useNativeScreenProjection)
        {
            _owner = owner;
            _camera = GetComponent<Camera>();
            _renderer = renderer;
            _mesh = mesh;
            _material = material;
            _useNativeScreenProjection = useNativeScreenProjection;
            enabled = true;
        }

        internal void Disable(object owner)
        {
            if (_owner == null || ReferenceEquals(_owner, owner))
            {
                _owner = null;
                _renderer = null;
                _mesh = null;
                _material = null;
                _useNativeScreenProjection = false;
                enabled = false;
            }
        }

        private void OnDestroy()
        {
            if (_drawBuffer != null)
            {
                _drawBuffer.Release();
                _drawBuffer = null;
            }
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            Graphics.Blit(source, destination);
            if (_owner == null
                || _camera == null
                || _renderer == null
                || _mesh == null
                || _material == null)
            {
                return;
            }

            if (_drawBuffer == null)
            {
                _drawBuffer = new CommandBuffer
                {
                    name = "MMGA custom G33 post-thermal reticle"
                };
            }

            _drawBuffer.Clear();
            var target = destination != null
                ? new RenderTargetIdentifier(destination)
                : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
            _drawBuffer.SetRenderTarget(target);
            _drawBuffer.SetViewProjectionMatrices(
                _camera.worldToCameraMatrix,
                GL.GetGPUProjectionMatrix(
                    _camera.projectionMatrix,
                    !_useNativeScreenProjection
                        && (destination != null
                            || _camera.targetTexture != null)));
            _drawBuffer.DrawMesh(
                _mesh,
                _renderer.localToWorldMatrix,
                _material);
            Graphics.ExecuteCommandBuffer(_drawBuffer);
        }
    }

    /// <summary>
    /// EFT normally applies Change Zoom to the sight represented by the
    /// weapon's current AimIndex. While Skeet's thermal aim point is selected,
    /// redirect that one proposed mode change to Skeet's immediate child.
    /// This keeps zoom ownership with the real optic and never uses Skeet's
    /// two physical bundle modes as zoom states.
    /// </summary>
    [HarmonyPatch]
    internal static class SkeetIrChildZoomForwardPatch
    {
        private static readonly FieldInfo PlayerField =
            AccessTools.Field(typeof(EFT.Player.FirearmController), "_player");

        private static FieldInfo _stateIdField;
        private static FieldInfo _stateModeField;
        private static FieldInfo _stateScopeIndexField;
        private static FieldInfo _stateCalibrationIndexField;

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase firearmMethod = null;
            foreach (var method in AccessTools.GetDeclaredMethods(
                typeof(EFT.Player.FirearmController)))
            {
                var parameters = method.GetParameters();
                if (!string.Equals(method.Name, "SetScopeMode", StringComparison.Ordinal)
                    || parameters.Length != 1
                    || !parameters[0].ParameterType.IsArray)
                {
                    continue;
                }

                var stateType = parameters[0].ParameterType.GetElementType();
                _stateIdField = AccessTools.Field(stateType, "Id");
                _stateModeField = AccessTools.Field(stateType, "ScopeMode");
                _stateScopeIndexField = AccessTools.Field(
                    stateType,
                    "ScopeIndexInsideSight");
                _stateCalibrationIndexField = AccessTools.Field(
                    stateType,
                    "ScopeCalibrationIndex");
                firearmMethod = method;
                break;
            }

            if (firearmMethod == null)
            {
                throw new MissingMethodException(
                    typeof(EFT.Player.FirearmController).FullName,
                    "SetScopeMode(scopeStates)");
            }

            // Patch the client override as well as the base implementation.
            // The override serializes the request before calling base, so the
            // BOSS-to-G33 redirect must exist before that packet is recorded.
            var stateArrayType = firearmMethod.GetParameters()[0].ParameterType;
            var clientMethod = AccessTools.DeclaredMethod(
                typeof(EFT.ClientFirearmController),
                "SetScopeMode",
                new[] { stateArrayType });
            if (clientMethod != null)
            {
                yield return clientMethod;
            }
            yield return firearmMethod;
        }

        [HarmonyPrefix]
        private static bool Prefix(
            EFT.Player.FirearmController __instance,
            MethodBase __originalMethod,
            object[] __args)
        {
            var states = __args != null && __args.Length > 0
                ? __args[0] as Array
                : null;
            var player = PlayerField != null
                ? PlayerField.GetValue(__instance) as EFT.Player
                : null;
            var animation = player != null
                ? player.ProceduralWeaponAnimation
                : null;
            if (animation == null
                || states == null
                || _stateIdField == null
                || _stateModeField == null
                || _stateScopeIndexField == null)
            {
                return true;
            }

            ScopePrefabCache childCache;
            SightComponent skeetSight;
            bool thermalSelected;
            ProceduralWeaponAnimation.SightNBone childAim;
            SightComponent routedChildSight = null;
            SightComponent collapsedDonorSight = null;
            var patchingClientOverride = __originalMethod != null
                && __originalMethod.DeclaringType
                    == typeof(EFT.ClientFirearmController);
            SkeetIrSightSelectionPatch.FrontClipOnState frontClipOn;
            if (SkeetIrSightSelectionPatch.TryGetFrontClipOnState(
                animation,
                out frontClipOn))
            {
                // The client override is patched early only for the BOSS
                // redirect so its network state is corrected before send.
                // Every established non-BOSS path remains owned by the
                // original base-method patch.
                if (patchingClientOverride
                    && !frontClipOn.UsesWilcoxBossDonor)
                {
                    return true;
                }
                thermalSelected = IsSelectedAim(animation, frontClipOn.SkeetAim);
                childCache = frontClipOn.ChildCache;
                skeetSight = frontClipOn.SkeetMod;
                childAim = frontClipOn.ChildAim;
                routedChildSight = frontClipOn.UsesWilcoxBossDonor
                    && frontClipOn.OriginalChildAim != null
                    ? frontClipOn.OriginalChildAim.Mod
                    : null;
                collapsedDonorSight = frontClipOn.UsesWilcoxBossDonor
                    && frontClipOn.G33OneXChildAim != null
                    ? frontClipOn.G33OneXChildAim.Mod
                    : null;
            }
            else
            {
                if (patchingClientOverride)
                {
                    return true;
                }
                var skeetAim = NestedSightAimPatch.FindSkeetIrAim(animation);
                if (skeetAim == null)
                {
                    return true;
                }

                thermalSelected = IsSelectedAim(animation, skeetAim);
                childCache = NestedSightAimPatch.FindImmediateChildScope(
                    skeetAim.ScopePrefabCache.transform);
                skeetSight = skeetAim.Mod;
                childAim = FindAim(animation, childCache);
            }

            if (skeetSight == null)
            {
                return true;
            }

            var skeetItemId = skeetSight.Item.Id.ToString();
            for (var index = 0; index < states.Length; index++)
            {
                var state = states.GetValue(index);
                if (state == null
                    || !string.Equals(
                        _stateIdField.GetValue(state) as string,
                        skeetItemId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                // Change Zoom must not persist Skeet's physical folded mode
                // as a sight zoom mode, including when the child is selected.
                var requestedMode = (int)_stateModeField.GetValue(state);
                if (requestedMode != 0)
                {
                    MakeMagnifierGreatAgainPlugin.LogInfo(
                        $"Ignored Change Zoom mode {requestedMode} for Skeet-IR; "
                        + "Switch Sight alone controls its physical mode.");
                }
                _stateModeField.SetValue(state, 0);
                states.SetValue(state, index);
            }

            var childSight = routedChildSight
                ?? FindSightComponent(animation, childCache);
            if (childSight == null)
            {
                return true;
            }

            if (!thermalSelected && !IsSelectedAim(animation, childAim))
            {
                return true;
            }

            // A 0 -> 0 wrap still starts EFT's complete sight/camera refresh.
            // Skip that no-op only for a fixed 1x child. LPVO, HHS, and G33
            // advertise an available zoom above 1x and keep the transaction.
            if (!childSight.HasCurrentZoomGreaterThenOne())
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Consumed Change Zoom for fixed 1x Skeet-IR child '"
                    + childCache.transform.name + "'.");
                return false;
            }

            var childScopeIndex = childSight.SelectedScopeIndex;
            if (childScopeIndex < 0
                || childScopeIndex >= childSight.ScopesSelectedModes.Length)
            {
                return true;
            }

            var childModesCount = childSight.GetScopeModesCount(childScopeIndex);
            var childMode = childSight.ScopesSelectedModes[childScopeIndex];
            if (childModesCount <= 1)
            {
                return true;
            }

            var nextChildMode = childModesCount > 0
                ? (childMode + 1) % childModesCount
                : childMode;
            if (frontClipOn != null && frontClipOn.UsesWilcoxBossDonor)
            {
                SkeetIrSightSelectionPatch.PrepareBossZoomTransition(
                    frontClipOn,
                    nextChildMode);
            }
            else if (nextChildMode >= 0
                && nextChildMode < childCache.ModesCount
                && childCache.GetScopeModeInfo(nextChildMode).OpticSight == null)
            {
                NestedSightAimPatch.RestoreAimBone(childAim.Bone);
            }

            var childItemId = childSight.Item.Id.ToString();
            var collapsedDonorItemId = collapsedDonorSight != null
                ? collapsedDonorSight.Item.Id.ToString()
                : null;
            var childStateFound = false;
            var donorStateIndex = -1;

            for (var index = 0; index < states.Length; index++)
            {
                var state = states.GetValue(index);
                if (state == null)
                {
                    continue;
                }

                var stateId = _stateIdField.GetValue(state) as string;
                var stateScopeIndex = (int)_stateScopeIndexField.GetValue(state);
                if (collapsedDonorSight != null
                    && string.Equals(
                        stateId,
                        collapsedDonorItemId,
                        StringComparison.Ordinal))
                {
                    if (donorStateIndex < 0)
                    {
                        donorStateIndex = index;
                    }

                    // The input translator increments the mode belonging to
                    // the current proxy Mod. At folded 1x that Mod is BOSS for
                    // calibration, even though zoom remains G33-owned. Restore
                    // every BOSS request to its existing value so no reticle or
                    // laser mode is changed by the native transaction.
                    if (stateScopeIndex >= 0
                        && stateScopeIndex
                            < collapsedDonorSight.ScopesSelectedModes.Length)
                    {
                        _stateModeField.SetValue(
                            state,
                            collapsedDonorSight.ScopesSelectedModes[
                                stateScopeIndex]);
                    }
                    if (_stateCalibrationIndexField != null
                        && stateScopeIndex >= 0
                        && stateScopeIndex
                            < collapsedDonorSight
                                .ScopesCurrentCalibPointIndexes.Length)
                    {
                        _stateCalibrationIndexField.SetValue(
                            state,
                            collapsedDonorSight
                                .ScopesCurrentCalibPointIndexes[
                                    stateScopeIndex]);
                    }
                    states.SetValue(state, index);
                }

                if (string.Equals(stateId, childItemId, StringComparison.Ordinal)
                    && stateScopeIndex == childScopeIndex)
                {
                    _stateModeField.SetValue(state, nextChildMode);
                    states.SetValue(state, index);
                    childStateFound = true;
                }
            }

            if (!childStateFound && donorStateIndex >= 0)
            {
                // Once the complete BOSS aim entry is installed for 1x, EFT's
                // generated request contains BOSS but may omit G33 entirely.
                // Reuse one suppressed BOSS state as the missing G33 request.
                var redirectedState = states.GetValue(donorStateIndex);
                _stateIdField.SetValue(redirectedState, childItemId);
                _stateScopeIndexField.SetValue(
                    redirectedState,
                    childScopeIndex);
                _stateModeField.SetValue(redirectedState, nextChildMode);
                if (_stateCalibrationIndexField != null
                    && childScopeIndex
                        < childSight.ScopesCurrentCalibPointIndexes.Length)
                {
                    _stateCalibrationIndexField.SetValue(
                        redirectedState,
                        childSight.ScopesCurrentCalibPointIndexes[
                            childScopeIndex]);
                }
                states.SetValue(redirectedState, donorStateIndex);
                childStateFound = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Redirected collapsed BOSS Change Zoom state to custom "
                    + $"G33 (mode {childMode} -> {nextChildMode}).");
            }

            if (childStateFound
                && (frontClipOn == null
                    || !frontClipOn.UsesWilcoxBossDonor
                    || patchingClientOverride))
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Forwarded Change Zoom to selected Skeet-IR child sight "
                    + $"'{childCache.transform.name}' (mode {childMode} -> {nextChildMode}).");
            }

            return true;
        }

        private static SightComponent FindSightComponent(
            ProceduralWeaponAnimation animation,
            ScopePrefabCache cache)
        {
            if (cache == null || animation.ScopeAimTransforms == null)
            {
                return null;
            }

            foreach (var aim in animation.ScopeAimTransforms)
            {
                if (ReferenceEquals(aim.ScopePrefabCache, cache))
                {
                    return aim.Mod;
                }
            }

            return null;
        }

        private static ProceduralWeaponAnimation.SightNBone FindAim(
            ProceduralWeaponAnimation animation,
            ScopePrefabCache cache)
        {
            if (cache == null || animation.ScopeAimTransforms == null)
            {
                return null;
            }

            foreach (var aim in animation.ScopeAimTransforms)
            {
                if (ReferenceEquals(aim.ScopePrefabCache, cache))
                {
                    return aim;
                }
            }

            return null;
        }

        internal static bool IsSelectedAim(
            ProceduralWeaponAnimation animation,
            ProceduralWeaponAnimation.SightNBone aim)
        {
            if (animation == null
                || aim == null
                || animation.ScopeAimTransforms == null
                || animation.ScopeAimTransforms.Count == 0)
            {
                return false;
            }

            var selectedIndex = animation.AimIndex % animation.ScopeAimTransforms.Count;
            return selectedIndex >= 0
                && ReferenceEquals(animation.ScopeAimTransforms[selectedIndex], aim);
        }
    }

    /// <summary>
    /// Prevent the ordinary sight-mode refresh from interpreting Skeet's
    /// inline/folded objects as magnification modes. SkeetIrSightSelectionPatch
    /// is the sole owner of those physical objects through Switch Sight.
    /// </summary>
    [HarmonyPatch(typeof(SightModVisualControllers), nameof(SightModVisualControllers.UpdateSightMode))]
    internal static class SkeetIrVisualModeIsolationPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(SightModVisualControllers __instance)
        {
            var cache = __instance != null
                ? __instance.GetComponent<ScopePrefabCache>()
                : null;
            return cache == null
                || !NestedSightAimPatch.IsSkeetIrRootName(cache.transform.name);
        }
    }

    [HarmonyPatch(typeof(ProceduralWeaponAnimation), "method_1")]
    internal static class MagnifierOpticActivationPatch
    {
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

            if (!optic.enabled)
            {
                optic.enabled = true;
            }

            var manager = CameraManager.Instance.OpticCameraManager;
            if (manager.CurrentOpticSight != optic)
            {
                manager.OnOpticSightEnabled(optic);
            }
        }
    }
}
