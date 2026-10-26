# Make Magnifier Great Again

Make Magnifier Great Again lets you pair standalone G33 magnifiers and the SkeetIR thermal clip-on with your choice of compatible sights, scopes, and mounts in SPT.

**Version 0.7.0 · For SPT 4.1.6 · Experimental**

[![](https://img.shields.io/github/v/release/alanyung-yl/MakeMagnifierGreatAgain?display_name=tag&sort=semver)](https://github.com/alanyung-yl/MakeMagnifierGreatAgain/releases/latest)
[![](https://img.shields.io/github/downloads/alanyung-yl/MakeMagnifierGreatAgain/total)](https://github.com/alanyung-yl/MakeMagnifierGreatAgain/releases)

## What you can do

- Pair one of four standalone EOTech G33 magnifiers with compatible 1x sights and risers.
- Use JSOC's Trijicon SkeetIR-L as a thermal clip-on with compatible standalone sights and scopes.
- Flip the G33 between 1x and 3x and toggle thermal vision using the game's normal optic controls.
- Adjust compatible setups with WeaponCustomizer to help with placement and model clipping.

See [Modding Samples](MODDING-SAMPLES.md) for a configuration gallery with space for screenshots.


## Installation and updates

1. Install the dependencies listed above.
2. If you previously customized `SPT_Runtime\user\mods\MakeMagnifierGreatAgain\config.json`, back it up as a reference.
3. Extract the release archive directly into your SPT installation folder.
4. Replace all existing FoldThatStock files, including `config.json`.
5. Reapply any desired custom settings by replacing `config.json` with the backed up `config.json`, then start SPT normally.


## Available optics

### EOTech G33 magnifiers

- EOTECH G33 Magnifier
- EOTECH G33 Magnifier (Tan)
- EOTECH G33 Magnifier w/ Unity FAST FTC OMNI (FDE)
- EOTECH G33 Magnifier w/ Unity FAST FTC OMNI

All four accept compatible 1x sights and risers. Magnified scopes and incompatible mounts are excluded.

### Trijicon SkeetIR-L thermal clip-on

MMGA upgrades JSOC's SkeetIR with a slot for compatible 1x sights and magnified scopes. It supports inline and folded modes, with thermal vision available through compatible attached optics. Night-vision optics, other thermal optics, offset mounts, and incompatible mounts are excluded.

When SkeetIR is equipped on EpicAIO's black and FDE Unity FAST optic risers, MMGA also adjusts the attached sight's height to help align the setup.

## Controls

Use your configured EFT keybinds:

| Control | Action |
| --- | --- |
| Switch Between Sights | Switch the active sight or toggle SkeetIR thermal vision, depending on the setup |
| Change Scope Magnification | Flip the G33 between 1x and 3x, or change the active optic's mode |

The exact behavior depends on the sights attached to your weapon.

## Compatibility and known issues

- **G33 aiming alignment:** Testing suggests that point of aim (POA) and point of impact (POI) are slightly off with certain combinations when an MMGA G33 is in use. The cause is still being investigated.
- **SkeetIR with a standalone sight or scope:** Testing has shown no POA/POI issue with these setups.
- **Wilcox BOSS:** BOSS is not yet supported with MMGA G33. It can be used with SkeetIR.
- **Sight alignment:** A 1x sight needs to line up with the clip-on or magnifier for a centered view. SkeetIR has no reticle of its own, so you still aim using the attached sight.
- **Clipping and placement:** Some combinations may overlap visually. WeaponCustomizer is recommended for adjusting compatible setups.
- **Other optics:** Some sights, mounts, or unusual combinations may display an incorrect scope image or behave unexpectedly.

MMGA is experimental. If you encounter a problem, please include the sights, mounts, risers, and mod versions used, along with a screenshot of the setup.

## Optional configuration

Most players can leave the default settings unchanged. To allow an additional custom sight or riser, add its item template ID to `additionalWhitelistIds` in:

```text
SPT_Runtime/user/mods/MakeMagnifierGreatAgain/config.json
```

```json
{
  "additionalWhitelistIds": [
    "example_item_template_id"
  ]
}
```

Restart the server after editing. Adding an item to this list does not guarantee that its model or attachment layout will work correctly.

## Uninstall

Remove MMGA custom items from your weapons and inventory first, then remove both folders:

- `BepInEx/plugins/MakeMagnifierGreatAgain`
- `SPT_Runtime/user/mods/MakeMagnifierGreatAgain`

## Credits

- **EpicAIO author [EpicRangeTime](https://sp-mod.com/user/36153/epicrangetime)** — Thank you for permission to use the EOTech G33, EXPS3, UH-1, and Unity FAST assets from [Epic's All In One](https://github.com/EpicRangeTime/EpicRangeTime-Weapons).
- **JSOC author [bobinstien](https://github.com/c11gunworks-lab/JSOC)** — Thank you for permission to use and modify the SkeetIR assets. See [JSOC](https://github.com/c11gunworks-lab/JSOC) and [Spy's JSOC-SWR](https://github.com/SpyPilotEFT/Spys-JSOC-SWR) for the original projects.
- Thanks to the original authors and contributors for creating and maintaining these assets.

## License

MakeMagnifierGreatAgain is licensed under the [GNU General Public License v3.0](LICENSE).
