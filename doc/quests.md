# CS1 quest authoring — verified model

This document records format/corpus evidence used by the editor. Names are only
promoted to editor concepts when the relationship has been observed in actual
game data. Unknown fields and selectors stay round-trippable under placeholder
names.

## Data ownership

### `data/text/<locale>/t_quest.tbl`

- `QSTitle.unknown_short` is the quest ID.
- `QSText.id` is the same quest ID; all matching records form its ordered
  journal-stage list.
- `QSTitle` owns the visible title and requester/person text.
- `QSText` owns journal text. Its order is the stage index passed by the script.
- `QSChapter` is the eight-entry chapter-name lookup.
- `QSRank` is the fifteen-entry rank threshold/reward lookup. The English table
  shows thresholds `0, 20, 45, ... 430`; the remaining numeric fields have not
  been assigned gameplay names yet.
- The exact meanings of the remaining `QSTitle`/`QSText` bytes are unresolved.
  They are preserved and displayed, not inferred.

`t_quest.tbl` does **not** contain the executable acceptance, validation,
reward, encounter or NPC interaction flow.

### Scenario scripts

Quest lifecycle is regular script control flow. The editor indexes it by raw
opcode and selector, not by function-name conventions.

Verified `OP103` forms:

| Form | Verified effect |
| --- | --- |
| `OP103(questId, 1, stage)` | Publish/select the zero-based `QSText` journal stage. |
| `OP103(questId, 3, 4)` | Activate/accept the quest. |
| `OP103(questId, 3, 8)` | Complete the quest. |

`OP103(questId, 3, 2)` occurs in Falcom's bulk debug initialization, but its
exact lifecycle label is not yet established. Selectors 2, 4, 5 and 6 are
indexed as unresolved operations.

Examples from the shipped corpus:

- `c0140.dat / EV_QS0413_01`: quest 28 receives lifecycle value 4 and journal
  stage 0 after the acceptance scene.
- `c0110.dat / EV_QS0413_02_E`: flags 2680–2684 gate journal stages 5 and 6.
- `c0140.dat / EV_QS0413_03`: quest 28 receives lifecycle value 8 and journal
  stage 7 after the completion scene.
- `r0000.dat / EV_QS0121_WIN`: quest 9 publishes stage 0 from a separate
  victory function, demonstrating that a quest is not necessarily owned by one
  function or one map.

## Authoring model

A mod quest is therefore an aggregate, not a second copy of the script:

1. A `QSTitle` record and its ordered `QSText` records.
2. Exact script references that mutate that quest ID.
3. Incoming dialogue/interaction paths that reach lifecycle value 4.
4. Branch/flag conditions that reach later journal stages or lifecycle value 8.
5. Reward/inventory instructions on the completion paths.
6. Optional `CreateMonsters` records and their field-spawn functions.
7. Optional `t_navi` records once their link mechanism is verified.

The quest editor's script-lifecycle page implements items 1–2. Items 3–6 should
be represented as references into the existing editable graph. This keeps calls,
branches and conditions byte-accurate and prevents two editors from owning the
same instructions.

## Remaining research

- Decode the exact bit/state model behind `OP103` selector 3 value 2.
- Name selectors 2, 4, 5 and 6 from executable behavior or sufficiently strong
  corpus evidence.
- Identify the exact reward/inventory opcode variants; proximity to quest
  completion is evidence of placement, not proof of semantics.
- Establish how `NaviTextData` records are selected and cleared.
- Establish the role of `QSBook`, `QSMons` and any quest-related records located
  outside `t_quest.tbl`.
- Define structural allocation rules for new quest IDs and journal records
  before enabling create/delete.
