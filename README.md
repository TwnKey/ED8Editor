# ED8Editor

Early Cold Steel 1 map-editor foundation.

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
11. decode complete material parameters and textures (in progress).

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

Inspect a texture payload with:

```powershell
dotnet run --project tests/ED8Editor.Tests -- --phyre-texture "path\to\asset.pkg" texture.dds.phyre
```
