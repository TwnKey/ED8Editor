# Third-party notices

## EDOpsParser

The OPS coordinate conversion behavior in `ED8Editor.Ops` was derived from the
community EDOpsParser reference implementation included under
`doc/ops/EDOpsParser-main`.

EDOpsParser is distributed under the MIT License. Its original license text is
preserved at `doc/ops/EDOpsParser-main/LICENSE`.

## Sen-no-Kiseki-PKG-Sharp and the effects editor PKG implementation

The PKG layout and NISLZSS behavior in `ED8Editor.Packages` were ported from the
project's existing effects editor implementation and cross-checked against
Sen-no-Kiseki-PKG-Sharp under `doc/archives/Sen-no-Kiseki-PKG-Sharp-master`.
The upstream license is preserved in that directory.

## ed8pkg2gltf

The native Phyre reader may use format knowledge from the community
ed8pkg2gltf implementation under `doc/models/ed8pkg2gltf-main`. The editor does
not invoke its file-export pipeline. Its MIT license is preserved in that
directory.

## PhyreEngine reference material

Binary layout behavior was cross-checked against the locally supplied
PhyreEngine 3.12 reference material in `ED8_12AssetTool-main`. That material is
marked proprietary and no license permitting redistribution was found, so no
PhyreEngine source code is copied into or linked by ED8Editor. The reader is an
independent managed implementation driven by the descriptors embedded in each
asset.

## Vortice.Windows

`ED8Editor.Rendering` uses Vortice.Direct3D11 and Vortice.D3DCompiler for
managed Direct3D 11 bindings and runtime compilation of editor shaders.
Vortice.Windows is distributed under the MIT License. The package and source
repository are available from https://github.com/amerkoleci/Vortice.Windows.
