//! Batch analysis: parse all .eff files and compute correlations.

use std::collections::{HashMap, BTreeMap, HashSet};

use crate::core::types::*;
use crate::core::parser::parse_eff_bytes;

/// Statistics gathered from analyzing a set of .eff files.
#[derive(Debug, Default)]
pub struct EffCorpus {
    /// filename -> parsed EffFile
    pub files: BTreeMap<String, EffFile>,
    /// Count of files per version
    pub version_counts: HashMap<String, usize>,
    /// Count of files per segment count
    pub segment_count_dist: HashMap<usize, usize>,
    /// Count per segment name across all files
    pub segment_name_counts: HashMap<String, usize>,
    /// Count per function name
    pub fn_name_counts: HashMap<String, usize>,
    /// Count per texture reference
    pub texture_counts: HashMap<String, usize>,
    /// Segment name -> set of struct_flags values seen
    pub segment_flags_variants: HashMap<String, HashSet<u32>>,
    /// Segment name -> field -> set of (nonzero?) values
    pub field_value_sets: HashMap<String, HashMap<String, HashSet<String>>>,
    /// Parse errors
    pub errors: Vec<(String, String)>,
}

impl EffCorpus {
    pub fn new() -> Self {
        Self::default()
    }

    /// Add a single .eff file to the corpus (from bytes + filename).
    pub fn add(&mut self, name: &str, data: &[u8]) {
        match parse_eff_bytes(data) {
            Ok(eff) => {
                // Version
                let ver_label = eff.version.label().to_string();
                *self.version_counts.entry(ver_label).or_insert(0) += 1;

                // Segment count distribution
                *self.segment_count_dist.entry(eff.segments.len()).or_insert(0) += 1;

                // Segment names & struct_flags
                for seg in &eff.segments {
                    *self.segment_name_counts.entry(seg.name.clone()).or_insert(0) += 1;
                    self.segment_flags_variants
                        .entry(seg.name.clone())
                        .or_default()
                        .insert(seg.struct_flags);

                    if !seg.fn_name_1.is_empty() {
                        *self.fn_name_counts.entry(seg.fn_name_1.clone()).or_insert(0) += 1;
                    }
                    if !seg.fn_name_2.is_empty() {
                        *self.fn_name_counts.entry(seg.fn_name_2.clone()).or_insert(0) += 1;
                    }

                    // Collect field values for correlation analysis
                    self.collect_field_values(&seg.name, seg);
                }

                // Textures
                for tex in &eff.textures {
                    *self.texture_counts.entry(tex.clone()).or_insert(0) += 1;
                }

                self.files.insert(name.to_string(), eff);
            }
            Err(e) => {
                self.errors.push((name.to_string(), e.to_string()));
            }
        }
    }

    /// Collect non-zero field values for a segment to help identify semantics.
    fn collect_field_values(&mut self, seg_name: &str, seg: &Segment) {
        let fields = self.field_value_sets.entry(seg_name.to_string()).or_default();

        // data_02 ints
        for (i, &v) in seg.data_02.iter().enumerate() {
            if v != 0 {
                fields.entry(format!("d02[{}]", i)).or_default().insert(v.to_string());
            }
        }
        // data_04 floats (only non-zero, rounded)
        for (i, &v) in seg.data_04.iter().enumerate() {
            if v.abs() > 0.0001 {
                fields.entry(format!("d04[{}]", i)).or_default()
                    .insert(format!("{:.3}", v));
            }
        }
        // data_06
        for (i, &v) in seg.data_06.iter().enumerate() {
            if v.abs() > 0.0001 {
                fields.entry(format!("d06[{}]", i)).or_default()
                    .insert(format!("{:.3}", v));
            }
        }
        // data_08
        for (i, &v) in seg.data_08.iter().enumerate() {
            if v.abs() > 0.0001 {
                fields.entry(format!("d08[{}]", i)).or_default()
                    .insert(format!("{:.3}", v));
            }
        }
    }

    /// Print a summary report to stdout.
    pub fn print_report(&self) {
        println!("=== EFF CORPUS REPORT ===");
        println!("Total files parsed: {}", self.files.len());
        println!("Parse errors: {}", self.errors.len());

        println!("\n--- Versions ---");
        for (ver, cnt) in &self.version_counts {
            println!("  {}: {} files", ver, cnt);
        }

        println!("\n--- Segment Count Distribution ---");
        let mut dist: Vec<_> = self.segment_count_dist.iter().collect();
        dist.sort_by_key(|(k, _)| *k);
        for (cnt, num) in dist {
            println!("  {} segments: {} files", cnt, num);
        }

        println!("\n--- Top Segment Names ---");
        let mut segs: Vec<_> = self.segment_name_counts.iter().collect();
        segs.sort_by_key(|(_, c)| std::cmp::Reverse(*c));
        for (name, cnt) in segs.iter().take(40) {
            let flags = self.segment_flags_variants.get(*name)
                .map(|s| s.iter().map(|f| format!("0x{:08X}", f)).collect::<Vec<_>>().join(", "))
                .unwrap_or_default();
            println!("  {:5}x '{}'  [flags: {}]", cnt, name, flags);
        }

        println!("\n--- Top Function Names ---");
        let mut fns: Vec<_> = self.fn_name_counts.iter().collect();
        fns.sort_by_key(|(_, c)| std::cmp::Reverse(*c));
        for (name, cnt) in fns.iter().take(30) {
            println!("  {:5}x '{}'", cnt, name);
        }

        println!("\n--- Top Textures ---");
        let mut texs: Vec<_> = self.texture_counts.iter().collect();
        texs.sort_by_key(|(_, c)| std::cmp::Reverse(*c));
        for (name, cnt) in texs.iter().take(30) {
            println!("  {:5}x '{}'", cnt, name);
        }

        // Field value diversity (helps identify enums, booleans, etc.)
        println!("\n--- Field Value Diversity (for naming hints) ---");
        for (seg_name, fields) in &self.field_value_sets {
            let mut interesting: Vec<_> = fields.iter()
                .filter(|(_, vals)| vals.len() >= 2 && vals.len() <= 20)
                .collect();
            if !interesting.is_empty() {
                interesting.sort_by_key(|(_, vals)| vals.len());
                println!("  [{}] ", seg_name);
                for (field, vals) in interesting {
                    let val_list: Vec<_> = vals.iter().take(10).collect();
                    println!("    {}: {} distinct values -> {:?}", field, vals.len(), val_list);
                }
            }
        }

        if !self.errors.is_empty() {
            println!("\n--- Errors ---");
            for (name, err) in &self.errors {
                println!("  {}: {}", name, err);
            }
        }
    }
}
