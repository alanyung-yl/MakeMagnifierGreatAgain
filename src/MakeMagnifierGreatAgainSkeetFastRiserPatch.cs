using System;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace MakeMagnifierGreatAgain
{
    /// <summary>
    /// Marks only EpicAIO's two Unity FAST optic risers while their prefab is
    /// assembled. It also restores a pooled Skeet prefab to its authored slot
    /// pose before EFT begins attaching Skeet's children.
    /// </summary>
    [HarmonyPatch(typeof(ObjectsFactory), nameof(ObjectsFactory.AttachMods))]
    internal static class SkeetFastRiserAssemblyPatch
    {
        internal const string SkeetIrTemplateId =
            "31d49a6fcf9e8c1864d7e410";
        internal const string UnityFastRiserBlackTemplateId =
            "2e681ada9836f6e030080191";
        internal const string UnityFastRiserFdeTemplateId =
            "e1543def58291668bac8e2c8";

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

            var prefabRoot = collectionView.GameObject.transform;
            var templateId = containerCollection.StringTemplateId;

            if (string.Equals(
                templateId,
                SkeetIrTemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                var skeetState = prefabRoot
                    .GetComponent<SkeetFastRiserSlotState>();
                if (skeetState == null)
                {
                    skeetState = prefabRoot.gameObject
                        .AddComponent<SkeetFastRiserSlotState>();
                }

                // Capture the pristine Skeet slot before AttachMods begins
                // inserting children and before WeaponCustomizer can apply a
                // saved transform to that slot. Serialized state survives any
                // subsequent Unity clone of this runtime view.
                skeetState.CaptureAuthoredPoseIfNeeded(prefabRoot);
            }

            var marker = prefabRoot.GetComponent<UnityFastRiserRuntimeMarker>();
            if (!IsUnityFastRiser(templateId))
            {
                marker?.Clear();
                return;
            }

            if (marker == null)
            {
                marker = prefabRoot.gameObject
                    .AddComponent<UnityFastRiserRuntimeMarker>();
            }

            marker.Mark(
                templateId,
                FindDescendantExact(prefabRoot, "mod_scope"));
        }

        internal static bool IsUnityFastRiser(string templateId)
        {
            return string.Equals(
                    templateId,
                    UnityFastRiserBlackTemplateId,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    templateId,
                    UnityFastRiserFdeTemplateId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static Transform FindDescendantExact(
            Transform root,
            string name)
        {
            if (root == null)
            {
                return null;
            }

            for (var index = 0; index < root.childCount; index++)
            {
                var child = root.GetChild(index);
                if (string.Equals(
                    child.name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                var nested = FindDescendantExact(child, name);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Applies the FAST riser's own attachment-vector exactly when EFT inserts
    /// Skeet into that riser's native mod_scope. SlotView.InsertItem is shared
    /// by the build screen, inventory preview, and in-hand weapon assembly.
    /// </summary>
    [HarmonyPatch(
        typeof(ContainerCollectionView.SlotView),
        nameof(ContainerCollectionView.SlotView.InsertItem))]
    internal static class SkeetFastRiserInsertionPatch
    {
        private const float MinimumRise = 0.0001f;
        private static bool _appliedLogWritten;
        private static bool _childInsertionLogWritten;
        private static bool _initialChildRebaseLogWritten;
        private static bool _doubleCompensationRepairLogWritten;
        private static bool _missingSlotWarningWritten;
        private static bool _weaponCustomizerRebaseWarningWritten;

        private struct CompensatedSlotSnapshot
        {
            internal bool IsCaptured;
            internal Transform Bone;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3 AuthoredLocalPosition;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(
            ContainerCollectionView.SlotView __instance,
            out CompensatedSlotSnapshot __state)
        {
            __state = default;
            var bone = __instance != null ? __instance.Bone : null;
            if (bone == null)
            {
                return;
            }

            var skeetState = bone
                .GetComponentInParent<SkeetFastRiserSlotState>();
            if (skeetState == null
                || !skeetState.TryGetChildInsertionOrigin(
                    bone,
                    out var localPosition,
                    out var localRotation,
                    out var authoredLocalPosition,
                    out var repairedDoubleCompensation))
            {
                return;
            }

            // At this point the empty slot is the exact pose shown in the
            // build UI. Preserve it across EFT/WeaponCustomizer's occupied-
            // slot refresh instead of deriving another compensation vector.
            __state.IsCaptured = true;
            __state.Bone = bone;
            __state.LocalPosition = localPosition;
            __state.LocalRotation = localRotation;
            __state.AuthoredLocalPosition = authoredLocalPosition;

            if (repairedDoubleCompensation
                && !_doubleCompensationRepairLogWritten)
            {
                _doubleCompensationRepairLogWritten = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Normalized a cloned Skeet-IR child slot that already "
                    + "contained a second Unity FAST compensation step.");
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("InsertModPatch")]
        private static void Postfix(
            ContainerCollectionView.SlotView __instance,
            Item item,
            GameObject itemView,
            CompensatedSlotSnapshot __state)
        {
            if (__instance == null
                || item == null
                || itemView == null)
            {
                return;
            }

            if (__state.IsCaptured && __state.Bone != null)
            {
                // WeaponCustomizer's component owns the occupied slot in
                // LateUpdate. Rebase its original/saved values onto the
                // compensated empty-slot pose instead of fighting that owner.
                // This keeps the FAST correction as the new origin while
                // allowing normal WeaponCustomizer dragging to persist.
                var customizedMod = FindWeaponCustomizerComponent(
                    __state.Bone);
                var rebased = customizedMod != null
                    && TryRebaseWeaponCustomizer(
                        customizedMod,
                        __state.Bone,
                        __state.LocalPosition,
                        __state.LocalRotation,
                        __state.AuthoredLocalPosition);

                if (!rebased)
                {
                    __state.Bone.localPosition = __state.LocalPosition;
                    __state.Bone.localRotation = __state.LocalRotation;
                }

                if (!_childInsertionLogWritten)
                {
                    _childInsertionLogWritten = true;
                    MakeMagnifierGreatAgainPlugin.LogInfo(
                        "Preserved Skeet-IR's compensated empty-slot pose "
                        + "while installing child item "
                        + item.StringTemplateId
                        + (rebased
                            ? "; rebased WeaponCustomizer onto that rail "
                                + "position so subsequent dragging remains "
                                + "available."
                            : "; no active WeaponCustomizer transform "
                                + "required rebasing."));
                }

                return;
            }

            if (!string.Equals(
                    item.StringTemplateId,
                    SkeetFastRiserAssemblyPatch.SkeetIrTemplateId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var skeetState = itemView.GetComponent<SkeetFastRiserSlotState>();
            if (skeetState == null)
            {
                skeetState = itemView.AddComponent<SkeetFastRiserSlotState>();
            }

            // Reset a pooled Skeet to its authored slot before applying this
            // insertion's direct FAST correction.
            skeetState.Restore();

            var marker = FindDirectFastRiserMarker(__instance.Bone);
            if (marker == null)
            {
                return;
            }

            if (!skeetState.TryApply(
                itemView.transform,
                marker.transform,
                __instance.Bone,
                out var compensatedRise,
                out var compensationOffset))
            {
                if (!_missingSlotWarningWritten)
                {
                    _missingSlotWarningWritten = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        "Could not compensate Skeet-IR on a Unity FAST optic "
                        + "riser because Skeet's direct mod_scope was not found.");
                }
                return;
            }

            // Skeet can arrive with a child already assembled. In that path,
            // WeaponCustomizer's child-slot postfix ran before this outer
            // Skeet insertion postfix and therefore captured the raised,
            // authored pose. Rebase that existing owner immediately after the
            // FAST correction so LateUpdate and Revert both use the corrected
            // rail-plane origin.
            var childScopeSlot = skeetState.ChildScopeSlot;
            var initialCustomizedMod = FindWeaponCustomizerComponent(
                childScopeSlot);
            if (initialCustomizedMod != null
                && TryRebaseWeaponCustomizer(
                    initialCustomizedMod,
                    childScopeSlot,
                    childScopeSlot.localPosition,
                    childScopeSlot.localRotation,
                    skeetState.AuthoredLocalPosition)
                && !_initialChildRebaseLogWritten)
            {
                _initialChildRebaseLogWritten = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Rebased WeaponCustomizer's initially occupied Skeet-IR "
                    + "child slot onto the corrected Unity FAST rail origin.");
            }

            if (!_appliedLogWritten && compensatedRise >= MinimumRise)
            {
                _appliedLogWritten = true;
                MakeMagnifierGreatAgainPlugin.LogInfo(
                    "Lowered Skeet-IR's child mod_scope by the Unity FAST "
                    + "optic riser's authored attachment offset ("
                    + (compensatedRise * 1000f).ToString("F2")
                    + " mm; prefab vector "
                    + FormatMillimetres(compensationOffset)
                    + "). This correction also applies in weapon-build "
                    + "and inventory previews.");
            }
        }

        private static Component FindWeaponCustomizerComponent(
            Transform bone)
        {
            if (bone == null)
            {
                return null;
            }

            foreach (var component in bone.GetComponents<Component>())
            {
                if (component != null
                    && string.Equals(
                        component.GetType().FullName,
                        "WeaponCustomizer.CustomizedMod",
                        StringComparison.Ordinal))
                {
                    return component;
                }
            }

            return null;
        }

        private static bool TryRebaseWeaponCustomizer(
            Component customizedMod,
            Transform bone,
            Vector3 compensatedPosition,
            Quaternion compensatedRotation,
            Vector3 authoredPosition)
        {
            try
            {
                var componentType = customizedMod.GetType();
                var customizationField = AccessTools.Field(
                    componentType,
                    "<Customization>k__BackingField");
                var customization = customizationField?.GetValue(
                    customizedMod);
                if (customization == null)
                {
                    return false;
                }

                var customizationType = customization.GetType();
                var originalPositionField = AccessTools.Field(
                    customizationType,
                    "OriginalPosition");
                var positionField = AccessTools.Field(
                    customizationType,
                    "Position");
                var originalRotationField = AccessTools.Field(
                    customizationType,
                    "OriginalRotation");
                var rotationField = AccessTools.Field(
                    customizationType,
                    "Rotation");
                if (originalPositionField == null
                    || positionField == null
                    || originalRotationField == null
                    || rotationField == null
                    || !(originalPositionField.GetValue(customization)
                        is Vector3 originalPosition)
                    || !(originalRotationField.GetValue(customization)
                        is Quaternion originalRotation))
                {
                    return false;
                }

                var savedPositionValue = positionField.GetValue(
                    customization);
                var hasSavedPosition = savedPositionValue is Vector3;
                var rebasedPosition = compensatedPosition;
                if (hasSavedPosition)
                {
                    var savedPosition = (Vector3)savedPositionValue;

                    if (IsLikelyDoubleCompensatedPosition(
                        authoredPosition,
                        compensatedPosition,
                        savedPosition))
                    {
                        // Older MMGA builds could persist the second FAST
                        // subtraction as WeaponCustomizer's saved position.
                        // Treat that exact displacement as poisoned state and
                        // clear it so Revert resolves to the corrected origin.
                        hasSavedPosition = false;
                        rebasedPosition = compensatedPosition;

                        if (!_doubleCompensationRepairLogWritten)
                        {
                            _doubleCompensationRepairLogWritten = true;
                            MakeMagnifierGreatAgainPlugin.LogInfo(
                                "Cleared a stale WeaponCustomizer position "
                                + "containing a second Unity FAST "
                                + "compensation step.");
                        }
                    }
                    else
                    {
                        // A position already closer to the compensated origin
                        // can come from a drag made while MMGA's prior hard-
                        // lock build was active. Keep it as-is; older positions
                        // based on the authored origin retain their user offset
                        // on the new rail-plane origin.
                        rebasedPosition = Vector3.Distance(
                                savedPosition,
                                compensatedPosition)
                            <= Vector3.Distance(
                                savedPosition,
                                originalPosition)
                            ? savedPosition
                            : savedPosition
                                + (compensatedPosition - originalPosition);
                    }
                }

                var savedRotationValue = rotationField.GetValue(
                    customization);
                var hasSavedRotation = savedRotationValue is Quaternion;
                var rebasedRotation = compensatedRotation;
                if (hasSavedRotation)
                {
                    var savedRotation = (Quaternion)savedRotationValue;
                    var relativeRotation = Quaternion.Inverse(
                        originalRotation) * savedRotation;
                    rebasedRotation = compensatedRotation
                        * relativeRotation;
                }

                originalPositionField.SetValue(
                    customization,
                    compensatedPosition);
                positionField.SetValue(
                    customization,
                    hasSavedPosition
                        ? (object)rebasedPosition
                        : null);
                originalRotationField.SetValue(
                    customization,
                    compensatedRotation);
                rotationField.SetValue(
                    customization,
                    hasSavedRotation
                        ? (object)rebasedRotation
                        : null);
                customizationField.SetValue(
                    customizedMod,
                    customization);

                bone.localPosition = hasSavedPosition
                    ? rebasedPosition
                    : compensatedPosition;
                bone.localRotation = hasSavedRotation
                    ? rebasedRotation
                    : compensatedRotation;
                return true;
            }
            catch (Exception exception)
            {
                if (!_weaponCustomizerRebaseWarningWritten)
                {
                    _weaponCustomizerRebaseWarningWritten = true;
                    MakeMagnifierGreatAgainPlugin.LogWarning(
                        "Could not rebase WeaponCustomizer's Skeet child "
                        + "slot data; restored the compensated pose once. "
                        + exception.GetType().Name
                        + ": "
                        + exception.Message);
                }

                return false;
            }
        }

        private static bool IsLikelyDoubleCompensatedPosition(
            Vector3 authoredPosition,
            Vector3 compensatedPosition,
            Vector3 candidatePosition)
        {
            const float tolerance = 0.001f;
            var compensationDelta = compensatedPosition - authoredPosition;
            if (compensationDelta.sqrMagnitude < 0.00000001f)
            {
                return false;
            }

            var expectedDoublePosition =
                compensatedPosition + compensationDelta;
            return Vector3.Distance(
                    candidatePosition,
                    expectedDoublePosition)
                <= tolerance;
        }

        private static string FormatMillimetres(Vector3 value)
        {
            return "["
                + (value.x * 1000f).ToString("F2") + ", "
                + (value.y * 1000f).ToString("F2") + ", "
                + (value.z * 1000f).ToString("F2") + "] mm";
        }

        private static UnityFastRiserRuntimeMarker FindDirectFastRiserMarker(
            Transform slotBone)
        {
            if (slotBone == null)
            {
                return null;
            }

            for (var cursor = slotBone; cursor != null; cursor = cursor.parent)
            {
                var marker = cursor.GetComponent<UnityFastRiserRuntimeMarker>();
                if (marker == null)
                {
                    continue;
                }

                // Exact-bone equality makes this a direct-parent rule. A Skeet
                // below another carrier nested on a FAST riser is untouched.
                return marker.IsMarked
                    && ReferenceEquals(marker.ScopeSlot, slotBone)
                    ? marker
                    : null;
            }

            return null;
        }
    }

    internal sealed class UnityFastRiserRuntimeMarker : MonoBehaviour
    {
        internal bool IsMarked { get; private set; }
        internal Transform ScopeSlot { get; private set; }
        internal string TemplateId { get; private set; }

        internal void Mark(string templateId, Transform scopeSlot)
        {
            TemplateId = templateId;
            ScopeSlot = scopeSlot;
            IsMarked = scopeSlot != null;
        }

        internal void Clear()
        {
            IsMarked = false;
            ScopeSlot = null;
            TemplateId = null;
        }
    }

    internal sealed class SkeetFastRiserSlotState : MonoBehaviour
    {
        // These fields must be serialized. EFT/Unity can clone an already
        // compensated runtime view: the transformed child slot is copied with
        // it, and non-serialized bookkeeping would reset to default. That made
        // the clone capture its shifted pose as authored and subtract the FAST
        // displacement a second time.
        [SerializeField]
        private Transform _childScopeSlot;

        [SerializeField]
        private Vector3 _authoredLocalPosition;

        [SerializeField]
        private Quaternion _authoredLocalRotation;

        [SerializeField]
        private Vector3 _compensatedLocalPosition;

        [SerializeField]
        private Quaternion _compensatedLocalRotation;

        [SerializeField]
        private bool _captured;

        [SerializeField]
        private bool _isCompensated;

        internal Transform ChildScopeSlot => _childScopeSlot;

        internal Vector3 AuthoredLocalPosition => _authoredLocalPosition;

        internal void CaptureAuthoredPoseIfNeeded(Transform skeetRoot)
        {
            var childScopeSlot = FindDirectScopeSlot(skeetRoot);
            if (childScopeSlot == null
                || (_captured
                    && ReferenceEquals(_childScopeSlot, childScopeSlot)))
            {
                return;
            }

            _childScopeSlot = childScopeSlot;
            _authoredLocalPosition = childScopeSlot.localPosition;
            _authoredLocalRotation = childScopeSlot.localRotation;
            _compensatedLocalPosition = _authoredLocalPosition;
            _compensatedLocalRotation = _authoredLocalRotation;
            _captured = true;
            _isCompensated = false;
        }

        internal void Restore()
        {
            if (_captured && _childScopeSlot != null)
            {
                _childScopeSlot.localPosition = _authoredLocalPosition;
                _childScopeSlot.localRotation = _authoredLocalRotation;
            }

            _isCompensated = false;
        }

        internal bool TryGetChildInsertionOrigin(
            Transform slot,
            out Vector3 localPosition,
            out Quaternion localRotation,
            out Vector3 authoredLocalPosition,
            out bool repairedDoubleCompensation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            authoredLocalPosition = Vector3.zero;
            repairedDoubleCompensation = false;

            if (!_captured
                || !_isCompensated
                || slot == null
                || !ReferenceEquals(_childScopeSlot, slot))
            {
                return false;
            }

            localPosition = slot.localPosition;
            localRotation = slot.localRotation;
            authoredLocalPosition = _authoredLocalPosition;

            var compensationDelta =
                _compensatedLocalPosition - _authoredLocalPosition;
            var expectedDoublePosition =
                _compensatedLocalPosition + compensationDelta;
            if (compensationDelta.sqrMagnitude >= 0.00000001f
                && Vector3.Distance(
                    localPosition,
                    expectedDoublePosition) <= 0.001f)
            {
                localPosition = _compensatedLocalPosition;
                localRotation = _compensatedLocalRotation;
                slot.localPosition = localPosition;
                slot.localRotation = localRotation;
                repairedDoubleCompensation = true;
            }

            return true;
        }

        internal bool TryApply(
            Transform skeetRoot,
            Transform riserRoot,
            Transform riserScopeSlot,
            out float compensatedRise,
            out Vector3 compensationOffset)
        {
            compensatedRise = 0f;
            compensationOffset = Vector3.zero;
            var childScopeSlot = FindDirectScopeSlot(skeetRoot);
            if (childScopeSlot == null
                || riserRoot == null
                || riserScopeSlot == null)
            {
                return false;
            }

            CaptureAuthoredPoseIfNeeded(skeetRoot);

            Restore();

            // Both points belong to the same instantiated FAST prefab. Their
            // world-space difference therefore contains the asset-authored
            // root-to-slot displacement while excluding weapon position and
            // WeaponCustomizer's placement of the complete riser. EFT later
            // rotates the assembled weapon for the build/inspection view, so
            // projecting this onto global Vector3.up writes the correction to
            // the wrong imported-prefab axis. Subtract the complete invariant
            // attachment vector instead; on the Unity FAST asset this is the
            // exact displacement which placed Skeet on the raised optic rail.
            var riserAttachmentOffset =
                riserScopeSlot.position - riserRoot.position;
            compensationOffset = riserAttachmentOffset;
            compensatedRise = riserAttachmentOffset.magnitude;
            if (compensatedRise < 0.0001f)
            {
                CaptureCompensatedPose();
                return true;
            }

            _childScopeSlot.position -= riserAttachmentOffset;
            CaptureCompensatedPose();
            return true;
        }

        private void CaptureCompensatedPose()
        {
            if (_childScopeSlot == null)
            {
                return;
            }

            _compensatedLocalPosition = _childScopeSlot.localPosition;
            _compensatedLocalRotation = _childScopeSlot.localRotation;
            _isCompensated = true;
        }


        private static Transform FindDirectScopeSlot(Transform skeetRoot)
        {
            if (skeetRoot == null)
            {
                return null;
            }

            for (var index = 0; index < skeetRoot.childCount; index++)
            {
                var child = skeetRoot.GetChild(index);
                if (string.Equals(
                    child.name,
                    "mod_scope",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }
    }
}
