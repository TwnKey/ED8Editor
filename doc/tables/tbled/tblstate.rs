/*
Tbl entries aren't necessarily sorted in any obvious or logical order, probably because these files are compiled line by line from handwritten source files.
I would be pretty surprised if the order of entries was important, but I don't know for sure, so I think it's a good idea to preserve the order wherever possible.
It is important to note that entries of the same type are not necessarily in one contiguous chunk; different entry types can alternate between each other.

Another complication is that we want to preserve edit history to enable the user to undo and redo changes.
Though the tbl files aren't too big, it's a bad idea to independently store the full state of the tbl after each edit.

We store the rows for every edit state as a vector of refcounted pointers to the actual rows.
That means on every edit, we clone the complete vector of pointers, clone one row, and edit that cloned row.

That is really inefficient in theory (it's possible to do better with immutable data structures, but that's complicated), but it should be efficient enough in practice:
The biggest table, t_vctiming.tbl, has ~30k entries.
Cloning a 30k vector of entries shouldn't be too much of an issue as long as we don't have a huge undo stack.
For that reason, we set an upper bound of 1000 edits on the undo stack.

The above is not necessarily true if we also consider data imports, but worrying about a user importing a CSV 1000 times for the same file is a waste of time.
*/

use tocs::tbl::{AtomicValue as Value, Attribute, Header};

use std::borrow::Borrow as _;

const MAX_UNDO: usize = 1000;

struct TblStateWithoutHistory {
    /// All tbl rows in their original order (or the user-desired order if there have been changes)
    all_rows: std::vec::Vec<std::rc::Rc<tocs::tbl::Entry>>, // we use refcounting because cloning all rows is rather expensive
}

impl FromIterator<tocs::tbl::Entry> for TblStateWithoutHistory {
    fn from_iter<I: IntoIterator<Item = tocs::tbl::Entry>>(iter: I) -> Self {
        Self {
            all_rows: iter.into_iter().map(std::rc::Rc::new).collect(),
        }
    }
}

#[derive(Clone, Debug)]
pub enum Change {
    DuplicateRow {
        /// Header name / type of the entry. Only used to describe the change to the user.
        header: Header,
        /// Index of the row in the full table (not partitioned by header)
        row: usize,
    },
    RemoveRow {
        /// Header name / type of the entry. Only used to describe the change to the user.
        header: Header,
        /// Index of the row in the full table (not partitioned by header)
        row: usize,
    },
    UpdateCell {
        /// Header name / type of the entry. Only used to describe the change to the user.
        header: Header,
        /// Index of the row in the full table (not partitioned by header)
        row: usize,
        column: usize,
        value: Value,
    },
    ImportTable {
        header: Header,
        rows: std::vec::Vec<std::rc::Rc<tocs::tbl::Entry>>,
    },
}

pub struct TblStateHistory {
    current: TblStateWithoutHistory,

    /// All (unique) headers with corresponding rows
    headers: indexmap::IndexSet<Header>,

    /// undo stack
    ///
    /// Stores older states and the change that brings us to the NEXT state.
    /// This is a VecDeque so we can have a fast limit to its size (whenever we push too many, we can drop from the front).
    /// The size of the redo stack is naturally limited by the (maximum) size of the undo stack.
    undo: std::collections::VecDeque<(TblStateWithoutHistory, Change)>,

    /// redo stack
    ///
    /// Stores newer states and the change that brings the PREVIOUS state to "this one" (inverse meaning compared to 'undo'!).
    redo: std::vec::Vec<(Change, TblStateWithoutHistory)>,
}

impl TblStateWithoutHistory {
    fn duplicate_row(&self, rowid: usize) -> Self {
        let mut all_rows = self.all_rows.clone();
        let row = std::rc::Rc::clone(&all_rows[rowid]);
        all_rows.insert(rowid + 1, row);
        TblStateWithoutHistory { all_rows }
    }

    fn remove_row(&self, rowid: usize) -> Self {
        let mut all_rows = self.all_rows.clone();
        let _ = all_rows.remove(rowid);
        TblStateWithoutHistory { all_rows }
    }

    fn update_cell(&self, rowid: usize, colid: usize, value: Value) -> Self {
        let mut all_rows = self.all_rows.clone();
        let row = std::rc::Rc::make_mut(&mut all_rows[rowid]); // Clones inner data and gives us a reference to the cloned data
        let (_, oldval) = row.values.get_index_mut(colid).expect("column index should be valid");
        match (oldval.r#type(), value.r#type()) {
            (tocs::tbl::AtomicType::X(_), tocs::tbl::AtomicType::X(_)) => {} // Ok, user is allowed to change length
            (oldtype, newtype) => assert_eq!(oldtype, newtype),
        }
        *oldval = value;
        TblStateWithoutHistory { all_rows }
    }
}

impl Change {
    pub fn describe(&self) -> String {
        match self {
            Change::DuplicateRow { header, row: _ } => format!("duplicate row in table {header}"),
            Change::RemoveRow { header, row: _ } => format!("remove row in table {header}"),
            Change::UpdateCell { header, row: _, column: _, value } => {
                let mut s = value.to_string();
                // we don't want to show the full string if it's very long, so we truncate it
                match s.char_indices().nth(15) {
                    None => {}
                    Some((idx, _)) => {
                        s.truncate(idx);
                        s.push_str("...");
                    }
                };
                format!("set cell value in table {header} to '{}'", s)
                // TODO: attribute name?
            }
            Change::ImportTable { header, rows } => format!("import {} rows into table {}", rows.len(), header),
        }
    }
}

impl TblStateHistory {
    pub fn new(tbl: tocs::tbl::Tbl) -> Self {
        let headers = tbl.headers().chain(tbl.get_entries().iter().map(|entry| &entry.header)).cloned().collect();
        let current = TblStateWithoutHistory::from_iter(tbl.into_entries());
        Self {
            current,
            headers,
            undo: std::collections::VecDeque::new(),
            redo: vec![],
        }
    }

    pub fn clear_all(&mut self) {
        let Self { current, headers, undo, redo } = self;
        headers.clear();
        undo.clear();
        redo.clear();
        let TblStateWithoutHistory { all_rows } = current;
        all_rows.clear();
    }

    pub fn headers(&self) -> &indexmap::IndexSet<Header> {
        &self.headers
    }

    pub fn has_header(&self, header: &Header) -> bool {
        self.headers.contains(header)
    }

    pub fn get_cell_contents(&mut self, rowid: usize, colid: usize) -> Option<(&Attribute, &Value)> {
        self.current.all_rows.get(rowid).and_then(|row| row.values.get_index(colid))
    }

    pub fn next_undo(&self) -> Option<&Change> {
        self.undo.back().map(|(_, change)| change)
    }

    pub fn next_redo(&self) -> Option<&Change> {
        self.redo.last().map(|(change, _)| change)
    }

    pub fn undo(&mut self) -> Option<()> {
        self.undo.pop_back().map(|(state, change)| {
            let oldcurrent = std::mem::replace(&mut self.current, state);
            self.redo.push((change, oldcurrent));
        })
    }

    pub fn redo(&mut self) -> Option<()> {
        self.redo.pop().map(|(change, state)| {
            let oldcurrent = std::mem::replace(&mut self.current, state);
            self.undo.push_back((oldcurrent, change));
        })
    }

    pub fn change(&mut self, change: Change) {
        let newstate = match &change {
            Change::DuplicateRow { header, row } => {
                assert_eq!(header, &self.current.all_rows[*row].header);
                self.current.duplicate_row(*row)
            }
            Change::RemoveRow { header, row } => {
                assert_eq!(header, &self.current.all_rows[*row].header);
                self.current.remove_row(*row)
            }
            Change::UpdateCell { header, row, column, value } => {
                assert_eq!(header, &self.current.all_rows[*row].header);
                self.current.update_cell(*row, *column, value.clone())
            }
            Change::ImportTable { header, rows: new_rows_for_header } => {
                // There's not really a good solution here for how to order the rows when the table is split
                // We could probably use a diffing algorithm to find an approximate solution for the fewest rows assigned to an incorrect chunk.
                // The benefit doesn't nearly justify the required effort though.
                let mut got_first_row_for_header = false;
                let mut all_rows_new = std::vec::Vec::new();
                for row in self.current.all_rows.iter() {
                    if &row.header == header {
                        if got_first_row_for_header {
                            continue;
                        } else {
                            got_first_row_for_header = true;
                            for row in new_rows_for_header {
                                all_rows_new.push(row.clone())
                            }
                        }
                    } else {
                        all_rows_new.push(row.clone())
                    }
                }
                if !got_first_row_for_header {
                    for row in new_rows_for_header {
                        all_rows_new.push(row.clone())
                    }
                }
                TblStateWithoutHistory { all_rows: all_rows_new }
            }
        };

        self.redo.clear();
        let oldstate = std::mem::replace(&mut self.current, newstate);
        self.undo.push_back((oldstate, change));
        if self.undo.len() > MAX_UNDO {
            let _ = self.undo.pop_front();
        }
    }

    pub fn all_current_rows(&self) -> impl Iterator<Item = &tocs::tbl::Entry> {
        self.current.all_rows.iter().map(|rc| rc.borrow())
    }

    pub fn rows_for_header<'a>(&'a self, header: &'a Header) -> impl Iterator<Item = &tocs::tbl::Entry> + 'a {
        self.all_current_rows().filter(move |entry| &entry.header == header)
    }
}

impl Default for TblStateHistory {
    fn default() -> Self {
        Self {
            current: TblStateWithoutHistory::from_iter([]),
            headers: indexmap::IndexSet::new(),
            undo: std::collections::VecDeque::new(),
            redo: vec![],
        }
    }
}

#[cfg(test)]
mod test {
    use super::{Change, TblStateHistory};
    use tocs::tbl::{AtomicSchemas, AtomicType, AtomicValue, Attribute, Header};

    fn compare_entries(expected: &[tocs::tbl::Entry], actual: &[tocs::tbl::Entry]) {
        assert_eq!(actual.len(), expected.len());
        for row in 0..expected.len() {
            println!("row {}: {}", row, actual[row].header);
            assert_eq!(actual[row].header, expected[row].header);
            assert_eq!(actual[row].values.len(), expected[row].values.len());
            for col in 0..actual[row].values.len() {
                let (actual_attr, actual_value) = actual[row].values.get_index(col).unwrap();
                let (expected_attr, expected_value) = expected[row].values.get_index(col).unwrap();
                assert_eq!(actual_attr, expected_attr);
                assert_eq!(actual_value.r#type(), expected_value.r#type());
                assert_eq!(actual_value.to_string(), expected_value.to_string());
            }
        }
    }

    lazy_static::lazy_static! {
    pub static ref SCHEMAS : AtomicSchemas = [
            ("MasterQuartzMemo", vec![("item_id", AtomicType::I16), ("memo_id", AtomicType::I16), ("text", AtomicType::CUtf8)]),
            ("MasterQuartzDummy", vec![("data", AtomicType::X(4))]),
            (
                "MasterQuartzStatus",
                vec![("pattern_id", AtomicType::U8), ("level", AtomicType::U16), ("main_hp", AtomicType::I16), ("sub_hp", AtomicType::I16)],
            ),
            (
                "MasterQuartzData",
                vec![
                    ("item_id", AtomicType::U16),
                    ("level", AtomicType::U16),
                    ("art_1", AtomicType::I16),
                    ("art_2", AtomicType::I16),
                    ("memo", AtomicType::I16),
                ],
            ),
            ("MasterQuartzBase", vec![("item_id", AtomicType::U16), ("mq_id", AtomicType::U16), ("hp_pattern", AtomicType::U16)]),
        ]
        .into_iter()
        .map(|(hdr, schema)| (Header::new(&hdr).unwrap(), schema.into_iter().map(|(attr, ty)| (Attribute::from(attr), ty)).collect()))
        .collect();
    }

    fn to_entry((header, data): (&str, std::vec::Vec<(&str, &str)>)) -> tocs::tbl::Entry {
        let header = Header::new(header).unwrap();
        let schema = SCHEMAS.get(&header).unwrap();
        let values = data
            .into_iter()
            .map(|(attr, s)| {
                let attr = Attribute::from(attr);
                let val = AtomicValue::from_str(schema.get(&attr).unwrap(), &s).unwrap();
                (attr, val)
            })
            .collect();
        tocs::tbl::Entry { header, values }
    }

    fn number_of_rows_for_header(state: &TblStateHistory, header: &Header) -> usize {
        state.all_current_rows().enumerate().filter(move |(_, entry)| &entry.header == header).map(|(i, _)| i).count()
    }

    #[test]
    fn test_order() {
        // we simulate a simplified mstqrt table with rows in some arbitrary order (but not all entries of the same type in one block because then the test would be pointless).
        // This test is probably not super important anymore since we don't partition the tables based on header here, but it doesn't hurt to be here, so.

        let original_data: std::vec::Vec<tocs::tbl::Entry> = [
            // first master quartz + MasterQuartzStatus and MasterQuartzDummy table in the middle
            // MasterQuartzMemo, MasterQuartzData, and MasterQuartzBase are "split" tables, i.e. not contiguous
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cd")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "400"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "3"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
            // second master quartz
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "9"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "1"), ("art_1", "123"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "4444"), ("mq_id", "0"), ("hp_pattern", "0")]),
            // third master quartz
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();

        let mut tbl_history = TblStateHistory::new(tocs::tbl::Tbl::new(&tocs::tbl::Version::V2(SCHEMAS.clone()), [], original_data));

        // Now we make some changes
        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzMemo").unwrap(),
            row: 20,
            column: 1,
            value: AtomicValue::I16(10),
        });
        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzDummy").unwrap(),
            row: 3,
            column: 0,
            value: AtomicValue::X(hexhex::decode("00ab00cc").unwrap().into()),
        });
        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzData").unwrap(),
            row: 14,
            column: 3,
            value: AtomicValue::I16(30),
        });
        // duplicate and remove some rows
        assert_eq!(number_of_rows_for_header(&tbl_history, &Header::new("MasterQuartzDummy").unwrap()), 2);
        assert_eq!(number_of_rows_for_header(&tbl_history, &Header::new("MasterQuartzMemo").unwrap()), 9);
        tbl_history.change(Change::DuplicateRow {
            header: Header::new("MasterQuartzMemo").unwrap(),
            row: 0,
        });
        assert_eq!(number_of_rows_for_header(&tbl_history, &Header::new("MasterQuartzDummy").unwrap()), 2);
        assert_eq!(number_of_rows_for_header(&tbl_history, &Header::new("MasterQuartzMemo").unwrap()), 10);
        tbl_history.change(Change::DuplicateRow {
            header: Header::new("MasterQuartzMemo").unwrap(),
            row: 21,
        });
        assert_eq!(number_of_rows_for_header(&tbl_history, &Header::new("MasterQuartzDummy").unwrap()), 2);
        assert_eq!(number_of_rows_for_header(&tbl_history, &Header::new("MasterQuartzMemo").unwrap()), 11);
        tbl_history.change(Change::RemoveRow {
            header: Header::new("MasterQuartzMemo").unwrap(),
            row: 29,
        });
        tbl_history.change(Change::RemoveRow {
            header: Header::new("MasterQuartzData").unwrap(),
            row: 23,
        });
        // completely replace MasterQuartzBase table
        let new_base_rows: std::vec::Vec<std::rc::Rc<tocs::tbl::Entry>> = [
            ("MasterQuartzBase", vec![("item_id", "10"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzBase", vec![("item_id", "11"), ("mq_id", "1"), ("hp_pattern", "1")]),
            ("MasterQuartzBase", vec![("item_id", "12"), ("mq_id", "2"), ("hp_pattern", "2")]),
            ("MasterQuartzBase", vec![("item_id", "13"), ("mq_id", "4"), ("hp_pattern", "1")]),
        ]
        .into_iter()
        .map(to_entry)
        .map(std::rc::Rc::new)
        .collect();
        tbl_history.change(Change::ImportTable {
            header: Header::new("MasterQuartzBase").unwrap(),
            rows: new_base_rows,
        });
        // now we modify one of the new entries and an original one
        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzBase").unwrap(),
            row: 21,
            column: 1,
            value: AtomicValue::U16(3),
        });
        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzBase").unwrap(),
            row: 21,
            column: 1,
            value: AtomicValue::U16(3),
        });
        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzData").unwrap(),
            row: 26,
            column: 2,
            value: AtomicValue::I16(42),
        });

        // now we check that the state after changes is as expected
        let expected: std::vec::Vec<tocs::tbl::Entry> = [
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cc")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "400"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "30"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "10"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzBase", vec![("item_id", "11"), ("mq_id", "1"), ("hp_pattern", "1")]),
            ("MasterQuartzBase", vec![("item_id", "12"), ("mq_id", "2"), ("hp_pattern", "2")]),
            ("MasterQuartzBase", vec![("item_id", "13"), ("mq_id", "3"), ("hp_pattern", "1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "42"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();

        let actual: std::vec::Vec<tocs::tbl::Entry> = tbl_history.all_current_rows().cloned().collect();

        compare_entries(&actual, &expected);
    }

    #[test]
    fn test_max_undo() {
        let mut tbl_history = TblStateHistory::default();
        let initial_row = tocs::tbl::Entry {
            header: Header::new("foo").unwrap(),
            values: [
                (Attribute::from("id"), AtomicValue::I16(0)),
                (Attribute::from("x"), AtomicValue::U32(3892)),
                (Attribute::from("y"), AtomicValue::CUtf8("lorem ipsum".into())),
            ]
            .into_iter()
            .collect(),
        };
        tbl_history.change(Change::ImportTable {
            header: Header::new("foo").unwrap(),
            rows: vec![std::rc::Rc::new(initial_row)],
        });

        for i in 1..5_000 {
            tbl_history.change(Change::DuplicateRow {
                header: Header::new("foo").unwrap(),
                row: 0,
            });
            tbl_history.change(Change::UpdateCell {
                header: Header::new("foo").unwrap(),
                row: i,
                column: 0,
                value: AtomicValue::I16(i as i16),
            });
            assert_eq!(tbl_history.undo.len(), std::cmp::min(1 + 2 * i, super::MAX_UNDO));
        }

        for i in 1..=1_000 {
            assert!(tbl_history.undo().is_some());
            assert_eq!(tbl_history.undo.len(), 1000 - i);
        }

        assert!(tbl_history.undo().is_none());
        assert_eq!(tbl_history.undo.len(), 0);
        assert_eq!(tbl_history.redo.len(), 1000);

        assert_eq!(tbl_history.all_current_rows().count(), 4500);
    }

    #[test]
    fn test_undo_redo() {
        // start state
        // first master quartz + MasterQuartzStatus and MasterQuartzDummy table in the middle
        // MasterQuartzMemo, MasterQuartzData, and MasterQuartzBase are "split" tables, i.e. not contiguous
        let state_0: std::vec::Vec<tocs::tbl::Entry> = [
            // first master quartz + MasterQuartzStatus and MasterQuartzDummy table in the middle
            // MasterQuartzMemo, MasterQuartzData, and MasterQuartzBase are "split" tables, i.e. not contiguous
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cd")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "400"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "3"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
            // second master quartz
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "9"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "1"), ("art_1", "123"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "4444"), ("mq_id", "0"), ("hp_pattern", "0")]),
            // third master quartz
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();

        let mut tbl_history = TblStateHistory::new(tocs::tbl::Tbl::new(
            &tocs::tbl::Version::V1(SCHEMAS.clone()),
            [].into_iter().map(|s| Header::new(s).unwrap()),
            state_0.clone(),
        ));
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_0);

        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzMemo").unwrap(),
            row: 20,
            column: 1,
            value: AtomicValue::I16(10),
        });
        let state_1: std::vec::Vec<tocs::tbl::Entry> = [
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cd")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "400"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "3"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "1"), ("art_1", "123"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "4444"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_1);

        assert!(tbl_history.undo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_0);

        assert!(tbl_history.undo().is_none());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_0);

        assert!(tbl_history.redo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_1);

        tbl_history.change(Change::DuplicateRow {
            header: Header::new("MasterQuartzBase").unwrap(),
            row: 31,
        });
        let state_2: std::vec::Vec<tocs::tbl::Entry> = [
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cd")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "400"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "3"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "1"), ("art_1", "123"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "4444"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_2);

        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzBase").unwrap(),
            row: 31,
            column: 2,
            value: AtomicValue::U16(2),
        });
        let state_3: std::vec::Vec<tocs::tbl::Entry> = [
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cd")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "400"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "3"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "1"), ("art_1", "123"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "4444"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "2")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_3);

        assert!(tbl_history.undo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_2);

        assert!(tbl_history.undo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_1);

        assert!(tbl_history.undo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_0);

        assert!(tbl_history.redo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_1);

        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzBase").unwrap(),
            row: 31,
            column: 2,
            value: AtomicValue::U16(2),
        });
        let state_4: std::vec::Vec<tocs::tbl::Entry> = [
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cd")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "400"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "3"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "1"), ("art_1", "123"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "4444"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "2")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_4);

        // completely replace MasterQuartzBase table
        let new_base_rows: std::vec::Vec<std::rc::Rc<tocs::tbl::Entry>> = [
            ("MasterQuartzBase", vec![("item_id", "10"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzBase", vec![("item_id", "11"), ("mq_id", "1"), ("hp_pattern", "1")]),
            ("MasterQuartzBase", vec![("item_id", "12"), ("mq_id", "2"), ("hp_pattern", "2")]),
            ("MasterQuartzBase", vec![("item_id", "13"), ("mq_id", "4"), ("hp_pattern", "1")]),
        ]
        .into_iter()
        .map(to_entry)
        .map(std::rc::Rc::new)
        .collect();
        tbl_history.change(Change::ImportTable {
            header: Header::new("MasterQuartzBase").unwrap(),
            rows: new_base_rows,
        });
        let state_5: std::vec::Vec<tocs::tbl::Entry> = [
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cd")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "400"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "3"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "10"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzBase", vec![("item_id", "11"), ("mq_id", "1"), ("hp_pattern", "1")]),
            ("MasterQuartzBase", vec![("item_id", "12"), ("mq_id", "2"), ("hp_pattern", "2")]),
            ("MasterQuartzBase", vec![("item_id", "13"), ("mq_id", "4"), ("hp_pattern", "1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "1"), ("art_1", "123"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_5);

        assert!(tbl_history.redo().is_none());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_5);

        assert!(tbl_history.undo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_4);

        assert!(tbl_history.undo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_1);

        assert!(tbl_history.undo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_0);

        assert!(tbl_history.undo().is_none());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_0);

        assert!(tbl_history.redo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_1);

        assert!(tbl_history.undo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_0);

        assert!(tbl_history.redo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_1);

        assert!(tbl_history.redo().is_some());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_4);

        tbl_history.change(Change::UpdateCell {
            header: Header::new("MasterQuartzStatus").unwrap(),
            row: 9,
            column: 2,
            value: AtomicValue::I16(500),
        });
        let state_6: std::vec::Vec<tocs::tbl::Entry> = [
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 1234 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 1234 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 1234 memo 10")]),
            ("MasterQuartzDummy", vec![("data", "00ab00cd")]),
            ("MasterQuartzDummy", vec![("data", "aaaabbbb")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "1"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "2"), ("main_hp", "600"), ("sub_hp", "60")]),
            ("MasterQuartzStatus", vec![("pattern_id", "0"), ("level", "3"), ("main_hp", "800"), ("sub_hp", "80")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "1"), ("main_hp", "300"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "2"), ("main_hp", "500"), ("sub_hp", "40")]),
            ("MasterQuartzStatus", vec![("pattern_id", "1"), ("level", "3"), ("main_hp", "500"), ("sub_hp", "50")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "1"), ("main_hp", "1000"), ("sub_hp", "100")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "2"), ("main_hp", "1100"), ("sub_hp", "110")]),
            ("MasterQuartzStatus", vec![("pattern_id", "2"), ("level", "3"), ("main_hp", "1200"), ("sub_hp", "120")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "815"), ("art_2", "3"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "42"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "4444"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "1"), ("art_1", "123"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "4444"), ("level", "3"), ("art_1", "555"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "4444"), ("mq_id", "0"), ("hp_pattern", "0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "0"), ("text", "mq 4444 memo 0")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "1"), ("text", "mq 4444 memo 1")]),
            ("MasterQuartzMemo", vec![("item_id", "1234"), ("memo_id", "10"), ("text", "mq 4444 memo 10")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "1"), ("art_1", "730"), ("art_2", "-1"), ("memo", "0")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "2"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "1")]),
            ("MasterQuartzData", vec![("item_id", "1234"), ("level", "3"), ("art_1", "-1"), ("art_2", "-1"), ("memo", "10")]),
            ("MasterQuartzBase", vec![("item_id", "1234"), ("mq_id", "0"), ("hp_pattern", "2")]),
        ]
        .into_iter()
        .map(to_entry)
        .collect();
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_6);

        assert!(tbl_history.redo().is_none());
        compare_entries(&tbl_history.all_current_rows().cloned().collect::<std::vec::Vec<_>>(), &state_6);
    }

    // TODO: randomized test for tbl_history
}