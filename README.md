# MakeMagnifierGreatAgain (SPT 4.1.3)

MakeMagnifierGreatAgain is an experimental SPT mod that separates magnifiers and thermal clip-ons from their original combined optics, then lets them accept ordinary attachments.

Version 0.6.0 expands the standalone G33 lineup using EpicAIO's assets, improves nested reticle handling, and adds WeaponCustomizer-aware SkeetIR placement for SPT 4.1.3. This remains a proof-of-concept release rather than a production-ready compatibility layer.

[![](https://img.shields.io/github/v/release/alanyung-yl/MakeMagnifierGreatAgain?display_name=tag&sort=semver)](https://github.com/alanyung-yl/MakeMagnifierGreatAgain/releases/latest)
[![](https://img.shields.io/github/downloads/alanyung-yl/MakeMagnifierGreatAgain/total)](https://github.com/alanyung-yl/MakeMagnifierGreatAgain/releases)

## Compatibility

- Built and tested on `SPT 4.1.3`
- Requires `WTT-CommonLib 3.0.6`
- Requires `Epic's All In One 5.0.1`
- Integrates with `Tyfon.WeaponCustomizer 4.0.0`; WeaponCustomizer is not a required dependency
- Other SPT or dependency versions may work, but are not guaranteed

## Features

- Four standalone EOTech G33 magnifier variants
- Trijicon SkeetIR-L thermal clip-on test item
- Inline and flipped modes using EFT's normal optic controls: `Switch Between Sights` toggles thermal vision, `Change Scope Magnification` toggles the G33 between inline and flip positions (1-3x)
- A functional front `mod_scope` slot on each MMGA item for compatible sights, risers, and mounts
- Reticle propagation into the G33 PiP
- Thermal reticle projection
- Unity FAST riser height compensation for SkeetIR assemblies
- WeaponCustomizer-aware rebasing of compensated child-slot positions

## Included Test Items

### EOTech G33 Magnifiers

- `EOTECH G33 Magnifier`
- `EOTECH G33 Magnifier (Tan)`
- `EOTECH G33 Magnifier w/ Unity FAST FTC OMNI (FDE)`
- `EOTECH G33 Magnifier w/ Unity FAST FTC OMNI`

All four are available from Peacekeeper at loyalty level 1. They accept pure 1x optics and risers that is compatible to a 1x sight. Magnified scope mounts and incompatible mounts are excluded. The G33 variants conflict with themselves and one another, limiting an assembly to one MMGA G33.

The FDE, black, and tan variants reuse EpicAIO's EXPS3 high, UH-1 high, and EXPS3 low pair prefabs respectively. MMGA suppresses each baked donor sight and reticle, creates a live child slot, and preserves the original EpicAIO item when its prefab instance is reused. The FDE adapter keeps the active `ftc_riser` transform hierarchy and original aim bone while disabling only its captured non-G33 renderers; this retains the donor's 3x housing size without displaying the baked riser or EXPS3.

### SkeetIR Thermal Clip-On

- `EOTech EXPS3 with Trijicon SkeetIR-L (MMGA test)`

The SkeetIR test item is available from Peacekeeper at loyalty level 1. It provides inline and folded 1x modes and accepts ordinary 1x and magnified sights while excluding night/thermal optics, offset mounts, and incompatible mounts.

When SkeetIR is installed directly on EpicAIO's black or FDE Unity FAST optic riser, MMGA lowers only SkeetIR's child `mod_scope` by that riser's displacement. SkeetIR remains on the raised platform while its child optic returns to the weapon-rail plane. If WeaponCustomizer controls that slot, MMGA moves its saved and current placement to the corrected position while keeping normal dragging. Other risers are not changed.

## Controls

Use EFT's normal controls:

- `Switch Between Sights` changes the active sight or thermal selector
- `Change Scope Magnification` changes the active magnifier or optic mode

Exact behavior depends on the installed sight stack. For the supported
SkeetIR + custom G33 + Wilcox BOSS route, `Switch Between Sights` toggles
thermal vision and `Change Scope Magnification` toggles the G33 between inline
and flip positions.

## Installation and Updates

1. Install WTT-CommonLib 3.0.6 and Epic's All In One 5.0.1.
2. Extract the MMGA release archive directly into the SPT installation folder.
3. Replace existing MMGA files when updating.
4. Restart both the SPT server and game.
5. Purchase the test items from Peacekeeper LL1.

## Configuration

The server configuration is located at:

```text
SPT_Runtime/user/mods/MakeMagnifierGreatAgain/config.json
```

Add item template IDs to `additionalWhitelistIds` to explicitly include custom
rail carriers or sights in MMGA's G33-compatible discovery results:

```json
{
  "additionalWhitelistIds": [
    "example_item_template_id",
    "example_item_template_id"
  ]
}
```

Restart the server after changing the configuration. Explicit entries still
need a usable EFT-style attachment structure at runtime.

## Verify It Loaded

- Start the SPT server and confirm that MMGA reports custom-item and compatibility registration.
- Confirm that Peacekeeper LL1 sells the MMGA test items.

## Troubleshooting

- If a previously tested assembly behaves differently after changing mounts, use WeaponCustomizer's revert function before comparing placement.
- Compare magnifiers on the same weapon, rail path, sight mode, resolution, and ammunition before diagnosing reticle scale or point-of-impact differences.

## Known Limitations

- MMGA is still an experimental proof of concept.
- G33 nested reticles may retain a small positional or point-of-impact offset.
- Some magnified or unusually structured optics may produce an incorrect PiP image.
- Compatibility depends on other mods using recognizable EFT optic, cache, and attachment-slot structures.

## Development Notes

- Client builds deploy the plugin DLL to `BepInEx/plugins/MakeMagnifierGreatAgain` unless `DeployToSPT=false` is set.
- Server builds deploy the server DLL and resources to `SPT_Runtime/user/mods/MakeMagnifierGreatAgain` unless `DeployToSPT=false` is set.
- The investigation history and regression boundaries are maintained in `docs/MMGA_SKEET_IR_BUG_RECOVERY_PLAYBOOK.md`.

## Uninstall

Remove both installed locations:

- `BepInEx/plugins/MakeMagnifierGreatAgain`
- `SPT_Runtime/user/mods/MakeMagnifierGreatAgain`

Remove MMGA custom items from weapons and inventory before uninstalling to
avoid leaving missing custom templates in an existing profile.

## License

MakeMagnifierGreatAgain is licensed under the
[GNU General Public License v3.0](LICENSE).
