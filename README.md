# MakeMagnifierGreatAgain

MakeMagnifierGreatAgain is an experimental SPT 4.1.3 mod that explores separating flip-to-side magnifiers from their original combined 1x optics.

This is **v0.5.4**, the initial public proof-of-concept snapshot. It is not production-ready.

## Features

- Standalone EOTech G33 magnifier test item.
- SkeetIR-L thermal clip-on test item with separated mount base geometry.
- Recursive discovery of compatible optics, risers, and scope mounts.
- Nested optic reticle propagation into the magnifier PiP view.
- Inline and flipped magnifier modes using EFT optic controls.
- Compatibility with custom optics and mounts that follow EFT's normal item structure.

## Test items

### EOTech G33 magnifier

- `EOTech G33 magnifier (MMGA test)`
- `EOTech G33 Unity FTC magnifier (MMGA EpicAIO test)` is a separate item
  cloned from EpicAIO's EXPS3 high-magnifier pair. At assembly time MMGA hides
  the baked `ftc_riser`/EXPS3 geometry, promotes the Unity G33 out of that
  parent, suppresses the pair's baked reticles, and creates a child `mod_scope`
  at the riser's base position for MMGA-compatible sights.
- Available from Peacekeeper at loyalty level 1.
- Both accept pure 1x optics and rail carriers with a recursive pure-1x route
  in their front `mod_scope` slot. A scope mount whose own optic slot accepts
  a magnified sight is excluded from the G33 whitelist; incompatible scope
  mounts reached through a separate riser are regulated with item conflicts.
  A carrier must expose a real scope slot and be registered across at least
  nine merged rail hosts; weapon-family bridge parts are excluded unless their
  ID is in `config.json`'s `trustedRiserIds`.

### SkeetIR thermal clip-on

- `EOTech EXPS3 with Trijicon SkeetIR-L (MMGA test)`
- Available from Peacekeeper at loyalty level 1.
- Provides inline and flipped-to-the-side 1x modes.
- Accepts ordinary 1x and magnified sights while excluding night/thermal
  optics, offset/angled mounts, and incompatible nested riser paths.

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
