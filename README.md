# MakeMagnifierGreatAgain

MakeMagnifierGreatAgain is an experimental SPT 4.1.3 mod that explores separating flip-to-side magnifiers from their original combined 1x optics.

This is **v0.5.4**, the initial public proof-of-concept snapshot. It is not production-ready.

## Features

- Standalone EOTech G33 magnifier test item.
- SkeetIR-L thermal clip-on test item with separated mount base geometry.
- Automatic discovery of compatible 1x optics and rail mounts.
- Nested optic reticle propagation into the magnifier PiP view.
- Inline and flipped magnifier modes using EFT optic controls.
- Compatibility with custom optics and mounts that follow EFT's normal item structure.

## Test items

### EOTech G33 magnifier

- `EOTech G33 magnifier (MMGA test)`
- Available from Peacekeeper at loyalty level 1.
- Accepts compatible 1x optics in its front `mod_scope` slot.

### SkeetIR thermal clip-on

- `EOTech EXPS3 with Trijicon SkeetIR-L (MMGA test)`
- Available from Peacekeeper at loyalty level 1.
- Provides inline and flipped-to-the-side 1x modes.

## Installation

1. Install the required SPT 4.1.3 dependencies, including WTT-CommonLib.
2. Copy the `MakeMagnifierGreatAgain` folder into `SPT_Runtime/user/mods/`.
3. Restart the SPT server and game.
4. Purchase either test item from Peacekeeper LL1.

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
