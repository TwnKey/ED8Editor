# CS1 camera instruction semantics

The bundled instruction registry comes from
`opcode_analyzer/cs1_instructions.json`. A different registry can be selected
from **Options > Instruction definitions...**; the path is persisted in the user
settings.

Camera-aware script editing first consumes operand metadata (`sem: "camera"` plus
a supported `sem_arg`). The current authoritative registry deliberately retains
some OP45 payloads as opaque byte spans, so the confirmed layouts below are
handled by a separate byte codec. The codec preserves every byte outside the
documented camera value.

## Confirmed OP45 selectors

| Selector | Meaning | Editor mapping |
| --- | --- | --- |
| 2 | Absolute camera X/Y/Z coordinates | `camera:position`. |
| 4 | Camera pitch/yaw/roll angles in degrees | Pitch and yaw are restored; roll is preserved when capturing because the editor camera stays upright. |
| 5 | Camera distance | The `f32` operand is `camera:distance`. |
| 11 | Vertical field of view in degrees | `camera:fov-degrees`. |
| 20 | Camera X/Y/Z coordinates | The three consecutive `f32` operands are `camera:position`. |
| 19 | Camera rotation and tilt | Not mapped yet: operand order, units, signs, and the role of the third `f32` still need to be established. |

The signed 16-bit operands following camera values have no confirmed camera-value
meaning and are deliberately not included in camera capture.

## Supported semantic arguments

The viewer currently understands `position`/`pos`, `target`, `forward`,
`distance`, `fov`/`fov-degrees`, `yaw-degrees`, and `pitch-degrees`. Vector
semantics require exactly three consecutive `f32` operands; scalar semantics
require exactly one `f32`. This validation prevents a schema annotation from
silently writing a value into an incompatible instruction layout.

Do not annotate selector 19 with a generic `camera:rotation` until its binary
convention is known. Once established, add an explicitly named encoding to the
semantic converter rather than relying on an implicit Euler convention.
