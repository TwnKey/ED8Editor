# ED8-eff-editor — Trails of Cold Steel effect (`.eff`) editor
<img width="952" height="490" alt="eff_showcase" src="https://github.com/user-attachments/assets/1d46011b-d984-4350-b186-9e1d22fac271" />

A parser, visualizer, and editor for the `.eff` particle/effect files used by
*The Legend of Heroes: Trails of Cold Steel* (Sen no Kiseki), plus a texture
importer that packages PC images into game-ready PhyreEngine `.pkg` textures.

The goal is to make it **much easier to create and edit effects*.

> ⚠️ **Read the [Limitations](#limitations) before using it on anything you care about.**
> This is a reverse-engineering tool: keep backups of the game files you touch.

---

## Features

**Editor (`eff-gui`)**
- **Animated 3D preview** of the whole effect (egui + OpenGL): quads, procedural
  meshes (cylinder / half-cylinder / sphere / dome / cross), trails, keyframe
  animation, base orientation, parent inheritance, orbit camera, mouse-wheel zoom,
  auto-fit, and background presets.
- **Segment hierarchy tree** with visibility toggles and **drag-and-drop reparenting**;
  create / delete nodes; create a new `.eff` from scratch.
- **Readable parameter editing** — the raw `d02…d20` fields are surfaced with English
  names and flag selectors (shape, blend mode, orientation/billboard, parent
  inheritance, draw order, sound id, physics/gravity/bounce, base rotation…), with a
  raw hex fallback for the bits that are still unknown.
- **Keyframe editing** for every animation track (position, rotation, scale, colour
  multiply/glow, spawns) with a mode selector (additive / uniform / random / loop) and
  random bounds.
- **Interactive crop editor** — drag a rectangle on the texture thumbnail to set the
  UV crop.
- **Texture replacement** — import a PNG/JPG/DDS and it is packaged **from scratch**
  into a standalone `.dds.phyre` + `.pkg` (with the required `asset_D3D11.xml`
  manifest, NISLZSS-compressed like the game's own), written into the game's
  `data/asset/D3D11` on save. Optional **"Fit quad to texture aspect"** adjusts the
  segment's Scale so a rectangular image isn't squished into a square quad. Existing
  game `.pkg` files are never overwritten.
- Byte-perfect **round-trip**: re-saving an unmodified file reproduces it exactly.

**Command line (`eff-cli`)**
- Dump / analyze / compare `.eff` files as JSON, batch round-trip validation, and
  texture decode diagnostics.

## Building

Requires a recent [Rust](https://rustup.rs) toolchain.

```sh
cd eff_editor_rs
cargo build --release
# binaries in target/release/: eff-gui (editor), eff-cli (tools)
```

Drop a `.eff` onto the editor window, or use **File → Open**. Texture import and
save target the game's install path by default (configurable in the editor).

## Limitations

This is an unofficial, reverse-engineered tool. In particular:

- **Only tested on the first Trails of Cold Steel (CS1 / Sen no Kiseki) on PC.**
  It has **not** been tested on CS2, CS3, CS4, or Reverie, whose formats may differ.
- **Not every effect or shape has been tested.** Some fields are still labelled
  "Unknown", and rarer shapes (e.g. some meshes) or effect features may be handled
  incompletely.
- **The preview is an approximation, not a frame-accurate emulator.** It reproduces a
  lot of the in-game behaviour (keyframe evaluation, spawns, orientation, blending,
  procedural meshes) but some details differ — there is no real particle-physics
  simulation, and exact blend/mip/rendering nuances won't always match. **Always
  verify changes in-game.**
- **Texture replacement relies on reverse-engineered conventions** (asset naming, the
  `asset_D3D11.xml` manifest, NISLZSS compression, the effect's file-level texture
  list). It works in the cases tested, but treat new textures as experimental and
  keep backups.

If something looks wrong in the preview, it may still be correct in-game (and vice
versa).

## Credits

This tool builds on the prior reverse-engineering work of the community. Huge thanks to:

- **[ed8_eff_tools](https://github.com/uyjulian/ed8_eff_tools)** by *uyjulian* — the
  reference for the ED8 `.eff` format.
- **[ed8pkg2gltf](https://github.com/eArmada8/ed8pkg2gltf)** by *eArmada8* — PhyreEngine
  `.pkg` / `.dds.phyre` texture handling.
- **[Sen-no-Kiseki-PKG-Sharp](https://github.com/Sewer56/Sen-no-Kiseki-PKG-Sharp)** by
  *Sewer56* — the `.pkg` structure and NISLZSS (type-1) compression
  algorithm, ported here to produce game-compatible compressed packages.

*Trails of Cold Steel* and PhyreEngine are the property of their respective owners;
this project is a fan-made, non-commercial modding tool and is not affiliated with or
endorsed by Nihon Falcom or Sony.
