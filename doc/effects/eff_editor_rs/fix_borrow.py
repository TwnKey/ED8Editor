with open("src/gui/app.rs", "r", encoding="utf-8") as f:
    content = f.read()
old = """            let rec = &mut arr[i];

            let header = egui::collapsing_header::CollapsingHeader::new(format!("  [{}]  {}={:.3}", i, time_label, rec.floats[8]))

                .default_open(arr.len() <= 4);"""
new = """            let len = arr.len();
            let rec = &mut arr[i];

            let header = egui::collapsing_header::CollapsingHeader::new(format!("  [{}]  {}={:.3}", i, time_label, rec.floats[8]))

                .default_open(len <= 4);"""
content = content.replace(old, new)
with open("src/gui/app.rs", "w", encoding="utf-8") as f:
    f.write(content)
print("Fixed")
