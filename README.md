# MakeMagnifierGreatAgain

MakeMagnifierGreatAgain is an experimental SPT 4.1.3 mod that explores separating flip-to-side magnifiers from their original combined 1x optics.

This is **v0.5.4**, the initial public proof-of-concept snapshot. It is not production-ready.

## Features

- Standalone EOTech G33 magnifier test item.
- SkeetIR-L thermal clip-on test item with separated mount base geometry.
- Automatic Skeet child-slot height compensation on EpicAIO's black/FDE Unity
  FAST optic risers.
- Recursive discovery of compatible optics, risers, and scope mounts.
- Nested optic reticle propagation into the magnifier PiP view.
- Inline and flipped magnifier modes using EFT optic controls.
- Compatibility with custom optics and mounts that follow EFT's normal item structure.

## Test items

### EOTECH G33 magnifiers

- `EOTECH G33 Magnifier`
- `EOTECH G33 Magnifier w/ Unity FAST FTC OMNI (FDE)`
- `EOTECH G33 Magnifier w/ Unity FAST FTC OMNI`
- `EOTECH G33 Magnifier (Tan)`
- The FDE and black Unity variants are cloned from EpicAIO's EXPS3 and UH-1
  high-magnifier pairs. The tan variant is cloned from the EXPS3 low-magnifier
  pair. At assembly time MMGA hides the baked collimator and any paired riser,
  preserves the G33, suppresses the donor reticles, and creates a functional
  child `mod_scope` at the original sight/riser anchor.
- Available from Peacekeeper at loyalty level 1.
- All four accept pure 1x optics and rail carriers with a recursive pure-1x route
  in their front `mod_scope` slot. A scope mount whose own optic slot accepts
  a magnified sight is excluded from the G33 whitelist; incompatible scope
  mounts reached through a separate riser are regulated with item conflicts.
  A carrier must expose a real scope slot and be registered across at least
  nine merged rail hosts; weapon-family bridge parts are otherwise excluded.
  The LT101 and EpicAIO's black/FDE Unity FAST optic risers are permanent
  built-in trusted risers. Add item template IDs to `config.json`'s
  `additionalWhitelistIds` array to explicitly add user-selected items to the
  G33-compatible whitelist and the Unity FAST riser filters derived from it.
- The custom-bundle, FDE Unity, black Unity, and tan G33 variants conflict with
  themselves and each other, limiting each weapon assembly to one MMGA G33.

### SkeetIR thermal clip-on

- `EOTech EXPS3 with Trijicon SkeetIR-L (MMGA test)`
- Available from Peacekeeper at loyalty level 1.
- Provides inline and flipped-to-the-side 1x modes.
- Accepts ordinary 1x and magnified sights while excluding night/thermal
  optics, offset/angled mounts, and incompatible nested riser paths.
- EpicAIO's black/FDE Unity FAST optic risers explicitly accept Skeet. When
  Skeet is installed directly on either one, MMGA lowers only Skeet's child
  `mod_scope` by that riser's prefab-local root-to-slot displacement so the
  child optic returns to the original rail plane. When WeaponCustomizer owns
  the occupied slot, MMGA rebases its original and saved transforms onto that
  corrected rail plane; the correction therefore survives insertion while
  normal slot dragging remains available. Other risers are intentionally left
  unchanged. Skeet conflicts with itself to prevent nested duplicate Skeets.

## Installation

1. Install the required SPT 4.1.3 dependencies: WTT-CommonLib 3.0.6 and
   Epic's All In One 5.0.1.
2. Copy the `MakeMagnifierGreatAgain` folder into `SPT_Runtime/user/mods/`.
3. Restart the SPT server and game.
4. Purchase the test items from Peacekeeper LL1.

## Known limitations

- This is a proof of concept, not a production release.
- G33 nested reticles may have a small positional offset.
- Some magnified or unusual optics may produce an incorrect PiP image.
- SkeetIR visual alignment and thermal presentation are still being tested.
- Compatibility depends on other mods using standard EFT optic and slot data.

## Controls

Use EFT's normal optic controls for switching and changing magnification.

## Version

`v0.5.4` — initial public proof-of-concept snapshot; not production-ready.
