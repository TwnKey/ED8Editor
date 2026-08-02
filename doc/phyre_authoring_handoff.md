# Writing a Phyre model cluster the game will load — handoff

## The goal

ED8Editor authors a `.dae.phyre` model cluster **from nothing** (no shipped file used
as a template) so that a user can import an FBX/glTF and have it appear in *Trails of
Cold Steel 1*. The geometry, the graph and the fields are written by us. Two things
are deliberately **not** authored and are looked up from the game instead, because
they cannot be invented:

* compiled shaders (`ed8.fx#<HASH>.phyre`) — GPU bytecode plus an engine ABI;
* the shader's **parameter block** (`PParameterBuffer` payload, sampler states,
  parameter definitions) — byte-identical wherever a given shader is used, so it is a
  table keyed by shader, not a borrowed model.

## Where it stands

**Works, verified in game:**

* `PkgArchiveWriter` — our `.pkg` container. Proven by `MapImport repack <asset>`,
  which rewrites a shipped package with our writer and unchanged entries. It loads.
  Uncompressed entries (flag 0) are accepted; the constant magic is not validated.
* **Grafting** — `MapImport graft-prop <asset> <model>`: keep the shipped cluster,
  rewrite only its geometry buffers via `PhyreModelReplacement.Replace`. Loads and
  renders. This is a usable feature today.

**Does not work:** a cluster written from nothing. The game crashes on load when such
a cluster is placed in a prop package. Nine in-game attempts, still crashing.

## The method — use it, do not go back to guessing

Nine fixes were made by inspection before this discipline existed; none of them was
sufficient and several were only found much later to be wrong for the same reason.
Two offline oracles now exist. **Exhaust them before asking for a game launch.**

### 1. Fidelity — `PhyreAuthoringProbe x --fidelity <package.pkg>`

Decomposes a **shipped** cluster into `PhyreClusterContents` and reassembles it with
our writer, then demands the game's bytes back. Prints the first differing offset,
how many bytes differ, both namespaces' counters, and any class the shipped file
lists that our closure drops.

Currently **IDENTICAL** on `O_T10CHR01`, `O_T10ETC02`, `M_A0005`, `M_A0003` — every
model without animation. Models *with* animation still differ: they carry a larger
class table than the canonical 125. That is unfinished but out of scope for the
shapes we author.

> Note: the older `--assemble-check` proves only **self-consistency** — it reads our
> bytes with our readers. Its "32 761 clusters" figure never meant engine
> compatibility. Do not cite it as evidence.

### 2. Wiring — `PhyreAuthoringProbe x --diff-wiring <shipped.phyre> <ours.phyre>`

Two models of the same kind cannot be compared byte for byte (their geometry
differs), but they must be *wired* the same. For each class it prints which members
carry a link in one and not the other.

Currently the only remaining difference is legitimate: `PNode` — the bench has eight
nodes chained by `m_parent`/`m_firstChild`/`m_next`, our cube has one, so there is
nothing to chain.

### 3. Bisection with a proven path

When both oracles are clean and it still crashes, bisect between something that
loads (the graft) and something that does not (authored). The graft reuses every
structural section of the shipped file and rewrites only the payload, so anything it
preserves and authoring rewrites is a suspect. That is how `PlatformId` was found.

## Reproducer

A cube, so nothing depends on model size:

```
MapImport <game> <project.ed8mod> replace-prop O_T10LIG03 <scratch>/cube.obj
```

`O_T10LIG03` is a Trista lamppost with **22 instances** — impossible to miss. Its
package supplies the shader, the parameter block and the textures, so only the
geometry and the cluster we write are new. Everything goes through the mod project;
`MapImport <game> <project> revert` restores the game folder.

## Defects found and fixed (all still needed)

| What | Why it mattered |
|---|---|
| `PlatformId` was `6` | Must be `0x44583131` = `"DX11"`. The engine reads the platform before touching anything GPU-bound. Our texture writer already had it right. |
| Class table computed per file | **The biggest one.** Four unrelated clusters carry the *same 125 classes in the same order*. The schema table is fixed; a class is identified by its position. Now `PhyreSchemaLibrary.CanonicalClasses` / `CanonicalTypes`. |
| Eleven classes nothing instantiates | `PClusterHeader`, `PClusterHeaderBase`, `PClusterHeaderD3D11`, `PInstanceListHeader`, `PShaderParameterCaptureBuffer{Sampler,Texture2D,TextureBase}`, `PTexture2D{,Base,D3D11}`, `PTextureCommonBase`. Two of them are how the engine reads the file itself. Worth exactly the 2 222 bytes our namespace was short. |
| Namespace word 1 | It is the namespace's own size, equal to cluster header word 2. We wrote a constant. |
| Instance groups unsorted | Every shipped cluster lists them alphabetically by class. Appending physics and material classes had broken it. |
| `PAssetReference.m_assetType` absent | A user fixup of type `PClassDescriptor` holding the target's class name (`"PNode"`, `"PMesh"`…). Without it a name points at an object of no stated kind. Same for `PAssetReferenceImport.m_targetAssetType` (`"PEffectVariant"` / `"PTexture2D"`). |
| Stream semantics typed `bool` | Must be `PRenderDataType`. The type id had been hardcoded. |
| `PDataBlockD3D11.m_buffers` = 0 | Must be 1. Zero declares data living in no buffer. |
| Parameter definitions carried without their names | They hold names in the group's array data; copying the objects alone left 165 dangling. |
| Sampler/definition objects passed as `Trailing` | `Trailing` goes *after* the class size — that is for header classes. These are ordinary objects; it doubled them and the engine walked off the end. |
| Object ids flattened across groups | An object id is local to its group. |
| Prop asset ids under the wrong folder | Objects are named after the cluster's path: `map/objects/<name>/…` for a prop. |
| Buffer's texture import dropped | The reader filtered out user-fixup pointers, leaving the block referring to a texture never brought along. Now carried by name, with the donor's textures shipped. |
| `m_gameMaterialIDs` never written | Needed by CS1. One id per segment, value 0. |

## The trap to expect

**Twelve of the defects above are the same mistake**: a value that is valid in one
numbering space reused in another without translation — type ids, member ids, object
ids local to a group, class ids, import indices, array offsets without their bytes,
folder names. When carrying anything out of a shipped file, carry it **by name** and
re-resolve it against the destination.

`PMeshInstance` deserves special suspicion. Its descriptor declares the class as 64
bytes and then places `m_gameMaterialIDs` at +96. Shipped objects are 64 bytes with
the count at **+48**. The engine uses its own compiled layout and reads the file's
only to resolve names. (Confirmed by the project owner's earlier reverse engineering:
*"phyre explorer misreads some of the classes… PMeshInstance in particular is
problematic."*)

## What to try next

1. **Compare object bytes group by group** between our cube and `O_T10CHR01`, for the
   classes both have, ignoring fields that legitimately differ (counts, sizes,
   bounds). The wiring is aligned, so a wrong *value* is now the likeliest fault.
2. **The node graph.** Shipped clusters give `PNode` a name in the group's array data
   and set `m_worldMatrix`; ours has one unnamed node. `#VisualSceneNode` is what the
   engine instantiates — check what it needs to find beneath it.
3. **`PMeshInstanceSegmentContext` and `PMeshSegment`** field values against a shipped
   single-segment model.
4. The project owner's notes also mention, for CS1: do not exclude
   `PhyreContextSwitches` / `PhyreMaterialSwitches` from the parameters (we copy the
   block whole, so this should hold — verify), and beware objects typed `PDataBlock`,
   which does not exist in the CS1 runtime and crashes it (we use
   `PDataBlockD3D11`, as shipped files do — verify nothing downgrades it).

## Files

* `src/ED8Editor.Phyre/Authoring/PhyreModelClusterWriter.cs` — writes the cluster.
* `src/ED8Editor.Phyre/Authoring/PhyreClusterContents.cs` — the description and the
  class list.
* `src/ED8Editor.Phyre/Authoring/PhyreClusterAssembler.cs` — header, `PlatformId`.
* `src/ED8Editor.Phyre/Authoring/PhyreNamespaceWriter.cs` — the packed namespace.
* `src/ED8Editor.Phyre/Authoring/PhyreMaterialTable.cs` — the shader's parameter block.
* `src/ED8Editor.Phyre/Authoring/PhyreSchemaLibrary.cs` — schema + canonical tables.
* `src/ED8Editor.Application/MapModelPackage.cs` — packages for maps and props.
* `tools/PhyreAuthoringProbe` — the oracles.
* `tools/MapImport` — `import`, `replace-model`, `replace-prop`, `graft-prop`,
  `repack`, `revert`.
* `plan.txt` — the running record, newest last. Read its last few sections first.

Tests: `dotnet run --project tests/ED8Editor.Tests` — 82 pass. They are unit tests
over our own code; **they cannot catch an engine-compatibility fault**, which is the
whole reason the two oracles exist.

## Update - physical-map ABI and world matrix (31 July)

The final `PWorldMatrix` discrepancy was real. Across unrelated shipped props and
maps, the neutral DX11 world matrix stores its three unit components at byte offsets
`+4`, `+12` and `+24`; the authored writer used `+0`, `+16`, `+32`. This is fixed.

A more fundamental numbering error was then exposed by comparing `M_R0510` with the
authored `M_Z9100`: collision classes caused the model writer to calculate member
fixups against one derived class table, after which `PhyreClusterContents` added
mandatory classes and the assembler wrote another table. Links such as `m_mesh`,
`m_materialSet` and `m_worldMatrix` consequently resolved as unrelated raw member
indices.

`M_R0500` and `M_R0510` have now established a second fixed ABI table: 15 types and
147 classes, identical and identically ordered in both physical map clusters.
`PhyreSchemaLibrary.CanonicalPhysicsClasses` records that table. The model writer,
cluster contents and assembler now select it together; the minimal-effect writer
likewise calculates fixups against the canonical table it actually emits.

Offline verification of the regenerated `M_Z9100.pkg`:

* types: 15/15 identical to `M_R0510`;
* classes: 147/147 identical and identically ordered;
* essential model, material, node and physics links resolve by their correct member
  names again;
* neutral `PWorldMatrix` bytes match shipped assets;
* authored minimal effect parses as one input with a 528-byte global constant buffer;
* 82 tests pass.

The real game package has been regenerated and awaits an in-game test.

## Update — authored prop loader crash fixed (1 August)

The four-byte overrun was not repaired by making `m_totalDataSize` four bytes
larger. That experiment made the file internally false: the header promised four
object-data bytes which were not present, shifted every following section, and made
`PhyreFixupReader` overrun an instance list. `m_totalDataSize` again equals the exact
sum of the instance-list group sizes.

Comparing ED8Editor's cube directly with the cube produced by Falcom's
`PhyreAssetProcessor.exe` then exposed the actual missing wiring:

* serialized `PString` values use raw array-fixup offsets into the allocated class
  block, not packed member IDs;
* for a derived class that offset is adjusted by `OffsetFromParent`
  (`PNode.m_name`: descriptor `+0x50`, allocated-block fixup `+0x4C`);
* the minimal `PParameterBuffer` has a pointer at `+0x0C` to
  `PShaderParameterDefinition[0]`.

After applying those format rules, `--diff-wiring` reports no difference between
the two cube clusters. A real game run with the authored `O_T10LIG03` passed the
asset preload and rendered frames for more than twenty seconds without the former
`0xC0000005`; the debugger later stopped only on an unrelated internal `int 3`.
The native lamp package was restored afterward and its SHA-256 matches the saved
original.

Regression coverage now checks the exact string offsets, the parameter-definition
pointer, and equality of `m_totalDataSize` with the bytes its groups contain.
