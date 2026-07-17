//! CLI tool for batch .eff file analysis.
//! Usage: eff-cli analyze <directory>
//!        eff-cli dump <file.eff>
//!        eff-cli compare <file1.eff> <file2.eff>

use std::fs;
use std::path::Path;
use clap::{Parser, Subcommand};

use eff_core::analysis::EffCorpus;

#[derive(Parser)]
#[command(name = "eff-cli")]
#[command(about = "Trails of Cold Steel .eff file analyzer", long_about = None)]
struct Cli {
    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand)]
enum Commands {
    /// Batch analyze all .eff files in a directory
    Analyze {
        /// Directory containing .eff files (recursive)
        directory: String,
        /// Optional: only parse, don't print full report
        #[arg(short, long)]
        quiet: bool,
    },
    /// Dump a single .eff file as JSON
    Dump {
        /// Path to .eff file
        file: String,
    },
    /// Compare two .eff files
    Compare {
        file1: String,
        file2: String,
    },
    /// Round-trip test: parse → write → re-parse, compare
    Roundtrip {
        /// Path to .eff file
        file: String,
    },
    /// Correlate unnamed fields across all .eff files
    Correlate {
        /// Directory containing .eff files (recursive)
        directory: String,
    },
    /// Round-trip every .eff in a directory, print a summary of divergences
    RoundtripAll {
        /// Directory containing .eff files (recursive)
        directory: String,
    },
    /// Diagnose a texture .pkg: report format/dims or where decoding fails
    Tex {
        /// Path to a I_EFTEXxxx.pkg file
        file: String,
    },
}

fn main() {
    let cli = Cli::parse();

    match cli.command {
        Commands::Analyze { directory, quiet } => {
            cmd_analyze(&directory, quiet);
        }
        Commands::Dump { file } => {
            cmd_dump(&file);
        }
        Commands::Compare { file1, file2 } => {
            cmd_compare(&file1, &file2);
        }
        Commands::Roundtrip { file } => {
            cmd_roundtrip(&file);
        }
        Commands::Correlate { directory } => {
            cmd_correlate(&directory);
        }
        Commands::Tex { file } => {
            cmd_tex(&file);
        }
        Commands::RoundtripAll { directory } => {
            cmd_roundtrip_all(&directory);
        }
    }
}

fn cmd_roundtrip_all(dir: &str) {
    use eff_core::core::{parse_eff_bytes, write_eff_to_bytes};
    let files = find_eff_files(dir);
    // Classify each file: perfect | benign (byte-diff but JSON same) | dataloss
    // (JSON differs) | sizediff | fail.
    let (mut perfect, mut benign, mut dataloss, mut sizediff, mut fail) = (0u32, 0u32, 0u32, 0u32, 0u32);
    let mut loss_field_hits: std::collections::BTreeMap<String, u32> = std::collections::BTreeMap::new();
    let mut loss_examples: Vec<String> = Vec::new();
    let mut ver_of_loss: std::collections::BTreeMap<String, u32> = std::collections::BTreeMap::new();
    for f in &files {
        let orig = match fs::read(f) { Ok(d) => d, Err(_) => { fail += 1; continue; } };
        let eff = match parse_eff_bytes(&orig) { Ok(e) => e, Err(_) => { fail += 1; continue; } };
        let written = match write_eff_to_bytes(&eff) { Ok(w) => w, Err(_) => { fail += 1; continue; } };
        if written.len() != orig.len() { sizediff += 1; continue; }
        if orig == written { perfect += 1; continue; }
        // Byte-diff: does the parsed model round-trip (JSON equal)?
        let re = match parse_eff_bytes(&written) { Ok(e) => e, Err(_) => { dataloss += 1; continue; } };
        let ja = serde_json::to_value(&eff).unwrap();
        let jb = serde_json::to_value(&re).unwrap();
        if ja == jb { benign += 1; }
        else {
            dataloss += 1;
            let vk = format!("v0x{:x}", eff.version.as_raw());
            *ver_of_loss.entry(vk).or_insert(0) += 1;
            record_diff_paths(&ja, &jb, "", &mut loss_field_hits);
            if loss_examples.len() < 15 {
                let name = f.rsplit(['/', '\\']).next().unwrap_or(f).to_string();
                loss_examples.push(name);
            }
        }
    }
    println!("TOTAL={} | byte-perfect={} | benign(json-same)={} | DATA-LOSS(json-differs)={} | size-diff={} | fail={}",
        files.len(), perfect, benign, dataloss, sizediff, fail);
    println!("--- data-loss by version: {:?}", ver_of_loss);
    println!("--- data-loss example files: {:?}", loss_examples);
    if !loss_field_hits.is_empty() {
        println!("--- data-loss JSON paths (field where values differ):");
        let mut v: Vec<_> = loss_field_hits.iter().collect();
        v.sort_by_key(|(_, c)| std::cmp::Reverse(**c));
        for (k, c) in v.iter().take(30) { println!("  {k}: {c} files"); }
    }
}

/// Record the field paths (like segments/data_XX) where two JSON values differ.
fn record_diff_paths(a: &serde_json::Value, b: &serde_json::Value, path: &str,
                     hits: &mut std::collections::BTreeMap<String, u32>) {
    use serde_json::Value;
    match (a, b) {
        (Value::Object(oa), Value::Object(ob)) => {
            for (k, va) in oa {
                if let Some(vb) = ob.get(k) {
                    if va != vb {
                        let p = if path.is_empty() { k.clone() } else { format!("{path}.{k}") };
                        // Count at the data_XX level (strip array indices already gone).
                        record_diff_paths(va, vb, &p, hits);
                    }
                }
            }
        }
        (Value::Array(aa), Value::Array(ba)) => {
            for (va, vb) in aa.iter().zip(ba.iter()) {
                if va != vb { record_diff_paths(va, vb, path, hits); }
            }
        }
        _ => { *hits.entry(path.to_string()).or_insert(0) += 1; }
    }
}

fn cmd_tex(file: &str) {
    let data = match std::fs::read(file) {
        Ok(d) => d,
        Err(e) => { println!("read error: {}", e); return; }
    };
    println!("pkg: {} ({} bytes)", file, data.len());
    let pkg = match eff_core::core::PkgArchive::parse(&data) {
        Some(p) => p,
        None => { println!("  FAIL: PkgArchive::parse returned None"); return; }
    };
    let tex_entry = match pkg.find_texture() {
        Some(e) => e,
        None => { println!("  FAIL: find_texture found no texture entry"); return; }
    };
    println!("  entry: {}", tex_entry.name);
    let decompressed = match pkg.extract(&tex_entry.name) {
        Some(d) => d,
        None => { println!("  FAIL: extract/decompress returned None"); return; }
    };
    println!("  decompressed: {} bytes", decompressed.len());
    // Which format strings are present in the phyre blob?
    let mut found = Vec::new();
    for s in [b"ARGB8".as_slice(), b"RGBA8".as_slice(), b"DXT1".as_slice(), b"DXT3".as_slice(),
              b"DXT5".as_slice(), b"BC1".as_slice(), b"BC2".as_slice(), b"BC3".as_slice(),
              b"BC4".as_slice(), b"BC5".as_slice(), b"BC6".as_slice(), b"BC7".as_slice(),
              b"A8".as_slice(), b"L8".as_slice()] {
        if decompressed.windows(s.len()).any(|w| w == s) {
            found.push(String::from_utf8_lossy(s).to_string());
        }
    }
    println!("  format strings present: {:?}", found);
    match eff_core::core::parse_phyre_texture(&decompressed) {
        Some(t) => println!("  OK: {}x{} format={:?} mips={}", t.width, t.height, t.format, t.mip_levels),
        None => println!("  FAIL: parse_phyre_texture returned None (unsupported format or size mismatch)"),
    }
}

fn find_eff_files(dir: &str) -> Vec<String> {
    let mut files = Vec::new();
    let path = Path::new(dir);
    if path.is_dir() {
        walk_dir(path, &mut files);
    }
    files.sort();
    files
}

fn walk_dir(dir: &Path, files: &mut Vec<String>) {
    if let Ok(entries) = fs::read_dir(dir) {
        for entry in entries.flatten() {
            let p = entry.path();
            if p.is_dir() {
                walk_dir(&p, files);
            } else if p.extension().map(|e| e == "eff").unwrap_or(false) {
                files.push(p.to_string_lossy().to_string());
            }
        }
    }
}

fn cmd_analyze(dir: &str, quiet: bool) {
    let eff_files = find_eff_files(dir);
    println!("Found {} .eff files in {}", eff_files.len(), dir);

    let mut corpus = EffCorpus::new();
    let mut count = 0;

    for fp in &eff_files {
        match fs::read(fp) {
            Ok(data) => {
                // Use relative path as key to avoid collisions
                let rel = fp.strip_prefix(dir)
                    .unwrap_or(fp)
                    .to_string();
                corpus.add(&rel, &data);
                count += 1;
                if count % 50 == 0 && !quiet {
                    println!("  Parsed {}/{}...", count, eff_files.len());
                }
            }
            Err(e) => {
                eprintln!("  Failed to read {}: {}", fp, e);
            }
        }
    }

    if !quiet {
        corpus.print_report();
    }
    println!("\nDone. Parsed {} files, {} errors.",
        corpus.files.len(), corpus.errors.len());
}

fn cmd_dump(file: &str) {
    let data = fs::read(file).expect("Failed to read file");
    let eff = eff_core::core::parse_eff_bytes(&data).expect("Failed to parse");
    let json = serde_json::to_string_pretty(&eff).expect("Failed to serialize");
    println!("{}", json);
}

fn cmd_compare(file1: &str, file2: &str) {
    let d1 = fs::read(file1).expect("Failed to read file1");
    let d2 = fs::read(file2).expect("Failed to read file2");
    let e1 = eff_core::core::parse_eff_bytes(&d1).expect("Failed to parse file1");
    let e2 = eff_core::core::parse_eff_bytes(&d2).expect("Failed to parse file2");

    println!("Comparing: {}", Path::new(file1).file_name().unwrap().to_string_lossy());
    println!("       vs: {}", Path::new(file2).file_name().unwrap().to_string_lossy());

    // Compare segments
    for (i, (s1, s2)) in e1.segments.iter().zip(e2.segments.iter()).enumerate() {
        if s1.name != s2.name {
            println!("  seg[{}] name: '{}' vs '{}'", i, s1.name, s2.name);
        }
        // data_02
        for (j, (a, b)) in s1.data_02.iter().zip(s2.data_02.iter()).enumerate() {
            if a != b {
                println!("  seg[{}] '{}' d02[{}]: {} -> {}", i, s1.name, j, a, b);
            }
        }
        // data_04
        for (j, (a, b)) in s1.data_04.iter().zip(s2.data_04.iter()).enumerate() {
            if (a - b).abs() > 0.0001 {
                println!("  seg[{}] '{}' d04[{}]: {:.4} -> {:.4}", i, s1.name, j, a, b);
            }
        }
        // data_06
        for (j, (a, b)) in s1.data_06.iter().zip(s2.data_06.iter()).enumerate() {
            if (a - b).abs() > 0.0001 {
                println!("  seg[{}] '{}' d06[{}]: {:.4} -> {:.4}", i, s1.name, j, a, b);
            }
        }
        // data_08
        for (j, (a, b)) in s1.data_08.iter().zip(s2.data_08.iter()).enumerate() {
            if (a - b).abs() > 0.0001 {
                println!("  seg[{}] '{}' d08[{}]: {:.4} -> {:.4}", i, s1.name, j, a, b);
            }
        }
        // Array counts
        if s1.data_09.len() != s2.data_09.len() {
            println!("  seg[{}] '{}' d09 count: {} -> {}", i, s1.name, s1.data_09.len(), s2.data_09.len());
        }
        if s1.data_0b.len() != s2.data_0b.len() {
            println!("  seg[{}] '{}' d0B count: {} -> {}", i, s1.name, s1.data_0b.len(), s2.data_0b.len());
        }
    }
}

fn cmd_roundtrip(file: &str) {
    use eff_core::core::write_eff_to_bytes;

    let orig_data = fs::read(file).expect("Failed to read file");
    let orig_size = orig_data.len();

    let eff = eff_core::core::parse_eff_bytes(&orig_data).expect("Parse failed");
    let written = write_eff_to_bytes(&eff).expect("Write failed");
    let written_size = written.len();

    let re_parsed = eff_core::core::parse_eff_bytes(&written).expect("Re-parse failed");

    // Compare JSON
    let json_orig = serde_json::to_value(&eff).unwrap();
    let json_round = serde_json::to_value(&re_parsed).unwrap();

    println!("Original size: {} bytes", orig_size);
    println!("Written size:  {} bytes", written_size);

    if orig_size == written_size {
        println!("Size match!");

        let mut diffs = 0;
        for (i, (a, b)) in orig_data.iter().zip(written.iter()).enumerate() {
            if a != b {
                if diffs < 10 {
                    println!("  byte [{}]: 0x{:02X} -> 0x{:02X}", i, a, b);
                }
                diffs += 1;
            }
        }
        if diffs == 0 {
            println!("Byte-perfect round-trip!");
        } else {
            println!("{} byte differences", diffs);
        }
    } else {
        println!("Size differs by {} bytes", written_size as i64 - orig_size as i64);

        // Show where they diverge
        let min_len = orig_size.min(written_size);
        let mut first_diff = None;
        let mut diff_count = 0;
        for i in 0..min_len {
            let a = orig_data[i];
            let b = if i < written_size { written[i] } else { continue; };
            if a != b {
                if first_diff.is_none() { first_diff = Some(i); }
                if diff_count < 20 {
                    println!("  byte [{}]: 0x{:02X} -> 0x{:02X}", i, a, b);
                }
                diff_count += 1;
            }
        }
        if diff_count == 0 {
            println!("(first {} bytes identical, difference is in trailing bytes)", min_len);
            if orig_size > written_size {
                println!("Original has {} extra bytes at end:", orig_size - written_size);
                for i in written_size..orig_size {
                    println!("  byte [{}]: 0x{:02X}", i, orig_data[i]);
                }
            }
        } else {
            println!("({} byte differences total, first at offset {})", diff_count, first_diff.unwrap_or(0));
        }
    }

    if json_orig == json_round {
        println!("JSON identical after round-trip");
    } else {
        println!("JSON differs after round-trip");
    }
}

fn cmd_correlate(dir: &str) {
    use std::collections::{HashMap, HashSet};
    let eff_files = find_eff_files(dir);
    println!("Found {} .eff files", eff_files.len());

    // seg_name -> field -> set of values
    let mut seg_fields: HashMap<String, HashMap<String, HashSet<String>>> = HashMap::new();
    // seg_name -> field -> (sum, count, min, max)
    let mut seg_stats: HashMap<String, HashMap<String, (f64, usize, f64, f64)>> = HashMap::new();

    let mut count = 0;
    for fp in &eff_files {
        if let Ok(data) = fs::read(fp) {
            if let Ok(eff) = eff_core::core::parse_eff_bytes(&data) {
                for seg in &eff.segments {
                    let fields = seg_fields.entry(seg.name.clone()).or_default();
                    let stats = seg_stats.entry(seg.name.clone()).or_default();

                    for (i, &v) in seg.data_04.iter().enumerate() {
                        let key = format!("d04[{}]", i);
                        fields.entry(key.clone()).or_default().insert(format!("{:.4}", v));
                        let s = stats.entry(key).or_insert((0.0, 0, f64::MAX, f64::MIN));
                        s.0 += v as f64; s.1 += 1; s.2 = s.2.min(v as f64); s.3 = s.3.max(v as f64);
                    }
                    for (i, &v) in seg.data_02.iter().enumerate() {
                        let key = format!("d02[{}]", i);
                        fields.entry(key.clone()).or_default().insert(v.to_string());
                        let s = stats.entry(key).or_insert((0.0, 0, f64::MAX, f64::MIN));
                        s.0 += v as f64; s.1 += 1; s.2 = s.2.min(v as f64); s.3 = s.3.max(v as f64);
                    }
                    for (i, &v) in seg.data_06.iter().enumerate() {
                        let key = format!("d06[{}]", i);
                        fields.entry(key.clone()).or_default().insert(format!("{:.4}", v));
                        let s = stats.entry(key).or_insert((0.0, 0, f64::MAX, f64::MIN));
                        s.0 += v as f64; s.1 += 1; s.2 = s.2.min(v as f64); s.3 = s.3.max(v as f64);
                    }
                    for (i, &v) in seg.data_08.iter().enumerate() {
                        let key = format!("d08[{}]", i);
                        let s = stats.entry(key).or_insert((0.0, 0, f64::MAX, f64::MIN));
                        s.0 += v as f64; s.1 += 1; s.2 = s.2.min(v as f64); s.3 = s.3.max(v as f64);
                    }
                }
                count += 1;
                if count % 100 == 0 { println!("  {} / {} ...", count, eff_files.len()); }
            }
        }
    }

    println!("\n=== FIELD CORRELATION BY SEGMENT TYPE ===\n");
    let mut seg_names: Vec<_> = seg_fields.keys().collect();
    seg_names.sort_by_key(|n| -(seg_fields[*n].len() as i32));

    for seg_name in seg_names.iter().take(30) {
        let fields = &seg_fields[*seg_name];
        let stats = &seg_stats[*seg_name];
        println!("\n--- {} ({} distinct fields) ---", seg_name, fields.len());

        // Low cardinality (2-10 values) = likely enums, flags, indices
        let mut low: Vec<_> = fields.iter().filter(|(_, vs)| vs.len() >= 2 && vs.len() <= 10).collect();
        low.sort_by_key(|(_, vs)| vs.len());
        for (field, values) in low.iter().take(12) {
            let vlist: Vec<_> = values.iter().take(8).collect();
            let s = stats.get(*field).map(|s| format!("  mean={:.2} range=[{:.2},{:.2}]", s.0/s.1 as f64, s.2, s.3)).unwrap_or_default();
            println!("  {} : {} vals -> {:?} {}", field, values.len(), vlist, s);
        }

        // High cardinality = continuous values, show range
        let mut wide: Vec<_> = fields.iter().filter(|(_, vs)| vs.len() > 20).collect();
        if !wide.is_empty() {
            for (field, _) in wide.iter().take(8) {
                let s = stats.get(*field).map(|s| format!("mean={:.4} range=[{:.4},{:.4}]", s.0/s.1 as f64, s.2, s.3)).unwrap_or_default();
                println!("    {} : {} vals | {}", field, fields[*field].len(), s);
            }
        }
    }
    println!("\nDone. {} files.", count);
}
