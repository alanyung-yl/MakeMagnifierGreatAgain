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
- Tested with `WTT-Armory`, `Epic's All In One`, and `JSOC`
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

## Included Items

### EOTech G33 Magnifiers

- `EOTECH G33 Magnifier`
- `EOTECH G33 Magnifier (Tan)`
- `EOTECH G33 Magnifier w/ Unity FAST FTC OMNI (FDE)`
- `EOTECH G33 Magnifier w/ Unity FAST FTC OMNI`

All four are available from Peacekeeper at loyalty level 1. They accept pure 1x optics and risers that is compatible to a 1x sight. Magnified scope mounts and incompatible mounts are excluded. The G33 variants conflict with themselves and one another, limiting an assembly to one MMGA G33.

The FDE, black, and tan variants reuse EpicAIO's EXPS3 high, UH-1 high, and EXPS3 low pair prefabs respectively. MMGA suppresses each baked donor sight and reticle, creates a live child slot, and preserves the original EpicAIO item when its prefab instance is reused. The FDE adapter keeps the active `ftc_riser` transform hierarchy and original aim bone while disabling only its captured non-G33 renderers; this retains the donor's 3x housing size without displaying the baked riser or EXPS3.

### SkeetIR Thermal Clip-On

- `EOTech EXPS3 with Trijicon SkeetIR-L (MMGA test)`

The SkeetIR test item is available from Peacekeeper at loyalty level 1. It provides inline and folded 1x modes and accepts ordinary 1x and magnified sights while excluding night/thermal optics, offset mounts, and incompatible mounts. For magnified child sights, MMGA applies the thermal shader to the child `mod_scope` sight's PiP and disables SkeetIR's physical render while the player is aiming down sights; if this behavior is compatible with FIKA, other players in the session should not see the physical SkeetIR while it is being aimed through.

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
risers or sights in MMGA's G33-compatible discovery results:

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

## Known Limitations

- MMGA is still an experimental proof of concept.
- A 1x sight's collimator must be aligned with the SkeetIR & G33 centerline for its reticle to appear centered in view. SkeetIR does not have its own reticle, and MMGA does not project the 1x reticle into the SkeetIR PiP; the player is still aiming through the 1x sight while SkeetIR adds the thermal view.
- G33 nested reticles may retain a small positional or point-of-impact offset.
- Point of aim and point of impact are not perfectly consistent in every assembly. Even the real-world EXPS3 + Unity FTC riser + Unity FAST FTC OMNI G33 riser arrangement may show a slight offset, and the exact cause is not yet confirmed.
- Some optics or unusually structured assemblies may produce an incorrect PiP image or behave unexpectedly.
- Because MMGA uses fixed `mod_scope` positions, models may clip into one another. WeaponCustomizer is highly recommended for adjusting compatible assemblies.
- Mod compatibility is not fully tested. Reports of incompatible sights, mounts, risers, or assemblies are greatly appreciated.
- Compatibility depends on other mods using recognizable EFT optic, cache, and attachment-slot structures.

## Uninstall

Remove both installed locations:

- `BepInEx/plugins/MakeMagnifierGreatAgain`
- `SPT_Runtime/user/mods/MakeMagnifierGreatAgain`

Remove MMGA custom items from weapons and inventory before uninstalling to avoid leaving missing custom templates in an existing profile.

## Credits

- SkeetIR model and JSOC integration by [bobinstien](https://github.com/c11gunworks-lab/JSOC). MMGA includes a modified SkeetIR bundle with the author's permission. The original JSOC projects are available at [JSOC](https://github.com/c11gunworks-lab/JSOC) and [Spy's JSOC-SWR](https://github.com/SpyPilotEFT/Spys-JSOC-SWR).
- EOTech G33, EXPS3, UH-1, and Unity FAST assets provided by [EpicRangeTime's Epic's All In One](https://github.com/EpicRangeTime/EpicRangeTime-Weapons).
- Thanks to the original authors and contributors for creating and maintaining these assets.

## License

MakeMagnifierGreatAgain is licensed under the
[GNU General Public License v3.0](LICENSE).
