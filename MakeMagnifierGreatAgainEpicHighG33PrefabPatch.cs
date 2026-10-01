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
    /// Adapts EpicAIO's combined EXPS3/UH-1 G33 prefabs only for MMGA's
    /// separately cloned FDE, black, and tan templates. EFT requires a live
    /// transform matching a database slot before ObjectsFactory.AttachMods
    /// scans bones.
    /// </summary>
    [HarmonyPatch(typeof(ObjectsFactory), nameof(ObjectsFactory.AttachMods))]
    internal static class EpicHighG33PrefabPatch
    {
        private const string EpicExps3HighPairTemplateId =
            "41e97223a2ad6374a096ae14";
        private const string EpicUh1HighPairTemplateId =
            "a5c171496c1870f205a3c972";
        private const string EpicExps3LowPairTemplateId =
            "7ca248803b89788492384ba2";
        private const string G33RuntimeRootName =
            "scope_all_eotech_g33_mmga_test_epicaio_unity";
        private const string RiserName = "ftc_riser";
        private const string UnityFtcRiserName = "Unity FTC Riser";
        private const string Uh1DonorSightName =
            "scope_all_vortex_razor_amg_uh-1 (merge)";
        private const string Exps3LowDonorSightName =
            "scope_all_eotech_exps3";
        private const string UnityG33Name = "Unity G33";
        private const string G33BaseName = "G33 Base";
        private const string AimBoneName = "mod_aim_camera";
        private const string ScopeSlotName = "mod_scope";
        private const float CalibratedUnityG33FieldOfView = 5.16f;  // original = 5.16; vanilla = 5.8333
        private static readonly Vector3 ScopeSlotLocalEulerAngles =
            new Vector3(270f, 0f, 0f);

        private static readonly HashSet<string> AdaptedTemplateLogs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> MissingHierarchyWarnings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DonorDiagnosticLogs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            if (IsDonorPairTemplate(templateId))
            {
                state?.RestoreEpicPair();
                InstallDonorAimDiagnostic(
                    prefabRoot,
                    templateId);
                return;
            }

            var pairLabel = GetMmgaPairLabel(templateId);
            if (pairLabel == null)
            {
                return;
            }

            if (state == null)
            {
                state = prefabRoot.gameObject.AddComponent<EpicHighG33RuntimeState>();
            }

            if (!TryAdapt(prefabRoot, state, templateId))
            {
                if (MissingHierarchyWarnings.Add(templateId))
                {
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        "Could not adapt the EpicAIO " + pairLabel
                        + " pair prefab: G33, donor sight, "
                        + "ScopePrefabCache, or mod_aim_camera was not found.");
                }
                return;
            }

            if (AdaptedTemplateLogs.Add(templateId))
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Adapted EpicAIO's " + pairLabel
                    + " pair for the separate MMGA G33 item: isolated the "
                    + "G33 from the donor pair, disabled baked sight/riser "
                    + "geometry where present, preserved "
                    + "the G33 aim bone, created its live mod_scope anchor, "
                    + "and suppressed the baked EpicAIO reticles.");
            }
        }

        private static bool IsDonorPairTemplate(string templateId)
        {
            return string.Equals(
                    templateId,
                    EpicExps3HighPairTemplateId,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    templateId,
                    EpicUh1HighPairTemplateId,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    templateId,
                    EpicExps3LowPairTemplateId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMmgaPairLabel(string templateId)
        {
            if (string.Equals(
                templateId,
                MakeMagnifierGreatAgainPlugin.EpicHighG33TemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                return "EXPS3/FDE";
            }

            if (string.Equals(
                templateId,
                MakeMagnifierGreatAgainPlugin.EpicUh1HighG33TemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                return "UH-1/black";
            }

            if (string.Equals(
                templateId,
                MakeMagnifierGreatAgainPlugin.EpicExps3LowG33TemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                return "EXPS3 low/tan";
            }

            return null;
        }

        private static bool TryAdapt(
            Transform prefabRoot,
            EpicHighG33RuntimeState state,
            string templateId)
        {
            var isLowPair = string.Equals(
                templateId,
                MakeMagnifierGreatAgainPlugin.EpicExps3LowG33TemplateId,
                StringComparison.OrdinalIgnoreCase);
            var isUh1Pair = string.Equals(
                templateId,
                MakeMagnifierGreatAgainPlugin.EpicUh1HighG33TemplateId,
                StringComparison.OrdinalIgnoreCase);
            var preserveFdeRiserHierarchy = string.Equals(
                templateId,
                MakeMagnifierGreatAgainPlugin.EpicHighG33TemplateId,
                StringComparison.OrdinalIgnoreCase);

            var riser = isLowPair
                ? null
                : FindDescendantExact(prefabRoot, RiserName)
                    ?? FindDescendantExact(prefabRoot, UnityFtcRiserName);
            var unityG33 = FindDescendantExact(
                prefabRoot,
                isLowPair ? G33BaseName : UnityG33Name);
            if (!isLowPair && riser == null && state.Riser != null)
            {
                riser = state.Riser;
            }
            if (unityG33 == null && state.UnityG33 != null)
            {
                unityG33 = state.UnityG33;
            }
            if ((!isLowPair && riser == null) || unityG33 == null)
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
            // After the first adaptation the generated runtime bone is the
            // only transform still named mod_aim_camera. Always prefer the
            // captured EpicAIO source on a repeated AttachMods pass; otherwise
            // the adapter can select its own generated bone and hide it.
            var sourceAim = state.SourceAimBone;
            if (sourceAim == null)
            {
                sourceAim = riser != null
                    ? FindDescendantExact(riser, AimBoneName)
                        // The UH-1 high pair stores its aim bone beside the
                        // riser, sight, and Unity G33 beneath the pair root.
                        // The EXPS3 high pair stores the same bone inside
                        // ftc_riser.
                        ?? FindDirectChildExact(riser.parent, AimBoneName)
                    // The low pair keeps mod_aim_camera beside its EXPS3 and
                    // G33.
                    : FindDirectChildExact(prefabRoot, AimBoneName);
            }
            if (sourceAim == null)
            {
                sourceAim = FindDescendantExact(prefabRoot, AimBoneName);
            }

            if (sourceAim == null)
            {
                return false;
            }

            var donorSight = isLowPair
                ? FindDirectChildExact(prefabRoot, Exps3LowDonorSightName)
                : preserveFdeRiserHierarchy
                    ? FindDescendantExact(
                        prefabRoot,
                        Exps3LowDonorSightName)
                    : FindDescendantExact(prefabRoot, Uh1DonorSightName);
            if (donorSight == null && state.DonorSight != null)
            {
                donorSight = state.DonorSight;
            }
            if ((isLowPair || isUh1Pair) && donorSight == null)
            {
                return false;
            }

            state.Capture(
                riser,
                unityG33,
                cache,
                sourceAim,
                donorSight,
                preserveFdeRiserHierarchy);
            state.NormalizeMagnifierFieldOfView(
                CalibratedUnityG33FieldOfView);

            if (preserveFdeRiserHierarchy)
            {
                // EXPS3/high owns the complete G33 and aim transform beneath
                // ftc_riser. Keep that authored transform container active and
                // retain its original aim bone. Only its captured renderers
                // are hidden below, so no parent scale or animation state is
                // lost merely to remove the baked riser and EXPS3 geometry.
                state.UseSourceAim();
            }
            else
            {
                // The other pairs can isolate their G33 without losing an
                // authored parent transform. Recreate the pair's aim transform
                // under the logical G33 before hiding the original hierarchy.
                var runtimeAim = state.GetOrCreateRuntimeAim(g33Host);
                CopyWorldPose(sourceAim, runtimeAim, false);
                runtimeAim.name = AimBoneName;
                runtimeAim.gameObject.layer = sourceAim.gameObject.layer;
                runtimeAim.gameObject.SetActive(true);
                sourceAim.name = EpicHighG33RuntimeState.HiddenAimBoneName;

                // Retain the bundle-authored world placement and all serialized
                // ScopeModeInfo references to mode_000/mode_001.
                state.PromoteUnityG33(
                    g33Host,
                    riser != null ? riser.parent : prefabRoot);
            }

            // High pairs use the FTC riser's base position. The low pair has no
            // riser, so its immediate EXPS3 child is the replacement anchor.
            // The 270-degree local X rotation is required for EFT to orient an
            // attached sight correctly beneath this prefab root.
            var slotAnchor = riser ?? donorSight;
            var runtimeSlot = state.GetOrCreateRuntimeSlot(g33Host);
            runtimeSlot.position = slotAnchor.position;
            runtimeSlot.localRotation = Quaternion.Euler(
                ScopeSlotLocalEulerAngles);
            runtimeSlot.localScale = Vector3.one;
            runtimeSlot.name = ScopeSlotName;
            runtimeSlot.gameObject.layer = slotAnchor.gameObject.layer;
            runtimeSlot.gameObject.SetActive(true);

            // EpicAIO's combined pair owns both a 1x CollimatorSight and a
            // magnified ScopeReticle/_MarkTex. Geometry visibility does not
            // govern those camera-driven marks, so disable the donor reticle
            // sources explicitly. MMGA will project the attached child sight's
            // collimator through the G33 instead.
            state.SuppressDonorReticles();
            state.EnableAdaptedReticleTextureScale();

            g33Host.name = G33RuntimeRootName;
            if (preserveFdeRiserHierarchy)
            {
                state.HideCapturedFdeRiserRenderers();
            }
            else
            {
                state.HideDonorGeometry();
                if (riser != null)
                {
                    riser.gameObject.SetActive(false);
                }
            }
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

        private static void InstallDonorAimDiagnostic(
            Transform prefabRoot,
            string templateId)
        {
            var isLowPair = string.Equals(
                templateId,
                EpicExps3LowPairTemplateId,
                StringComparison.OrdinalIgnoreCase);
            var riser = isLowPair
                ? null
                : FindDescendantExact(prefabRoot, RiserName)
                    ?? FindDescendantExact(prefabRoot, UnityFtcRiserName);
            var unityG33 = FindDescendantExact(
                prefabRoot,
                isLowPair ? G33BaseName : UnityG33Name);
            var aim = riser != null
                ? FindDescendantExact(riser, AimBoneName)
                    ?? FindDirectChildExact(riser.parent, AimBoneName)
                : FindDirectChildExact(prefabRoot, AimBoneName);
            if (aim == null)
            {
                aim = FindDescendantExact(prefabRoot, AimBoneName);
            }
            if (aim == null || unityG33 == null)
            {
                return;
            }

            var diagnostic = aim.GetComponent<
                EpicOriginalG33AimDiagnostic>();
            if (diagnostic == null)
            {
                diagnostic = aim.gameObject.AddComponent<
                    EpicOriginalG33AimDiagnostic>();
            }
            diagnostic.Initialize(
                GetDonorPairLabel(templateId),
                unityG33);
            if (DonorDiagnosticLogs.Add(templateId))
            {
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Armed the " + GetDonorPairLabel(templateId)
                    + " rendered-ADS aim diagnostic on '"
                    + aim.name + "' relative to '" + unityG33.name + "'.");
            }
        }

        private static string GetDonorPairLabel(string templateId)
        {
            if (string.Equals(
                templateId,
                EpicExps3HighPairTemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                return "EpicAIO EXPS3/high pair";
            }

            if (string.Equals(
                templateId,
                EpicUh1HighPairTemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                return "EpicAIO UH-1/high pair";
            }

            return "EpicAIO EXPS3/low pair";
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

        private static Transform FindDirectChildExact(
            Transform parent,
            string name)
        {
            if (parent == null)
            {
                return null;
            }

            for (var index = 0; index < parent.childCount; index++)
            {
                var child = parent.GetChild(index);
                if (string.Equals(
                    child.name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }

    }

    internal sealed class EpicOriginalG33AimDiagnostic : MonoBehaviour
    {
        private Transform _unityG33;
        private string _pairLabel;
        private Vector3 _authoredPositionInUnityG33;
        private Quaternion _authoredRotationInUnityG33;
        private bool _initialized;
        private bool _comparisonLogWritten;

        internal void Initialize(string pairLabel, Transform unityG33)
        {
            if (unityG33 == null)
            {
                return;
            }

            _pairLabel = pairLabel;
            _unityG33 = unityG33;
            _authoredPositionInUnityG33 = unityG33.InverseTransformPoint(
                transform.position);
            _authoredRotationInUnityG33 = Quaternion.Inverse(
                    unityG33.rotation)
                * transform.rotation;
            _comparisonLogWritten = false;
            _initialized = true;
        }

        private void OnEnable()
        {
            Camera.onPreCull -= OnCameraPreCull;
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

        private void OnCameraPreCull(Camera camera)
        {
            if (_comparisonLogWritten
                || !EpicHighG33RuntimeState.IsSelectedAimAtMainCamera(
                    camera,
                    transform)
                || !TryCapturePose(
                    out var renderedPosition,
                    out var renderedRotation))
            {
                return;
            }

            _comparisonLogWritten = true;
            MakeMagnifierGreatAgainPlugin.LogInfo(
                _pairLabel
                + " rendered ADS mod_aim_camera in G33-local space: "
                + "authored="
                + EpicHighG33RuntimeState.FormatAimPose(
                    _authoredPositionInUnityG33,
                    _authoredRotationInUnityG33)
                + "; rendered="
                + EpicHighG33RuntimeState.FormatAimPose(
                    renderedPosition,
                    renderedRotation)
                + " (authored delta "
                + EpicHighG33RuntimeState.FormatAimPoseDelta(
                    _authoredPositionInUnityG33,
                    _authoredRotationInUnityG33,
                    renderedPosition,
                    renderedRotation)
                + ").");
        }

        internal bool TryCapturePose(
            out Vector3 positionInUnityG33,
            out Quaternion rotationInUnityG33)
        {
            positionInUnityG33 = Vector3.zero;
            rotationInUnityG33 = Quaternion.identity;
            if (!_initialized || _unityG33 == null)
            {
                return false;
            }

            positionInUnityG33 = _unityG33.InverseTransformPoint(
                transform.position);
            rotationInUnityG33 = Quaternion.Inverse(_unityG33.rotation)
                * transform.rotation;
            return true;
        }

        internal void LogRuntimeAimComparison(
            Vector3 positionBeforeAlignment,
            Quaternion rotationBeforeAlignment)
        {
            if (_comparisonLogWritten
                || !TryCapturePose(
                    out var positionAfterAlignment,
                    out var rotationAfterAlignment))
            {
                return;
            }

            _comparisonLogWritten = true;
            MakeMagnifierGreatAgainPlugin.LogInfo(
                _pairLabel
                + " runtime mod_aim_camera comparison in G33-local space: "
                + "authored="
                + EpicHighG33RuntimeState.FormatAimPose(
                    _authoredPositionInUnityG33,
                    _authoredRotationInUnityG33)
                + "; before OpticLookAtGlobal="
                + EpicHighG33RuntimeState.FormatAimPose(
                    positionBeforeAlignment,
                    rotationBeforeAlignment)
                + " (authored delta "
                + EpicHighG33RuntimeState.FormatAimPoseDelta(
                    _authoredPositionInUnityG33,
                    _authoredRotationInUnityG33,
                    positionBeforeAlignment,
                    rotationBeforeAlignment)
                + "); after="
                + EpicHighG33RuntimeState.FormatAimPose(
                    positionAfterAlignment,
                    rotationAfterAlignment)
                + " (authored delta "
                + EpicHighG33RuntimeState.FormatAimPoseDelta(
                    _authoredPositionInUnityG33,
                    _authoredRotationInUnityG33,
                    positionAfterAlignment,
                    rotationAfterAlignment)
                + ", alignment shift "
                + EpicHighG33RuntimeState.FormatAimPoseDelta(
                    positionBeforeAlignment,
                    rotationBeforeAlignment,
                    positionAfterAlignment,
                    rotationAfterAlignment)
                + ").");
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
        internal Transform DonorSight { get; private set; }

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

        private sealed class RendererVisibilityState
        {
            internal Renderer Renderer;
            internal bool Enabled;
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
        private Transform _activeAdaptedAim;
        private Transform _runtimeSlot;
        private string _originalG33HostName;
        private Transform _originalUnityG33Parent;
        private int _originalUnityG33SiblingIndex;
        private Vector3 _originalUnityG33LocalPosition;
        private Quaternion _originalUnityG33LocalRotation;
        private Vector3 _originalUnityG33LocalScale;
        private Vector3 _originalAimPositionInUnityG33;
        private Quaternion _originalAimRotationInUnityG33;
        private bool _originalRiserActive;
        private bool _originalDonorSightActive;
        private bool _originalHasCollimators;
        private DonorReticleState[] _donorReticles;
        private ScopeCameraState[] _scopeCameras;
        private RendererVisibilityState[] _fdeRiserRenderers;
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
        private bool _runtimeAimComparisonLogWritten;
        private bool _fdeRendererSuppressionLogWritten;
        private bool _adapted;
        private bool _captured;

        internal bool IsAdapted => _adapted;

        internal void Capture(
            Transform riser,
            Transform unityG33,
            ScopePrefabCache cache,
            Transform sourceAimBone,
            Transform donorSight,
            bool preserveFdeRiserHierarchy)
        {
            if (_captured)
            {
                return;
            }

            Riser = riser;
            UnityG33 = unityG33;
            G33Cache = cache;
            SourceAimBone = sourceAimBone;
            DonorSight = donorSight;
            _originalG33HostName = cache.transform.name;
            _originalUnityG33Parent = unityG33.parent;
            _originalUnityG33SiblingIndex = unityG33.GetSiblingIndex();
            _originalUnityG33LocalPosition = unityG33.localPosition;
            _originalUnityG33LocalRotation = unityG33.localRotation;
            _originalUnityG33LocalScale = unityG33.localScale;
            _originalAimPositionInUnityG33 = unityG33.InverseTransformPoint(
                sourceAimBone.position);
            _originalAimRotationInUnityG33 = Quaternion.Inverse(
                    unityG33.rotation)
                * sourceAimBone.rotation;
            _originalRiserActive = riser != null
                && riser.gameObject.activeSelf;
            _originalDonorSightActive = donorSight != null
                && donorSight.gameObject.activeSelf;
            CaptureDonorReticles(cache);
            CaptureScopeCameras(cache);
            if (preserveFdeRiserHierarchy)
            {
                CaptureFdeRiserRenderers(riser, unityG33);
            }
            _captured = true;
        }

        private void CaptureFdeRiserRenderers(
            Transform riser,
            Transform unityG33)
        {
            if (riser == null || unityG33 == null)
            {
                _fdeRiserRenderers = new RendererVisibilityState[0];
                return;
            }

            var states = new List<RendererVisibilityState>();
            foreach (var renderer in riser.GetComponentsInChildren<
                Renderer>(true))
            {
                if (renderer == null
                    || ReferenceEquals(renderer.transform, unityG33)
                    || renderer.transform.IsChildOf(unityG33))
                {
                    continue;
                }

                states.Add(new RendererVisibilityState
                {
                    Renderer = renderer,
                    Enabled = renderer.enabled
                });
            }
            _fdeRiserRenderers = states.ToArray();
        }

        internal void HideCapturedFdeRiserRenderers()
        {
            if (Riser != null)
            {
                Riser.gameObject.SetActive(true);
            }
            if (_fdeRiserRenderers == null)
            {
                return;
            }

            var hidden = 0;
            foreach (var state in _fdeRiserRenderers)
            {
                if (state == null || state.Renderer == null)
                {
                    continue;
                }
                state.Renderer.enabled = false;
                hidden++;
            }

            if (!_fdeRendererSuppressionLogWritten)
            {
                _fdeRendererSuppressionLogWritten = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Preserved the EpicAIO EXPS3/high ftc_riser transform "
                    + "hierarchy and original mod_aim_camera while hiding "
                    + hidden + " captured non-G33 renderer(s).");
            }
        }

        private void RestoreCapturedFdeRiserRenderers()
        {
            if (_fdeRiserRenderers == null)
            {
                return;
            }

            foreach (var state in _fdeRiserRenderers)
            {
                if (state != null && state.Renderer != null)
                {
                    state.Renderer.enabled = state.Enabled;
                }
            }
        }

        internal void HideDonorGeometry()
        {
            // In the EXPS3 prefab the baked sight is beneath ftc_riser and is
            // hidden with it. In the UH-1 prefab it is a sibling, so it must be
            // disabled independently.
            if (DonorSight != null)
            {
                DonorSight.gameObject.SetActive(false);
            }
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
            LogRenderedRuntimeAimComparison(camera);
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

        private void LogRenderedRuntimeAimComparison(Camera camera)
        {
            if (_runtimeAimComparisonLogWritten
                || !IsSelectedAimAtMainCamera(camera, _activeAdaptedAim)
                || !TryCaptureRuntimeAimPose(
                    out var renderedPosition,
                    out var renderedRotation))
            {
                return;
            }

            _runtimeAimComparisonLogWritten = true;
            var liveSourcePosition = SourceAimBone != null
                ? UnityG33.InverseTransformPoint(SourceAimBone.position)
                : _originalAimPositionInUnityG33;
            var liveSourceRotation = SourceAimBone != null
                ? Quaternion.Inverse(UnityG33.rotation)
                    * SourceAimBone.rotation
                : _originalAimRotationInUnityG33;
            MakeMagnifierGreatAgainPlugin.LogInfo(
                "MMGA Unity G33 rendered ADS mod_aim_camera in G33-local "
                + "space: EpicAIO authored="
                + FormatAimPose(
                    _originalAimPositionInUnityG33,
                    _originalAimRotationInUnityG33)
                + "; hidden source now="
                + FormatAimPose(liveSourcePosition, liveSourceRotation)
                + "; rendered="
                + FormatAimPose(renderedPosition, renderedRotation)
                + " (authored delta "
                + FormatAimPoseDelta(
                    _originalAimPositionInUnityG33,
                    _originalAimRotationInUnityG33,
                    renderedPosition,
                    renderedRotation)
                + ").");
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
            _activeAdaptedAim = _runtimeAim;
            return _runtimeAim;
        }

        internal Transform UseSourceAim()
        {
            if (_runtimeAim != null)
            {
                _runtimeAim.name = InactiveRuntimeAimName;
                _runtimeAim.gameObject.SetActive(false);
            }
            if (SourceAimBone == null)
            {
                _activeAdaptedAim = null;
                return null;
            }

            SourceAimBone.name = "mod_aim_camera";
            SourceAimBone.gameObject.SetActive(true);
            _activeAdaptedAim = SourceAimBone;
            return SourceAimBone;
        }

        internal static EpicHighG33RuntimeState FindForRuntimeAim(
            Transform aimBone)
        {
            if (aimBone == null)
            {
                return null;
            }

            var state = aimBone.GetComponentInParent<
                EpicHighG33RuntimeState>();
            return state != null
                && ReferenceEquals(state._activeAdaptedAim, aimBone)
                ? state
                : null;
        }

        internal static bool IsSelectedAimAtMainCamera(
            Camera camera,
            Transform aimBone)
        {
            if (camera == null || aimBone == null)
            {
                return false;
            }

            var cameraManager = CameraManager.Instance;
            var player = EFT.GamePlayerOwner.MyPlayer;
            var animation = player != null
                ? player.ProceduralWeaponAnimation
                : null;
            var selectedAim = animation != null
                ? animation.CurrentScope
                : null;
            return cameraManager != null
                && ReferenceEquals(cameraManager.Camera, camera)
                && animation.IsAiming
                && animation.FirstPersonPointOfView
                && selectedAim != null
                && ReferenceEquals(selectedAim.Bone, aimBone);
        }

        internal bool TryCaptureRuntimeAimPose(
            out Vector3 positionInUnityG33,
            out Quaternion rotationInUnityG33)
        {
            positionInUnityG33 = Vector3.zero;
            rotationInUnityG33 = Quaternion.identity;
            if (!_adapted
                || _activeAdaptedAim == null
                || UnityG33 == null)
            {
                return false;
            }

            positionInUnityG33 = UnityG33.InverseTransformPoint(
                _activeAdaptedAim.position);
            rotationInUnityG33 = Quaternion.Inverse(UnityG33.rotation)
                * _activeAdaptedAim.rotation;
            return true;
        }

        internal void LogRuntimeAimComparison(
            Vector3 positionBeforeAlignment,
            Quaternion rotationBeforeAlignment)
        {
            if (_runtimeAimComparisonLogWritten
                || !TryCaptureRuntimeAimPose(
                    out var positionAfterAlignment,
                    out var rotationAfterAlignment))
            {
                return;
            }

            _runtimeAimComparisonLogWritten = true;
            var liveSourcePosition = SourceAimBone != null
                ? UnityG33.InverseTransformPoint(SourceAimBone.position)
                : _originalAimPositionInUnityG33;
            var liveSourceRotation = SourceAimBone != null
                ? Quaternion.Inverse(UnityG33.rotation)
                    * SourceAimBone.rotation
                : _originalAimRotationInUnityG33;

            MakeMagnifierGreatAgainPlugin.LogInfo(
                "Unity G33 runtime mod_aim_camera comparison in G33-local "
                + "space: EpicAIO authored="
                + FormatAimPose(
                    _originalAimPositionInUnityG33,
                    _originalAimRotationInUnityG33)
                + "; hidden source now="
                + FormatAimPose(liveSourcePosition, liveSourceRotation)
                + "; runtime before OpticLookAtGlobal="
                + FormatAimPose(
                    positionBeforeAlignment,
                    rotationBeforeAlignment)
                + " (authored delta "
                + FormatAimPoseDelta(
                    _originalAimPositionInUnityG33,
                    _originalAimRotationInUnityG33,
                    positionBeforeAlignment,
                    rotationBeforeAlignment)
                + "); runtime after="
                + FormatAimPose(
                    positionAfterAlignment,
                    rotationAfterAlignment)
                + " (authored delta "
                + FormatAimPoseDelta(
                    _originalAimPositionInUnityG33,
                    _originalAimRotationInUnityG33,
                    positionAfterAlignment,
                    rotationAfterAlignment)
                + ", alignment shift "
                + FormatAimPoseDelta(
                    positionBeforeAlignment,
                    rotationBeforeAlignment,
                    positionAfterAlignment,
                    rotationAfterAlignment)
                + ").");
        }

        internal static string FormatAimPose(
            Vector3 position,
            Quaternion rotation)
        {
            return "pos " + FormatMillimetres(position)
                + ", rot " + FormatDegrees(rotation.eulerAngles);
        }

        internal static string FormatAimPoseDelta(
            Vector3 sourcePosition,
            Quaternion sourceRotation,
            Vector3 targetPosition,
            Quaternion targetRotation)
        {
            return FormatMillimetres(targetPosition - sourcePosition)
                + ", "
                + Quaternion.Angle(sourceRotation, targetRotation)
                    .ToString("F4")
                + " deg";
        }

        private static string FormatMillimetres(Vector3 value)
        {
            return "["
                + (value.x * 1000f).ToString("F3") + ", "
                + (value.y * 1000f).ToString("F3") + ", "
                + (value.z * 1000f).ToString("F3") + "] mm";
        }

        private static string FormatDegrees(Vector3 value)
        {
            return "["
                + ToSignedDegrees(value.x).ToString("F3") + ", "
                + ToSignedDegrees(value.y).ToString("F3") + ", "
                + ToSignedDegrees(value.z).ToString("F3") + "] deg";
        }

        private static float ToSignedDegrees(float value)
        {
            return value > 180f ? value - 360f : value;
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
            _runtimeAimComparisonLogWritten = false;
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
            RestoreCapturedFdeRiserRenderers();
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
            if (DonorSight != null)
            {
                DonorSight.gameObject.SetActive(_originalDonorSightActive);
            }
            if (_runtimeAim != null)
            {
                _runtimeAim.name = InactiveRuntimeAimName;
                _runtimeAim.gameObject.SetActive(false);
            }
            _activeAdaptedAim = null;
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
