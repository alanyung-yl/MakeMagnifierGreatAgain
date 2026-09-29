using System;
using System.Collections.Generic;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace MakeMagnifierGreatAgain
{
    /// <summary>
    /// Adapts EpicAIO's combined EXPS3/Unity-G33 prefab only for MMGA's
    /// separately cloned template. EFT requires a live transform matching a
    /// database slot before ObjectsFactory.AttachMods begins its bone scan.
    /// </summary>
    [HarmonyPatch(typeof(ObjectsFactory), nameof(ObjectsFactory.AttachMods))]
    internal static class EpicHighG33PrefabPatch
    {
        private const string EpicHighPairTemplateId =
            "41e97223a2ad6374a096ae14";
        private const string G33RuntimeRootName =
            "scope_all_eotech_g33_mmga_test_epicaio_unity";
        private const string RiserName = "ftc_riser";
        private const string UnityG33Name = "Unity G33";
        private const string AimBoneName = "mod_aim_camera";
        private const string ScopeSlotName = "mod_scope";
        private const float CalibratedUnityG33FieldOfView = 7.5f;
        private static readonly Vector3 ScopeSlotLocalEulerAngles =
            new Vector3(270f, 0f, 0f);

        private static bool _adaptedLogWritten;
        private static bool _missingHierarchyWarningWritten;

        [HarmonyPrefix]
        private static void Prefix(
            ContainerCollection containerCollection,
            ContainerCollectionView collectionView)
        {
            if (containerCollection == null
                || collectionView == null
                || collectionView.GameObject == null)
            {
                return;
            }

            var templateId = containerCollection.StringTemplateId;
            var prefabRoot = collectionView.GameObject.transform;
            var state = prefabRoot.GetComponent<EpicHighG33RuntimeState>();

            // EpicAIO and MMGA may receive successive instances from the same
            // prefab pool. Always restore the donor hierarchy when its original
            // template is assembled.
            if (string.Equals(
                templateId,
                EpicHighPairTemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                state?.RestoreEpicPair();
                return;
            }

            if (!string.Equals(
                templateId,
                MakeMagnifierGreatAgainPlugin.EpicHighG33TemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (state == null)
            {
                state = prefabRoot.gameObject.AddComponent<EpicHighG33RuntimeState>();
            }

            if (!TryAdapt(prefabRoot, state))
            {
                if (!_missingHierarchyWarningWritten)
                {
                    _missingHierarchyWarningWritten = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        "Could not adapt the EpicAIO EXPS3 high-magnifier "
                        + "prefab: ftc_riser, Unity G33, ScopePrefabCache, "
                        + "or mod_aim_camera was not found.");
                }
                return;
            }

            if (!_adaptedLogWritten)
            {
                _adaptedLogWritten = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Adapted EpicAIO's EXPS3 high-magnifier pair for the "
                    + "separate MMGA G33 item: promoted Unity G33 out of "
                    + "ftc_riser, disabled the riser, preserved "
                    + "the G33 aim bone, created its live mod_scope anchor, "
                    + "and suppressed the baked EpicAIO reticles.");
            }
        }

        private static bool TryAdapt(
            Transform prefabRoot,
            EpicHighG33RuntimeState state)
        {
            var riser = FindDescendantExact(prefabRoot, RiserName);
            var unityG33 = FindDescendantExact(prefabRoot, UnityG33Name);
            if (riser == null && state.Riser != null)
            {
                riser = state.Riser;
            }
            if (unityG33 == null && state.UnityG33 != null)
            {
                unityG33 = state.UnityG33;
            }
            if (riser == null || unityG33 == null)
            {
                return false;
            }

            var cache = FindG33Cache(prefabRoot, unityG33);
            if (cache == null && state.G33Cache != null)
            {
                cache = state.G33Cache;
            }
            if (cache == null)
            {
                return false;
            }

            var g33Host = cache.transform;
            var sourceAim = FindDescendantExact(riser, AimBoneName);
            if (sourceAim == null && state.SourceAimBone != null)
            {
                sourceAim = state.SourceAimBone;
            }

            if (sourceAim == null)
            {
                return false;
            }

            state.Capture(
                riser,
                unityG33,
                cache,
                sourceAim);
            state.NormalizeMagnifierFieldOfView(
                CalibratedUnityG33FieldOfView);

            // The baked pair's aim transform lives under ftc_riser. Recreate
            // the same empty bone under the logical G33 before hiding the
            // complete raiser hierarchy.
            var runtimeAim = state.GetOrCreateRuntimeAim(g33Host);
            CopyWorldPose(sourceAim, runtimeAim, false);
            runtimeAim.name = AimBoneName;
            runtimeAim.gameObject.layer = sourceAim.gameObject.layer;
            runtimeAim.gameObject.SetActive(true);
            sourceAim.name = EpicHighG33RuntimeState.HiddenAimBoneName;

            // Unity G33 is also nested below ftc_riser in EpicAIO's asset.
            // Promote the complete G33 subtree before the riser is disabled,
            // retaining the bundle-authored world placement and all serialized
            // ScopeModeInfo references to mode_000/mode_001.
            state.PromoteUnityG33(g33Host, riser.parent);

            // Use the FTC riser's base position, not the baked raised EXPS3
            // position. The 270-degree local X rotation is required for EFT to
            // orient an attached sight correctly beneath this prefab root.
            // Keep the slot immediately below ScopePrefabCache so MMGA's
            // existing nested-sight discovery treats it like the custom G33.
            var runtimeSlot = state.GetOrCreateRuntimeSlot(g33Host);
            runtimeSlot.position = riser.position;
            runtimeSlot.localRotation = Quaternion.Euler(
                ScopeSlotLocalEulerAngles);
            runtimeSlot.localScale = Vector3.one;
            runtimeSlot.name = ScopeSlotName;
            runtimeSlot.gameObject.layer = riser.gameObject.layer;
            runtimeSlot.gameObject.SetActive(true);

            // EpicAIO's combined pair owns both a 1x CollimatorSight and a
            // magnified ScopeReticle/_MarkTex. Geometry visibility does not
            // govern those camera-driven marks, so disable the donor reticle
            // sources explicitly. MMGA will project the attached child sight's
            // collimator through the G33 instead.
            state.SuppressDonorReticles();
            state.EnableAdaptedReticleTextureScale();

            g33Host.name = G33RuntimeRootName;
            riser.gameObject.SetActive(false);
            return true;
        }

        private static ScopePrefabCache FindG33Cache(
            Transform prefabRoot,
            Transform unityG33)
        {
            var caches = prefabRoot.GetComponentsInChildren<ScopePrefabCache>(true);
            ScopePrefabCache closestAncestor = null;
            var closestAncestorDepth = int.MaxValue;

            foreach (var cache in caches)
            {
                if (cache == null)
                {
                    continue;
                }

                if (ReferenceEquals(cache.transform, unityG33)
                    || cache.transform.IsChildOf(unityG33))
                {
                    return cache;
                }

                for (var modeIndex = 0; modeIndex < cache.ModesCount; modeIndex++)
                {
                    var mode = cache.GetScopeModeInfo(modeIndex);
                    var modeTransform = mode != null && mode.ModeGameObject != null
                        ? mode.ModeGameObject.transform
                        : null;
                    if (modeTransform != null
                        && (ReferenceEquals(modeTransform, unityG33)
                            || modeTransform.IsChildOf(unityG33)))
                    {
                        return cache;
                    }
                }

                var depth = AncestorDistance(unityG33, cache.transform);
                if (depth >= 0 && depth < closestAncestorDepth)
                {
                    closestAncestor = cache;
                    closestAncestorDepth = depth;
                }
            }

            return closestAncestor ?? prefabRoot.GetComponent<ScopePrefabCache>();
        }

        private static int AncestorDistance(Transform child, Transform ancestor)
        {
            var distance = 0;
            for (var cursor = child; cursor != null; cursor = cursor.parent)
            {
                if (ReferenceEquals(cursor, ancestor))
                {
                    return distance;
                }
                distance++;
            }
            return -1;
        }

        private static void CopyWorldPose(
            Transform source,
            Transform target,
            bool forceUnitScale)
        {
            target.position = source.position;
            target.rotation = source.rotation;
            target.localScale = forceUnitScale
                ? Vector3.one
                : source.localScale;
        }

        private static Transform FindDescendantExact(
            Transform root,
            string name)
        {
            if (root == null)
            {
                return null;
            }
            if (string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
            for (var index = 0; index < root.childCount; index++)
            {
                var found = FindDescendantExact(root.GetChild(index), name);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

    }

    internal sealed class EpicHighG33RuntimeState : MonoBehaviour
    {
        internal const float CenteredReticleArtworkScale = 0.5f;
        private const float ReticleTextureSupersampling = 4f;
        private const int MaximumReticleTextureDimension = 1024;
        internal const string HiddenAimBoneName =
            "mmga_hidden_epicaio_mod_aim_camera";
        private const string InactiveRuntimeAimName =
            "mmga_runtime_mod_aim_camera";
        private const string InactiveRuntimeSlotName =
            "mmga_runtime_mod_scope";
        private const string MarkTexturePropertyName = "_MarkTex";

        internal Transform Riser { get; private set; }
        internal Transform UnityG33 { get; private set; }
        internal ScopePrefabCache G33Cache { get; private set; }
        internal Transform SourceAimBone { get; private set; }

        private sealed class DonorReticleState
        {
            internal ScopePrefabCache.ScopeModeInfo Mode;
            internal CollimatorSight Collimator;
            internal bool CollimatorEnabled;
            internal MeshRenderer CollimatorRenderer;
            internal bool CollimatorRendererEnabled;
            internal EFT.CameraControl.ScopeData ScopeData;
            internal EFT.CameraControl.ScopeReticle ScopeReticle;
            internal Material LensMaterial;
            internal Texture LensMarkTexture;
            internal bool LensHasMarkTexture;
        }

        private sealed class ScopeCameraState
        {
            internal EFT.CameraControl.ScopeCameraData CameraData;
            internal float FieldOfView;
        }

        private sealed class ReticleRendererOverrideState
        {
            internal Renderer Renderer;
            internal Material[] OriginalMaterials;
        }

        private sealed class ScaledReticleMaterialState
        {
            internal Material Source;
            internal Material Scaled;
        }

        private sealed class ScaledReticleTextureState
        {
            internal Texture Source;
            internal Texture2D Scaled;
        }

        private Transform _runtimeAim;
        private Transform _runtimeSlot;
        private string _originalG33HostName;
        private Transform _originalUnityG33Parent;
        private int _originalUnityG33SiblingIndex;
        private Vector3 _originalUnityG33LocalPosition;
        private Quaternion _originalUnityG33LocalRotation;
        private Vector3 _originalUnityG33LocalScale;
        private bool _originalRiserActive;
        private bool _originalHasCollimators;
        private DonorReticleState[] _donorReticles;
        private ScopeCameraState[] _scopeCameras;
        private readonly List<ReticleRendererOverrideState>
            _activeReticleOverrides =
                new List<ReticleRendererOverrideState>();
        private readonly Dictionary<int, ScaledReticleMaterialState>
            _scaledReticleMaterials =
                new Dictionary<int, ScaledReticleMaterialState>();
        private readonly Dictionary<int, ScaledReticleTextureState>
            _scaledReticleTextures =
                new Dictionary<int, ScaledReticleTextureState>();
        private readonly HashSet<int> _seenReticleRenderers =
            new HashSet<int>();
        private bool _fieldOfViewLogWritten;
        private bool _reticleTextureScaleLogWritten;
        private bool _adapted;
        private bool _captured;

        internal bool IsAdapted => _adapted;

        internal void Capture(
            Transform riser,
            Transform unityG33,
            ScopePrefabCache cache,
            Transform sourceAimBone)
        {
            if (_captured)
            {
                return;
            }

            Riser = riser;
            UnityG33 = unityG33;
            G33Cache = cache;
            SourceAimBone = sourceAimBone;
            _originalG33HostName = cache.transform.name;
            _originalUnityG33Parent = unityG33.parent;
            _originalUnityG33SiblingIndex = unityG33.GetSiblingIndex();
            _originalUnityG33LocalPosition = unityG33.localPosition;
            _originalUnityG33LocalRotation = unityG33.localRotation;
            _originalUnityG33LocalScale = unityG33.localScale;
            _originalRiserActive = riser.gameObject.activeSelf;
            CaptureDonorReticles(cache);
            CaptureScopeCameras(cache);
            _captured = true;
        }

        private void CaptureScopeCameras(ScopePrefabCache cache)
        {
            var cameraData = cache.GetComponentsInChildren<
                EFT.CameraControl.ScopeCameraData>(true);
            _scopeCameras = new ScopeCameraState[cameraData.Length];
            for (var index = 0; index < cameraData.Length; index++)
            {
                _scopeCameras[index] = new ScopeCameraState
                {
                    CameraData = cameraData[index],
                    FieldOfView = cameraData[index].FieldOfView
                };
            }
        }

        internal void NormalizeMagnifierFieldOfView(float fieldOfView)
        {
            if (_scopeCameras == null || _scopeCameras.Length == 0)
            {
                return;
            }

            var originalFieldOfView = _scopeCameras[0].FieldOfView;
            foreach (var state in _scopeCameras)
            {
                if (state != null && state.CameraData != null)
                {
                    state.CameraData.FieldOfView = fieldOfView;
                }
            }

            if (!_fieldOfViewLogWritten)
            {
                _fieldOfViewLogWritten = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Calibrated the adapted Unity G33 optic-camera FOV from "
                    + originalFieldOfView.ToString("0.####") + " to "
                    + fieldOfView.ToString("0.####")
                    + "; the child reticle keeps its native projection and "
                    + "point of aim.");
            }
        }

        internal void EnableAdaptedReticleTextureScale()
        {
            _adapted = true;
        }

        internal static bool IsAdaptedUnityG33(ScopePrefabCache g33Cache)
        {
            if (g33Cache == null)
            {
                return false;
            }

            var runtime = g33Cache.GetComponentInParent<
                EpicHighG33RuntimeState>();
            return runtime != null && runtime.IsAdapted;
        }

        internal static Texture2D CreateCenteredScaledReticleTexture(
            Texture markTexture)
        {
            if (markTexture == null)
            {
                return null;
            }

            var sourceWidth = Mathf.Max(1, markTexture.width);
            var sourceHeight = Mathf.Max(1, markTexture.height);
            var maximumSourceDimension = Mathf.Max(
                sourceWidth,
                sourceHeight);
            var outputScale = Mathf.Clamp(
                Mathf.Min(
                    ReticleTextureSupersampling,
                    MaximumReticleTextureDimension
                        / (float)maximumSourceDimension),
                1f,
                ReticleTextureSupersampling);
            var width = Mathf.Max(
                1,
                Mathf.RoundToInt(sourceWidth * outputScale));
            var height = Mathf.Max(
                1,
                Mathf.RoundToInt(sourceHeight * outputScale));
            var temporary = RenderTexture.GetTemporary(
                sourceWidth,
                sourceHeight,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Texture2D readable = null;
            Texture2D scaled = null;
            try
            {
                Graphics.Blit(markTexture, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(
                    sourceWidth,
                    sourceHeight,
                    TextureFormat.RGBA32,
                    false,
                    true);
                readable.ReadPixels(
                    new Rect(0f, 0f, sourceWidth, sourceHeight),
                    0,
                    0,
                    false);
                readable.Apply(false, false);

                var scaledPixels = new Color32[width * height];
                var inverseScale = 1f / CenteredReticleArtworkScale;
                for (var y = 0; y < height; y++)
                {
                    var destinationV = (y + 0.5f) / height;
                    var sourceV = 0.5f
                        + (destinationV - 0.5f) * inverseScale;
                    if (sourceV < 0f || sourceV > 1f)
                    {
                        continue;
                    }

                    var row = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var destinationU = (x + 0.5f) / width;
                        var sourceU = 0.5f
                            + (destinationU - 0.5f) * inverseScale;
                        if (sourceU < 0f || sourceU > 1f)
                        {
                            continue;
                        }

                        scaledPixels[row + x] = (Color32)readable
                            .GetPixelBilinear(sourceU, sourceV);
                    }
                }

                var mipChain = markTexture.mipmapCount > 1;
                scaled = new Texture2D(
                    width,
                    height,
                    TextureFormat.RGBA32,
                    mipChain,
                    true)
                {
                    name = markTexture.name
                        + "_MMGA_UnityG33_CenteredHalfScale_"
                        + width + "x" + height,
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = markTexture.wrapMode,
                    anisoLevel = markTexture.anisoLevel
                };
                scaled.SetPixels32(scaledPixels);
                scaled.Apply(mipChain, false);
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Created a supersampled centered Unity G33 reticle copy "
                    + "for '" + markTexture.name + "': "
                    + sourceWidth + "x" + sourceHeight + " -> "
                    + width + "x" + height + ", artwork scale "
                    + CenteredReticleArtworkScale.ToString("0.###") + ".");
                return scaled;
            }
            catch (Exception exception)
            {
                if (scaled != null)
                {
                    UnityEngine.Object.Destroy(scaled);
                }
                MakeMagnifierGreatAgainPlugin.LogWarning(
                    "Could not create the centered Unity G33 reticle texture "
                    + "copy for '" + markTexture.name + "'; retaining the "
                    + "native mark. " + exception.Message);
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

        private void OnEnable()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            RestoreReticleMaterials();
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            RestoreReticleMaterials();
            DestroyScaledReticleResources();
        }

        private void OnCameraPreCull(Camera camera)
        {
            RestoreReticleMaterials();
            if (!_adapted
                || camera == null
                || _runtimeSlot == null
                || G33Cache == null)
            {
                return;
            }

            var cameraManager = CameraManager.Instance;
            if (cameraManager == null
                || !ReferenceEquals(
                    cameraManager.OpticCameraManager.Camera,
                    camera))
            {
                return;
            }

            var currentOptic =
                cameraManager.OpticCameraManager.CurrentOpticSight;
            if (currentOptic == null
                || (!ReferenceEquals(currentOptic.transform, G33Cache.transform)
                    && !currentOptic.transform.IsChildOf(G33Cache.transform)))
            {
                return;
            }

            _seenReticleRenderers.Clear();
            var collimators = _runtimeSlot.GetComponentsInChildren<
                CollimatorSight>(true);
            foreach (var collimator in collimators)
            {
                var renderer = collimator != null
                    ? collimator.CollimatorMeshRenderer
                    : null;
                if (renderer == null
                    || !_seenReticleRenderers.Add(renderer.GetInstanceID()))
                {
                    continue;
                }

                ApplyReticleMaterialOverride(renderer);
            }

            if (_activeReticleOverrides.Count > 0
                && !_reticleTextureScaleLogWritten)
            {
                _reticleTextureScaleLogWritten = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Scaled the adapted Unity G33 3x reticle artwork to "
                    + (CenteredReticleArtworkScale * 100f).ToString("0.#")
                    + "% around UV center 0.5 while retaining native "
                    + "_MarkScale, _MarkShift, and the normal 1x material.");
            }
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            var cameraManager = CameraManager.Instance;
            if (cameraManager != null
                && ReferenceEquals(
                    cameraManager.OpticCameraManager.Camera,
                    camera))
            {
                RestoreReticleMaterials();
            }
        }

        private void ApplyReticleMaterialOverride(Renderer renderer)
        {
            var originalMaterials = renderer.sharedMaterials;
            if (originalMaterials == null || originalMaterials.Length == 0)
            {
                return;
            }

            var scaledMaterials = (Material[])originalMaterials.Clone();
            var changed = false;
            for (var index = 0; index < originalMaterials.Length; index++)
            {
                var scaled = GetScaledReticleMaterial(
                    originalMaterials[index]);
                if (scaled == null)
                {
                    continue;
                }

                scaledMaterials[index] = scaled;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            _activeReticleOverrides.Add(
                new ReticleRendererOverrideState
                {
                    Renderer = renderer,
                    OriginalMaterials = originalMaterials
                });
            renderer.sharedMaterials = scaledMaterials;
        }

        private Material GetScaledReticleMaterial(Material source)
        {
            if (source == null
                || !source.HasProperty(MarkTexturePropertyName))
            {
                return null;
            }

            var sourceMark = source.GetTexture(MarkTexturePropertyName);
            var scaledMark = GetOrCreateScaledReticleTexture(sourceMark);
            if (scaledMark == null)
            {
                return null;
            }

            var sourceId = source.GetInstanceID();
            ScaledReticleMaterialState state;
            if (!_scaledReticleMaterials.TryGetValue(sourceId, out state)
                || state == null
                || state.Source != source
                || state.Scaled == null
                || state.Scaled.shader != source.shader)
            {
                if (state != null && state.Scaled != null)
                {
                    UnityEngine.Object.Destroy(state.Scaled);
                }

                state = new ScaledReticleMaterialState
                {
                    Source = source,
                    Scaled = new Material(source)
                    {
                        name = source.name
                            + " (MMGA Unity G33 centered reticle)",
                        hideFlags = HideFlags.HideAndDontSave
                    }
                };
                _scaledReticleMaterials[sourceId] = state;
            }
            else
            {
                state.Scaled.CopyPropertiesFromMaterial(source);
            }

            state.Scaled.SetTexture(
                MarkTexturePropertyName,
                scaledMark);
            return state.Scaled;
        }

        private Texture2D GetOrCreateScaledReticleTexture(Texture source)
        {
            if (source == null)
            {
                return null;
            }

            var sourceId = source.GetInstanceID();
            ScaledReticleTextureState state;
            if (_scaledReticleTextures.TryGetValue(sourceId, out state)
                && state != null
                && state.Source == source
                && state.Scaled != null)
            {
                return state.Scaled;
            }

            if (state != null && state.Scaled != null)
            {
                UnityEngine.Object.Destroy(state.Scaled);
            }

            var scaled = CreateCenteredScaledReticleTexture(source);
            if (scaled == null)
            {
                return null;
            }

            _scaledReticleTextures[sourceId] =
                new ScaledReticleTextureState
                {
                    Source = source,
                    Scaled = scaled
                };
            return scaled;
        }

        private void RestoreReticleMaterials()
        {
            for (var index = _activeReticleOverrides.Count - 1;
                index >= 0;
                index--)
            {
                var state = _activeReticleOverrides[index];
                if (state != null
                    && state.Renderer != null
                    && state.OriginalMaterials != null)
                {
                    state.Renderer.sharedMaterials = state.OriginalMaterials;
                }
            }

            _activeReticleOverrides.Clear();
        }

        private void DestroyScaledReticleResources()
        {
            foreach (var pair in _scaledReticleMaterials)
            {
                var state = pair.Value;
                if (state != null && state.Scaled != null)
                {
                    UnityEngine.Object.Destroy(state.Scaled);
                }
            }
            _scaledReticleMaterials.Clear();

            foreach (var pair in _scaledReticleTextures)
            {
                var state = pair.Value;
                if (state != null && state.Scaled != null)
                {
                    UnityEngine.Object.Destroy(state.Scaled);
                }
            }
            _scaledReticleTextures.Clear();
        }

        private void CaptureDonorReticles(ScopePrefabCache cache)
        {
            _originalHasCollimators = cache.HasCollimators;
            _donorReticles = new DonorReticleState[cache.ModesCount];
            for (var modeIndex = 0; modeIndex < cache.ModesCount; modeIndex++)
            {
                var mode = cache.GetScopeModeInfo(modeIndex);
                var collimator = mode != null ? mode.CollimatorSight : null;
                var collimatorRenderer = collimator != null
                    ? collimator.CollimatorMeshRenderer
                    : null;
                var optic = mode != null ? mode.OpticSight : null;
                var scopeData = optic != null ? optic.ScopeData : null;
                var lensMaterial = optic != null && optic.LensRenderer != null
                    ? optic.LensRenderer.material
                    : null;
                var hasMarkTexture = lensMaterial != null
                    && lensMaterial.HasProperty("_MarkTex");

                _donorReticles[modeIndex] = new DonorReticleState
                {
                    Mode = mode,
                    Collimator = collimator,
                    CollimatorEnabled = collimator != null
                        && collimator.enabled,
                    CollimatorRenderer = collimatorRenderer,
                    CollimatorRendererEnabled = collimatorRenderer != null
                        && collimatorRenderer.enabled,
                    ScopeData = scopeData,
                    ScopeReticle = scopeData != null
                        ? scopeData.Reticle
                        : null,
                    LensMaterial = lensMaterial,
                    LensMarkTexture = hasMarkTexture
                        ? lensMaterial.GetTexture("_MarkTex")
                        : null,
                    LensHasMarkTexture = hasMarkTexture
                };
            }
        }

        internal void SuppressDonorReticles()
        {
            if (G33Cache == null || _donorReticles == null)
            {
                return;
            }

            foreach (var donor in _donorReticles)
            {
                if (donor == null)
                {
                    continue;
                }
                if (donor.Mode != null)
                {
                    donor.Mode.CollimatorSight = null;
                }
                if (donor.Collimator != null)
                {
                    donor.Collimator.enabled = false;
                }
                if (donor.CollimatorRenderer != null)
                {
                    donor.CollimatorRenderer.enabled = false;
                }
                if (donor.ScopeData != null)
                {
                    donor.ScopeData.Reticle = null;
                }
                if (donor.LensHasMarkTexture && donor.LensMaterial != null)
                {
                    donor.LensMaterial.SetTexture(
                        "_MarkTex",
                        Texture2D.blackTexture);
                }
            }

            G33Cache.HasCollimators = false;
        }

        private void RestoreDonorReticles()
        {
            if (G33Cache == null || _donorReticles == null)
            {
                return;
            }

            foreach (var donor in _donorReticles)
            {
                if (donor == null)
                {
                    continue;
                }
                if (donor.Mode != null)
                {
                    donor.Mode.CollimatorSight = donor.Collimator;
                }
                if (donor.Collimator != null)
                {
                    donor.Collimator.enabled = donor.CollimatorEnabled;
                }
                if (donor.CollimatorRenderer != null)
                {
                    donor.CollimatorRenderer.enabled =
                        donor.CollimatorRendererEnabled;
                }
                if (donor.ScopeData != null)
                {
                    donor.ScopeData.Reticle = donor.ScopeReticle;
                }
                if (donor.LensHasMarkTexture && donor.LensMaterial != null)
                {
                    donor.LensMaterial.SetTexture(
                        "_MarkTex",
                        donor.LensMarkTexture);
                }
            }

            G33Cache.HasCollimators = _originalHasCollimators;
        }

        internal void PromoteUnityG33(
            Transform g33Host,
            Transform riserParent)
        {
            if (UnityG33 == null)
            {
                return;
            }

            // A cache on/inside Unity G33 moves with that subtree. A cache on
            // the pair root can directly become Unity G33's new parent.
            var targetParent = g33Host != null
                && !ReferenceEquals(g33Host, UnityG33)
                && !g33Host.IsChildOf(UnityG33)
                ? g33Host
                : riserParent;
            if (targetParent != null
                && !ReferenceEquals(UnityG33.parent, targetParent))
            {
                UnityG33.SetParent(targetParent, true);
            }
        }

        internal Transform GetOrCreateRuntimeAim(Transform parent)
        {
            if (_runtimeAim == null)
            {
                var gameObject = new GameObject(InactiveRuntimeAimName);
                _runtimeAim = gameObject.transform;
            }
            _runtimeAim.SetParent(parent, true);
            return _runtimeAim;
        }

        internal Transform GetOrCreateRuntimeSlot(Transform parent)
        {
            if (_runtimeSlot == null)
            {
                var gameObject = new GameObject(InactiveRuntimeSlotName);
                _runtimeSlot = gameObject.transform;
            }
            _runtimeSlot.SetParent(parent, true);
            return _runtimeSlot;
        }

        internal void RestoreEpicPair()
        {
            if (!_captured)
            {
                return;
            }

            _adapted = false;
            RestoreReticleMaterials();

            if (G33Cache != null)
            {
                G33Cache.transform.name = _originalG33HostName;
            }
            if (SourceAimBone != null)
            {
                SourceAimBone.name = "mod_aim_camera";
            }
            RestoreDonorReticles();
            RestoreScopeCameras();
            if (UnityG33 != null && _originalUnityG33Parent != null)
            {
                UnityG33.SetParent(_originalUnityG33Parent, false);
                UnityG33.localPosition = _originalUnityG33LocalPosition;
                UnityG33.localRotation = _originalUnityG33LocalRotation;
                UnityG33.localScale = _originalUnityG33LocalScale;
                UnityG33.SetSiblingIndex(Math.Min(
                    _originalUnityG33SiblingIndex,
                    _originalUnityG33Parent.childCount - 1));
            }
            if (Riser != null)
            {
                Riser.gameObject.SetActive(_originalRiserActive);
            }
            if (_runtimeAim != null)
            {
                _runtimeAim.name = InactiveRuntimeAimName;
                _runtimeAim.gameObject.SetActive(false);
            }
            if (_runtimeSlot != null)
            {
                _runtimeSlot.name = InactiveRuntimeSlotName;
                _runtimeSlot.gameObject.SetActive(false);
            }
        }

        private void RestoreScopeCameras()
        {
            if (_scopeCameras == null)
            {
                return;
            }

            foreach (var state in _scopeCameras)
            {
                if (state != null && state.CameraData != null)
                {
                    state.CameraData.FieldOfView = state.FieldOfView;
                }
            }
        }
    }
}
