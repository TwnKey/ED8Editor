//! egui-based GUI for .eff file visualization and editing.
//!
//! Features planned:
//! - File browser / drag-drop .eff files
//! - Tree view of segments and their data
//! - Parameter editing with undo/redo
//! - Field annotation / naming / documentation
//! - Snapshot / diff system for iterative testing

mod app;
mod gl_render;


use eframe::egui;

fn main() -> Result<(), eframe::Error> {
    env_logger::init();

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1280.0, 800.0]),
        ..Default::default()
    };

    eframe::run_native(
        "Eff Editor Trails of Cold Steel",
        options,
        Box::new(|cc| {
            // Add CJK font for Japanese segment names
            let mut fonts = egui::FontDefinitions::default();
            // Try to load a Japanese font from Windows
            let jp_fonts = [
                "C:\\Windows\\Fonts\\msgothic.ttc",
                "C:\\Windows\\Fonts\\msmincho.ttc",
                "C:\\Windows\\Fonts\\yugothib.ttc",
            ];
            for path in &jp_fonts {
                if let Ok(data) = std::fs::read(path) {
                    fonts.font_data.insert(
                        "jp_font".to_string(),
                        egui::FontData::from_owned(data),
                    );
                    // Add Japanese font first in BOTH proportional and monospace lists
                    fonts.families
                        .entry(egui::FontFamily::Proportional)
                        .or_default()
                        .insert(0, "jp_font".to_string());
                    fonts.families
                        .entry(egui::FontFamily::Monospace)
                        .or_default()
                        .insert(0, "jp_font".to_string());
                    break;
                }
            }
            cc.egui_ctx.set_fonts(fonts);
            Ok(Box::new(app::EffEditorApp::default()))
        }),
    )
}
