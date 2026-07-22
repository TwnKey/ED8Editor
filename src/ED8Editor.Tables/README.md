# CS1 TBL schemas

`cs1_tbl_schemas.json` is the editable schema database used by the TBL reader and editor.
The copy next to the executable takes precedence over the embedded copy, so schemas can
be added or corrected without recompiling ED8Editor. Restart the application after editing it.

The root object contains `entries` (TBL entry categories) and `common` (reusable structures).
Each schema contains an ordered `fields` array. Supported field types are:

- `i8`, `i16`, `i32`, `u8`, `u16`, `u32`
- `f32`
- `cutf8` for NUL-terminated UTF-8 text
- `bytes`, with a required `size`
- `ref`, with a required `ref` naming a schema in `entries` or `common`

Any field can specify `count` to repeat it. References and repetitions are flattened in the
editor using names such as `effects[1] id`, matching the structure expressed by the source
schemas.

An entry schema may additionally define:

- `key`: an integer field used as the value stored by a script operand referencing this table
- `label`: a text field shown next to that value in semantic selectors

If `key` is absent, semantic selectors use the entry's zero-based ordinal within its category.
If `label` is absent, they show the category name.

Example:

```json
{
  "ExampleData": {
    "key": "id",
    "label": "name",
    "fields": [
      { "name": "id", "type": "u16" },
      { "name": "name", "type": "cutf8" },
      { "name": "effects", "type": "ref", "count": 2, "ref": "effect" }
    ]
  }
}
```
