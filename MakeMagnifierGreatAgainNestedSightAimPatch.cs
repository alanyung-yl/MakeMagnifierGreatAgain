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
                        CollapsedG33ChildAims[__instance.GetInstanceID()] = candidate;
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
            internal Renderer ProjectedCollimatorRenderer;
            internal Mesh ProjectedCollimatorMesh;
            internal Material ProjectedCollimatorMaterial;
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
        private static int _lastLoggedCompositeInstanceId;
        private static bool _frontClipOnConfiguredWasLogged;
        private static bool _oneXChildRouteWasLogged;
        private static bool _hybridOneXRouteWasLogged;
        private static bool _g33OneXAimRouteWasLogged;
        private static bool _missingG33ReticleShaderWasLogged;
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
                    childCache)
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
            CopyAimPose(state.G33OneXChildAim, state.ChildAim);
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
            renderer = collimator != null
                ? collimator.CollimatorMeshRenderer
                : null;
            var sourceMaterial = collimator != null
                ? collimator.CollimatorMaterial
                : null;
            if (sourceMaterial == null && renderer != null)
            {
                sourceMaterial = renderer.sharedMaterial;
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

            state.ProjectedCollimatorRenderer = null;
            state.ProjectedCollimatorMesh = null;
            state.ProjectedCollimatorMaterial = null;
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
                CopyAimPose(childAimSource, state.ChildAim);

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

            // Keep the selector entry a stable proxy for the child sight. EFT
            // builds its optic list and camera-depth state from these fields;
            // changing Mod/ScopePrefabCache later corrupts hybrid 1x ADS. Only
            // the G33 pose changes, because its folded mode looks through the
            // attached sight instead of the magnifier's aim camera.
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
            if (reticleCache != null
                && NestedSightAimPatch.IsG33RootName(reticleCache.transform.name))
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
            var instanceId = skeetCache.GetInstanceID();
            if (_lastLoggedCompositeInstanceId == instanceId)
            {
                return;
            }

            _lastLoggedCompositeInstanceId = instanceId;
            var childName = childCache != null ? childCache.transform.name : "<missing>";
            var reticleName = reticleCache != null ? reticleCache.transform.name : "<missing>";
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
        private Material _oneXReticleSourceMaterial;
        private Material _oneXReticleProjectionMaterial;
        private RenderTexture _oneXReticleProjectionTexture;
        private CommandBuffer _oneXReticleProjectionBuffer;
        private int _oneXReticleRootId;
        private bool _oneXReticleRequiresVerticalFlip;
        private static readonly HashSet<int> LoggedReticleProjectionRoots =
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
                SkeetIrSightSelectionPatch.DisableSkeetThermalReticle(
                    _skeetAim.ScopePrefabCache);
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
            if (_oneXReticleRootId != rootId
                || _oneXReticleRenderer == null
                || _oneXReticleMesh == null
                || _oneXReticleProjectionMaterial == null)
            {
                ReleaseOneXReticleResources();
                _oneXReticleRequiresVerticalFlip =
                    NestedSightAimPatch.IsG33RootName(
                        foregroundRoot.transform.name);
                SkeetIrSightSelectionPatch.CreateProjectedCollimatorMaterial(
                    foregroundRoot,
                    _oneXReticleRequiresVerticalFlip,
                    out _oneXReticleRenderer,
                    out _oneXReticleMesh,
                    out _oneXReticleProjectionMaterial);
                _oneXReticleRootId = rootId;
                _oneXReticleSourceMaterial = _oneXReticleRenderer != null
                    ? _oneXReticleRenderer.sharedMaterial
                    : null;
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
                    + "projection; no main-camera redraw or lens cutout is used"
                    + (_oneXReticleRequiresVerticalFlip
                        ? ", with the nested G33 mark corrected vertically."
                        : "."));
            }
            return true;
        }

        private void SyncOneXReticleProjectionProperties()
        {
            var liveMaterial = _oneXReticleRenderer.sharedMaterial;
            if (liveMaterial != null)
            {
                _oneXReticleSourceMaterial = liveMaterial;
            }

            CopyTextureProperty("_MarkTex");
            CopyTextureProperty("_NoiseTex");
            CopyTextureProperty("_FadeTex");
            CopyVectorProperty("_MarkShift");
            CopyFloatProperty("_MarkScale");
            CopyFloatProperty("_HDR");

            if (_oneXReticleProjectionMaterial.HasProperty(
                    "_MMGA_FlipMarkY"))
            {
                _oneXReticleProjectionMaterial.SetFloat(
                    "_MMGA_FlipMarkY",
                    _oneXReticleRequiresVerticalFlip ? 1f : 0f);
            }

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
            if (_lensMaterial != null
                && _lensMaterial.HasProperty(
                    ReticleProjectionEnabledProperty))
            {
                _lensMaterial.SetFloat(ReticleProjectionEnabledProperty, 0f);
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
            _oneXReticleSourceMaterial = null;
            _oneXReticleProjectionMaterial = null;
            _oneXReticleRootId = 0;
            _oneXReticleRequiresVerticalFlip = false;
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
        private static bool _g33ProjectedReticleWasLogged;
        private ProceduralWeaponAnimation _animation;
        private SkeetIrSightSelectionPatch.FrontClipOnState _state;
        private Renderer[] _occludingRenderers = new Renderer[0];
        private bool[] _rendererEnabledStates = new bool[0];
        private bool _renderersHidden;
        private bool _collimatorWasEnabled;
        private bool _collimatorHidden;
        private SkeetIrG33ReticlePostProcess _reticlePostProcess;

        internal void Initialize(
            ProceduralWeaponAnimation animation,
            SkeetIrSightSelectionPatch.FrontClipOnState state)
        {
            DisableReticleOverlay();
            RestoreCollimator();
            RestoreRenderers();
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
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            DisableReticleOverlay();
            RestoreRenderers();
            RestoreCollimator();
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
                return;
            }

            if (!SkeetIrSightSelectionPatch.IsActiveFrontClipOnState(
                _animation,
                _state))
            {
                DisableReticleOverlay();
                RestoreRenderers();
                RestoreCollimator();
                return;
            }

            var cameraClass = CameraManager.Instance;
            if (cameraClass == null)
            {
                DisableReticleOverlay();
                RestoreRenderers();
                RestoreCollimator();
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
                return;
            }

            RestoreRenderers();
            RestoreCollimator();
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
                _state.ProjectedCollimatorMaterial);

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
        private CommandBuffer _drawBuffer;

        internal void Configure(
            object owner,
            Renderer renderer,
            Mesh mesh,
            Material material)
        {
            _owner = owner;
            _camera = GetComponent<Camera>();
            _renderer = renderer;
            _mesh = mesh;
            _material = material;
        }

        internal void Disable(object owner)
        {
            if (ReferenceEquals(_owner, owner))
            {
                _owner = null;
                _renderer = null;
                _mesh = null;
                _material = null;
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
                    destination != null || _camera.targetTexture != null));
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

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
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
                return method;
            }

            throw new MissingMethodException(
                typeof(EFT.Player.FirearmController).FullName,
                "SetScopeMode(scopeStates)");
        }

        [HarmonyPrefix]
        private static bool Prefix(
            EFT.Player.FirearmController __instance,
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
            SkeetIrSightSelectionPatch.FrontClipOnState frontClipOn;
            if (SkeetIrSightSelectionPatch.TryGetFrontClipOnState(
                animation,
                out frontClipOn))
            {
                thermalSelected = IsSelectedAim(animation, frontClipOn.SkeetAim);
                childCache = frontClipOn.ChildCache;
                skeetSight = frontClipOn.SkeetMod;
                childAim = frontClipOn.ChildAim;
            }
            else
            {
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

            var childSight = FindSightComponent(animation, childCache);
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
            if (nextChildMode >= 0
                && nextChildMode < childCache.ModesCount
                && childCache.GetScopeModeInfo(nextChildMode).OpticSight == null)
            {
                NestedSightAimPatch.RestoreAimBone(childAim.Bone);
            }

            var childItemId = childSight.Item.Id.ToString();
            var childStateFound = false;

            for (var index = 0; index < states.Length; index++)
            {
                var state = states.GetValue(index);
                if (state == null)
                {
                    continue;
                }

                var stateId = _stateIdField.GetValue(state) as string;
                var stateScopeIndex = (int)_stateScopeIndexField.GetValue(state);
                if (string.Equals(stateId, childItemId, StringComparison.Ordinal)
                    && stateScopeIndex == childScopeIndex)
                {
                    _stateModeField.SetValue(state, nextChildMode);
                    states.SetValue(state, index);
                    childStateFound = true;
                }
            }

            if (childStateFound)
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
