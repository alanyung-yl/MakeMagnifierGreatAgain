using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.Animations;
using EFT.CameraControl;
using EFT.InventoryLogic;
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

        internal static void RestoreAimBone(Transform bone)
        {
            var guard = bone != null
                ? bone.GetComponent<SkeetIrAimBonePoseGuard>()
                : null;
            if (guard != null)
            {
                guard.Restore();
            }
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
            internal Material G33LensMaterial;
            internal Texture G33OriginalMarkTexture;
            internal Texture G33ThermalMarkSource;
            internal Texture2D G33ThermalMarkTexture;
            internal float G33ThermalMarkScale = 1f;
            internal Color G33OriginalLensColor;
            internal float G33OriginalLensHdr;
            internal bool G33LensHasColor;
            internal bool G33LensHasHdr;
            internal bool G33ThermalReticleApplied;
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

        private static readonly Dictionary<int, FrontClipOnState> FrontClipOnStates =
            new Dictionary<int, FrontClipOnState>();
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
        private static bool _g33ThermalReticleRouteWasLogged;
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
                SetG33ThermalReticle(previousState, false);
                DestroyG33ThermalMarkTexture(previousState);
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
                skeetAim.Bone = childAim.Bone;
                skeetAim.Rotation = childAim.Rotation;
                skeetAim.LeftStanceRotation = childAim.LeftStanceRotation;
                skeetAim.EyeGuardShift = childAim.EyeGuardShift;

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
            CaptureG33LensMaterial(state);
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

        private static void CaptureG33LensMaterial(FrontClipOnState state)
        {
            if (state.G33OneXChildAim == null || !state.ChildCache.HasOptics)
            {
                return;
            }

            var optic = state.ChildCache.FirstOptic;
            var renderer = optic != null ? optic.LensRenderer : null;
            if (renderer == null)
            {
                return;
            }

            var material = renderer.material;
            if (material == null || !material.HasProperty("_MarkTex"))
            {
                return;
            }

            state.G33LensMaterial = material;
            state.G33OriginalMarkTexture = material.GetTexture("_MarkTex");
            state.G33LensHasColor = material.HasProperty("_Color");
            if (state.G33LensHasColor)
            {
                state.G33OriginalLensColor = material.GetColor("_Color");
            }

            state.G33LensHasHdr = material.HasProperty("_HDR");
            if (state.G33LensHasHdr)
            {
                state.G33OriginalLensHdr = material.GetFloat("_HDR");
            }
        }

        private static void SetG33ThermalReticle(
            FrontClipOnState state,
            bool enabled)
        {
            var targetMaterial = state.G33LensMaterial;
            if (targetMaterial == null)
            {
                return;
            }

            if (!enabled)
            {
                if (!state.G33ThermalReticleApplied)
                {
                    return;
                }

                targetMaterial.SetTexture("_MarkTex", state.G33OriginalMarkTexture);
                if (state.G33LensHasColor)
                {
                    targetMaterial.SetColor("_Color", state.G33OriginalLensColor);
                }
                if (state.G33LensHasHdr)
                {
                    targetMaterial.SetFloat("_HDR", state.G33OriginalLensHdr);
                }

                state.G33ThermalReticleApplied = false;
                return;
            }

            var nestedSight = NestedSightAimPatch.FindImmediateChildScope(
                state.ChildCache.transform);
            var collimator = nestedSight != null && nestedSight.HasCollimators
                ? nestedSight.FirstCollimator
                : null;
            var sourceRenderer = collimator != null
                ? collimator.CollimatorMeshRenderer
                : null;
            var sourceMaterial = collimator != null
                ? collimator.CollimatorMaterial
                : null;
            if (sourceMaterial == null && sourceRenderer != null)
            {
                sourceMaterial = sourceRenderer.sharedMaterial;
            }

            var markTexture = sourceMaterial != null
                && sourceMaterial.HasProperty("_MarkTex")
                ? sourceMaterial.GetTexture("_MarkTex")
                : null;
            if (markTexture == null)
            {
                return;
            }

            var markScale = sourceMaterial.HasProperty("_MarkScale")
                ? sourceMaterial.GetFloat("_MarkScale")
                : 1f;
            if (markScale <= 0f)
            {
                markScale = 1f;
            }

            targetMaterial.SetTexture(
                "_MarkTex",
                GetG33ThermalMarkTexture(state, markTexture, markScale));
            if (state.G33LensHasColor && sourceMaterial.HasProperty("_Color"))
            {
                targetMaterial.SetColor("_Color", sourceMaterial.GetColor("_Color"));
            }
            if (state.G33LensHasHdr && sourceMaterial.HasProperty("_HDR"))
            {
                targetMaterial.SetFloat("_HDR", sourceMaterial.GetFloat("_HDR"));
            }

            state.G33ThermalReticleApplied = true;
            if (!_g33ThermalReticleRouteWasLogged)
            {
                _g33ThermalReticleRouteWasLogged = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Custom G33 thermal 3x now uses the attached sight's mark on "
                    + "the G33 optic lens while its live collimator plane is omitted "
                    + "from the thermal camera pass; the copied mark preserves the "
                    + $"collimator's {markScale:0.###} scale.");
            }
        }

        private static Texture GetG33ThermalMarkTexture(
            FrontClipOnState state,
            Texture source,
            float visualScale)
        {
            if (Mathf.Approximately(visualScale, 1f))
            {
                DestroyG33ThermalMarkTexture(state);
                return source;
            }

            if (state.G33ThermalMarkTexture != null
                && ReferenceEquals(state.G33ThermalMarkSource, source)
                && Mathf.Approximately(state.G33ThermalMarkScale, visualScale))
            {
                return state.G33ThermalMarkTexture;
            }

            DestroyG33ThermalMarkTexture(state);

            var width = Math.Max(1, source.width);
            var height = Math.Max(1, source.height);
            var temporary = RenderTexture.GetTemporary(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            var previousActive = RenderTexture.active;
            var previousWrapMode = source.wrapMode;

            try
            {
                // Collimator marks are authored with _MarkScale, whereas
                // OpticSight's baked mark pass has no equivalent property.
                // Resample only the copied texture so the live thermal image,
                // masks, and G33 camera calibration remain untouched.
                source.wrapMode = TextureWrapMode.Clamp;
                var sampleScale = 1f / visualScale;
                var sampleOffset = (1f - sampleScale) * 0.5f;
                Graphics.Blit(
                    source,
                    temporary,
                    new Vector2(sampleScale, sampleScale),
                    new Vector2(sampleOffset, sampleOffset));

                RenderTexture.active = temporary;
                var scaled = new Texture2D(
                    width,
                    height,
                    TextureFormat.RGBA32,
                    false);
                scaled.name = source.name + "_MMGA_G33_Thermal";
                scaled.hideFlags = HideFlags.HideAndDontSave;
                scaled.filterMode = source.filterMode;
                scaled.wrapMode = TextureWrapMode.Clamp;
                scaled.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                scaled.Apply(false, true);

                state.G33ThermalMarkSource = source;
                state.G33ThermalMarkTexture = scaled;
                state.G33ThermalMarkScale = visualScale;
                return scaled;
            }
            finally
            {
                source.wrapMode = previousWrapMode;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private static void DestroyG33ThermalMarkTexture(FrontClipOnState state)
        {
            if (state.G33ThermalMarkTexture != null)
            {
                UnityEngine.Object.Destroy(state.G33ThermalMarkTexture);
            }

            state.G33ThermalMarkSource = null;
            state.G33ThermalMarkTexture = null;
            state.G33ThermalMarkScale = 1f;
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

            SetG33ThermalReticle(
                state,
                ShouldUseG33ThermalReticle(state));

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

        private static bool ShouldUseG33ThermalReticle(FrontClipOnState state)
        {
            if (state == null
                || !state.ThermalActive
                || state.ThermalUsesSkeetOptic
                || state.G33OneXChildAim == null
                || state.Animation == null
                || !state.Animation.IsAiming
                || !state.Animation.FirstPersonPointOfView)
            {
                return false;
            }

            var mode = state.ChildCache.CurrentModeId;
            var modeInfo = mode >= 0 && mode < state.ChildCache.ModesCount
                ? state.ChildCache.GetScopeModeInfo(mode)
                : null;
            return modeInfo != null && modeInfo.OpticSight != null;
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
                SetG33ThermalReticle(
                    frontClipOn,
                    ShouldUseG33ThermalReticle(frontClipOn));
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

            // The Skeet bundle's OpticSightBridge already composites _MarkTex
            // after sampling the live thermal camera. Copy the immediate
            // child's reticle texture into that material instead of feeding
            // its physical lens mesh into ScopeReticle: the latter expects a
            // purpose-authored scope-reticle mesh, not a collimator glass mesh.
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

        internal void Restore()
        {
            if (_captured)
            {
                transform.localPosition = _localPosition;
            }
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
    /// Finishes the clip-on transition after EFT's same-frame optic updates
    /// and prevents the child optic camera from photographing the physical
    /// clip-on that sits in front of it. Renderer suppression is scoped only
    /// to the optic-camera render; the Skeet model remains visible normally.
    /// </summary>
    internal sealed class SkeetIrFrontClipOnRuntime : MonoBehaviour
    {
        private static bool _g33CollimatorSuppressionWasLogged;
        private ProceduralWeaponAnimation _animation;
        private SkeetIrSightSelectionPatch.FrontClipOnState _state;
        private Renderer[] _occludingRenderers = new Renderer[0];
        private bool[] _rendererEnabledStates = new bool[0];
        private bool _renderersHidden;

        internal void Initialize(
            ProceduralWeaponAnimation animation,
            SkeetIrSightSelectionPatch.FrontClipOnState state)
        {
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

            if (NestedSightAimPatch.IsG33RootName(
                    state.ChildCache.transform.name))
            {
                var nestedSight = NestedSightAimPatch.FindImmediateChildScope(
                    state.ChildCache.transform);
                var collimator = nestedSight != null && nestedSight.HasCollimators
                    ? nestedSight.FirstCollimator
                    : null;
                var collimatorRenderer = collimator != null
                    ? collimator.CollimatorMeshRenderer
                    : null;
                if (collimatorRenderer != null)
                {
                    renderers.Add(collimatorRenderer);
                    if (!_g33CollimatorSuppressionWasLogged)
                    {
                        _g33CollimatorSuppressionWasLogged = true;
                        MakeMagnifierGreatAgainPlugin.LogInfo(
                            "Custom G33 thermal 3x will omit nested collimator renderer '"
                            + collimatorRenderer.name
                            + "' only while the optic camera renders.");
                    }
                }
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
            RestoreRenderers();
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            RestoreRenderers();
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
                RestoreRenderers();
                return;
            }

            if (!SkeetIrSightSelectionPatch.IsActiveFrontClipOnState(
                _animation,
                _state))
            {
                return;
            }

            var cameraClass = CameraManager.Instance;
            if (cameraClass == null
                || !ReferenceEquals(
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
                return;
            }

            RestoreRenderers();
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
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (!_renderersHidden || camera == null)
            {
                return;
            }

            var cameraClass = CameraManager.Instance;
            if (cameraClass != null
                && ReferenceEquals(
                    cameraClass.OpticCameraManager.Camera,
                    camera))
            {
                RestoreRenderers();
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
