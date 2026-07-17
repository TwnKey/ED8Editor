# ED8Editor

Early Cold Steel 1 map-editor foundation.

On the first interactive launch, the viewer asks for the Trails of Cold Steel
installation directory and validates its `data/scripts`, `data/ops`, and
`data/asset` folders. The normalized installation path is stored per user in
`%LocalAppData%\ED8Editor\settings.json`; it is requested again only if the
saved installation is no longer valid. The next dialog selects the script to
open from the configured game data.

The first supported workflow deliberately starts from a game script:

1. open a `.dat` script;
2. validate and read its minimal header;
3. classify the script from its game directory;
4. for scenario scripts, resolve the matching `data/ops/<identifier>.ops` file.
5. parse the OPS map objects into a read-only common `MapScene`.
6. resolve every OPS asset ID to its base or English `.pkg` package.
7. inventory PKG contents and extract raw or NISLZSS-compressed entries on demand.
8. parse `asset_D3D11.xml` and select the asset block matching the OPS asset ID.
9. parse the embedded Phyre class layouts and decompress object/array/pointer fixups;
10. decode D3D11 index and vertex data directly into CPU-side meshes;
11. decode material parameters and textures into file-free CPU resources;
12. build transformed scene instances for rendering, bounds and exact triangle picking.

The full script decompiler is intentionally outside `ED8Editor.ScriptHeaders`. This
small bootstrap module can be replaced later without coupling the editor to the
decompiler implementation.

OPS source attributes and the complete original document bytes are retained so a
future writer can patch known values without silently discarding unknown data.

## Current command-line probe

```powershell
dotnet run --project src/ED8Editor.Cli -- "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\scripts\scena\dat\a0000.dat"
```

Run the dependency-free test executable with:

```powershell
dotnet run --project tests/ED8Editor.Tests
```

Validate the OPS reader against a complete game installation with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --ops-corpus "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\ops"
```

Validate package resolution for every asset referenced by OPS files with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --asset-corpus "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data"
```

Validate and decompress every entry of one package without writing extracted files:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --pkg "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11\M_A0000.pkg"
```

Print one textual package entry for diagnostics with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --pkg-entry "path\to\asset.pkg" asset_D3D11.xml
```

Validate manifests for all OPS-referenced assets with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --manifest-corpus "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data"
```

Inspect a Phyre asset entirely in memory, including its class layouts and fixup
graph, with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --phyre-metadata "path\to\asset.pkg" model.dae.phyre
```

`ED8Editor.Phyre` does not generate GLB or another intermediate model file. Its
cluster view retains the decompressed PKG entry in memory and exposes bounded
slices for serialized objects, array storage, and the trailing D3D11 VRAM data.
`PhyreD3D11ModelReader` follows the fixup graph and returns indexed primitives,
raw vertex buffers, strides, attribute offsets/formats, material indices, and
primitive topology through the `IPhyreModelReader` interface.

Textures follow the same file-free path. `PhyreD3D11TextureReader` reads the
dimensions, mip count and native D3D11 format from `PTexture2D`, then keeps the
exact GPU payload in `CpuTexture.Data`. It supports the D3D11 formats declared
by this Phyre version, including DXT1/3/5 and BC4-7. The application loader
attaches every texture from the primary asset manifest directly to its
`CpuModel`; no DDS or GLB is written to disk.

Materials are decoded from their variable-sized `PParameterBuffer` storage.
The reader preserves numeric shader constants, resolves every serialized
`PAssetReferenceImport` by parameter name, and remaps each mesh's local
material table to the global `CpuMaterial` list. Once textures are loaded, the
application produces parameter-to-texture indices (for example
`DiffuseMapSampler` and `SpecularMapSampler`) and selects the diffuse binding as
the material's base-color texture.

Inspect a texture payload with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --phyre-texture "path\to\asset.pkg" texture.dds.phyre
```

Inspect raw material definitions and imported asset references with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --phyre-material "path\to\asset.pkg" model.dae.phyre
```

## Direct3D 11 renderer

`ED8Editor.Rendering` is an injectable GPU backend built on Vortice.Direct3D11.
It creates immutable vertex and index buffers, native compressed texture
resources with every mip level, shader-resource views, and material bindings
directly from `CpuModel`. `D3D11SceneRenderer` retains a position-only offscreen
pass used to validate every primitive and topology independently of a window.

`ED8Editor.Viewer` provides that interactive viewport: a flip-model swap chain,
depth buffer, OPS model instances, perspective camera, and textured/solid shader
paths. Hold the right mouse button to orbit, drag with the middle mouse button to
pan, use the wheel to zoom, and press `F` to frame the selected prop. `WASD` moves
laterally, `Q/E` moves vertically, and Shift accelerates movement. Left-click a
visible model to select its nearest intersected triangle; the selected instance
is highlighted and identified in the window title. OPS volumes and markers are
selectable through the same nearest-hit query and turn white when selected.
Press `1`, `2`, or `3` to display translation axes, rotation rings, or scale axes
for the current selection. Drag a handle with the left mouse button to transform
the element; `Ctrl+Z` and `Ctrl+Y` undo and redo committed drags. Capabilities
are explicit: props and volumes support all three modes, while point-like OPS
elements currently expose translation only.

`Ctrl+S` saves the current editable scene through `OpsWriter`. The first save
opens a Save As dialog defaulting to `<map>.edited.ops`; subsequent saves reuse
that explicit destination, while `Ctrl+Shift+S` chooses another. The writer
updates only changed spatial attributes, retains unknown XML elements and
attributes, reparses the temporary output, and atomically replaces the selected
destination only after validation.

The asset library panel indexes PKG filenames and variants without opening their
archives. Search for an asset and choose **Add selected asset**; only then is that
single package decoded and uploaded, at the current camera pivot. New props use
the centralized neutral OPS `NewObject` profile. `Ctrl+D` duplicates the selected prop and
Delete removes it. Addition, duplication, deletion, and transforms share the
same undo/redo history, and `OpsWriter` persists the resulting `AssetObject`
collection.

Selecting a prop also populates a generic OPS attribute grid. Transform and
asset identity fields are read-only there because they are controlled by the
gizmos and asset loader; flags, clipping, material values, and previously
unknown attributes can be edited, added, or removed. Attribute changes are
undoable and are preserved by `OpsWriter`.

OPS spatial data is visible directly in the 3D viewport: `EntryBox` volumes are
cyan, transitions with an explicit destination are blue, `GroupBox` volumes are
violet, `LookPoint` markers are yellow, map cameras plus their sight lines are
green, sounds and their ranges are orange, and lights use their OPS color and
outer range. These overlays are generated directly in memory from exact OPS
coordinates.

```powershell
dotnet run --project src/ED8Editor.Viewer -- "path\to\scripts\scena\dat\a0000.dat"
```

`ED8Editor.Scene` owns scene-instance construction, transformed bounds, viewport
ray creation, vertex-position decoding and exact indexed-triangle intersection.
Unsupported formats and malformed geometry are returned as structured issues
instead of being approximated by a bounding-box selection.

Validate scene geometry without creating a window or writing intermediate files:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --scene-scan "path\to\scripts\scena\dat\a0000.dat"
```

Run the complete script-to-GPU validation with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --gpu-upload "path\to\scripts\scena\dat\a0000.dat"
```
