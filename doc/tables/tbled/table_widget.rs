// table widget with (possibly) filtered rows and (possibly) reordered/hidden columns

use crate::filter::row_passes_filter;

use tocs::tbl::AtomicSchema as Schema;
use tocs::tbl::{AtomicType, Attribute, Entry, Header};

use std::borrow::Borrow;
use std::sync::{Arc, Mutex};

use fltk::prelude::TableExt as _;
use fltk::prelude::WidgetExt as _;

struct TableData {
    attributes: std::vec::Vec<Attribute>,
    attribute_types: std::vec::Vec<AtomicType>,
    original_col_indexes: std::vec::Vec<usize>,
    original_row_indexes: std::vec::Vec<usize>,
    cells: grid::Grid<ecow::EcoString>,
}

pub struct FilteredTable {
    widget: fltk::table::Table,
    data: Arc<Mutex<TableData>>,
}

impl FilteredTable {
    pub fn new() -> Self {
        let table_data = Arc::new(Mutex::new(TableData {
            attributes: std::vec::Vec::new(),
            attribute_types: std::vec::Vec::new(),
            original_col_indexes: std::vec::Vec::new(),
            original_row_indexes: std::vec::Vec::new(),
            cells: grid::Grid::new(0, 0),
        }));

        let mut table = fltk::table::Table::default();
        table.set_col_header(true);
        table.set_col_resize(true);
        table.set_row_header(true);
        table.set_row_resize(true);
        table.set_col_width_all(40);

        table.draw_cell({
            // We move a reference to the table data into the following closure
            let table_data = table_data.clone();
            move |table, ctx, row, col, x, y, w, h| {
                let nrows = table.rows();
                let ncols = table.cols();
                log::trace!("table draw: ctx={ctx:?} row={row}/{nrows} row={col}/{ncols}, x={x}, y={y}, w={w}, h={h}");
                let mut guard = table_data.lock().unwrap();
                let TableData {
                    attributes, attribute_types, cells, ..
                } = &mut *guard;
                let Ok(row_idx) = usize::try_from(row) else { return };
                let Ok(col_idx) = usize::try_from(col) else { return };
                match ctx {
                    fltk::table::TableContext::ColHeader => {
                        let Some(attr) = attributes.get(col_idx) else { return };
                        fltk::draw::push_clip(x, y, w, h);
                        fltk::draw::draw_box(fltk::enums::FrameType::ThinUpBox, x, y, w, h, fltk::enums::Color::FrameDefault);
                        fltk::draw::set_draw_color(fltk::enums::Color::Black);
                        fltk::draw::draw_text2(attr, x, y, w, h, fltk::enums::Align::Center);
                        fltk::draw::pop_clip();
                    }
                    fltk::table::TableContext::RowHeader => {
                        // TODO: original row index as row header? -> needs space for 5 digits
                    }
                    fltk::table::TableContext::Cell => {
                        let Some(s) = cells.get(row_idx, col_idx) else { return };
                        fltk::draw::push_clip(x, y, w, h);
                        let bg_color = if table.is_selected(row, col) {
                            0x00_D0_D0_D0 // selected cell
                        } else if row % 2 == 0 {
                            0x00_F0_F0_F0 // even rows
                        } else {
                            0x00_ff_ff_ff // odd rows
                        };
                        fltk::draw::set_draw_color(fltk::enums::Color::from_u32(bg_color));
                        fltk::draw::draw_rectf(x, y, w, h); // background
                        fltk::draw::set_draw_color(fltk::enums::Color::Dark1);
                        fltk::draw::draw_rect(x, y, w, h); // outline

                        fltk::draw::set_draw_color(fltk::enums::Color::Gray0);
                        let (font, text_alignment) = match attribute_types[col_idx] {
                            tocs::tbl::AtomicType::I8
                            | tocs::tbl::AtomicType::I16
                            | tocs::tbl::AtomicType::I32
                            | tocs::tbl::AtomicType::U8
                            | tocs::tbl::AtomicType::U16
                            | tocs::tbl::AtomicType::U32
                            | tocs::tbl::AtomicType::F32 => (fltk::enums::Font::Courier, fltk::enums::Align::Right),
                            tocs::tbl::AtomicType::CUtf8 => (fltk::enums::Font::Helvetica, fltk::enums::Align::Left),
                            tocs::tbl::AtomicType::X(_) => (fltk::enums::Font::Courier, fltk::enums::Align::Left),
                        };

                        fltk::draw::set_font(font, 14);

                        let (x, w) = if w > 5 { (x + 2, w - 4) } else { (x, w) };
                        let (y, h) = if h > 5 { (y + 2, h - 4) } else { (y, h) };
                        fltk::draw::draw_text2(s, x, y, w, h, text_alignment);
                        fltk::draw::pop_clip();

                        fltk::draw::set_font(fltk::enums::Font::Helvetica, 14);
                    }
                    _ => {}
                }
            }
        });

        let mut this = Self { widget: table, data: table_data };

        this.auto_size_cells();

        this
    }

    pub fn clear(&mut self) {
        let header = Header::new("").expect("empty string is valid ASCII");
        let schema = Schema::new();
        let attributes = indexmap::IndexSet::new();
        let filters = &[];
        let all_tbl_rows = &[];
        self.update_data(header, &schema, &attributes, filters, all_tbl_rows.iter());
    }

    pub fn update_data<I, E>(&mut self, header_name: Header, schema: &Schema, columns: &indexmap::IndexSet<Attribute>, row_filters: &[crate::filter::RowFilter], all_tbl_rows: I)
    where
        I: Iterator<Item = E>,
        E: Borrow<Entry>,
    {
        let mut guard = self.data.lock().unwrap();
        let TableData {
            ref mut attributes,
            ref mut attribute_types,
            ref mut original_col_indexes,
            ref mut original_row_indexes,
            ref mut cells,
        } = &mut *guard;

        // The Grid datastructure doesn't have a resize operation and we don't want to allocate a new one.
        // Instead we clear the grid (which clears the underlying vector but keeps its capacity), then create a new grid from that old vector.
        // This allows the new grid (with possibly different column count!) to grow without allocations at least until it reaches the size of the old grid.
        // However, Rust doesn't allow us to move self.grid, so we have to instead create a new empty Grid (this should not allocate), and then swap the two
        cells.clear();
        let mut new_cells = grid::Grid::new(0, 0);
        std::mem::swap(cells, &mut new_cells);
        let mut row_major_vector_of_cells = new_cells.into_vec();

        // update columns: for each visible column, look up its corresponding position in the schema
        attributes.clear();
        attribute_types.clear();
        original_col_indexes.clear();
        for attr in columns.iter() {
            let (col_idx, _, ty) = schema.get_full(attr).expect("column should be in schema");
            attributes.push(attr.clone());
            attribute_types.push(*ty);
            original_col_indexes.push(col_idx);
        }

        // update rows: filter based on header and explicit filters
        original_row_indexes.clear();
        for (row_idx, row) in all_tbl_rows.enumerate() {
            if row.borrow().header == header_name && row_filters.iter().all(|filter| row_passes_filter(row.borrow(), filter)) {
                original_row_indexes.push(row_idx);
                for (attr, attr_idx) in attributes.iter().zip(original_col_indexes.iter()) {
                    let (k, v) = row.borrow().values.get_index(*attr_idx).expect("entry attributes should correspond to schema");
                    assert_eq!(k, attr, "entry should have attributes in the same order as the schema");
                    row_major_vector_of_cells.push(ecow::eco_format!("{v}"));
                }
            }
        }

        // Now we create the new Grid and swap it in for the empty placeholder Grid
        let mut new_cells = grid::Grid::from_vec(row_major_vector_of_cells, attributes.len());
        std::mem::swap(cells, &mut new_cells);

        // We need to drop the guard before redrawing the table to allow draw_cell access to the data
        let (nrows, ncols) = (cells.rows().try_into().unwrap(), attributes.len().try_into().unwrap());

        drop(guard);

        self.widget.set_rows(nrows);
        self.widget.set_cols(ncols);
        log::debug!("table data updated; {} rows, {} columns", self.widget.rows(), self.widget.cols());
        self.widget.redraw();
    }

    pub fn auto_size_cells(&mut self) {
        let guard = self.data.lock().unwrap();
        let TableData { attributes, cells, .. } = &*guard;
        let mut column_widths: std::vec::Vec<i32> = attributes.iter().map(|attr| fltk::draw::measure(attr, false).0).collect();
        let nrows: i32 = cells.rows().try_into().unwrap();
        let mut row_height = 1;
        for r in 0..cells.rows() {
            #[allow(clippy::needless_range_loop)]
            for c in 0..cells.cols() {
                let (w, h) = fltk::draw::measure(cells.get(r, c).unwrap(), false);
                column_widths[c] = std::cmp::max(column_widths[c], w);
                row_height = std::cmp::max(row_height, h);
            }
        }
        // We need to drop the guard before redrawing the table to allow draw_cell access to the data
        drop(guard);

        log::trace!("column widths: {:?} rows: {} row height: {}", column_widths, nrows, row_height);

        for (i, w) in column_widths.into_iter().enumerate() {
            self.widget.set_col_width(i.try_into().unwrap(), w + 4);
        }
        self.widget.set_row_height_all(row_height);

        self.widget.redraw();
    }

    pub fn emit<T: 'static + Clone + Send + Sync>(&mut self, sender: fltk::app::Sender<T>, msg: T) {
        self.widget.emit(sender, msg)
    }

    fn original_row(&self, filtered_row_index: i32) -> Option<usize> {
        let row: usize = filtered_row_index.try_into().unwrap();
        let guard = self.data.lock().unwrap();
        let TableData { ref original_row_indexes, .. } = &*guard;
        original_row_indexes.get(row).copied()
    }

    fn original_col(&self, filtered_col_index: i32) -> Option<usize> {
        let col: usize = filtered_col_index.try_into().unwrap();
        let guard = self.data.lock().unwrap();
        let TableData { ref original_col_indexes, .. } = &*guard;
        original_col_indexes.get(col).copied()
    }

    pub fn selected_cell(&self) -> Option<(usize, usize)> {
        let (row, col, row2, col2) = self.widget.try_get_selection()?;
        if row == row2 && col == col2 {
            Some((self.original_row(row)?, self.original_col(col)?))
        } else {
            None
        }
    }

    pub fn selected_row(&self) -> Option<usize> {
        let (row, col, row2, col2) = self.widget.try_get_selection()?;
        if row == row2 && col == 0 && col2 == self.widget.cols() - 1 {
            self.original_row(row)
        } else {
            None
        }
    }
}