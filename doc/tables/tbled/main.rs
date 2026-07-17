#![forbid(unsafe_code)]
#![windows_subsystem = "windows"] // don't open terminal on windows

mod filter;
mod misc;
mod recent_files;
mod select_cols;
mod table_widget;
mod tblstate;
mod warn_diag;

use fltk::prelude::GroupExt as _;
use fltk::prelude::InputExt as _;
use fltk::prelude::MenuExt as _;
use fltk::prelude::WidgetBase as _;
use fltk::prelude::WidgetExt as _;

use camino::Utf8PathBuf;

use clap::Parser;

use tblstate::{Change, TblStateHistory};

#[derive(Debug, Parser)]
struct CliArgs {
    #[arg(short, long)]
    game_version: Option<tocs::tbl::Version>,
    #[arg(short, long)]
    file: Option<Utf8PathBuf>,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum BuiltinSchemaVersion {
    CS1En,
    CS2En,
    CS3,
    CS4,
    CS5,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum CustomSchemaVersion {
    V1,              // CS1
    V2,              // CS2-5
    RecentV1(usize), // index into the "most recent schemas (v1) queue"
    RecentV2(usize), // same but v2
}

#[derive(Clone, Debug)]
enum Msg {
    /// user wants to change to a set of builtin schemas
    SetBuiltinVersion(BuiltinSchemaVersion),
    /// user wants to import custom schemas
    SetCustomVersion(CustomSchemaVersion),
    /// user wants to export a builtin schema
    ExportSchemas(BuiltinSchemaVersion),
    /// user wants to load a file
    LoadFile,
    /// user wants to load a recently used tbl
    LoadRecentTbl(usize),
    /// user wants to save to the same file
    SaveFile,
    /// user wants to save a file
    SelectAndSaveFile,
    /// user wants to export file to csv
    ExportCSV,
    /// user wants to import table from csv
    ImportCSV,
    /// user wants to export tbl to json
    ExportJson,
    /// user wants to import tbl from json
    ImportJson,
    /// user wants to see warnings
    ShowWarnings,
    /// user selected a different header; update the table widget
    DifferentHeaderSelected,
    /// user wants to undo last action
    Undo,
    /// user wants to redo last action
    Redo,
    /// user wants to duplicate the current row
    DuplicateEntry,
    /// user wants to delete the current row
    DeleteEntry,
    /// user wants to automatically size cells to fit content
    AutoSizeTable,
    /// User wants to select/reorder attributes/columns
    SelectAttributes,
    /// an arbitrary event on the table
    TableEvent,
    /// user is done editing, now the value should be updated
    DoneEditing,
    /// user changed the quick search/filter
    UpdateSearch,
}

struct Widgets {
    menu: fltk::menu::SysMenuBar,
    input: fltk::input::Input,
    search: fltk::input::Input,
    header_selector: fltk::misc::InputChoice,
    table: table_widget::FilteredTable,
}

pub struct AppData {
    tbl_history: TblStateHistory,
    tbl_version: tocs::tbl::Version,
    path: Utf8PathBuf, // TODO: remove
    active_entry_type: Option<tocs::tbl::Header>,
    selected_attributes: std::collections::HashMap<tocs::tbl::Header, indexmap::IndexSet<tocs::tbl::Attribute>>, // which attributes should be shown in which order for a given header?
    warnings: std::vec::Vec<tocs::tbl::DeserializeError>,
    //edited: std::collections::HashSet<(i32, i32)>, // edited since last save
    recent_schemas_v1: recent_files::RecentFiles,
    recent_schemas_v2: recent_files::RecentFiles,
    recent_tbls: recent_files::RecentFiles,
}

struct App {
    app: fltk::app::App,
    sender: fltk::app::Sender<Msg>,
    receiver: fltk::app::Receiver<Msg>,
    widgets: Widgets,
    state: AppData,
}

impl App {
    pub fn new(state: AppData) -> Self {
        fltk::draw::set_font(fltk::enums::Font::Helvetica, 14);

        let (w, h) = (1280, 720);
        let app = fltk::app::App::default();
        let (sender, receiver) = fltk::app::channel();
        let mut win = fltk::window::Window::default()
            .with_size(w, h)
            .with_label(&format!("{} version {}", env! {"CARGO_PKG_NAME"}, env! {"CARGO_PKG_VERSION"}));
        win.set_callback(|_| {
            // override close window on pressing escape
            if fltk::app::event() == fltk::enums::Event::Close {
                fltk::app::quit();
            }
        });
        let mut flex = fltk::group::Flex::default().size_of_parent();
        flex.set_type(fltk::group::FlexType::Column);

        let mut flex_row = fltk::group::Flex::default().size_of_parent();
        flex_row.set_type(fltk::group::FlexType::Row);

        let mut menu = fltk::menu::SysMenuBar::default();
        menu.add_emit(
            "&Schemas/Use builtin schemas for CS1 (XSeed PC version, English)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SetBuiltinVersion(BuiltinSchemaVersion::CS1En),
        );

        menu.add_emit(
            "&Schemas/Use builtin schemas for CS2 (XSeed PC version, English)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SetBuiltinVersion(BuiltinSchemaVersion::CS2En),
        );

        menu.add_emit(
            "&Schemas/Use builtin schemas for CS3 (NISA PC version)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SetBuiltinVersion(BuiltinSchemaVersion::CS3),
        );

        menu.add_emit(
            "&Schemas/Use builtin schemas for CS4 (NISA Switch version)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SetBuiltinVersion(BuiltinSchemaVersion::CS4),
        );

        menu.add_emit(
            "&Schemas/Use builtin schemas for Reverie (NISA PC version)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SetBuiltinVersion(BuiltinSchemaVersion::CS5),
        );

        menu.add_emit(
            "&Schemas/Export/Builtin schemas for CS1 (XSeed PC version, English)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ExportSchemas(BuiltinSchemaVersion::CS1En),
        );

        menu.add_emit(
            "&Schemas/Export/Builtin schemas for CS2 (XSeed PC version, English)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ExportSchemas(BuiltinSchemaVersion::CS2En),
        );

        menu.add_emit(
            "&Schemas/Export/Builtin schemas for CS3 (NISA PC version)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ExportSchemas(BuiltinSchemaVersion::CS3),
        );

        menu.add_emit(
            "&Schemas/Export/Builtin schemas for CS4 (NISA Switch version)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ExportSchemas(BuiltinSchemaVersion::CS4),
        );

        menu.add_emit(
            "&Schemas/Export/Builtin schemas for Reverie (NISA Switch version)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ExportSchemas(BuiltinSchemaVersion::CS5),
        );

        menu.add_emit(
            "&Schemas/Import/Schemas without entry types in header headers (CS1)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SetCustomVersion(CustomSchemaVersion::V1),
        );

        menu.add_emit(
            "&Schemas/Import/Schemas with entry types in header (CS2 onward)\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SetCustomVersion(CustomSchemaVersion::V2),
        );

        menu.add("&Schemas/Import/Recently used (CS1)\t", fltk::enums::Shortcut::None, fltk::menu::MenuFlag::Submenu, |_| {});

        menu.add("&Schemas/Import/Recently used (CS2 onward)\t", fltk::enums::Shortcut::None, fltk::menu::MenuFlag::Submenu, |_| {});

        menu.add_emit(
            "&File/Load tbl with current schema\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::LoadFile,
        );

        menu.add("&File/Load recently used tbl\t", fltk::enums::Shortcut::None, fltk::menu::MenuFlag::Submenu, |_| {});

        menu.add_emit("&File/Save\t", fltk::enums::Shortcut::Ctrl | 's', fltk::menu::MenuFlag::Normal, sender.clone(), Msg::SaveFile);

        menu.add_emit(
            "&File/Save as...\t",
            fltk::enums::Shortcut::Ctrl | fltk::enums::Shortcut::Shift | 's',
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SelectAndSaveFile,
        );

        menu.add_emit("&File/Show warnings...\t", fltk::enums::Shortcut::None, fltk::menu::MenuFlag::Normal, sender.clone(), Msg::ShowWarnings);

        menu.add_emit(
            "&Export/Export table to csv...\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ExportCSV,
        );

        menu.add_emit(
            "&Export/Export tbl to json...\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ExportJson,
        );

        menu.add_emit(
            "&Import/Import from csv...\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ImportCSV,
        );

        menu.add_emit(
            "&Import/Import tbl from json...\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::ImportJson,
        );

        menu.add_emit("&Edit/Undo\t", fltk::enums::Shortcut::Ctrl | 'u', fltk::menu::MenuFlag::Inactive, sender.clone(), Msg::Undo);

        menu.add_emit("&Edit/Redo\t", fltk::enums::Shortcut::Ctrl | 'r', fltk::menu::MenuFlag::Inactive, sender.clone(), Msg::Redo);

        menu.add_emit(
            "&Edit/Duplicate currently selected row\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::DuplicateEntry,
        );

        menu.add_emit(
            "&Edit/Delete currently selected row\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::DeleteEntry,
        );

        menu.add_emit(
            "&View/Fit cells to content\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::AutoSizeTable,
        );

        menu.add_emit(
            "&View/Select and reorder visible columns\t",
            fltk::enums::Shortcut::None,
            fltk::menu::MenuFlag::Normal,
            sender.clone(),
            Msg::SelectAttributes,
        );

        let mut header_selector = fltk::misc::InputChoice::default();
        header_selector.emit(sender.clone(), Msg::DifferentHeaderSelected);
        header_selector.input().set_readonly(true);
        header_selector.set_tooltip("Select header (entry type).");
        flex_row.fixed(&header_selector, 200);

        flex_row.end();
        flex.fixed(&flex_row, 26);
        let mut flex_row = fltk::group::Flex::default().size_of_parent();
        flex_row.set_type(fltk::group::FlexType::Row);

        let mut input = fltk::input::Input::default();
        input.set_trigger(fltk::enums::CallbackTrigger::EnterKeyAlways);
        input.emit(sender.clone(), Msg::DoneEditing);
        input.set_tooltip("Input field: Press enter to update the selected table cell.");

        let mut search = fltk::input::Input::default();
        search.handle({
            let sender = sender.clone();
            move |_, event| {
                if event == fltk::enums::Event::KeyUp {
                    sender.send(Msg::UpdateSearch); // We want search to update on every change
                }
                false // we haven't handled the event
            }
        });
        search.set_tooltip("Search filter: Rows which do not contain the search string in any (hidden or visible) cell will be hidden. Search is case-insensitive. Special characters :=>< cause different behaviour, see documentation for a full explanation.");

        flex_row.end();
        flex_row.fixed(&search, 200);
        flex.fixed(&flex_row, 26);

        let mut table = table_widget::FilteredTable::new();
        table.emit(sender.clone(), Msg::TableEvent);

        flex.end();

        win.make_resizable(true);
        win.end();
        win.show();

        let mut this = Self {
            app,
            sender,
            receiver,
            widgets: Widgets {
                menu,
                header_selector,
                input,
                search,
                table,
            },
            state,
        };

        this.update_menu_recent_schemas_v1();
        this.update_menu_recent_schemas_v2();
        this.update_menu_recent_tbls();
        this.update_after_file_reload();

        this
    }

    pub fn run(mut self) {
        while self.app.wait() {
            let msg = self.receiver.recv();
            if msg.is_some() {
                self.update(msg);
            }
        }
    }

    pub fn update(&mut self, msg: Option<Msg>) {
        log::debug!("update: {msg:?}");
        match msg {
            Some(Msg::LoadFile) => {
                if let Some(path) = misc::load_file_dialog("tbl") {
                    self.load_tbl_from_file(path);
                }
            }
            Some(Msg::LoadRecentTbl(i)) => self.load_tbl_from_file(self.state.recent_tbls.get(i).unwrap().to_owned()),
            Some(Msg::SetBuiltinVersion(v)) => {
                self.state.tbl_version = match v {
                    BuiltinSchemaVersion::CS1En => tocs::tbl::Version::CS1En,
                    BuiltinSchemaVersion::CS2En => tocs::tbl::Version::CS2En,
                    BuiltinSchemaVersion::CS3 => tocs::tbl::Version::CS3,
                    BuiltinSchemaVersion::CS4 => tocs::tbl::Version::CS4,
                    BuiltinSchemaVersion::CS5 => tocs::tbl::Version::CS5,
                };
                self.state.tbl_history.clear_all(); // we don't want a file incompatible with the new schema
                self.update_after_file_reload();
            }
            Some(Msg::SetCustomVersion(v)) => {
                let path = match v {
                    CustomSchemaVersion::V1 | CustomSchemaVersion::V2 => {
                        let Some(path) = misc::load_file_dialog("json") else { return };
                        path
                    }
                    CustomSchemaVersion::RecentV1(i) => self.state.recent_schemas_v1.get(i).unwrap().to_owned(),
                    CustomSchemaVersion::RecentV2(i) => self.state.recent_schemas_v2.get(i).unwrap().to_owned(),
                };
                let raw_schemas = match misc::import_schemas(&path) {
                    Ok(s) => s,
                    Err(err) => {
                        fltk::dialog::alert_default(&format!("error importing schema {path}: {err}"));
                        return;
                    }
                };
                match v {
                    CustomSchemaVersion::V1 | CustomSchemaVersion::RecentV1(_) => {
                        self.state.tbl_version = tocs::tbl::Version::V1(raw_schemas);
                        self.state.recent_schemas_v1.mark_as_updated(&path);
                        recent_files::save_recent_schemas_v1(&self.state.recent_schemas_v1);
                        self.update_menu_recent_schemas_v1();
                    }
                    CustomSchemaVersion::V2 | CustomSchemaVersion::RecentV2(_) => {
                        self.state.tbl_version = tocs::tbl::Version::V2(raw_schemas);
                        self.state.recent_schemas_v2.mark_as_updated(&path);
                        recent_files::save_recent_schemas_v2(&self.state.recent_schemas_v2);
                        self.update_menu_recent_schemas_v2();
                    }
                }
                self.state.tbl_history.clear_all();
                self.update_after_file_reload();
            }
            Some(Msg::ExportSchemas(v)) => {
                let (name, schemas): (&str, &tocs::tbl::Schemas) = match v {
                    BuiltinSchemaVersion::CS1En => ("cs1.json", &tocs::tbl::schemas::CS1),
                    BuiltinSchemaVersion::CS2En => ("cs2.json", &tocs::tbl::schemas::CS2),
                    BuiltinSchemaVersion::CS3 => ("cs3.json", &tocs::tbl::schemas::CS3),
                    BuiltinSchemaVersion::CS4 => ("cs4.json", &tocs::tbl::schemas::CS4),
                    BuiltinSchemaVersion::CS5 => ("cs5.json", &tocs::tbl::schemas::CS5),
                };
                let Some(path) = misc::save_file_dialog("json", name) else { return };
                match misc::export_schemas(&path, schemas) {
                    Ok(()) => match v {
                        BuiltinSchemaVersion::CS1En => {
                            self.state.recent_schemas_v1.mark_as_updated(&path);
                            recent_files::save_recent_schemas_v1(&self.state.recent_schemas_v1);
                            self.update_menu_recent_schemas_v1();
                        }
                        _ => {
                            self.state.recent_schemas_v2.mark_as_updated(&path);
                            recent_files::save_recent_schemas_v2(&self.state.recent_schemas_v2);
                            self.update_menu_recent_schemas_v2();
                        }
                    }, // ok
                    Err(e) => {
                        fltk::dialog::alert_default(&format!("error saving to {path}: {e:?}"));
                    }
                }
            }
            Some(Msg::SaveFile) => {
                self.save_tbl_to_file(None);
            }
            Some(Msg::SelectAndSaveFile) => {
                let Some(path) = misc::save_file_dialog("tbl", &format!("{}", self.state.path)) else {
                    return;
                };
                self.save_tbl_to_file(Some(path));
            }
            Some(Msg::ExportCSV) => {
                let AppData {
                    tbl_history,
                    tbl_version,
                    active_entry_type,
                    ..
                } = &mut self.state;
                if let Some(hdr) = active_entry_type.as_ref() {
                    let Some(path) = misc::save_file_dialog("csv", &format!("{}.csv", hdr)) else { return };
                    let entries: std::vec::Vec<_> = tbl_history.rows_for_header(hdr).map(|entry| &entry.values).cloned().collect();
                    // note: we're intentionally ignoring the choice and order of headers used for the table
                    match misc::export_csv(&path, misc::schema_for_header(hdr, tbl_version.schemas()), &entries) {
                        Ok(_) => {}
                        Err(e) => {
                            fltk::dialog::alert_default(&format!("error exporting to csv: {:?}", e));
                        }
                    }
                } else {
                    fltk::dialog::alert_default("no entry type selected, can't export");
                }
            }
            Some(Msg::ImportCSV) => {
                if let Some(hdr) = self.state.active_entry_type.as_ref().cloned() {
                    let Some(path) = misc::load_file_dialog("csv") else { return };
                    match misc::import_csv(&path, misc::schema_for_header(&hdr, self.state.tbl_version.schemas())) {
                        Ok(entries) => {
                            let rows = entries.into_iter().map(|values| std::rc::Rc::new(tocs::tbl::Entry { header: hdr.clone(), values })).collect();
                            self.state.tbl_history.change(Change::ImportTable { header: hdr.clone(), rows });
                        }
                        Err(e) => {
                            fltk::dialog::alert_default(&format!("error importing data from csv: {:?}", e));
                        }
                    }
                } else {
                    fltk::dialog::alert_default("no entry type selected, can't import");
                }
                self.update_table();
            }
            Some(Msg::ExportJson) => {
                let AppData { tbl_history, tbl_version, path, .. } = &mut self.state;
                let tbl = tocs::tbl::Tbl::new(tbl_version, tbl_history.headers().iter().cloned(), tbl_history.all_current_rows().cloned());
                let Some(path) = misc::save_file_dialog("json", path.with_extension("json").as_str()) else {
                    return;
                };
                match misc::export_json(&path, &tbl) {
                    Ok(()) => {}
                    Err(e) => {
                        fltk::dialog::alert_default(&format!("error exporting to csv: {:?}", e));
                    }
                }
            }
            Some(Msg::ImportJson) => {
                // open a file browser dialog to select the file
                let Some(import_path) = misc::load_file_dialog("json") else { return };
                // reset state related to tbl data
                let AppData {
                    tbl_history,
                    active_entry_type: _, // handled by update_after_file_reload
                    selected_attributes,
                    tbl_version,
                    path,
                    warnings,
                    recent_schemas_v1: _,
                    recent_schemas_v2: _,
                    recent_tbls: _,
                } = &mut self.state;
                tbl_history.clear_all();
                selected_attributes.clear();
                warnings.clear();
                path.clear();
                // try loading data from file
                match misc::import_json(&import_path, tbl_version.schemas()) {
                    Ok(tbl) => {
                        *tbl_history = TblStateHistory::new(tbl);
                    }
                    Err(e) => {
                        fltk::dialog::alert_default(&format!("error importing data: {}", e));
                    }
                }
                // we need to update other ui elements
                self.update_after_file_reload();
            }
            Some(Msg::DifferentHeaderSelected) => {
                self.update_table();
                self.widgets.table.auto_size_cells();
            }
            Some(Msg::DuplicateEntry) => {
                if let Some(row) = self.widgets.table.selected_row() {
                    let Some(hdr) = self.state.active_entry_type.as_ref() else { return };
                    self.state.tbl_history.change(Change::DuplicateRow { header: hdr.clone(), row });
                    self.update_table();
                } else {
                    fltk::dialog::alert_default("please select exactly one row");
                }
            }
            Some(Msg::DeleteEntry) => {
                if let Some(row) = self.widgets.table.selected_row() {
                    let Some(hdr) = self.state.active_entry_type.as_ref() else { return };
                    self.state.tbl_history.change(Change::RemoveRow { header: hdr.clone(), row });
                    self.update_table();
                } else {
                    fltk::dialog::alert_default("please select exactly one row");
                    // TODO: allow multiple
                }
            }
            Some(Msg::Undo) => {
                self.state.tbl_history.undo();
                self.update_table();
            }
            Some(Msg::Redo) => {
                self.state.tbl_history.redo();
                self.update_table();
            }
            Some(Msg::TableEvent) => {
                match fltk::app::event() {
                    fltk::enums::Event::Released if fltk::app::event_clicks() => {
                        log::debug!("double click on table");
                        if let Some((row, col)) = self.widgets.table.selected_cell() {
                            // copy table value to input
                            log::debug!("double click on cell");
                            let value = ecow::eco_format!("{}", &self.state.tbl_history.get_cell_contents(row, col).unwrap().1);
                            self.widgets.input.set_value(value.as_str());
                            self.widgets.input.take_focus().unwrap();
                            self.widgets.input.redraw();
                        }
                    }
                    _ => {}
                }
            }
            Some(Msg::DoneEditing) => {
                // find the currently selected cell (if it's exactly one)
                let input = &mut self.widgets.input;
                if !input.has_focus() {
                    return;
                };
                let table = &mut self.widgets.table;
                let Some((row, column)) = table.selected_cell() else { return };
                // copy text input to selected cell
                let text = input.value();
                let header = self.state.active_entry_type.clone().unwrap();
                let ty = misc::schema_for_header(&header, self.state.tbl_version.schemas()).get_index(column).unwrap().1;
                if let Ok(value) = tocs::tbl::AtomicValue::from_str_nonstrict(ty, &text) {
                    self.state.tbl_history.change(Change::UpdateCell { header, row, column, value });
                    self.update_table();
                } else {
                    fltk::dialog::alert_default(&format!("invalid input for cell, expected a {}", ty.describe()));
                }
            }
            Some(Msg::UpdateSearch) => {
                self.update_table();
            }
            Some(Msg::ShowWarnings) => {
                warn_diag::WarningDialog::new(self.state.warnings.iter());
            }
            Some(Msg::AutoSizeTable) => {
                self.widgets.table.auto_size_cells();
            }
            Some(Msg::SelectAttributes) => {
                // TODO: disable menu when no header is selected
                let Some(hdr) = self.state.active_entry_type.as_ref() else { return };
                let schema = misc::schema_for_header(hdr, self.state.tbl_version.schemas());
                let visible = self.state.selected_attributes.get_mut(hdr).expect("selected attributes should be in schema");
                if let Some(new_visible) = select_cols::select_cols(schema.keys().cloned().collect(), visible) {
                    *visible = new_visible;
                    self.update_table();
                    self.widgets.table.auto_size_cells();
                }
            }
            None => {}
        }
    }

    fn update_menu_recent_schemas_v1(&mut self) {
        let i = self.widgets.menu.find_index("&Schemas/Import/Recently used (CS1)\t");
        self.widgets.menu.clear_submenu(i).unwrap();
        let mut recent_menu = self.widgets.menu.at(i).unwrap();
        if self.state.recent_schemas_v1.is_empty() {
            recent_menu.deactivate();
        } else {
            recent_menu.activate();
        }
        drop(recent_menu);
        for (i, path) in self.state.recent_schemas_v1.iter().enumerate() {
            self.widgets.menu.add_emit(
                &format!("&Schemas/Import/Recently used (CS1)\t/{}", misc::QuotedFltkMenuLabel::new(path.as_str())),
                fltk::enums::Shortcut::None,
                fltk::menu::MenuFlag::Normal,
                self.sender.clone(),
                Msg::SetCustomVersion(CustomSchemaVersion::RecentV1(i)),
            );
        }
    }

    fn update_menu_recent_schemas_v2(&mut self) {
        let i = self.widgets.menu.find_index("&Schemas/Import/Recently used (CS2 onward)\t");
        self.widgets.menu.clear_submenu(i).unwrap();
        let mut recent_menu = self.widgets.menu.at(i).unwrap();
        if self.state.recent_schemas_v2.is_empty() {
            recent_menu.deactivate();
        } else {
            recent_menu.activate();
        }
        drop(recent_menu);
        for (i, path) in self.state.recent_schemas_v2.iter().enumerate() {
            self.widgets.menu.add_emit(
                &format!("&Schemas/Import/Recently used (CS2 onward)\t/{}", misc::QuotedFltkMenuLabel::new(path.as_str())),
                fltk::enums::Shortcut::None,
                fltk::menu::MenuFlag::Normal,
                self.sender.clone(),
                Msg::SetCustomVersion(CustomSchemaVersion::RecentV2(i)),
            );
        }
    }

    fn update_menu_recent_tbls(&mut self) {
        let i = self.widgets.menu.find_index("&File/Load recently used tbl\t");
        self.widgets.menu.clear_submenu(i).unwrap();
        let mut recent_menu = self.widgets.menu.at(i).unwrap();
        if self.state.recent_tbls.is_empty() {
            recent_menu.deactivate();
        } else {
            recent_menu.activate();
        }
        drop(recent_menu);
        for (i, path) in self.state.recent_tbls.iter().enumerate() {
            self.widgets.menu.add_emit(
                &format!("&File/Load recently used tbl\t/{}", misc::QuotedFltkMenuLabel::new(path.as_str())),
                fltk::enums::Shortcut::None,
                fltk::menu::MenuFlag::Normal,
                self.sender.clone(),
                Msg::LoadRecentTbl(i),
            );
        }
    }

    fn update_table(&mut self) {
        // get active entry type from header selector
        self.state.active_entry_type = self.widgets.header_selector.value().and_then(|hdr| {
            let hdr = tocs::tbl::Header::new(&hdr).expect("it shouldn't be possible to set the selector to an invalid value");
            if self.state.tbl_history.has_header(&hdr) {
                Some(hdr)
            } else {
                None
            }
        });

        self.update_undo_redo_menu();

        // notify table widget about current data
        if let Some(header) = self.state.active_entry_type.as_ref() {
            let schema = misc::schema_for_header(header, self.state.tbl_version.schemas());
            let attributes = self
                .state
                .selected_attributes
                .get(header)
                .expect("attributes for every header should always be set in update_after_file_reload");
            let filters = [filter::RowFilter::from_str(&self.widgets.search.value(), schema)];
            let all_tbl_rows = self.state.tbl_history.all_current_rows();
            self.widgets.table.update_data(header.clone(), schema, attributes, &filters, all_tbl_rows)
        } else {
            self.widgets.table.clear();
        }
    }

    fn update_after_file_reload(&mut self) {
        let AppData {
            active_entry_type,
            selected_attributes,
            tbl_version,
            path: _,
            tbl_history,
            warnings,
            recent_schemas_v1: _,
            recent_schemas_v2: _,
            recent_tbls: _,
        } = &mut self.state;

        // if there are warnings, show button to display them
        let mut warning_menu = self.widgets.menu.find_item("&File/Show warnings...\t").unwrap();
        let show_warnings = if warnings.is_empty() {
            warning_menu.deactivate();
            false
        } else {
            warning_menu.activate();
            true
        };

        // reset header selector
        let previously_active_entry_type = active_entry_type.clone();
        self.widgets.header_selector.clear();
        *active_entry_type = None;
        for (i, hdr) in tbl_history.headers().iter().enumerate() {
            self.widgets.header_selector.add(hdr.as_str());
            if i == 0 || Some(hdr) == previously_active_entry_type.as_ref() {
                self.widgets.header_selector.set_value_index(i.try_into().unwrap());
            }
        }

        // by default, display every attribute
        selected_attributes.clear();
        for hdr in tbl_history.headers().iter() {
            let schema = misc::schema_for_header(hdr, tbl_version.schemas());
            selected_attributes.insert(hdr.clone(), schema.keys().cloned().collect());
        }

        self.update_table();

        self.widgets.table.auto_size_cells();

        if show_warnings {
            warn_diag::WarningDialog::new(self.state.warnings.iter());
        }
    }

    fn update_undo_redo_menu(&mut self) {
        let edit_menu = self.widgets.menu.find_item("&Edit").unwrap();
        assert!(edit_menu.is_submenu());
        let mut undo_item = edit_menu.at(1).unwrap();
        let mut redo_item = edit_menu.at(2).unwrap();
        assert!(undo_item.label().unwrap().starts_with("Undo"));
        assert!(redo_item.label().unwrap().starts_with("Redo"));

        if let Some(change) = self.state.tbl_history.next_undo() {
            undo_item.set_label(&format!("Undo: {}\t", change.describe()));
            undo_item.activate();
        } else {
            undo_item.set_label("Undo\t");
            undo_item.deactivate();
        }

        if let Some(change) = self.state.tbl_history.next_redo() {
            redo_item.set_label(&format!("Redo: {}\t", change.describe()));
            redo_item.activate();
        } else {
            redo_item.set_label("Redo\t");
            redo_item.deactivate();
        }
    }

    fn load_tbl_from_file(&mut self, new_path: Utf8PathBuf) {
        // reset state related to tbl data
        let AppData {
            tbl_history,
            active_entry_type: _, // handled by update_after_file_reload
            selected_attributes,
            tbl_version,
            path,
            warnings,
            recent_schemas_v1: _,
            recent_schemas_v2: _,
            recent_tbls: _,
        } = &mut self.state;
        tbl_history.clear_all();
        selected_attributes.clear();
        warnings.clear();
        // try loading data from file
        match tocs::tbl::read_file_with_fixes(&new_path, tbl_version) {
            tocs::tbl::ReadTblResult::Ok(tbl) | tocs::tbl::ReadTblResult::KnownFixApplied(tbl) => {
                *tbl_history = TblStateHistory::new(tbl);
                *path = new_path;
            }
            tocs::tbl::ReadTblResult::Warn { tbl, warnings: w } => {
                *warnings = w;
                *tbl_history = TblStateHistory::new(tbl);
                *path = new_path;
                // if errors are harmless (e.g. all length bytes & all headers are in the schema), don't alert
                let schemas = tbl_version.schemas();
                if tbl_history.all_current_rows().all(|entry| schemas.contains_key(&entry.header))
                    && warnings.iter().all(|w| matches!(w.detail, tocs::tbl::DeserializeErrorDetail::EntryDataLengthMismatch { .. }))
                {
                    // it's fine
                } else {
                    fltk::dialog::alert_default("Warning: Some errors/inconsistencies in the file were detected. Please look at them under the Warnings menu option and ensure that they are harmless");
                }
            }
            tocs::tbl::ReadTblResult::Err(e) => {
                fltk::dialog::alert_default(&format!("error loading file: {}", e));
                *path = Utf8PathBuf::new();
                return;
            }
        }
        self.state.recent_tbls.mark_as_updated(&self.state.path);
        recent_files::save_recent_tbls(&self.state.recent_tbls);
        self.update_after_file_reload();
        self.update_menu_recent_tbls();
    }

    fn save_tbl_to_file(&mut self, path: Option<Utf8PathBuf>) {
        // try saving data to file
        let version = &self.state.tbl_version;
        let headers = self.state.tbl_history.headers().iter().cloned();
        let rows = self.state.tbl_history.all_current_rows().cloned();
        let tbl = tocs::tbl::Tbl::new(version, headers, rows);
        let new_path = path.as_ref().unwrap_or(&self.state.path);
        let result = tocs::tbl::write_file(new_path, &tbl);
        match result {
            Ok(_) => {
                if let Some(new_path) = path {
                    self.state.path = new_path;
                    self.state.recent_tbls.mark_as_updated(&self.state.path);
                    self.update_menu_recent_tbls();
                    recent_files::save_recent_tbls(&self.state.recent_tbls);
                }
            }
            Err(e) => {
                fltk::dialog::alert_default(&format!("error saving to {}: {:?}", new_path, e));
            }
        }
    }
}

fn main() {
    simple_logger::SimpleLogger::new().with_level(log::LevelFilter::Info).env().init().unwrap();

    let CliArgs { game_version, file } = CliArgs::parse();

    let appstate = match (game_version, file) {
        (Some(version), Some(path)) => {
            log::debug!("parsing {} as a {:?} file", path, version);
            // try loading data from file
            match tocs::tbl::read_file_with_fixes(&path, &version) {
                tocs::tbl::ReadTblResult::Ok(tbl) | tocs::tbl::ReadTblResult::KnownFixApplied(tbl) => AppData {
                    tbl_history: TblStateHistory::new(tbl),
                    active_entry_type: None,
                    selected_attributes: version.schemas().iter().map(|(hdr, schema)| (hdr.clone(), schema.keys().cloned().collect())).collect(),
                    tbl_version: version,
                    path,
                    warnings: vec![],
                    recent_schemas_v1: recent_files::load_recent_schemas_v1(),
                    recent_schemas_v2: recent_files::load_recent_schemas_v2(),
                    recent_tbls: recent_files::load_recent_tbls(),
                },
                tocs::tbl::ReadTblResult::Warn { tbl, warnings } => AppData {
                    tbl_history: TblStateHistory::new(tbl),
                    active_entry_type: None,
                    selected_attributes: version.schemas().iter().map(|(hdr, schema)| (hdr.clone(), schema.keys().cloned().collect())).collect(),
                    tbl_version: version,
                    path,
                    warnings,
                    recent_schemas_v1: recent_files::load_recent_schemas_v1(),
                    recent_schemas_v2: recent_files::load_recent_schemas_v2(),
                    recent_tbls: recent_files::load_recent_tbls(),
                },
                tocs::tbl::ReadTblResult::Err(e) => {
                    log::error!("Unable to read file: {e}");
                    return;
                }
            }
        }
        (Some(_), None) | (None, Some(_)) => {
            log::error!("game version and file must be given both (or neither)");
            return;
        }
        (None, None) => AppData {
            tbl_history: TblStateHistory::default(),
            active_entry_type: None,
            selected_attributes: [].into_iter().collect(),
            tbl_version: tocs::tbl::Version::CS1En,
            path: Utf8PathBuf::new(),
            warnings: vec![],
            recent_schemas_v1: recent_files::load_recent_schemas_v1(),
            recent_schemas_v2: recent_files::load_recent_schemas_v2(),
            recent_tbls: recent_files::load_recent_tbls(),
        },
    };

    log::debug!("starting app");
    let app = App::new(appstate);
    app.run();
}