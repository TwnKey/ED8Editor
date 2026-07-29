# Battle-map `.inf` and `.uvb` observations (CS1)

This note records only properties verified against
`data/map/battle` in the installed CS1 data set.

## Relationship

Battle-map metadata is stored in:

`data/map/battle/<map asset>/<map asset>.inf`

An INF document uses the exact root name `node_infomation`. Material animation
bindings have this form:

```xml
<material_anim_set
    name="water0"
    gameMateiralIDs="1"
    source="bm0500_water0.uvb" />
```

The INF therefore associates a named material/game material ID with a separate
animation file. It is not battle geometry.

## UVB container

All 40 shipped `.uvb` files were checked.

- Bytes `0x00..0x03` are ASCII `UVab`.
- The little-endian `u32` at `0x04` is the number of subsequent 32-bit words.
- This count matches the physical file size in 40/40 files:
  `fileSize == 8 + wordCount * 4`.
- Observed file sizes are 92, 100 and 108 bytes.

The payload is a command stream. The whole corpus parses with these neutral
command IDs and operand counts:

| Command ID | Following 32-bit words |
|---:|---:|
| 2 | 2 |
| 3 | 4 |
| 5 | 2 |
| 7 | 2 |
| 8 | 2 |
| 14 | 3 |
| 16 | 3 |
| 17 | 1 |
| 18 | 3 |

Only three command sequences occur:

```text
17, 5, 8, 3, 7, 2, 16       (38 files)
5, 8, 3, 7, 2, 14           (1 file)
17, 17, 5, 8, 3, 7, 2, 18   (1 file)
```

The two operands of command 7 decode as small `f32` values. In files bound as
water/cloud/shadow UV scrolling by the INF, examples include `(0, -0.01)`,
`(-0.0025, 0)` and `(0.001, 0.002)`. This establishes that the UVB stream
contains material UV animation parameters; the precise engine names of command
7 and the other commands remain intentionally unnamed until verified from the
consumer.

Creating a minimal INF must not create a UVB implicitly. A new UV animation
requires separately authored command data and a `material_anim_set` binding.
