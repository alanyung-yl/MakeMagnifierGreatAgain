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
        }

        internal sealed class FixedOneXState
        {
            internal ProceduralWeaponAnimation Animation;
            internal ProceduralWeaponAnimation.SightNBone SkeetAim;
            internal ProceduralWeaponAnimation.SightNBone OriginalSkeetAim;
            internal ScopePrefabCache SkeetCache;
            internal ProceduralWeaponAnimation.SightNBone ChildAim;
            internal ScopePrefabCache ChildCache;
            internal Renderer ProjectedCollimatorRenderer;
            internal Material ProjectedCollimatorMaterial;
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
        private static bool _missingChildMarkTextureWasLogged;
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

                if (previousFixedOneX.ProjectedCollimatorMaterial != null)
                {
                    UnityEngine.Object.Destroy(
                        previousFixedOneX.ProjectedCollimatorMaterial);
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
                CreateProjectedCollimatorMaterial(
                    childCache,
                    false,
                    out fixedOneX.ProjectedCollimatorRenderer,
                    out fixedOneX.ProjectedCollimatorMaterial);
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
            CaptureProjectedCollimator(state);
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
            if (corrected && !state.PoseCorrectionWasLogged)
            {
                state.PoseCorrectionWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Restored fixed 1x mod_aim_camera '"
                    + state.ChildAim.Bone.name
                    + "' after shared optic state changed its local position.");
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
                out state.ProjectedCollimatorMaterial);
        }

        private static void CreateProjectedCollimatorMaterial(
            ScopePrefabCache childCache,
            bool preferNestedChild,
            out Renderer renderer,
            out Material projectedMaterial)
        {
            renderer = null;
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

            if (renderer == null || sourceMaterial == null)
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
            var sourceRenderer = collimator != null
                ? collimator.CollimatorMeshRenderer
                : sourceOptic != null ? sourceOptic.LensRenderer : null;
            var sourceMaterial = collimator != null && collimator.CollimatorMaterial != null
                ? collimator.CollimatorMaterial
                : sourceRenderer != null ? sourceRenderer.sharedMaterial : null;
            var markTexture = sourceMaterial != null && sourceMaterial.HasProperty("_MarkTex")
                ? sourceMaterial.GetTexture("_MarkTex")
                : null;

            if (markTexture == null)
            {
                targetMaterial.SetTexture("_MarkTex", Texture2D.blackTexture);
                if (childCache != null && !_missingChildMarkTextureWasLogged)
                {
                    _missingChildMarkTextureWasLogged = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        $"Could not obtain a ScopeReticle or _MarkTex from Skeet-IR child "
                        + $"'{childCache.transform.name}'; the thermal view will have no reticle.");
                }

                return;
            }

            if (collimator != null)
            {
                // CW FX/Collimator projects the mark from the rendering
                // camera, _MarkShift, _MarkScale, and the real glass mesh.
                // A lens-space texture copy cannot preserve that result. The
                // camera runtime redraws this collimator with the mark-only
                // projection shader after thermal processing instead.
                targetMaterial.SetTexture("_MarkTex", Texture2D.blackTexture);
                var collimatorShader = sourceMaterial.shader != null
                    ? sourceMaterial.shader.name
                    : "<missing>";
                LogThermalReticleSource(
                    skeetCache,
                    childCache,
                    reticleCache,
                    "projected collimator=" + collimatorShader);
                return;
            }

            // Some non-collimator sights expose only a baked lens mark and no
            // ScopeReticle mesh. Keep that fallback for those assets.
            targetMaterial.SetTexture("_MarkTex", markTexture);
            if (targetMaterial.HasProperty("_Color")
                && sourceMaterial.HasProperty("_Color"))
            {
                targetMaterial.SetColor("_Color", sourceMaterial.GetColor("_Color"));
            }

            if (targetMaterial.HasProperty("_HDR")
                && sourceMaterial.HasProperty("_HDR"))
            {
                targetMaterial.SetFloat("_HDR", sourceMaterial.GetFloat("_HDR"));
            }

            var shaderName = sourceMaterial.shader != null
                ? sourceMaterial.shader.name
                : "<missing>";
            LogThermalReticleSource(
                skeetCache,
                childCache,
                reticleCache,
                $"texture={markTexture.name} ({markTexture.width}x{markTexture.height}), "
                    + $"sourceShader={shaderName}");

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

        internal void Capture()
        {
            if (_captured)
            {
                return;
            }

            _localPosition = transform.localPosition;
            _captured = true;
        }

        internal bool Restore()
        {
            if (!_captured)
            {
                return false;
            }

            var changed = (transform.localPosition - _localPosition).sqrMagnitude
                > 0.0000000001f;
            transform.localPosition = _localPosition;
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
        private ProceduralWeaponAnimation _animation;
        private ProceduralWeaponAnimation.SightNBone _skeetAim;
        private Material _lensMaterial;

        internal void Initialize(
            ProceduralWeaponAnimation animation,
            ProceduralWeaponAnimation.SightNBone skeetAim)
        {
            _animation = animation;
            _skeetAim = skeetAim;
            var optic = skeetAim.ScopePrefabCache.FirstOptic;
            var renderer = optic != null ? optic.LensRenderer : null;
            _lensMaterial = renderer != null ? renderer.material : null;
            SetVisible(false);
        }

        private void LateUpdate()
        {
            SetVisible(_animation != null
                && _animation.IsAiming
                && _animation.FirstPersonPointOfView
                && SkeetIrChildZoomForwardPatch.IsSelectedAim(
                    _animation,
                    _skeetAim));
        }

        private void OnDisable()
        {
            SetVisible(false);
        }

        private void SetVisible(bool visible)
        {
            if (_lensMaterial != null && _lensMaterial.HasProperty(VisibleProperty))
            {
                _lensMaterial.SetFloat(VisibleProperty, visible ? 1f : 0f);
            }
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
        private static bool _projectedReticleWasLogged;
        private ProceduralWeaponAnimation _animation;
        private SkeetIrSightSelectionPatch.FixedOneXState _state;
        private CommandBuffer _reticleBuffer;
        private Camera _reticleCamera;
        private bool _collimatorWasEnabled;
        private bool _collimatorHidden;

        internal void Initialize(
            ProceduralWeaponAnimation animation,
            SkeetIrSightSelectionPatch.FixedOneXState state)
        {
            ReleaseReticleBuffer();
            RestoreCollimator();
            _animation = animation;
            _state = state;
            Restore();
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
            DetachReticleBuffer();
            RestoreCollimator();
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            ReleaseReticleBuffer();
            RestoreCollimator();
        }

        private void Update()
        {
            Restore();
        }

        private void LateUpdate()
        {
            Restore();
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

        private void OnCameraPreCull(Camera camera)
        {
            SkeetIrSightSelectionPatch.FixedOneXState activeState;
            var cameraManager = CameraManager.Instance;
            var shouldDraw = camera != null
                && cameraManager != null
                && ReferenceEquals(cameraManager.Camera, camera)
                && _animation != null
                && _animation.IsAiming
                && _animation.FirstPersonPointOfView
                && SkeetIrSightSelectionPatch.TryGetFixedOneXState(
                    _animation,
                    out activeState)
                && ReferenceEquals(activeState, _state)
                && SkeetIrChildZoomForwardPatch.IsSelectedAim(
                    _animation,
                    _state.SkeetAim)
                && _state.ProjectedCollimatorRenderer != null
                && _state.ProjectedCollimatorMaterial != null;
            if (!shouldDraw)
            {
                return;
            }

            HideCollimator();
            if (_reticleBuffer == null)
            {
                _reticleBuffer = new CommandBuffer
                {
                    name = "MMGA projected fixed-1x thermal reticle"
                };
                _reticleBuffer.DrawRenderer(
                    _state.ProjectedCollimatorRenderer,
                    _state.ProjectedCollimatorMaterial);
            }

            if (!ReferenceEquals(_reticleCamera, camera))
            {
                DetachReticleBuffer();
                camera.AddCommandBuffer(
                    CameraEvent.AfterImageEffects,
                    _reticleBuffer);
                _reticleCamera = camera;
            }

            if (!_projectedReticleWasLogged)
            {
                _projectedReticleWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Fixed 1x thermal now redraws the attached sight's real "
                    + "collimator projection after the main-camera image effects.");
            }
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera != null && ReferenceEquals(_reticleCamera, camera))
            {
                DetachReticleBuffer();
                RestoreCollimator();
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

        private void DetachReticleBuffer()
        {
            if (_reticleCamera != null && _reticleBuffer != null)
            {
                _reticleCamera.RemoveCommandBuffer(
                    CameraEvent.AfterImageEffects,
                    _reticleBuffer);
            }
            _reticleCamera = null;
        }

        private void ReleaseReticleBuffer()
        {
            DetachReticleBuffer();
            if (_reticleBuffer != null)
            {
                _reticleBuffer.Release();
                _reticleBuffer = null;
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
        private CommandBuffer _g33ReticleBuffer;
        private Camera _g33ReticleCamera;

        internal void Initialize(
            ProceduralWeaponAnimation animation,
            SkeetIrSightSelectionPatch.FrontClipOnState state)
        {
            ReleaseG33ReticleBuffer();
            RestoreRenderers();
            _animation = animation;
            _state = state;

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
            DetachG33ReticleBuffer();
            RestoreRenderers();
            RestoreCollimator();
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            ReleaseG33ReticleBuffer();
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
            if (_state == null
                || camera == null
                || !_state.ThermalActive)
            {
                DetachG33ReticleBuffer();
                RestoreRenderers();
                RestoreCollimator();
                return;
            }

            if (!SkeetIrSightSelectionPatch.IsActiveFrontClipOnState(
                _animation,
                _state))
            {
                DetachG33ReticleBuffer();
                RestoreRenderers();
                RestoreCollimator();
                return;
            }

            var cameraClass = CameraManager.Instance;
            if (cameraClass == null)
            {
                DetachG33ReticleBuffer();
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

                RestoreRenderers();
                RestoreCollimator();
                HideCollimator();
                AttachG33ReticleBuffer(camera);
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
                DetachG33ReticleBuffer();
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
            HideCollimator();
            AttachG33ReticleBuffer(camera);
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera == null
                || !ReferenceEquals(_g33ReticleCamera, camera))
            {
                return;
            }

            DetachG33ReticleBuffer();
            RestoreRenderers();
            RestoreCollimator();
        }

        private void AttachG33ReticleBuffer(Camera camera)
        {
            if (_state == null
                || _state.ProjectedCollimatorRenderer == null
                || _state.ProjectedCollimatorMaterial == null)
            {
                DetachG33ReticleBuffer();
                return;
            }

            if (_g33ReticleBuffer == null)
            {
                _g33ReticleBuffer = new CommandBuffer
                {
                    name = "MMGA projected G33 thermal reticle"
                };
                _g33ReticleBuffer.DrawRenderer(
                    _state.ProjectedCollimatorRenderer,
                    _state.ProjectedCollimatorMaterial);
            }

            if (!ReferenceEquals(_g33ReticleCamera, camera))
            {
                DetachG33ReticleBuffer();
                camera.AddCommandBuffer(
                    CameraEvent.AfterImageEffects,
                    _g33ReticleBuffer);
                _g33ReticleCamera = camera;
            }

            if (!_g33ProjectedReticleWasLogged)
            {
                _g33ProjectedReticleWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Thermal sight path now redraws the attached sight's "
                    + "real collimator projection after thermal processing; "
                    + "eye distance, _MarkShift, _MarkScale, and glass geometry "
                    + "therefore match the normal 3x reticle.");
            }
        }

        private void DetachG33ReticleBuffer()
        {
            if (_g33ReticleCamera != null && _g33ReticleBuffer != null)
            {
                _g33ReticleCamera.RemoveCommandBuffer(
                    CameraEvent.AfterImageEffects,
                    _g33ReticleBuffer);
            }

            _g33ReticleCamera = null;
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

        private void ReleaseG33ReticleBuffer()
        {
            DetachG33ReticleBuffer();
            if (_g33ReticleBuffer != null)
            {
                _g33ReticleBuffer.Release();
                _g33ReticleBuffer = null;
            }
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
