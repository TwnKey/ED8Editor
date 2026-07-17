// miscellaneous utility functions

use tocs::tbl::AtomicAttributeValues as AttributeValues;
use tocs::tbl::AtomicSchema as Schema;
use tocs::tbl::Header;

use camino::{Utf8Path, Utf8PathBuf};

pub fn load_file_dialog(filetype: &str) -> Option<Utf8PathBuf> {
    // open a file browser dialog to select the file
    let mut diag = fltk::dialog::FileDialog::new(fltk::dialog::FileDialogType::BrowseFile);
    diag.set_title(&format!("Select .{} file to load ...", filetype));
    diag.show();
    let path = diag.filename();
    if path.as_os_str().is_empty() {
        None
    } else {
        path.try_into().ok()
    }
}

pub fn save_file_dialog(filetype: &str, preset: &str) -> Option<Utf8PathBuf> {
    // open a file browser dialog to select the file
    let mut diag = fltk::dialog::FileDialog::new(fltk::dialog::FileDialogType::BrowseSaveFile);
    diag.set_title(&format!("Select .{} file to save to ...", filetype));
    diag.set_preset_file(preset);
    diag.show();
    let path = diag.filename();
    if path.as_os_str().is_empty() {
        None
    } else {
        path.try_into().ok()
    }
}

pub fn export_csv(path: &Utf8Path, schema: &Schema, data: &[AttributeValues]) -> std::io::Result<()> {
    log::debug!("export data as CSV to '{}'", path);
    let mut csv = csv::WriterBuilder::new().quote_style(csv::QuoteStyle::Always).from_path(path)?;

    log::debug!("writing csv header");
    for attr in schema.keys() {
        csv.write_field(attr.as_bytes())?;
    }
    // end of record
    let eor: [&str; 0] = [];
    csv.write_record(eor)?;

    log::debug!("writing csv records");
    for entry in data {
        // one field for each attribute value
        for val in entry.values() {
            csv.write_field(val.to_string().as_bytes())?;
        }
        // end of record
        let eor: [&str; 0] = [];
        csv.write_record(eor)?;
    }
    Ok(())
}

pub fn export_json(path: &Utf8Path, tbl: &tocs::tbl::Tbl) -> std::io::Result<()> {
    log::debug!("export tbl to '{}'", path);
    let data = tocs::tbl::tbl_to_json(tbl);
    std::fs::write(path, data.as_bytes())
}

pub fn import_json(path: &Utf8Path, schemas: &tocs::tbl::AtomicSchemas) -> std::io::Result<tocs::tbl::Tbl> {
    let data = std::fs::read(path)?;
    tocs::tbl::try_tbl_from_json(schemas, data.as_slice()).map_err(|err| std::io::Error::new(std::io::ErrorKind::Other, err))
}

pub fn import_csv(path: &Utf8Path, schema: &Schema) -> Result<std::vec::Vec<AttributeValues>, String> {
    let mut csv = csv::ReaderBuilder::new().has_headers(true).from_path(path).map_err(|e| e.to_string())?;

    // read headers and compare to the expected schema
    // note that the order of columns does not matter
    let expected_headers: std::collections::BTreeSet<_> = schema.keys().map(|s| s.as_ref()).collect();
    let csv_headers = csv.headers().map_err(|e| e.to_string())?;
    let actual_headers: std::collections::BTreeSet<_> = csv_headers.iter().collect();
    if let Some(h) = expected_headers.difference(&actual_headers).next() {
        return Err(format!("expected header '{}' is missing from CSV", h));
    }
    if let Some(h) = actual_headers.difference(&expected_headers).next() {
        return Err(format!("unexpected header '{}' present in CSV", h));
    }
    if actual_headers.len() != csv_headers.len() {
        return Err("CSV file has duplicate column header".to_string()); // TODO: should probably list which ones they are
    }
    let csv_column_index: std::collections::HashMap<_, usize> = csv_headers
        .iter()
        .enumerate()
        .map(|(i, s)| {
            (
                // we need to drop csv_headers, so we reference the schema string instead of the csv header one
                schema.get_key_value(s).expect("csv headers should equal schema headers").0,
                i,
            )
        })
        .collect();
    drop(expected_headers);

    // read rows
    let mut result = vec![];
    for row in csv.records() {
        let row = row.map_err(|e| format!("error reading row: {}", e))?;
        if row.len() != schema.len() {
            return Err(format!("row has {} entries instead of the expected {}", row.len(), schema.len()));
        }
        let mut entry = AttributeValues::new();
        for (name, r#type) in schema.iter() {
            let s = row
                .get(*csv_column_index.get(name).expect("csv headers should equal schema attributes"))
                .expect("number of columns should match");
            let v = tocs::tbl::AtomicValue::from_str_nonstrict(r#type, s).map_err(|_| format!("invalid value '{}' for attribute '{}' (expected {})", s, name, r#type.describe()))?;
            entry.insert(name.clone(), v);
        }
        result.push(entry);
    }

    Ok(result)
}

pub fn export_schemas(path: &Utf8Path, schemas: &tocs::tbl::Schemas) -> Result<(), std::io::Error> {
    std::fs::write(path, tocs::tbl::schemas_to_json(schemas).as_bytes())
}

pub fn import_schemas(path: &Utf8Path) -> Result<tocs::tbl::AtomicSchemas, std::io::Error> {
    let data = std::fs::read(path)?;
    let schemas: tocs::tbl::Schemas = tocs::tbl::try_schemas_from_json(data.as_slice()).map_err(|err| std::io::Error::new(std::io::ErrorKind::Other, err))?;
    let atomic_schemas = tocs::tbl::get_nf1_for_all(&schemas).map_err(|err| std::io::Error::new(std::io::ErrorKind::Other, err))?;
    Ok(atomic_schemas)
}

pub fn schema_for_header<'a>(header: &Header, schemas: &'a indexmap::IndexMap<Header, Schema>) -> &'a Schema {
    lazy_static::lazy_static! {
        pub static ref GENERIC : Schema = [
            ("data".into(), tocs::tbl::AtomicType::X(0))
        ].into_iter().collect();
    };
    schemas.get(header).unwrap_or(&GENERIC)
}

pub struct QuotedFltkMenuLabel<'a> {
    raw: &'a str,
}

impl<'a> QuotedFltkMenuLabel<'a> {
    pub fn new(s: &'a str) -> Self {
        Self { raw: s }
    }
}

impl<'a> std::fmt::Display for QuotedFltkMenuLabel<'a> {
    fn fmt(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
        use std::fmt::Write as _;

        const SPECIAL: [char; 4] = ['\\', '/', '&', '_'];
        for c in self.raw.chars() {
            if SPECIAL.contains(&c) {
                f.write_char('\\')?;
            }
            f.write_char(c)?;
        }
        Ok(())
    }
}