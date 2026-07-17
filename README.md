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
paths. Model normals feed an explicit neutral viewport light, while Phyre
`AlphaThreshold` parameters drive per-material alpha testing for foliage and
other cutout geometry. This editor light is intentionally independent from the
game's OPS environment lights. Drag with the left mouse button to look freely in the direction of the
mouse; releasing it without crossing the normal drag threshold performs a
selection click instead. Right-drag is retained as an alternate look binding,
middle-drag pans, and the wheel moves forward/backward along the exact view
direction without a pivot-distance stop. Press `F`, or double-click an object in
the scene outliner, to place the camera in front of it and look at its center.
The cursor is hidden and recentered only after a look drag crosses the selection
threshold, allowing unlimited horizontal rotation and pitch up to the vertical
without ever introducing camera roll; it returns to its original screen
position on release. Releasing a navigation button or leaving the window always
ends its drag mode. The navigation selector switches persistently between
`ZQSD` (AZERTY) and `WASD` (QWERTY). Movement follows the complete view direction;
`E`/Space moves up, `C` moves down, and Shift
accelerates movement. Left-click a visible model to select its nearest intersected triangle; the selected instance
is highlighted and identified in the window title. OPS volumes and markers are
selectable through the same nearest-hit query and turn white when selected.
Press `1`, `2`, or `3` to display translation axes, rotation rings, or scale axes
for the current selection. Drag a handle with the left mouse button to transform
the element; `Ctrl+Z` and `Ctrl+Y` undo and redo committed drags. Capabilities
are explicit: props and volumes support all three modes, while point-like OPS
elements currently expose translation only.

The optional snap control rounds translation to 0.25 world units, rotation to
15-degree increments, and scale to 0.1 increments. Prop names are made unique
inside the document automatically, without asking for additional input.

`Ctrl+S` saves the current editable scene through `OpsWriter`. The first save
opens a Save As dialog defaulting to `<map>.edited.ops`; subsequent saves reuse
that explicit destination, while `Ctrl+Shift+S` chooses another. The writer
updates only changed spatial attributes, retains unknown XML elements and
attributes, reparses the temporary output, and atomically replaces the selected
destination only after validation.
Closing a document whose current undo state differs from the last successful
save offers to save, discard, or cancel the close operation.

The asset library panel indexes PKG filenames and variants without opening their
archives. Search for an asset and choose **Add selected asset**; only then is that
single package decoded and uploaded, at the current camera pivot. New props use
the centralized neutral OPS `NewObject` profile. `Ctrl+D` duplicates the selected
scene element and Delete removes it. For non-prop OPS elements, duplication
copies the complete source attribute profile, including attributes not yet
understood by the editor; no default flags are invented. Addition, duplication,
deletion, and transforms share the same undo/redo history, and `OpsWriter`
persists every supported spatial collection.

Adding an asset enters surface-placement mode instead of modifying the document
immediately. A green preview follows the nearest exact triangle hit under the
cursor; left-click confirms the new prop and `Esc` cancels without creating an
undo entry or changing the OPS snapshot.

Selecting any OPS element populates a generic attribute grid. Transform and
identity fields are read-only there because they are controlled by the gizmos
or asset loader. Flags, TP destinations, point radii, camera parameters, sound
settings, light colors/ranges, and previously unknown attributes can be edited,
added, or removed. Known numeric and vector fields are validated before they
enter the document; attribute changes are undoable and preserved by `OpsWriter`.

OPS spatial data is visible directly in the 3D viewport: `EntryBox` volumes are
cyan, transitions with an explicit destination are blue, `GroupBox` volumes are
violet, `LookPoint` markers are yellow, map cameras plus their sight lines are
green, sounds and their ranges are orange, and lights use their OPS color and
outer range. These overlays are generated directly in memory from exact OPS
coordinates.
The scene outliner on the left groups every currently editable OPS element by
kind, and each child repeats its readable category. Selecting an entry selects
the same object in the viewport and exposes its transform gizmo. The generic OPS
attribute panel sits directly below the outliner so selection and properties stay
together.

The OPS reader also exposes the exact `default` map environment fog color. It is
used as the viewport background when a map has no explicit sky model. A sky such
as `O_S00SKY00` remains an ordinary in-memory OPS model; the debug map `m0010`
contains no such sky asset.

The **Create OPS element** panel provides evidence-backed creation profiles for
a type-2 TP/`EntryBox`, `GroupBox`, type-0 event `LookPoint`, type-3 map camera,
point sound, and point light. Required external values are limited to the TP destination map and
entry, or the sound name. Creation then enters the same exact surface-placement
mode as props. Each profile records the shipped OPS element from which its
attribute set was derived; the generic attribute panel remains available after
placement. Missing XML sections are inserted in the canonical OPS section order
when the edited map did not originally contain that element family.

OPS asset transforms and raw Phyre vertex data share the same Y-up world basis.
The editor reflects the source X coordinate and corresponding Y Euler rotation
for handedness, but does not add an export-oriented 90-degree rotation;
zero-rotation trees, maps, and lamp posts therefore remain upright.

For a selected map camera, the cyan eye handle controls the complete camera
translation and keeps its sight vector intact. Clicking the magenta look-at
handle activates a separate translation gizmo for the target alone. Both edits
participate in the same preview, snapping, undo/redo, and OPS save history.

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

## Test distribution

Create a self-contained Windows x64 test build with:

```powershell
dotnet publish src/ED8Editor.Viewer/ED8Editor.Viewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist/ED8Editor-win-x64-single
```

The resulting `ED8Editor.Viewer.exe` includes the .NET runtime and the managed
project dependencies, so no adjacent DLL and no separate .NET installation is
required. The tester still needs a Windows x64 machine with Direct3D 11 support
and a local Trails of Cold Steel installation; the viewer asks for that game
directory on first launch.
