# Format-neutral model import

`ED8Editor.Models` is the input boundary for custom 3D assets. It is independent
from Phyre serialization and from the Direct3D preview.

## Supported sources

- Autodesk FBX
- glTF / GLB
- Wavefront OBJ
- COLLADA DAE

A package can be a single file or a folder containing a model and external
textures. When a folder contains several supported model files, callers receive
every candidate and must ask the user which source to use. No filename priority
or model-selection heuristic is applied.

## Canonical contract

`ImportedModelScene` preserves:

- the complete node hierarchy and local transforms;
- meshes, triangle indices, normals, tangents, UV sets and vertex colours;
- every original skin influence and per-mesh inverse bind matrix;
- materials and their declared texture bindings;
- all encoded source images, including embedded images;
- every imported animation clip and its translation, rotation and scale tracks;
- normalized geometry unit scale and the unit originally declared by the source
  as two distinct values (preventing FBX units from being applied twice);
- diagnostics, including duplicate node names and unbound package textures.

Format-specific parsing ends at this contract.

## Target adapters

`ImportedModelCpuAdapter` produces a preview `CpuModel`. Texture decoding is
injected by the UI. The D3D11 limit of four influences is enforced only in this
adapter; the canonical model remains lossless.

`ImportedModelPhyreAdapter` produces the current `PhyreModelSource` used by the
authoring pipeline. It applies the same target-only influence and unit policies.
It intentionally does not invent Phyre shader assignments for source textures
whose roles were not declared by the source material.

The Character and Enemy studios expose file and package import. Imported clips
remain individually selectable for preview and are not collapsed to a single
animation.
