//! Main application state for the Eff Editor GUI.
//! Streamlined for rapid iteration: load, edit, save, launch.

use eframe::egui;
use egui::{Color32, RichText, ScrollArea, SidePanel, TopBottomPanel, CentralPanel};
use std::collections::BTreeMap;
use std::path::PathBuf;
use std::process::Command;

use eff_core::core::EffFile;
use eff_core::core::{GameVersion, Segment, ArrayRecord48};
use eff_core::core::PhyreTexFormat;

// -------- Annotation types ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

#[allow(dead_code)]
#[derive(Debug, Clone, Default)]
pub struct FieldAnnotation {
    pub display_name: String,
    pub description: String,
    pub type_hint: FieldTypeHint,
}

#[allow(dead_code)]
#[derive(Debug, Clone, Default, PartialEq)]
pub enum FieldTypeHint {
    #[default] Unknown,
    Float32, Uint32, Boolean,
    ColorRGBA, ColorRGB, Vector3D, Vector2D,
    Angle, Percentage, Enumeration(Vec<String>),
    TextureIndex, TimeSeconds, Count,
}

impl FieldTypeHint {
    fn label(&self) -> &str {
        match self {
            Self::Unknown => "???", Self::Float32 => "f32", Self::Uint32 => "u32",
            Self::Boolean => "bool", Self::ColorRGBA => "RGBA", Self::ColorRGB => "RGB",
            Self::Vector3D => "Vec3", Self::Vector2D => "Vec2", Self::Angle => "deg",
            Self::Percentage => "%", Self::Enumeration(_) => "Enum",
            Self::TextureIndex => "Tex", Self::TimeSeconds => "sec", Self::Count => "#",
        }
    }
}

// -------- Snapshot ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

#[derive(Debug, Clone)]
struct Snapshot {
    pub eff: EffFile,
    pub description: String,
}

// -------- App state ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

pub struct EffEditorApp {
    current_path: Option<PathBuf>,
    effect: Option<EffFile>,
    snapshots: Vec<Snapshot>,
    snapshot_idx: usize,
    selected_segment: Option<usize>,
    status: String,
    dropped_file: Option<String>,
    auto_snapshot_pending: bool,
    game_effects_dir: String,
    game_exe_path: String,
    game_asset_dir: String,
    annotations: BTreeMap<String, FieldAnnotation>,
    // Texture preview (keep handle alive ---- egui frees texture on drop!)
    texture_handle: Option<egui::TextureHandle>,  // selected segment's texture
    texture_handles: std::collections::HashMap<String, egui::TextureHandle>,  // all loaded textures
    texture_name: String,
    texture_w: u32,
    texture_h: u32,
    texture_rgba: Vec<u8>, // raw RGBA for clipboard copy
    // Animation preview state
    anim_time: f64,
    anim_playing: bool,
    anim_speed: f32,
    orbit_yaw: f32,   // manual orbit rotation (radians)
    orbit_pitch: f32,
    zoom: f32,   // zoom level for preview
    fit_pending: bool,  // auto-fit zoom to the effect's bounds on the next frame
    crop_drag_start: Option<egui::Pos2>,  // normalized start of an interactive crop drag
    hidden_nodes: std::collections::HashSet<usize>,  // eye toggle per segment
    // GIF recording
    gif_recording: bool,
    gif_frame_time: f64,
    gif_captured: std::sync::Arc<std::sync::Mutex<Vec<(u16, u16, Vec<u8>)>>>,
    // Simulates the spawn-context flag +0x158 & 0x10: the game can force
    // camera billboarding for a whole effect (UI markers like mk_talk whose
    // segments carry no orientation bits in d02[1]).
    force_billboard: bool,
    // Preview background: 0=checker, 1=black, 2=white, 3=magenta
    bg_mode: u8,
    // Drag & drop reparenting state
    drag_source: Option<usize>,
    drag_target: Option<usize>,
    drag_label: String, // the label text being dragged, for the floating ghost
    // Texture replacement: imported textures pending write into the asset dir on save,
    // keyed by base asset name (== segment fn_name_1 == pkg stem).
    pending_textures: std::collections::HashMap<String, eff_core::core::NewTexture>,
    // On "Replace from image", set the segment's quad corners (d08) to the imported
    // texture's aspect ratio so a rectangular image isn't squished into a square quad.
    fit_quad_to_texture: bool,
}

impl Default for EffEditorApp {
    fn default() -> Self {
        Self {
            current_path: None,
            effect: None,
            snapshots: Vec::new(),
            snapshot_idx: 0,
            selected_segment: None,
            status: "Drop a .eff file or use File > Open".to_string(),
            dropped_file: None,
            auto_snapshot_pending: false,
            game_effects_dir: r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects".to_string(),
            game_exe_path: r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\ed8.exe".to_string(),
            game_asset_dir: r"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11".to_string(),
            annotations: BTreeMap::new(),
            texture_handle: None,
            texture_handles: std::collections::HashMap::new(),
            texture_name: String::new(),
            texture_w: 0,
            texture_h: 0,
            texture_rgba: Vec::new(),
            anim_time: 0.0,
            anim_playing: false,
            anim_speed: 1.0,
            orbit_yaw: 0.0,
            orbit_pitch: 0.0,
            zoom: 1.0,
            fit_pending: false,
            crop_drag_start: None,
            hidden_nodes: std::collections::HashSet::new(),
            gif_recording: false,
            gif_frame_time: 0.0,
            gif_captured: std::sync::Arc::new(std::sync::Mutex::new(Vec::new())),
            force_billboard: false,
            bg_mode: 0,
            drag_source: None,
            drag_target: None,
            drag_label: String::new(),
            pending_textures: std::collections::HashMap::new(),
            fit_quad_to_texture: true,
        }
    }
}

impl EffEditorApp {
    fn load_file(&mut self, path: &str) {
        let path_buf = PathBuf::from(path);
        match std::fs::read(&path_buf) {
            Ok(data) => match eff_core::core::parse_eff_bytes(&data) {
                Ok(eff) => {
                    self.status = format!(
                        "Loaded: {} ({} segments)",
                        path_buf.file_name().unwrap().to_string_lossy(),
                        eff.segments.len()
                    );
                    self.current_path = Some(path_buf);
                    self.snapshots.clear();
                    self.snapshots.push(Snapshot { eff: eff.clone(), description: "load".into() });
                    self.snapshot_idx = 0;
                    self.effect = Some(eff);
                    self.selected_segment = None;
                    self.fit_pending = true;
                    self.texture_handle = None;
                    self.texture_handles.clear();
                    self.texture_name.clear();
                    self.texture_w = 0;
                    self.texture_h = 0;
                }
                Err(e) => self.status = format!("Parse error: {}", e),
            },
            Err(e) => self.status = format!("Read error: {}", e),
        }
    }

    fn take_snapshot(&mut self, desc: &str) {
        if let Some(ref eff) = self.effect {
            self.snapshots.truncate(self.snapshot_idx + 1);
            self.snapshots.push(Snapshot { eff: eff.clone(), description: desc.into() });
            self.snapshot_idx = self.snapshots.len() - 1;
            self.auto_snapshot_pending = false;
        }
    }

    fn ensure_snapshot(&mut self) {
        if !self.auto_snapshot_pending {
            if let Some(ref eff) = self.effect {
                let current = &self.snapshots[self.snapshot_idx].eff;
                if let (Ok(a), Ok(b)) = (serde_json::to_value(current), serde_json::to_value(eff)) {
                    if a != b {
                        self.take_snapshot("edit");
                        return;
                    }
                }
            }
        }
    }

    fn undo(&mut self) {
        if self.snapshot_idx > 0 {
            self.snapshot_idx -= 1;
            self.effect = Some(self.snapshots[self.snapshot_idx].eff.clone());
            self.auto_snapshot_pending = false;
            self.status = format!("Undo ({}/{})", self.snapshot_idx + 1, self.snapshots.len());
        }
    }

    fn redo(&mut self) {
        if self.snapshot_idx + 1 < self.snapshots.len() {
            self.snapshot_idx += 1;
            self.effect = Some(self.snapshots[self.snapshot_idx].eff.clone());
            self.auto_snapshot_pending = false;
            self.status = format!("Redo ({}/{})", self.snapshot_idx + 1, self.snapshots.len());
        }
    }

    fn save_to(&self, path: &std::path::Path) -> Result<(), String> {
        let eff = self.effect.as_ref().ok_or("No effect loaded")?;
        let data = eff_core::core::write_eff_to_bytes(eff).map_err(|e| format!("Write error: {}", e))?;
        std::fs::write(path, &data).map_err(|e| format!("Save error: {}", e))?;

        // Package any imported textures into the game's asset dir. Per spec, only
        // write a pkg that isn't already there (never overwrite existing game files).
        let asset_dir = std::path::Path::new(&self.game_asset_dir);
        let mut written = 0usize;
        for tex in self.pending_textures.values() {
            match eff_core::core::save_texture_pkg(asset_dir, tex) {
                Ok(Some(_)) => written += 1,
                Ok(None) => {} // already existed — leave it
                Err(e) => return Err(format!("Saved .eff, but texture '{}' failed: {}", tex.base_name, e)),
            }
        }
        if written > 0 {
            eprintln!("Wrote {} texture pkg(s) into {}", written, self.game_asset_dir);
        }
        Ok(())
    }

    fn new_effect(&mut self, path: Option<std::path::PathBuf>) {
        let eff = EffFile::new_default(GameVersion::V0x04);
        self.current_path = path.clone();
        self.snapshots.clear();
        self.snapshots.push(Snapshot { eff: eff.clone(), description: "new".into() });
        self.snapshot_idx = 0;
        self.effect = Some(eff);
        self.selected_segment = Some(0);
        self.fit_pending = true;
        self.texture_handle = None;
        self.texture_handles.clear();
        self.texture_name.clear();
        self.texture_w = 0;
        self.texture_h = 0;
        if let Some(ref p) = path {
            if let Err(e) = self.save_to(p) {
                self.status = format!("New effect created but save failed: {}", e);
            } else {
                self.status = format!("New effect saved: {}", p.display());
            }
        } else {
            self.status = "New effect created (unsaved)".to_string();
        }
    }

    fn copy_texture(&mut self) {
        if self.texture_rgba.is_empty() { return; }
        if let Ok(mut clip) = arboard::Clipboard::new() {
            let img = arboard::ImageData {
                width: self.texture_w as usize,
                height: self.texture_h as usize,
                bytes: std::borrow::Cow::Borrowed(&self.texture_rgba),
            };
            if clip.set_image(img).is_ok() {
                self.status = "Texture copied to clipboard!".to_string();
            }
        }
    }

    /// Get the texture name used by a segment (from fn_name_1).
    fn segment_texture_name(&self, seg_idx: usize) -> Option<String> {
        let eff = self.effect.as_ref()?;
        let seg = eff.segments.get(seg_idx)?;
        if !seg.fn_name_1.is_empty() && seg.fn_name_1.starts_with("I_EFTEX") {
            Some(seg.fn_name_1.clone())
        } else {
            None
        }
    }

    /// Try to load ALL textures referenced by the current effect.
    fn try_load_texture(&mut self, ctx: &egui::Context) {
        let eff = match &self.effect { Some(e) => e, None => return };
        
        // Collect all unique texture names from all segments
        let mut needed: Vec<String> = Vec::new();
        for seg in &eff.segments {
            if !seg.fn_name_1.is_empty() && seg.fn_name_1.starts_with("I_") {
                if !needed.contains(&seg.fn_name_1) { needed.push(seg.fn_name_1.clone()); }
            }
        }
        if needed.is_empty() {
            if let Some(first) = eff.textures.first() { needed.push(first.clone()); }
        }
        
        // Remove already-loaded textures
        needed.retain(|n| !self.texture_handles.contains_key(n));
        if needed.is_empty() { return; }
        
        for tex_name in &needed {
            let pkg_path = std::path::PathBuf::from(&self.game_asset_dir).join(format!("{}.pkg", tex_name));
            if !pkg_path.exists() { continue; }
            
            match std::fs::read(&pkg_path) {
                Ok(data) => {
                    if let Some(pkg) = eff_core::core::PkgArchive::parse(&data) {
                        if let Some(tex_entry) = pkg.find_texture() {
                            if let Some(decompressed) = pkg.extract(&tex_entry.name) {
                                if let Some(tex) = eff_core::core::parse_phyre_texture(&decompressed) {
                                    let mut premul = tex.rgba_data.clone();
                                    for i in (0..premul.len()).step_by(4) {
                                        let a = premul[i + 3] as f32 / 255.0;
                                        premul[i] = (premul[i] as f32 * a) as u8;
                                        premul[i + 1] = (premul[i + 1] as f32 * a) as u8;
                                        premul[i + 2] = (premul[i + 2] as f32 * a) as u8;
                                    }
                                    let color_image = egui::ColorImage::from_rgba_premultiplied(
                                        [tex.width as usize, tex.height as usize], &premul,
                                    );
                                    let handle = ctx.load_texture(tex_name, color_image, egui::TextureOptions::NEAREST);
                                    self.texture_handles.insert(tex_name.clone(), handle);
                                    // Set the primary texture to the first one loaded
                                    if self.texture_handle.is_none() {
                                        self.texture_handle = self.texture_handles.get(tex_name).cloned();
                                        self.texture_name = tex_name.clone();
                                        self.texture_w = tex.width;
                                        self.texture_h = tex.height;
                                        self.texture_rgba = tex.rgba_data.clone();
                                    }
                                    self.status = format!("Loaded: {} ({} textures)", tex_name, self.texture_handles.len());
                                }
                            }
                        }
                    }
                }
                Err(_) => {}
            }
        }
    }


}
impl eframe::App for EffEditorApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // -------- Drag & drop --------
        if !ctx.input(|i| i.raw.dropped_files.is_empty()) {
            let dropped = ctx.input(|i| i.raw.dropped_files.clone());
            for file in &dropped {
                if let Some(path) = &file.path {
                    self.dropped_file = Some(path.to_string_lossy().to_string());
                }
            }
        }
        if let Some(ref path) = self.dropped_file.clone() {
            self.load_file(path);
            self.dropped_file = None;
        }

        // -------- Animation time update --------
        if self.anim_playing {
            self.anim_time += ctx.input(|i| i.unstable_dt) as f64 * self.anim_speed as f64;
            ctx.request_repaint();
        }

        // Load texture if available
        self.try_load_texture(ctx);

        // -------- Top bar --------
        TopBottomPanel::top("top_bar").show(ctx, |ui| {
            ui.horizontal(|ui| {
                if ui.button("New").clicked() {
                    // Show save dialog, then create and save
                    if let Some(path) = rfd::FileDialog::new()
                        .add_filter("Effect files", &["eff"])
                        .set_file_name("new_effect.eff")
                        .save_file()
                    {
                        self.new_effect(Some(path));
                    }
                }
                if ui.button("Open").clicked() {
                    if let Some(path) = rfd::FileDialog::new()
                        .add_filter("Effect files", &["eff"])
                        .pick_file()
                    {
                        self.load_file(&path.to_string_lossy());
                    }
                }

                let has_file = self.effect.is_some();
                ui.add_enabled_ui(has_file, |ui| {
                    if ui.button("Save").clicked() {
                        if let Some(ref p) = self.current_path.clone() {
                            match self.save_to(p) {
                                Ok(()) => self.status = format!("Saved: {}", p.file_name().unwrap().to_string_lossy()),
                                Err(e) => self.status = e,
                            }
                        }
                    }
                    if ui.button("Save && Launch").clicked() {
                        let path = self.current_path.clone().unwrap_or_else(|| {
                            PathBuf::from(&self.game_effects_dir)
                                .join(format!("{}.eff", self.effect.as_ref().unwrap().effect_name))
                        });
                        match self.save_to(&path) {
                            Ok(()) => {
                                let exe = &self.game_exe_path;
                                if std::path::Path::new(exe).exists() {
                                    match Command::new(exe).spawn() {
                                        Ok(_) => self.status = "Saved & launched!".into(),
                                        Err(e) => self.status = format!("Launch error: {}", e),
                                    }
                                } else {
                                    self.status = format!("Saved (exe not found: {})", exe);
                                }
                            }
                            Err(e) => self.status = e,
                        }
                    }
                });

                ui.separator();

                ui.add_enabled_ui(self.snapshot_idx > 0, |ui| {
                    if ui.button("Undo").clicked() { self.undo(); }
                });
                ui.add_enabled_ui(self.snapshot_idx + 1 < self.snapshots.len(), |ui| {
                    if ui.button("Redo").clicked() { self.redo(); }
                });

                if has_file {
                    ui.label(format!("Snapshots: {}/{}", self.snapshot_idx + 1, self.snapshots.len()));
                }

                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    ui.label(RichText::new(&self.status).color(Color32::GRAY));
                });
            });
        });

        // -------- Bottom bar: config --------
        TopBottomPanel::bottom("bottom_bar").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label("Effects:");
                ui.add(egui::TextEdit::singleline(&mut self.game_effects_dir).font(egui::TextStyle::Monospace).desired_width(300.0));
                ui.label("Assets:");
                ui.add(egui::TextEdit::singleline(&mut self.game_asset_dir).font(egui::TextStyle::Monospace).desired_width(300.0));
                ui.label("Exe:");
                ui.add(egui::TextEdit::singleline(&mut self.game_exe_path).font(egui::TextStyle::Monospace).desired_width(200.0));
            });
        });

        // -------- Left panel --------
        SidePanel::left("segments").resizable(true).default_width(220.0).show(ctx, |ui| {
            ui.heading("Segments");
            ui.separator();
            // Interactive crop drag result (segment idx, [L,T,R,B]), applied after
            // the immutable `eff` borrow below ends.
            let mut pending_crop: Option<(usize, [f32; 4])> = None;
            let mut crop_drag_started = false;
            // Pending actions to apply after the immutable borrow on eff ends
            let mut pending_add_node = false;
            let mut pending_delete_node = false;
            let mut pending_reparent: Option<(usize, Option<usize>)> = None; // (src, target)
            if let Some(ref eff) = self.effect {
                ui.label(format!("{}", eff.effect_name));
                ui.label(RichText::new(format!("{} segments", eff.segments.len())).small());
                // Build parent----children hierarchy from d14
                let children_map = build_children_map(eff);
                // Find roots (segments not listed as children of anyone)
                let all_children: std::collections::HashSet<usize> = children_map.values().flatten().copied().collect();
                let roots: Vec<usize> = (0..eff.segments.len()).filter(|i| !all_children.contains(i)).collect();

                ui.separator();
                // Cap the tree height so the texture preview below stays visible.
                let tree_h = (ui.available_height() - 330.0).max(80.0);
                ScrollArea::vertical().max_height(tree_h).auto_shrink([false, true]).show(ui, |ui| {
                    // Reset drag target — tree nodes will set it on hover
                    if self.drag_source.is_some() {
                        self.drag_target = None;
                    }
                    for &root_idx in &roots {
                        show_segment_tree(ui, root_idx, eff, &children_map, &mut self.selected_segment, 0, &mut self.hidden_nodes, &mut self.drag_source, &mut self.drag_target, &mut self.drag_label);
                    }
                    // Drop zone at the bottom: drop here to make root
                    if self.drag_source.is_some() {
                        let drop_id = ui.next_auto_id();
                        let drop_rect = ui.available_rect_before_wrap();
                        let drop_resp = ui.interact(drop_rect, drop_id, egui::Sense::hover());
                        let hovering = drop_resp.hovered();
                        if hovering {
                            ui.painter().rect_filled(drop_rect, 0.0, Color32::from_rgba_unmultiplied(0, 255, 0, 40));
                            ui.painter().rect_stroke(drop_rect, 0.0, egui::Stroke::new(2.0, Color32::GREEN));
                            ui.painter().text(
                                drop_rect.center(),
                                egui::Align2::CENTER_CENTER,
                                "Drop here to make root",
                                egui::FontId::proportional(12.0),
                                Color32::GREEN,
                            );
                            self.drag_target = None; // None = make root
                        }
                    }
                });

                // Process drag & drop reparent (collect action, apply later)
                if let Some(src) = self.drag_source {
                    let released = ui.input(|i| i.pointer.any_released());
                    if released {
                        let target = self.drag_target;
                        if target != Some(src) && target.map_or(true, |t| t < eff.segments.len()) {
                            pending_reparent = Some((src, target));
                        }
                        self.drag_source = None;
                        self.drag_target = None;
                        self.drag_label.clear();
                    }
                }

                // Add / Delete node buttons
                ui.separator();
                ui.horizontal(|ui| {
                    if ui.button("+").on_hover_text("Add new segment").clicked() { pending_add_node = true; }
                    if ui.add_enabled(self.selected_segment.is_some(), egui::Button::new("-").fill(Color32::TRANSPARENT)).on_hover_text("Delete selected segment").clicked() {
                        pending_delete_node = true;
                    }
                });

                // Texture preview in left panel ---- show selected segment's texture
                let seg_tex: Option<&str> = self.selected_segment
                    .and_then(|idx| eff.segments.get(idx))
                    .and_then(|seg| if seg.fn_name_1.is_empty() { None } else { Some(seg.fn_name_1.as_str()) });
                let preview_handle = seg_tex.and_then(|name| self.texture_handles.get(name))
                    .or_else(|| self.texture_handles.values().next());
                
                if let Some(handle) = preview_handle {
                    let tex_name = seg_tex.unwrap_or(&self.texture_name);
                    ui.separator();
                    ui.label(RichText::new(tex_name).small());
                    let tex_size = handle.size_vec2();
                    let max_size = 200.0;
                    let (w, h) = (tex_size[0] as f32, tex_size[1] as f32);
                    let scl = if w > 0.0 && h > 0.0 { (max_size / w).min(max_size / h) } else { 1.0 };
                    let img_size = [w * scl, h * scl];
                    ui.label(RichText::new(format!("{}x{}", w as u32, h as u32)).small().color(Color32::GRAY));

                    // Checkerboard background for transparency preview
                    let img_pos = ui.next_widget_position();
                    let cs = 6.0;
                    let mut cx = img_pos.x;
                    let mut row = 0;
                    while cx < img_pos.x + img_size[0] {
                        let mut cy = img_pos.y;
                        let mut col = 0;
                        while cy < img_pos.y + img_size[1] {
                            let c = if (row + col) % 2 == 0 { Color32::from_gray(180) } else { Color32::from_gray(210) };
                            ui.painter().rect_filled(
                                egui::Rect::from_min_size(egui::pos2(cx, cy), egui::vec2(cs, cs)),
                                0.0, c,
                            );
                            cy += cs; col += 1;
                        }
                        cx += cs; row += 1;
                    }

                    let img = egui::Image::new(egui::load::SizedTexture::new(handle.id(), img_size))
                        .sense(egui::Sense::click_and_drag());
                    let resp = ui.add(img);
                    let img_rect = resp.rect;

                    // Interactive crop: drag a rectangle on the texture to set the
                    // selected segment's crop (data_04[0..4], normalized). Only when
                    // the thumbnail shows this segment's own texture.
                    let norm = |p: egui::Pos2| egui::pos2(
                        ((p.x - img_rect.left()) / img_rect.width().max(1.0)).clamp(0.0, 1.0),
                        ((p.y - img_rect.top()) / img_rect.height().max(1.0)).clamp(0.0, 1.0),
                    );
                    if seg_tex.is_some() {
                        if resp.drag_started() {
                            self.crop_drag_start = resp.interact_pointer_pos().map(norm);
                            crop_drag_started = true;
                        }
                        if resp.dragged() {
                            if let (Some(s), Some(p)) = (self.crop_drag_start, resp.interact_pointer_pos()) {
                                let e = norm(p);
                                let crop = [s.x.min(e.x), s.y.min(e.y), s.x.max(e.x), s.y.max(e.y)];
                                if let Some(idx) = self.selected_segment { pending_crop = Some((idx, crop)); }
                            }
                        }
                        if resp.drag_stopped() { self.crop_drag_start = None; }
                        ui.label(RichText::new("Drag on the texture to set the crop").small().color(Color32::GRAY));
                    }

                    // Crop overlay from d04[0..4] = [left, top, right, bottom] normalized
                    if let Some(idx) = self.selected_segment {
                        if let Some(seg) = eff.segments.get(idx) {
                            let l = seg.data_04[0] as f32;
                            let t = seg.data_04[1] as f32;
                            let r = seg.data_04[2] as f32;
                            let b = seg.data_04[3] as f32;
                            // Draw for any non-empty crop, including flipped (l>r)
                            // and degenerate (l==r -> line). Clamp to the thumbnail
                            // for tiling crops (>1).
                            if l != 0.0 || t != 0.0 || r != 0.0 || b != 0.0 {
                                let (x0, x1) = (l.min(r).clamp(0.0, 1.0), l.max(r).clamp(0.0, 1.0));
                                let (y0, y1) = (t.min(b).clamp(0.0, 1.0), t.max(b).clamp(0.0, 1.0));
                                let x = img_rect.left() + x0 * img_rect.width();
                                let y = img_rect.top() + y0 * img_rect.height();
                                let w = (x1 - x0) * img_rect.width();
                                let h = (y1 - y0) * img_rect.height();
                                ui.painter().rect_stroke(
                                    egui::Rect::from_min_size(egui::pos2(x, y), egui::vec2(w, h)),
                                    0.0,
                                    egui::Stroke::new(2.0, Color32::RED),
                                );
                            }
                        }
                    }

                    if ui.button("Copy texture").clicked() {
                        self.copy_texture();
                    }
                }
            } else {
                ui.label("No file loaded. Drop .eff here.");
            }

            // Apply an interactive crop drag (after the immutable eff borrow ends).
            // One snapshot at drag start = one undo step for the whole drag.
            if crop_drag_started { self.take_snapshot("crop"); }
            if let Some((idx, crop)) = pending_crop {
                if let Some(eff_mut) = self.effect.as_mut() {
                    if let Some(seg) = eff_mut.segments.get_mut(idx) {
                        for k in 0..4 { seg.data_04[k] = crop[k]; }
                    }
                }
            }
            // Apply pending hierarchy actions (after immutable eff borrow ends)
            if let Some((src, target)) = pending_reparent {
                self.ensure_snapshot();
                if let Some(eff_mut) = self.effect.as_mut() {
                    if reparent_segment(eff_mut, src, target) {
                        let msg = if let Some(t) = target {
                            format!("Reparented [{}] under [{}]", src, t)
                        } else {
                            format!("Reparented [{}] to root", src)
                        };
                        self.status = msg;
                    } else {
                        self.status = "Cannot reparent: would create a cycle".to_string();
                    }
                }
            }
            if pending_add_node {
                self.ensure_snapshot();
                let new_idx_opt = if let Some(eff_mut) = self.effect.as_mut() {
                    let new_idx = eff_mut.segments.len();
                    eff_mut.segments.push(Segment::default_segment(eff_mut.version));
                    Some(new_idx)
                } else { None };
                if let Some(new_idx) = new_idx_opt {
                    self.selected_segment = Some(new_idx);
                    self.status = format!("Added segment [{}]", new_idx);
                }
            }
            if pending_delete_node {
                if let Some(idx) = self.selected_segment {
                    self.ensure_snapshot();
                    let result = if let Some(eff_mut) = self.effect.as_mut() {
                        if eff_mut.segments.len() > 1 {
                            delete_segment(eff_mut, idx);
                            let new_sel = if idx > 0 { Some(idx - 1) } else { Some(0) };
                            Some((new_sel, format!("Deleted segment [{}]", idx)))
                        } else {
                            None
                        }
                    } else { None };
                    if let Some((new_sel, msg)) = result {
                        self.selected_segment = new_sel;
                        self.status = msg;
                    } else if self.effect.is_some() {
                        self.status = "Cannot delete last segment".to_string();
                    }
                }
            }
        });

        // Set cursor to grabbing during drag
        if self.drag_source.is_some() {
            ctx.set_cursor_icon(egui::CursorIcon::Grabbing);
        }

        // -------- Right panel: animation preview --------
        self.show_preview_panel(ctx);

        // -------- Center --------
        CentralPanel::default().show(ctx, |ui| {
            if self.effect.is_none() {
                ui.centered_and_justified(|ui| {
                    ui.label(RichText::new("Drop a .eff file here").size(24.0).color(Color32::GRAY));
                });
                return;
            }

            let eff = self.effect.as_ref().unwrap();
            if let Some(idx) = self.selected_segment {
                ScrollArea::vertical().show(ui, |ui| {
                    show_editable_segment(ui, idx, self);
                });
            } else {
                ui.heading(&eff.effect_name);
                ui.separator();
                ui.label(format!("Path: {:?}", self.current_path));
                ui.label(format!("Version: {}", eff.version.label()));
                ui.label(format!("Textures: {:?}", eff.textures));
                ui.label(format!("Segments: {} (click left to edit)", eff.segments.len()));

                // Texture preview
                if let Some(handle) = self.texture_handle.as_ref().or_else(|| self.texture_handles.values().next()) {
                    ui.separator();
                    ui.label(format!("Preview: {}", self.texture_name));
                    ui.add(egui::Image::new(egui::load::SizedTexture::new(handle.id(), [256.0, 256.0])));
                }

                ui.separator();
                ui.label("Ctrl+Z/Y = undo/redo  |  Ctrl+S = save  |  Edit values then Save & Launch");
            }
        });

        // -------- Shortcuts --------
        ctx.input(|i| {
            if i.modifiers.ctrl && !i.modifiers.shift && i.key_pressed(egui::Key::Z) { self.undo(); }
            if i.modifiers.ctrl && i.key_pressed(egui::Key::Y) { self.redo(); }
            if i.modifiers.ctrl && i.key_pressed(egui::Key::S) {
                if let Some(ref p) = self.current_path.clone() {
                    if self.save_to(p).is_ok() { self.status = "Saved!".into(); }
                }
            } // <-- L'accolade fermante manquante est ici
        });
    } // <-- Correction : suppression du point-virgule après l'accolade
}


// -------- Texture replacement helpers ---------------------------------------------------------------

/// Keep the effect's file-level texture manifest (`eff.textures`) in sync with the
/// segments' `fn_name_1` references. The GAME preloads THIS list and binds each segment
/// to it BY NAME — a segment texture that isn't listed here renders as a blank quad
/// (every `fn_name_1` in a vanilla effect is present in `eff.textures`). Rebuilds the
/// list as the set of referenced names, preserving existing order.
fn resync_effect_textures(eff: &mut EffFile) {
    let referenced: std::collections::HashSet<String> = eff
        .segments
        .iter()
        .map(|s| s.fn_name_1.clone())
        .filter(|n| !n.is_empty())
        .collect();
    let mut list: Vec<String> = Vec::new();
    for t in &eff.textures {
        if referenced.contains(t) && !list.contains(t) { list.push(t.clone()); }
    }
    for s in &eff.segments {
        if !s.fn_name_1.is_empty() && !list.contains(&s.fn_name_1) { list.push(s.fn_name_1.clone()); }
    }
    eff.textures = list;
}

/// Sanitize a filename stem into a valid asset base name (uppercase, alphanumeric +
/// underscore, ≤15 chars) that doesn't collide with an existing pkg or a pending import.
fn make_unique_base(
    asset_dir: &std::path::Path,
    pending: &std::collections::HashMap<String, eff_core::core::NewTexture>,
    raw_stem: &str,
) -> String {
    let cleaned: String = raw_stem
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() || c == '_' { c.to_ascii_uppercase() } else { '_' })
        .collect();
    let cleaned = cleaned.trim_matches('_').to_string();
    // Prefix "I_" so the game resolves it like other effect textures (I_EFTEX###).
    let body = if cleaned.is_empty() { "TEX".to_string() } else { cleaned };
    let mut s = format!("I_{body}");
    s.truncate(12);
    let taken = |name: &str| pending.contains_key(name) || asset_dir.join(format!("{name}.pkg")).exists();
    if !taken(&s) { return s; }
    for n in 0..1000 {
        let cand = format!("{}{:03}", s, n);
        if !taken(&cand) { return cand; }
    }
    format!("{}_{}", s, std::process::id())
}

/// Build an egui preview handle from RGBA8 and register it so the segment shows the
/// imported texture immediately (before it's written to disk on save).
fn register_preview_texture(app: &mut EffEditorApp, ui: &egui::Ui, name: &str, rgba: &[u8], w: u32, h: u32) {
    let mut premul = rgba.to_vec();
    for i in (0..premul.len()).step_by(4) {
        let a = premul[i + 3] as f32 / 255.0;
        premul[i] = (premul[i] as f32 * a) as u8;
        premul[i + 1] = (premul[i + 1] as f32 * a) as u8;
        premul[i + 2] = (premul[i + 2] as f32 * a) as u8;
    }
    let ci = egui::ColorImage::from_rgba_premultiplied([w as usize, h as usize], &premul);
    let handle = ui.ctx().load_texture(name, ci, egui::TextureOptions::NEAREST);
    app.texture_handles.insert(name.to_string(), handle.clone());
    app.texture_handle = Some(handle);
    app.texture_name = name.to_string();
    app.texture_w = w;
    app.texture_h = h;
    app.texture_rgba = rgba.to_vec();
}

/// Import a PC image, package it into a new texture pkg using the segment's current
/// texture (or a user-picked pkg) as the size/format template, and point the segment at it.
fn replace_texture_from_image(app: &mut EffEditorApp, ui: &egui::Ui, seg: &Segment, idx: usize) {
    let Some(img_path) = rfd::FileDialog::new()
        .add_filter("Images", &["png", "jpg", "jpeg", "dds"])
        .set_title("Choose an image to import as this segment's texture")
        .pick_file()
    else { return; };

    let asset_dir = std::path::PathBuf::from(&app.game_asset_dir);

    // Fully standalone: the texture is generated from scratch at the image's own
    // dimensions — no template, no prompt, one click.
    let raw_stem = img_path.file_stem().unwrap_or_default().to_string_lossy().to_string();
    let base = make_unique_base(&asset_dir, &app.pending_textures, &raw_stem);

    let (rgba, w, h) = match eff_core::core::load_image_rgba(&img_path) {
        Ok(v) => v,
        Err(e) => { app.status = format!("Image load failed: {e}"); return; }
    };
    // Detect existing texture format to match it (critical: using RGBA8 where
    // the game expects ARGB8 causes hatched/interlaced artifacts in-game).
    // Read the format from the segment's CURRENT texture before replacement.
    let existing_fmt = if !seg.fn_name_1.is_empty() {
        eff_core::core::detect_existing_format(&asset_dir, &seg.fn_name_1)
    } else {
        None
    };
    match eff_core::core::build_texture_pkg(&rgba, w, h, &base, existing_fmt) {
        Err(e) => { app.status = format!("Texture encode failed: {e}"); }
        Ok(nt) => {
            app.ensure_snapshot();
            register_preview_texture(app, ui, &nt.base_name, &nt.preview_rgba, nt.width, nt.height);
            let fit = app.fit_quad_to_texture;
            let mut fitted = false;
            if let Some(ref mut eff_mut) = app.effect {
                let s = &mut eff_mut.segments[idx];
                s.fn_name_1 = nt.base_name.clone();
                s.fn1_raw.clear();
                // Fit the quad to the texture aspect. In-game a flat quad (shape 0x00)
                // is a UNIT square scaled by the Scale track (d0B) — its aspect is
                // scale_x/scale_y. A "uniform" scale keyframe (mode bit1=0x2) broadcasts
                // one value to all axes → forced square. So we convert each uniform
                // scale keyframe to per-axis (clear bit1) and set y = x/aspect, which
                // makes the quad rectangular while preserving the animation. d08 is
                // reset to the unit square (the game ignores d08 corners for 0x00).
                let shape = (s.data_02[4] & 0xFF) as u8;
                if fit && shape == 0x00 && nt.height > 0 {
                    let ar = nt.width as f32 / nt.height as f32; // width / height
                    // Keep the larger texture dimension at the original scale.
                    let (fx, fy) = if ar >= 1.0 { (1.0, 1.0 / ar) } else { (ar, 1.0) };
                    if s.data_0b.is_empty() {
                        let mut r = eff_core::core::ArrayRecord48::default_record();
                        r.floats[0] = fx; r.floats[1] = fy; r.floats[2] = 1.0;
                        r.ints[0] = 0; // per-axis, absolute, t=0
                        s.data_0b.push(r);
                    } else {
                        for kf in s.data_0b.iter_mut() {
                            if kf.ints[0] & 0x2 != 0 {
                                // uniform → per-axis with aspect (base = floats[0])
                                let base = kf.floats[0];
                                kf.floats[0] = base * fx;
                                kf.floats[1] = base * fy;
                                kf.floats[2] = base;
                                kf.ints[0] &= !0x2; // clear uniform bit
                            }
                        }
                    }
                    fitted = true;
                }
                resync_effect_textures(eff_mut);
            }
            app.status = format!(
                "Imported '{}' → {}×{} texture ({:?}){}, will write {}.pkg on save",
                raw_stem, nt.width, nt.height, existing_fmt.unwrap_or(PhyreTexFormat::RGBA8),
                if fitted { " + quad aspect fit" } else { "" }, nt.base_name
            );
            app.pending_textures.insert(nt.base_name.clone(), nt);
        }
    }
}

/// Point the segment at an existing game texture pkg (no packaging).
fn use_existing_texture(app: &mut EffEditorApp, seg: &Segment, idx: usize) {
    let asset_dir = std::path::PathBuf::from(&app.game_asset_dir);
    let Some(path) = rfd::FileDialog::new()
        .add_filter("Texture package", &["pkg"])
        .set_title("Pick an existing texture .pkg from the game")
        .set_directory(if asset_dir.exists() { asset_dir } else { std::env::current_dir().unwrap_or_default() })
        .pick_file()
    else { return; };
    let stem = path.file_stem().unwrap_or_default().to_string_lossy().to_string();
    app.ensure_snapshot();
    app.pending_textures.remove(&seg.fn_name_1);
    if let Some(ref mut eff_mut) = app.effect {
        eff_mut.segments[idx].fn_name_1 = stem.clone();
        eff_mut.segments[idx].fn1_raw.clear();
        resync_effect_textures(eff_mut);
    }
    app.status = format!("Texture set to existing '{stem}'");
}

// -------- Editable segment --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

fn show_editable_segment(ui: &mut egui::Ui, idx: usize, app: &mut EffEditorApp) {
    // Clone the segment for editing, write back on modification
    let seg = app.effect.as_ref().unwrap().segments[idx].clone();

    // Editable segment name
    let mut rename_pending: Option<String> = None;
    ui.horizontal(|ui| {
        ui.label(RichText::new(format!("[{}]", idx)).strong());
        let mut name = seg.name.clone();
        let resp = ui.add(egui::TextEdit::singleline(&mut name).desired_width(200.0).font(egui::TextStyle::Heading));
        if resp.changed() {
            rename_pending = Some(name);
        }
    });
    if let Some(new_name) = rename_pending {
        app.ensure_snapshot();
        if let Some(ref mut eff_mut) = app.effect {
            eff_mut.segments[idx].name = new_name;
            eff_mut.segments[idx].name_raw.clear();
        }
    }
    ui.horizontal(|ui| {
        if !seg.fn_name_1.is_empty() { ui.label(format!("fn1: {}", seg.fn_name_1)); }
        else { ui.label(RichText::new("fn1: (no texture)").color(Color32::DARK_GRAY)); }
        if !seg.fn_name_2.is_empty() { ui.label(format!("3D Model: {}", seg.fn_name_2)); }
        ui.label(format!("flags: 0x{:08X}", seg.struct_flags));
    });
    // Texture selection / replacement
    ui.horizontal(|ui| {
        if ui.button("Replace from image…")
            .on_hover_text("Import a PNG/JPG from your PC at its own dimensions, packaged\n\
                            into a new .pkg written to the game asset dir on save\n\
                            (existing game .pkg files are never overwritten).")
            .clicked()
        {
            replace_texture_from_image(app, ui, &seg, idx);
        }
        if ui.button("Use existing game texture…")
            .on_hover_text("Point this segment at a texture .pkg already in the game\n\
                            (no packaging — just references it by name).")
            .clicked()
        {
            use_existing_texture(app, &seg, idx);
        }
        if !seg.fn_name_1.is_empty() {
            if ui.small_button("✕").on_hover_text("Remove texture reference").clicked() {
                app.ensure_snapshot();
                app.pending_textures.remove(&seg.fn_name_1);
                if let Some(ref mut eff_mut) = app.effect {
                    eff_mut.segments[idx].fn_name_1.clear();
                    eff_mut.segments[idx].fn1_raw.clear();
                    resync_effect_textures(eff_mut);
                }
            }
        }
    });
    ui.checkbox(&mut app.fit_quad_to_texture, "Fit quad to texture aspect")
        .on_hover_text("On import, make the segment's Scale (d0B) non-uniform to match the\n\
                        image's aspect ratio, so a rectangular texture isn't squished into a\n\
                        square (in-game a quad is a unit square scaled by d0B — a uniform\n\
                        scale forces a square). Converts uniform scale keyframes to per-axis\n\
                        and resets d08 to the unit quad. Quad shapes (0x00) only.");
    // Pending-import status for this segment's texture.
    if let Some(nt) = app.pending_textures.get(&seg.fn_name_1) {
        let path = std::path::Path::new(&app.game_asset_dir).join(format!("{}.pkg", nt.base_name));
        let exists = path.exists();
        ui.label(
            RichText::new(format!(
                "⬤ Imported {}×{} — writes {}.pkg to asset dir on save{}",
                nt.width, nt.height, nt.base_name,
                if exists { " (already present — will keep existing)" } else { "" },
            ))
            .color(if exists { Color32::from_rgb(220, 160, 60) } else { Color32::from_rgb(80, 200, 120) })
            .small(),
        );
    }
    ui.separator();

    let mut modified = false;
    let mut new_seg = seg;

    egui::collapsing_header::CollapsingHeader::new("Parameters (d02-d08)")
        .default_open(true)
        .show(ui, |ui| {
        // d02: mixed u32/f32 per field
        let d02_hints = ["", "", "", "", "Shape / Blend", "Sound ID (high u16)", "Trail framerate (low u16)", "Unknown (float)"];
        let d02_is_float = [false, false, false, false, false, false, false, true]; // only d02[7] is f32
        ui.collapsing(format!("d02 (8 values)"), |ui| {
            for i in 0..8 {
                if i == 4 {
                    // d02[4] = byte0=shape, byte1=blend, byte2=hi flags
                    let v = &mut new_seg.data_02[i];
                    ui.horizontal(|ui| {
                        ui.label("[04]");
                        let shape_name = match (*v & 0xFF) as u8 {
                            0x00 => "Quad", 0x02 => "Ground", 0x04 => "Sphere",
                            0x05 => "Cross", 0x08 => "HalfCyl", 0x09 => "Trail", 0x0C => "Wire",
                            _ => "?",
                        };
                        ui.label(RichText::new(format!("Shape/Blend ({})", shape_name)).small().color(Color32::YELLOW));
                        ui.label(RichText::new(format!("0x{:08X}", *v)).small().color(Color32::DARK_GRAY));
                    });
                    // Shape
                    ui.horizontal(|ui| {
                        let b0 = (*v & 0xFF) as u8;
                        ui.label("   Shape:");
                        let shapes: &[(&str, u8)] = &[
                            ("Quad", 0x00), ("Ground", 0x02), ("Sphere", 0x04),
                            ("Cross", 0x05), ("HalfCyl", 0x08), ("Trail", 0x09), ("Wire", 0x0C),
                        ];
                        let shape_label = shapes.iter().find(|&&(_, val)| val == b0).map(|&(s, _)| s).unwrap_or("Custom");
                        egui::ComboBox::from_id_salt("shape")
                            .selected_text(if shape_label == "Custom" { format!("0x{:02X}", b0) } else { shape_label.to_string() })
                            .show_ui(ui, |ui| {
                                for &(name, val) in shapes {
                                    if ui.selectable_label(b0 == val, name).clicked() {
                                        *v = (*v & !0xFF) | (val as u32);
                                        modified = true;
                                    }
                                }
                            });
                        let mut sb = b0;
                        if ui.add(egui::DragValue::new(&mut sb).range(0..=255).hexadecimal(2, false, false)).changed() {
                            *v = (*v & !0xFF) | (sb as u32);
                            modified = true;
                        }
                    });
                    // Blend
                    ui.horizontal(|ui| {
                        let b1 = ((*v >> 8) & 0xFF) as u8;
                        ui.label("   Blend:");
                        let blends: &[(&str, u8)] = &[
                            ("Opaque", 0x00), ("Alpha", 0x01), ("Additive", 0x02), ("Subtract", 0x04),
                        ];
                        let blend_label = blends.iter().find(|&&(_, val)| val == b1).map(|&(s, _)| s).unwrap_or("Custom");
                        egui::ComboBox::from_id_salt("blend")
                            .selected_text(if blend_label == "Custom" { format!("0x{:02X}", b1) } else { blend_label.to_string() })
                            .show_ui(ui, |ui| {
                                for &(name, val) in blends {
                                    if ui.selectable_label(b1 == val, name).clicked() {
                                        *v = (*v & !0xFF00) | ((val as u32) << 8);
                                        modified = true;
                                    }
                                }
                            });
                        let mut cb = b1;
                        if ui.add(egui::DragValue::new(&mut cb).range(0..=255).hexadecimal(2, false, false)).changed() {
                            *v = (*v & !0xFF00) | ((cb as u32) << 8);
                            modified = true;
                        }
                        ui.label(RichText::new("(?)").small().color(Color32::GRAY))
                            .on_hover_text("Opaque: no blending.\nAlpha: normal transparency (over background).\nAdditive: adds light (glows, brightens background).\nSubtract: reverse-subtract (dst - src) — darkens the background by the texture's brightness.");
                    });
                    // byte2: purpose unclear — raw editor only.
                    ui.horizontal(|ui| {
                        let b2 = ((*v >> 16) & 0xFF) as u8;
                        ui.label(RichText::new("   flags (byte2):").small().color(Color32::GRAY));
                        let mut lb = b2;
                        if ui.add(egui::DragValue::new(&mut lb).range(0..=255).hexadecimal(2, false, false)).changed() {
                            *v = (*v & !0xFF0000) | ((lb as u32) << 16);
                            modified = true;
                        }
                        ui.label(RichText::new("(purpose unclear)").small().color(Color32::DARK_GRAY));
                    });
                } else if i == 0 {
                    // d02[0] "Flags A": bit0 = container (segment's own quad not
                    // drawn; children still render).
                    let v = &mut new_seg.data_02[i];
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("[00] Segment flags").small().color(Color32::YELLOW));
                        let mut container = (*v & 1) != 0;
                        if ui.checkbox(&mut container, "Container (don't draw own quad)").changed() {
                            if container { *v |= 1; } else { *v &= !1; }
                            modified = true;
                        }
                        let mut raw = *v;
                        if ui.add(egui::DragValue::new(&mut raw).hexadecimal(8, false, false)).changed() { *v = raw; modified = true; }
                    });
                } else if i == 1 {
                    // d02[1] "Orientation & enable": bit0 enable (mandatory),
                    // 0x4 orient-enable, 0x8 orient source (velocity/camera), 0x10 camera billboard.
                    let v = &mut new_seg.data_02[i];
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("[01] Orientation & enable").small().color(Color32::YELLOW));
                        ui.label(RichText::new(format!("0x{:08X}", *v)).small().color(Color32::DARK_GRAY));
                    });
                    let bits: [(u32, &str); 4] = [
                        (0x1, "Enable"), (0x4, "Orient"),
                        (0x8, "Orient-src"), (0x10, "Billboard"),
                    ];
                    ui.horizontal_wrapped(|ui| {
                        for (mask, name) in bits {
                            let mut on = (*v & mask) != 0;
                            if ui.checkbox(&mut on, name).changed() {
                                if on { *v |= mask; } else { *v &= !mask; }
                                modified = true;
                            }
                        }
                    });
                    let mut raw = *v;
                    if ui.add(egui::DragValue::new(&mut raw).hexadecimal(8, false, false).prefix("raw ")).changed() { *v = raw; modified = true; }
                } else if i == 2 {
                    // d02[2] "Transform / parent inheritance" (runtime master flags).
                    let v = &mut new_seg.data_02[i];
                    let b1 = ((*v >> 8) & 0xFF) as u8;
                    let b2 = ((*v >> 16) & 0xFF) as u8;
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("[02] Transform / parent inheritance").small().color(Color32::YELLOW));
                        ui.label(RichText::new(format!("0x{:08X}", *v)).small().color(Color32::DARK_GRAY));
                    });
                    // Byte 1: parent inheritance (pos/scale) — bits 0x10 pos-live,
                    // 0x20 attach, 0x40 scale-live, 0x80 pos-frozen-at-spawn
                    ui.horizontal(|ui| {
                        ui.label("   Inherit:");
                        let mut fb = b1;
                        if ui.add(egui::DragValue::new(&mut fb).range(0..=255).hexadecimal(2, false, false)).changed() {
                            *v = (*v & !0xFF00) | ((fb as u32) << 8);
                            modified = true;
                        }
                        // Show bit breakdown
                        for bit in 0..8 {
                            let mask = 1u32 << (bit + 8);
                            let mut on = (*v & mask) != 0;
                            if ui.checkbox(&mut on, "").changed() {
                                if on { *v |= mask; } else { *v &= !mask; }
                                modified = true;
                            }
                        }
                        let mut fd = String::new();
                        if b1 & 0x10 != 0 { fd.push_str(" pos:live"); }
                        if b1 & 0x20 != 0 { fd.push_str(" attach"); }
                        if b1 & 0x40 != 0 { fd.push_str(" scale:live"); }
                        if b1 & 0x80 != 0 { fd.push_str(" pos:spawn"); }
                        if fd.is_empty() { fd.push_str(" no-inherit"); }
                        ui.label(RichText::new(fd.trim().to_string()).small().color(Color32::YELLOW));
                    });
                    // Byte 2: orientation / spawn-frozen inheritance bits
                    ui.horizontal(|ui| {
                        ui.label("   Orient:");
                        let mut ib = b2;
                        if ui.add(egui::DragValue::new(&mut ib).range(0..=255).hexadecimal(2, false, false)).changed() {
                            *v = (*v & !0xFF0000) | ((ib as u32) << 16);
                            modified = true;
                        }
                        let desc = match b2 {
                            0x00 => "own rot/scale",
                            0x01 => "rot: parent @spawn",
                            0x02 => "scale: parent @spawn",
                            0x03 => "rot+scale: parent @spawn",
                            0x08 => "follow trajectory (d0C rotates quad)",
                            _ => "bits: 1=rot@spawn 2=scale@spawn 8=follow-traj",
                        };
                        ui.label(RichText::new(desc).small().color(Color32::YELLOW));
                    });
                } else if i == 3 {
                    // d02[3]: byte1 = draw order (Z); bytes 2/3 = mesh subdivisions
                    // (radial / stacks) for non-quad shapes.
                    let v = &mut new_seg.data_02[i];
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("[03] Draw order").small().color(Color32::YELLOW));
                        let mut z = ((*v >> 8) & 0xFF) as u8;
                        if ui.add(egui::DragValue::new(&mut z).range(0..=255)).changed() {
                            *v = (*v & !0xFF00) | ((z as u32) << 8);
                            modified = true;
                        }
                        ui.label(RichText::new("(higher = on top)").small().color(Color32::GRAY));
                    });
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("   Mesh subdiv:").small().color(Color32::YELLOW));
                        let mut na = ((*v >> 24) & 0xFF) as u8;
                        let mut nb = ((*v >> 16) & 0xFF) as u8;
                        ui.label("radial");
                        if ui.add(egui::DragValue::new(&mut na).range(0..=255)).changed() { *v = (*v & !0xFF000000) | ((na as u32) << 24); modified = true; }
                        ui.label("stacks");
                        if ui.add(egui::DragValue::new(&mut nb).range(0..=255)).changed() { *v = (*v & !0xFF0000) | ((nb as u32) << 16); modified = true; }
                        ui.label(RichText::new("(non-quad shapes)").small().color(Color32::GRAY));
                    });
                } else {
                    ui.horizontal(|ui| {
                        ui.label(format!("[{:02}]", i));
                        if d02_is_float[i] {
                            let mut fv = f32::from_bits(new_seg.data_02[i]);
                            if ui.add(egui::DragValue::new(&mut fv).speed(0.01)).changed() {
                                modified = true; new_seg.data_02[i] = fv.to_bits();
                            }
                        } else {
                            let mut v = new_seg.data_02[i];
                            if ui.add(egui::DragValue::new(&mut v)).changed() {
                                modified = true; new_seg.data_02[i] = v;
                            }
                            ui.label(RichText::new(format!("0x{:08X}", v)).small().color(Color32::DARK_GRAY));
                            let fv = f32::from_bits(v);
                            if (fv - 0.0).abs() > 0.0001 && fv < 1000.0 && fv > -1000.0 {
                                ui.label(RichText::new(format!("----{:.4}f", fv)).small().color(Color32::GRAY));
                            }
                        }
                        if i < d02_hints.len() && !d02_hints[i].is_empty() {
                            ui.label(RichText::new(d02_hints[i]).small().color(Color32::YELLOW));
                        }
                    });
            }
            }
        });

        // d04: 12 f32
        let d04_hints = ["Crop Left", "Crop Top", "Crop Right", "Crop Bottom", "Lifetime (s)",
            "Unknown", "Unknown", "Unknown", "Init Y velocity (min)", "Init Y velocity (max)",
            "Gravity", "Bounce (floor restitution)"];
        ui.collapsing(format!("d04 (12 x f32)"), |ui| {
            for i in 0..12 {
                let mut v = new_seg.data_04[i];
                ui.horizontal(|ui| {
                    ui.label(format!("[{:02}]", i));
                    if ui.add(egui::DragValue::new(&mut v).speed(0.01).fixed_decimals(3)).changed() {
                        modified = true; new_seg.data_04[i] = v;
                    }
                    ui.label(RichText::new(format!("0x{:08X}", v.to_bits())).small().color(Color32::DARK_GRAY));
                    if i < d04_hints.len() && !d04_hints[i].is_empty() {
                        ui.label(RichText::new(d04_hints[i]).small().color(Color32::YELLOW));
                    }
                });
            }
        });

        // d06: 9 f32 — base orientation (5,6,7 = Euler rot deg; 8 = Y offset)
        let d06_hints = ["Unknown", "Unknown", "Unknown", "Unknown", "Unknown",
            "Base rotation X (deg)", "Base rotation Y (deg)", "Base rotation Z (deg)", "Base Y offset"];
        ui.collapsing(format!("d06 (base orientation)"), |ui| {
            for i in 0..9 {
                let mut v = new_seg.data_06[i];
                ui.horizontal(|ui| {
                    ui.label(format!("[{:02}]", i));
                    if ui.add(egui::DragValue::new(&mut v).speed(0.01)).changed() { modified = true; new_seg.data_06[i] = v; }
                    ui.label(RichText::new(format!("0x{:08X}", v.to_bits())).small().color(Color32::DARK_GRAY));
                    if i < d06_hints.len() && !d06_hints[i].is_empty() {
                        ui.label(RichText::new(d06_hints[i]).small().color(Color32::YELLOW));
                    }
                });
            }
        });

        // d08: 8 f32 — meaning depends on the shape byte (data_02[4] & 0xFF).
        let shape = (new_seg.data_02[4] & 0xFF) as u8;
        let (d08_title, d08_hints): (&str, [&str; 8]) = match shape {
            0x00 => ("d08 (unused for quads — not read by game; aspect = Scale d0B)",
                ["unused", "", "", "", "unused", "", "", ""]),
            0x02 | 0x01 | 0x08 => ("d08 (cylinder: radius/height)",
                ["Radius 0", "Height 0", "", "", "Radius 1", "Height 1", "", ""]),
            0x04 | 0x06 => ("d08 (sphere/dome radii)",
                ["Radius horizontal", "Radius vertical", "", "", "", "", "", ""]),
            0x14 | 0x15 => ("d08 (trail head/tail colors)",
                ["Head R", "Head G", "Head B", "Head A", "Tail R", "Tail G", "Tail B", "Tail A"]),
            _ => ("d08 (8 x f32)", ["", "", "", "", "", "", "", ""]),
        };
        ui.collapsing(d08_title.to_string(), |ui| {
            for i in 0..8 {
                let mut v = new_seg.data_08[i];
                ui.horizontal(|ui| {
                    ui.label(format!("[{:02}]", i));
                    if ui.add(egui::DragValue::new(&mut v).speed(0.01)).changed() { modified = true; new_seg.data_08[i] = v; }
                    if !d08_hints[i].is_empty() {
                        ui.label(RichText::new(d08_hints[i]).small().color(Color32::YELLOW));
                    }
                });
            }
        });

        // Optional blocks - extract before collapsing to avoid borrow conflicts
        let mut opt_d03 = new_seg.data_03;
        if opt_d03.is_some() {
            ui.collapsing("d03 (2 x f32)", |ui| {
                let mut arr = opt_d03.unwrap();
                for i in 0..2 {
                    if ui.add(egui::DragValue::new(&mut arr[i]).speed(0.01)).changed() { modified = true; }
                }
                if modified { opt_d03 = Some(arr); }
            });
        }
        let mut opt_d05 = new_seg.data_05;
        if opt_d05.is_some() {
            ui.collapsing("d05 (3 x f32)", |ui| {
                let mut arr = opt_d05.unwrap();
                for i in 0..3 {
                    if ui.add(egui::DragValue::new(&mut arr[i]).speed(0.01)).changed() { modified = true; }
                }
                if modified { opt_d05 = Some(arr); }
            });
        }
        let mut opt_d07 = new_seg.data_07;
        if opt_d07.is_some() {
            ui.collapsing("d07 (4 x f32)", |ui| {
                let mut arr = opt_d07.unwrap();
                for i in 0..4 {
                    if ui.add(egui::DragValue::new(&mut arr[i]).speed(0.01)).changed() { modified = true; }
                }
                if modified { opt_d07 = Some(arr); }
            });
        }
        if modified {
            new_seg.data_03 = opt_d03;
            new_seg.data_05 = opt_d05;
            new_seg.data_07 = opt_d07;
        }

    // Animation tracks (keyframes): each track is a list of keyframes with a
    // value, a time, and a mode (additive / uniform / random / loop).
    egui::collapsing_header::CollapsingHeader::new("Animation tracks (keyframes)")
        .default_open(true)
        .show(ui, |ui| {
        edit_array(ui, "Position (d09) — x, y, z", &mut new_seg.data_09, &mut modified, ArrayMode::Floats);
        edit_array(ui, "Rotation (d0A) — Euler deg", &mut new_seg.data_0a, &mut modified, ArrayMode::Floats);
        edit_array(ui, "Scale (d0B)", &mut new_seg.data_0b, &mut modified, ArrayMode::Floats);
        edit_array(ui, "Rotation 2 (d0C) — Euler deg", &mut new_seg.data_0c, &mut modified, ArrayMode::Floats);
        edit_array(ui, "Color multiply / tint (d0D)", &mut new_seg.data_0d, &mut modified, ArrayMode::Color);
        edit_array(ui, "Color add / glow (d0E)", &mut new_seg.data_0e, &mut modified, ArrayMode::Color);
    });

    // Children / spawn descriptors (d14) — separate from keyframes
    edit_children(ui, "Spawns / children (d14)", &mut new_seg.data_14, &mut modified);

    // Conditional arrays
    if !new_seg.data_0f.is_empty() { edit_array(ui, "d0F", &mut new_seg.data_0f, &mut modified, ArrayMode::Hex); }
    if !new_seg.data_10.is_empty() { edit_array(ui, "d10", &mut new_seg.data_10, &mut modified, ArrayMode::Hex); }
    if !new_seg.data_11.is_empty() { edit_array(ui, "d11", &mut new_seg.data_11, &mut modified, ArrayMode::Hex); }
    if !new_seg.data_12.is_empty() { edit_array(ui, "d12", &mut new_seg.data_12, &mut modified, ArrayMode::Hex); }

    // Write back
    if modified {
        app.ensure_snapshot();
        if let Some(ref mut eff_mut) = app.effect {
            eff_mut.segments[idx] = new_seg.clone();
        }
    }
    });
}



/// Build a parent -> children map from d14 records.

fn build_children_map(eff: &EffFile) -> std::collections::HashMap<usize, Vec<usize>> {

    let mut map: std::collections::HashMap<usize, Vec<usize>> = std::collections::HashMap::new();

    for (parent_idx, seg) in eff.segments.iter().enumerate() {

        for rec in &seg.data_14 {

            let u0 = rec.floats[0].to_bits();

            let child_idx = ((u0 >> 8) & 0xFF) as usize;

            if child_idx < eff.segments.len() {

                map.entry(parent_idx).or_default().push(child_idx);

            }

        }

    }

    map

}

/// Reparent a segment: move `src_idx` from its current parent to `new_parent_idx`.
/// If `new_parent_idx` is None, the segment becomes a root (removed from all d14).
/// Returns false if the reparent would create a cycle.
fn reparent_segment(eff: &mut EffFile, src_idx: usize, new_parent_idx: Option<usize>) -> bool {
    // Cycle check: attaching src under target creates a cycle iff target == src or
    // target is already inside src's own subtree. Test that by walking *down* from
    // src over the real d14 child graph (robust to nodes with multiple parents —
    // the old parent-chain walk missed those and let a cycle through → stack overflow).
    if let Some(target) = new_parent_idx {
        if target == src_idx || subtree_contains(eff, src_idx, target) {
            return false;
        }
    }
    // Remove src from all existing d14 entries
    for seg in eff.segments.iter_mut() {
        seg.data_14.retain(|rec| {
            let child = ((rec.floats[0].to_bits() >> 8) & 0xFF) as usize;
            child != src_idx
        });
    }
    // Add to new parent's d14 if specified
    if let Some(parent) = new_parent_idx {
        if parent < eff.segments.len() {
            let mut rec = ArrayRecord48::default_record();
            let bits = rec.floats[0].to_bits();
            rec.floats[0] = f32::from_bits((bits & !0xFF00) | ((src_idx as u32) << 8));
            rec.floats[0] = f32::from_bits(rec.floats[0].to_bits() | (1u32 << 24));
            rec.ints[0] = (1.0f32 / 30.0).to_bits();
            rec.floats[8] = 0.0;
            eff.segments[parent].data_14.push(rec);
        }
    }
    true
}

/// Does `root`'s subtree (following d14 child links) contain `needle`?
/// Iterative DFS with a visited guard, so it terminates even on a cyclic graph.
fn subtree_contains(eff: &EffFile, root: usize, needle: usize) -> bool {
    let mut stack = vec![root];
    let mut visited = std::collections::HashSet::new();
    while let Some(n) = stack.pop() {
        if n == needle { return true; }
        if !visited.insert(n) { continue; }
        if let Some(seg) = eff.segments.get(n) {
            for rec in &seg.data_14 {
                let child = ((rec.floats[0].to_bits() >> 8) & 0xFF) as usize;
                if child < eff.segments.len() { stack.push(child); }
            }
        }
    }
    false
}

/// Delete a segment and fix all d14 references.
fn delete_segment(eff: &mut EffFile, idx: usize) {
    if idx >= eff.segments.len() { return; }
    // Remove all d14 entries pointing to this segment
    for seg in eff.segments.iter_mut() {
        seg.data_14.retain(|rec| {
            let child = ((rec.floats[0].to_bits() >> 8) & 0xFF) as usize;
            child != idx
        });
    }
    // Shift down all child indices > idx in remaining d14 entries
    for seg in eff.segments.iter_mut() {
        for rec in seg.data_14.iter_mut() {
            let bits = rec.floats[0].to_bits();
            let child = ((bits >> 8) & 0xFF) as usize;
            if child > idx {
                let new_bits = (bits & !0xFF00) | (((child - 1) as u32) << 8);
                rec.floats[0] = f32::from_bits(new_bits);
            }
        }
    }
    // Remove the segment
    eff.segments.remove(idx);
}



/// Compute cumulative delay from root to a given segment.

fn compute_delay_to(eff: &EffFile, children_map: &std::collections::HashMap<usize, Vec<usize>>, target: usize) -> f32 {

    // Walk parents until root

    let mut current = target;

    let mut total = 0.0f32;

    // Find parent by scanning all segments' d14

    loop {

        let mut found_parent = None;

        let mut found_delay = 0.0f32;

        for (p_idx, seg) in eff.segments.iter().enumerate() {

            for rec in &seg.data_14 {

                let u0 = rec.floats[0].to_bits();

                let c = ((u0 >> 8) & 0xFF) as usize;

                if c == current {

                    found_parent = Some(p_idx);

                    found_delay = rec.floats[8];

                    break;

                }

            }

            if found_parent.is_some() { break; }

        }

        match found_parent {

            Some(p) => { total += found_delay; current = p; }

            None => break,

        }

    }

    total

}



/// Recursive tree view for segment hierarchy.
fn show_segment_tree(
    ui: &mut egui::Ui,
    idx: usize,
    eff: &EffFile,
    children_map: &std::collections::HashMap<usize, Vec<usize>>,
    selected: &mut Option<usize>,
    depth: usize,
    hidden_nodes: &mut std::collections::HashSet<usize>,
    drag_source: &mut Option<usize>,
    drag_target: &mut Option<usize>,
    drag_label: &mut String,
) {
    // Safety net: never recurse unboundedly (guards against a malformed/cyclic d14).
    if depth > 128 { return; }

    let seg = &eff.segments[idx];
    let indent = "  ".repeat(depth);
    let label = format!("{}[{}] {}", indent, idx, seg.name);
    let is_selected = *selected == Some(idx);
    let is_drag_src = *drag_source == Some(idx);
    let is_dragging = drag_source.is_some();

    let text_color = if is_drag_src {
        Color32::from_rgba_unmultiplied(150, 150, 150, 200)
    } else if is_selected {
        Color32::WHITE
    } else {
        Color32::LIGHT_GRAY
    };

    // Allocate the row as a single interactive widget so hit-testing is reliable
    // (the previous manual painter + overlapping ui.interact() rects only let the
    // topmost/root row receive clicks — clicks on children silently did nothing).
    let row_h = 20.0;
    let (row_rect, resp) = ui.allocate_exact_size(
        egui::vec2(ui.available_width(), row_h),
        egui::Sense::click_and_drag(),
    );
    let cb_rect = egui::Rect::from_min_size(row_rect.left_top(), egui::vec2(18.0, row_h));
    let label_rect = egui::Rect::from_min_max(
        egui::pos2(cb_rect.right() + 4.0, row_rect.top()),
        row_rect.right_bottom(),
    );

    // Did the interaction land on the checkbox column?
    let on_checkbox = resp
        .interact_pointer_pos()
        .map_or(false, |p| p.x <= cb_rect.right());

    let mut visible = !hidden_nodes.contains(&idx);
    if resp.clicked() {
        if on_checkbox {
            if visible { hidden_nodes.insert(idx); visible = false; }
            else { hidden_nodes.remove(&idx); visible = true; }
        } else {
            *selected = Some(idx);
        }
    }
    if resp.drag_started() && !on_checkbox {
        *drag_source = Some(idx);
        *drag_label = label.clone();
    }

    // Drop-target highlight (pointer_latest_pos works while dragging).
    let hovered = ui.ctx().pointer_latest_pos()
        .map_or(false, |p| label_rect.contains(p));
    let is_drop_target = is_dragging && !is_drag_src && hovered;
    if is_drop_target { *drag_target = Some(idx); }

    // Backgrounds
    if is_drop_target {
        ui.painter().rect_filled(label_rect, 0.0, Color32::from_rgba_unmultiplied(60, 160, 255, 160));
        ui.painter().rect_stroke(label_rect, 0.0, egui::Stroke::new(2.0, Color32::from_rgb(80, 200, 255)));
    } else if is_drag_src {
        ui.painter().rect_filled(label_rect, 0.0, Color32::from_rgba_unmultiplied(80, 80, 80, 80));
    } else if is_selected {
        ui.painter().rect_filled(label_rect, 0.0, Color32::from_rgba_unmultiplied(60, 60, 180, 80));
    } else if resp.hovered() && !is_dragging {
        ui.painter().rect_filled(label_rect, 0.0, Color32::from_rgba_unmultiplied(255, 255, 255, 15));
    }

    // Checkbox glyph
    ui.painter().text(
        cb_rect.center(), egui::Align2::CENTER_CENTER,
        if visible { "☑" } else { "☐" },
        egui::FontId::proportional(13.0),
        if resp.hovered() && on_checkbox { Color32::WHITE } else { Color32::LIGHT_GRAY },
    );
    // Label text
    ui.painter().text(
        label_rect.left_center(), egui::Align2::LEFT_CENTER,
        &label,
        egui::FontId::monospace(13.0),
        text_color,
    );

    if let Some(children) = children_map.get(&idx) {
        for &child in children {
            show_segment_tree(ui, child, eff, children_map, selected, depth + 1, hidden_nodes, drag_source, drag_target, drag_label);
        }
    }
}



#[derive(Debug, Clone)]

struct AnimKeyframe {

    time: f32,

    pos: [f32; 4],

    rot: [f32; 4],

    scale: [f32; 4],

    unk: [f32; 4],

    color1: [f32; 4],

    color2: [f32; 4],

}



fn extract_keyframes(seg: &eff_core::core::Segment) -> Vec<AnimKeyframe> {

    let arrays = [&seg.data_09, &seg.data_0a, &seg.data_0b, &seg.data_0c, &seg.data_0d, &seg.data_0e];

    let mut times: Vec<f32> = Vec::new();

    for arr in arrays {

        for rec in arr.iter() {

            let t = rec.floats[8];

            if !times.contains(&t) { times.push(t); }

        }

    }

    times.sort_by(|a, b| a.partial_cmp(b).unwrap());



    times.iter().map(|&t| {

        let lerp_arr = |arr: &Vec<eff_core::core::ArrayRecord48>, idx: usize| -> [f32; 4] {

            if arr.is_empty() { return [0.0; 4]; }

            let mut before: Option<&eff_core::core::ArrayRecord48> = None;

            let mut after: Option<&eff_core::core::ArrayRecord48> = None;

            for rec in arr.iter() {

                if rec.floats[8] <= t { before = Some(rec); }

                if rec.floats[8] >= t && after.is_none() { after = Some(rec); }

            }

            if after.is_none() { after = arr.last(); }

            if before.is_none() { before = arr.first(); }

            match (before, after) {

                (Some(b), Some(a)) if (a.floats[8] - b.floats[8]).abs() > 0.0001 => {

                    let frac = (t - b.floats[8]) / (a.floats[8] - b.floats[8]);

                    [

                        b.floats[idx] + (a.floats[idx] - b.floats[idx]) * frac,

                        b.floats[idx+1] + (a.floats[idx+1] - b.floats[idx+1]) * frac,

                        b.floats[idx+2] + (a.floats[idx+2] - b.floats[idx+2]) * frac,

                        b.floats[idx+3] + (a.floats[idx+3] - b.floats[idx+3]) * frac,

                    ]

                }

                (Some(b), _) => [b.floats[idx], b.floats[idx+1], b.floats[idx+2], b.floats[idx+3]],

                _ => [0.0; 4],

            }

        };

        AnimKeyframe {

            time: t,

            pos: lerp_arr(&seg.data_09, 0),

            rot: lerp_arr(&seg.data_0a, 0),

            scale: lerp_arr(&seg.data_0b, 0),

            unk: lerp_arr(&seg.data_0c, 0),

            color1: lerp_arr(&seg.data_0d, 0),

            color2: lerp_arr(&seg.data_0e, 0),

        }

    }).collect()

}



// Track evaluation matching the game's keyframe evaluator (reversed from the
// CS1 exe). Keyframe = ArrayRecord48: floats[0..4] value, floats[4..8] random
// second bound, floats[8] time (+0x20), ints[0] low u16 = flags (+0x24),
// ints[0] high u16 = track type (+0x26, 0 = plain value).
// Flags: bit0 additive (value adds to the previous keyframe's evaluated
// target), bit1 uniform (floats[0] broadcast to xyz, w=0), bit2 random
// (game rolls between the two bounds; we use the midpoint), bit4 loop start,
// bit5 loop end (when reached, time jumps back to the bit4 keyframe).

fn kf_flags(r: &eff_core::core::ArrayRecord48) -> u16 {
    (r.ints[0] & 0xFFFF) as u16
}

fn kf_time(r: &eff_core::core::ArrayRecord48) -> f32 {
    r.floats[8]
}

/// Deterministic per-instance random in [0,1): hashed from the instance seed,
/// keyframe index and component, so a given particle rolls once per keyframe
/// and stays stable from frame to frame (no flicker).
fn kf_rand01(seed: u32, kf_index: usize, comp: usize) -> f32 {
    let mut h = seed ^ 0x9E37_79B9
        ^ (kf_index as u32).wrapping_mul(0x85EB_CA6B)
        ^ (comp as u32).wrapping_mul(0xC2B2_AE35);
    h ^= h >> 16; h = h.wrapping_mul(0x7FEB_352D);
    h ^= h >> 15; h = h.wrapping_mul(0x846C_A68B);
    h ^= h >> 16;
    (h >> 8) as f32 / 16_777_216.0
}

/// Seed for one spawned instance of a segment; the panel reproduces the first
/// instance of a root-level spawn with instance_seed(idx, 0, 0, 0).
fn instance_seed(idx: usize, burst: u32, particle: u32, parent_seed: u32) -> u32 {
    (idx as u32).wrapping_mul(0x9E37_79B9)
        .wrapping_add(burst.wrapping_mul(0x85EB_CA6B))
        .wrapping_add(particle.wrapping_mul(0xC2B2_AE35))
        .wrapping_add(parent_seed.rotate_left(9))
}

/// Evaluate one keyframe's target value; `prev` is the previous keyframe's
/// evaluated target (base for the additive bit). Random keyframes roll
/// uniformly between the two bounds (floats[i] and floats[i+4]) — confirmed
/// exact vs FUN_0044cd20.
fn eval_kf_target(r: &eff_core::core::ArrayRecord48, prev: &[f32; 4], seed: u32, kf_index: usize) -> [f32; 4] {
    let flags = kf_flags(r);
    let comp = |i: usize| -> f32 {
        if flags & 4 != 0 {
            let f = kf_rand01(seed, kf_index, i);
            r.floats[i] + (r.floats[i + 4] - r.floats[i]) * f
        } else {
            r.floats[i]
        }
    };
    let mut v = if flags & 2 != 0 {
        let s = comp(0);
        [s, s, s, 0.0]
    } else {
        [comp(0), comp(1), comp(2), comp(3)]
    };
    if flags & 1 != 0 {
        for k in 0..4 { v[k] += prev[k]; }
    }
    v
}

/// Evaluate a track at time `t`: chained targets, linear interpolation
/// (confirmed exact vs FUN_0044c300 = plain lerp), hold before the first /
/// after the last keyframe, loop via bits 4/5.
fn eval_track48(track: &[eff_core::core::ArrayRecord48], t: f32, default: [f32; 4], seed: u32) -> [f32; 4] {
    if track.is_empty() { return default; }

    // Chain every keyframe's target from the start of the track.
    let mut targets: Vec<[f32; 4]> = Vec::with_capacity(track.len());
    let mut prev = default;
    for (i, r) in track.iter().enumerate() {
        let v = eval_kf_target(r, &prev, seed, i);
        targets.push(v);
        prev = v;
    }

    let mut tt = t;
    let jump = track.iter().position(|r| kf_flags(r) & 0x20 != 0);
    let start = track.iter().position(|r| kf_flags(r) & 0x10 != 0);
    if let (Some(j), Some(s)) = (jump, start) {
        let (t_jump, t_start) = (kf_time(&track[j]), kf_time(&track[s]));
        let period = t_jump - t_start;
        if s <= j && period > 0.0001 && t > t_jump {
            let n = ((t - t_jump) / period).floor() as usize + 1; // jumps taken
            tt = t_start + (t - t_jump) - (n - 1) as f32 * period;
            // Re-chain the loop region once per jump: additive keyframes keep
            // accumulating across iterations, absolute ones make it converge.
            for _ in 0..n.min(1000) {
                let mut prev = targets[j];
                let mut changed = false;
                for i in s..=j {
                    let v = eval_kf_target(&track[i], &prev, seed, i);
                    if v != targets[i] { changed = true; }
                    targets[i] = v;
                    prev = v;
                }
                if !changed { break; }
            }
        }
    }

    if tt <= kf_time(&track[0]) { return targets[0]; }
    let last = track.len() - 1;
    if tt >= kf_time(&track[last]) { return targets[last]; }
    for i in 0..last {
        let (ta, tb) = (kf_time(&track[i]), kf_time(&track[i + 1]));
        if tt >= ta && tt <= tb {
            if tb - ta <= 0.0001 { return targets[i + 1]; }
            let f = (tt - ta) / (tb - ta);
            let (a, b) = (&targets[i], &targets[i + 1]);
            return [
                a[0] + (b[0] - a[0]) * f,
                a[1] + (b[1] - a[1]) * f,
                a[2] + (b[2] - a[2]) * f,
                a[3] + (b[3] - a[3]) * f,
            ];
        }
    }
    targets[last]
}



#[derive(Clone, Copy, PartialEq)]

enum ArrayMode { Floats, Color, Hex }



fn edit_children(
    ui: &mut egui::Ui,
    label: &str,
    arr: &mut Vec<eff_core::core::ArrayRecord48>,
    modified: &mut bool,
) {
    egui::collapsing_header::CollapsingHeader::new(format!("{} ({} children)", label, arr.len()))
        .default_open(true)
        .show(ui, |ui| {
        let mut to_delete: Option<usize> = None;
        for i in 0..arr.len() {
            let rec = &mut arr[i];
            let u0 = rec.floats[0].to_bits();
            let child_idx = ((u0 >> 8) & 0xFF) as usize;
            let count = ((u0 >> 24) & 0xFF) as u8;
            let per_burst = (u0 & 0xFF) as u8;
            let trigger = (rec.floats[1].to_bits() & 0xFF) as u8;

            ui.collapsing(format!("  [{}] delay={:.3} child=seg#{} count={}", i, rec.floats[8], child_idx, count), |ui| {
                ui.horizontal(|ui| {
                    if ui.button("🗑").on_hover_text("Delete this child").clicked() {
                        to_delete = Some(i);
                    }
                });
                ui.horizontal(|ui| {
                    ui.label("child:");
                    let mut ci = child_idx as u32;
                    if ui.add(egui::DragValue::new(&mut ci).range(0..=255)).changed() {
                        let new_u0 = (u0 & !0xFF00) | ((ci as u32) << 8);
                        rec.floats[0] = f32::from_bits(new_u0);
                        *modified = true;
                    }
                });
                ui.horizontal(|ui| {
                    ui.label("count:");
                    let mut cn = count;
                    if ui.add(egui::DragValue::new(&mut cn).range(0..=255)).changed() {
                        let new_u0 = (rec.floats[0].to_bits() & !0xFF000000) | ((cn as u32) << 24);
                        rec.floats[0] = f32::from_bits(new_u0);
                        *modified = true;
                    }
                });
                ui.horizontal(|ui| {
                    ui.label("per-burst:");
                    let mut pb = per_burst;
                    if ui.add(egui::DragValue::new(&mut pb).range(0..=255)).changed() {
                        let new_u0 = (rec.floats[0].to_bits() & !0xFF) | (pb as u32);
                        rec.floats[0] = f32::from_bits(new_u0);
                        *modified = true;
                    }
                });
                ui.horizontal(|ui| {
                    ui.label("trigger:");
                    let trigger_names = ["time", "parent-death", "loop-wrap"];
                    let mut tg = trigger;
                    egui::ComboBox::from_id_salt(format!("trigger_{}", i))
                        .selected_text(*trigger_names.get(tg as usize).unwrap_or(&"?"))
                        .show_ui(ui, |ui| {
                            for (j, name) in trigger_names.iter().enumerate() {
                                if ui.selectable_label(tg == j as u8, *name).clicked() {
                                    let new_u1 = (rec.floats[1].to_bits() & !0xFF) | (j as u32);
                                    rec.floats[1] = f32::from_bits(new_u1);
                                    *modified = true;
                                }
                            }
                        });
                });
                ui.horizontal(|ui| {
                    ui.label("delay:");
                    if ui.add(egui::DragValue::new(&mut rec.floats[8]).speed(0.01)).changed() { *modified = true; }
                });
                ui.horizontal(|ui| {
                    ui.label("interval:");
                    let mut iv = f32::from_bits(rec.ints[0]);
                    if ui.add(egui::DragValue::new(&mut iv).speed(0.01)).changed() { rec.ints[0] = iv.to_bits(); *modified = true; }
                });
            });
        }
        if let Some(del_idx) = to_delete {
            arr.remove(del_idx);
            *modified = true;
        }
        // Add new child button
        if ui.button("+ Add child").clicked() {
            let mut rec = eff_core::core::ArrayRecord48 { floats: [0.0; 9], ints: [0, 0], trailing: 0.0 };
            // Default: child=0, count=1, per-burst=1, delay=0
            rec.floats[0] = f32::from_bits((1u32 << 24) | 1); // count=1, per-burst=1
            rec.ints[0] = (1.0f32 / 30.0).to_bits(); // interval
            rec.floats[8] = 0.0;
            arr.push(rec);
            *modified = true;
        }
    });
}



fn edit_array(
    ui: &mut egui::Ui,
    label: &str,
    arr: &mut Vec<eff_core::core::ArrayRecord48>,
    modified: &mut bool,
    mode: ArrayMode,
) {
    let time_label = if arr.len() > 1 && (arr.last().unwrap().floats[8] - arr.first().unwrap().floats[8]).abs() > 0.001 {
        "t"
    } else { " " };

    ui.collapsing(format!("{} ({} records)", label, arr.len()), |ui| {
        let mut to_delete: Option<usize> = None;
        for i in 0..arr.len() {
            let len = arr.len();
            let rec = &mut arr[i];

            let header = egui::collapsing_header::CollapsingHeader::new(format!("  [{}]  {}={:.3}", i, time_label, rec.floats[8]))
                .default_open(len <= 4);
            header.show(ui, |ui| {
                ui.horizontal(|ui| {
                    if ui.button("🗑").on_hover_text("Delete this keyframe").clicked() {
                        to_delete = Some(i);
                    }
                });
                match mode {
                    ArrayMode::Floats => {
                        for j in 0..4 {
                            if ui.add(egui::DragValue::new(&mut rec.floats[j]).speed(0.01)).changed() { *modified = true; }
                        }
                    }
                    ArrayMode::Color => {
                        let mut cr = rec.floats[0].clamp(0.0, 1.0);
                        let mut cg = rec.floats[1].clamp(0.0, 1.0);
                        let mut cb = rec.floats[2].clamp(0.0, 1.0);
                        let mut ca = rec.floats[3].clamp(0.0, 1.0);
                        let mut col = [cr, cg, cb, ca];
                        ui.horizontal(|ui| {
                            if ui.color_edit_button_rgba_unmultiplied(&mut col).changed() {
                                rec.floats[0] = col[0]; rec.floats[1] = col[1];
                                rec.floats[2] = col[2]; rec.floats[3] = col[3];
                                *modified = true;
                            }
                            ui.label(format!("RGBA: {:.3} {:.3} {:.3} {:.3}", col[0], col[1], col[2], col[3]));
                        });
                    }
                    ArrayMode::Hex => {
                        for j in 0..4 {
                            let mut bits = rec.floats[j].to_bits();
                            if ui.add(egui::DragValue::new(&mut bits).hexadecimal(8, false, false)).changed() {
                                rec.floats[j] = f32::from_bits(bits);
                                *modified = true;
                            }
                        }
                    }
                }
                // Show time and ints
                ui.horizontal(|ui| {
                    ui.label("t:");
                    if ui.add(egui::DragValue::new(&mut rec.floats[8]).speed(0.01)).changed() { *modified = true; }
                });
                // Keyframe mode = low u16 of ints[0] (bit flags).
                ui.horizontal(|ui| {
                    ui.label("mode:");
                    let flag_bits: [(u32, &str); 5] = [
                        (0x1, "Additive"), (0x2, "Uniform"), (0x4, "Random"),
                        (0x10, "Loop-start"), (0x20, "Loop-end"),
                    ];
                    for (mask, name) in flag_bits {
                        let mut on = (rec.ints[0] & mask) != 0;
                        if ui.checkbox(&mut on, name).changed() {
                            if on { rec.ints[0] |= mask; } else { rec.ints[0] &= !mask; }
                            *modified = true;
                        }
                    }
                });
                ui.horizontal(|ui| {
                    ui.label(RichText::new("raw ints:").small().color(Color32::GRAY));
                    if ui.add(egui::DragValue::new(&mut rec.ints[0]).hexadecimal(8, false, false)).changed() { *modified = true; }
                    if ui.add(egui::DragValue::new(&mut rec.ints[1]).hexadecimal(8, false, false)).changed() { *modified = true; }
                });
                // When Random is set, floats[4..8] are the second (random) bound.
                if rec.ints[0] & 0x4 != 0 {
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("random bound:").small().color(Color32::LIGHT_BLUE));
                        for j in 4..8 {
                            if ui.add(egui::DragValue::new(&mut rec.floats[j]).speed(0.01)).changed() { *modified = true; }
                        }
                    });
                }

                if (rec.trailing - 0.0).abs() > 0.0001 || rec.ints[0] != 0 || rec.ints[1] != 0 {
                    ui.horizontal(|ui| {
                        ui.label(format!("trail={:.4}", rec.trailing));
                    });
                }
            });
        }
        if let Some(del_idx) = to_delete {
            arr.remove(del_idx);
            *modified = true;
        }
        // Add new keyframe button
        if ui.button("+ Add keyframe").clicked() {
            let last_time = arr.last().map(|r| r.floats[8]).unwrap_or(0.0);
            let default_val = match mode {
                ArrayMode::Color => [1.0, 1.0, 1.0, 1.0],
                _ => [0.0, 0.0, 0.0, 0.0],
            };
            // For Scale track (d0B), default to 1,1,1,1
            let is_scale = label.contains("Scale");
            let val = if is_scale { [1.0, 1.0, 1.0, 1.0] } else { default_val };
            let mut rec = eff_core::core::ArrayRecord48 {
                floats: [val[0], val[1], val[2], val[3], 0.0, 0.0, 0.0, 0.0, last_time + 0.5],
                ints: [0, 0],
                trailing: 0.0,
            };
            arr.push(rec);
            *modified = true;
        }
    });
}



fn color_f32(v: f32) -> Color32 {

    if v == 0.0 { Color32::DARK_GRAY }

    else if v == 1.0 { Color32::GREEN }

    else if (0.0..=1.0).contains(&v) { Color32::LIGHT_BLUE }

    else if (1.0..=100.0).contains(&v) { Color32::YELLOW }

    else { Color32::LIGHT_RED }

}



impl EffEditorApp {

    fn show_preview_panel(&mut self, ctx: &egui::Context) {

        let has_tex = !self.texture_handles.is_empty();

        let eff = self.effect.as_ref();

        let seg_idx = self.selected_segment;

        let seg = eff.and_then(|e| seg_idx.and_then(|i| e.segments.get(i)));



        SidePanel::right("preview").resizable(true).default_width(300.0).show(ctx, |ui| {

            ui.heading("Preview");

            if !has_tex {

                ui.label("Load a texture first (set Asset dir in bottom bar)");

                return;

            }

            let eff = match self.effect.as_ref() { Some(e) => e, None => return };

            let children_map = build_children_map(eff);

            let all_children: std::collections::HashSet<usize> = children_map.values().flatten().copied().collect();

            let roots: Vec<usize> = (0..eff.segments.len()).filter(|i| !all_children.contains(i)).collect();

            if roots.is_empty() {

                ui.label("No root segments found.");

                return;

            }



            // Calculate max animation time across all segments (spawn delay + track length)

            let max_time: f32 = (0..eff.segments.len())

                .filter_map(|i| {
                    extract_keyframes(&eff.segments[i]).last()
                        .map(|k| compute_delay_to(eff, &children_map, i) + k.time)
                })

                .fold(0.0f32, |a, b| a.max(b))

                .max(1.0);

            // Loop playback
            if self.anim_playing && self.anim_time as f32 > max_time {
                self.anim_time = 0.0;
            }



            // Controls

            ui.horizontal(|ui| {

                if ui.button(if self.anim_playing { "Pause" } else { "Play" }).clicked() {

                    self.anim_playing = !self.anim_playing;

                }

                if ui.button("Stop").clicked() { self.anim_time = 0.0; self.anim_playing = false; }

                let mut t = self.anim_time as f32;

                if ui.add(egui::Slider::new(&mut t, 0.0..=max_time).text("t")).changed() {

                    self.anim_time = t as f64;

                }

                ui.add(egui::DragValue::new(&mut self.anim_speed).speed(0.1).range(0.1..=10.0));

                ui.label("x");

            });



            // Zoom + reset view

            ui.horizontal(|ui| {

                if ui.small_button("Front").clicked() { self.orbit_yaw = 0.0; self.orbit_pitch = 0.0; }
                if ui.small_button("3D view").on_hover_text("Angle de biais type caméra de combat, pour voir les effets qui bougent en profondeur (runhorse, projectiles).").clicked() {
                    self.orbit_yaw = 0.35; self.orbit_pitch = 0.30;
                }
                if ui.small_button("Fit").on_hover_text("Ajuste le zoom pour montrer tout l'effet (molette pour zoomer/dézoomer).").clicked() { self.fit_pending = true; }
                if ui.small_button("Reset").clicked() { self.orbit_yaw = 0.0; self.orbit_pitch = 0.0; self.zoom = 1.0; }

                ui.add(egui::DragValue::new(&mut self.zoom).speed(0.01).range(0.001..=20.0).prefix("Zoom: "));

                ui.checkbox(&mut self.force_billboard, "Force billboard")
                    .on_hover_text("Simule le flag de spawn du jeu (+0x158 & 0x10) : billboard caméra pour tout l'effet.\nÀ cocher pour les effets UI (mk_talk...) dont les segments n'ont pas de bits d'orientation dans d02[1].");

                egui::ComboBox::from_id_salt("bg_mode")
                    .selected_text(match self.bg_mode { 1 => "BG: black", 2 => "BG: white", 3 => "BG: magenta", _ => "BG: checker" })
                    .show_ui(ui, |ui| {
                        ui.selectable_value(&mut self.bg_mode, 0, "checker");
                        ui.selectable_value(&mut self.bg_mode, 1, "black");
                        ui.selectable_value(&mut self.bg_mode, 2, "white");
                        ui.selectable_value(&mut self.bg_mode, 3, "magenta");
                    });

                if ui.small_button(if self.gif_recording { "Recording..." } else { "Copy GIF" }).clicked() && !self.gif_recording {
                    self.gif_recording = true;
                    self.gif_frame_time = 0.0;
                    self.anim_time = 0.0;
                    self.anim_playing = false;
                    self.gif_captured.lock().unwrap().clear();
                    self.status = "Recording GIF...".into();
                }

            });



            // Copy hex — raw bytes of selected segment for Cheat Engine

            if ui.small_button("Copy hex").clicked() {

                if let (Some(ref eff), Some(idx)) = (&self.effect, self.selected_segment) {

                    if let Some(seg) = eff.segments.get(idx) {

                        let mut hex = String::new();
                        let h = |data: &[u8]| data.iter().map(|b| format!("{:02X}", b)).collect::<Vec<_>>().join(" ");

                        // Fixed data blocks
                        hex.push_str(&format!("d02: {}\n", h(&seg.data_02.iter().flat_map(|v| v.to_le_bytes()).collect::<Vec<_>>())));
                        if let Some(ref d) = seg.data_03 { hex.push_str(&format!("d03: {}\n", h(&d.iter().flat_map(|v| v.to_le_bytes()).collect::<Vec<_>>()))); }
                        hex.push_str(&format!("d04: {}\n", h(&seg.data_04.iter().flat_map(|v| v.to_le_bytes()).collect::<Vec<_>>())));
                        if let Some(ref d) = seg.data_05 { hex.push_str(&format!("d05: {}\n", h(&d.iter().flat_map(|v| v.to_le_bytes()).collect::<Vec<_>>()))); }
                        hex.push_str(&format!("d06: {}\n", h(&seg.data_06.iter().flat_map(|v| v.to_le_bytes()).collect::<Vec<_>>())));
                        if let Some(ref d) = seg.data_07 { hex.push_str(&format!("d07: {}\n", h(&d.iter().flat_map(|v| v.to_le_bytes()).collect::<Vec<_>>()))); }
                        hex.push_str(&format!("d08: {}\n", h(&seg.data_08.iter().flat_map(|v| v.to_le_bytes()).collect::<Vec<_>>())));

                        // Array blocks (48-byte records)
                        let dump_arr = |label: &str, arr: &[eff_core::core::ArrayRecord48]| -> String {
                            if arr.is_empty() { return String::new(); }
                            let mut s = format!("{} ({}):\n", label, arr.len());
                            for (i, r) in arr.iter().enumerate() {
                                let raw = unsafe { std::slice::from_raw_parts(r as *const _ as *const u8, 48) };
                                s.push_str(&format!("  [{}] {}\n", i, h(raw)));
                            }
                            s
                        };
                        hex.push_str(&dump_arr("d09", &seg.data_09));
                        hex.push_str(&dump_arr("d0A", &seg.data_0a));
                        hex.push_str(&dump_arr("d0B", &seg.data_0b));
                        hex.push_str(&dump_arr("d0C", &seg.data_0c));
                        hex.push_str(&dump_arr("d0D", &seg.data_0d));
                        hex.push_str(&dump_arr("d0E", &seg.data_0e));
                        if !seg.data_0f.is_empty() { hex.push_str(&dump_arr("d0F", &seg.data_0f)); }
                        if !seg.data_10.is_empty() { hex.push_str(&dump_arr("d10", &seg.data_10)); }
                        if !seg.data_11.is_empty() { hex.push_str(&dump_arr("d11", &seg.data_11)); }
                        if !seg.data_12.is_empty() { hex.push_str(&dump_arr("d12", &seg.data_12)); }
                        for (i, arr) in seg.data_13.iter().enumerate() { hex.push_str(&dump_arr(&format!("d13[{}]", i), arr)); }
                        hex.push_str(&dump_arr("d14", &seg.data_14));

                        ctx.output_mut(|o| o.copied_text = hex);
                        self.status = format!("Segment [{}] hex copied!", idx);

                    }

                }

            }



            // Draw area

            let available = ui.available_size();

            let size = available.x.min(available.y - 40.0).max(100.0);

            let center = ui.next_widget_position() + egui::vec2(size / 2.0, size / 2.0);

            let (resp, painter) = ui.allocate_painter(egui::vec2(size, size), egui::Sense::click_and_drag());



            // Mouse orbit

            if resp.dragged() {

                self.orbit_yaw += resp.drag_delta().x * 0.01;

                self.orbit_pitch += resp.drag_delta().y * 0.01;

                self.orbit_pitch = self.orbit_pitch.clamp(-1.5, 1.5);

            }

            if resp.double_clicked() {

                self.orbit_yaw = 0.0;

                self.orbit_pitch = 0.0;

            }

            // Mouse wheel zoom (when hovering the preview)
            if resp.hovered() {
                let scroll = ui.input(|i| i.raw_scroll_delta.y);
                if scroll != 0.0 {
                    self.zoom = (self.zoom * (1.0 + scroll * 0.001)).clamp(0.001, 20.0);
                }
            }



            let t = (self.anim_time as f32).min(max_time);



            // Background (checker / solid). Solid backgrounds help see dark,
            // subtractive or low-alpha content (runhorse, gameover).
            match self.bg_mode {
                1 => { painter.rect_filled(resp.rect, 0.0, Color32::BLACK); }
                2 => { painter.rect_filled(resp.rect, 0.0, Color32::WHITE); }
                3 => { painter.rect_filled(resp.rect, 0.0, Color32::from_rgb(255, 0, 255)); }
                _ => {
                    let cs = 8.0;
                    let mut x = resp.rect.left(); let mut row = 0;
                    while x < resp.rect.right() {
                        let mut y = resp.rect.top(); let mut col = 0;
                        while y < resp.rect.bottom() {
                            let c = if (row + col) % 2 == 0 { Color32::from_gray(45) } else { Color32::from_gray(60) };
                            painter.rect_filled(egui::Rect::from_min_size(egui::pos2(x, y), egui::vec2(cs, cs)), 0.0, c);
                            y += cs; col += 1;
                        }
                        x += cs; row += 1;
                    }
                }
            }



            // Collect all draw commands, then sort by Z globally
            let mut draws: Vec<DrawCmd> = Vec::new();
            let mut selected_outline: Vec<[egui::Pos2; 4]> = Vec::new();

            if !self.texture_handles.is_empty() {

                for &root_idx in &roots {

                    render_node_hierarchy(

                        &mut draws, &mut selected_outline, self.selected_segment,
                        &self.texture_handles, eff, &children_map,

                        root_idx, 0, t, 0.0,

                        center, size, resp.rect, self.zoom, self.orbit_yaw, self.orbit_pitch,

                        ParentFrame::IDENTITY,
                        &self.hidden_nodes,
                        instance_seed(root_idx, 0, 0, 0),
                        self.force_billboard,

                    );

                }

            }

            // Auto-fit: scale zoom so the whole effect fits. Sample the geometry
            // bbox across the animation (effects develop over time) and union it,
            // so the fit covers the max spread, not just t=0.
            if self.fit_pending {
                let (mut mnx, mut mny, mut mxx, mut mxy) = (f32::MAX, f32::MAX, f32::MIN, f32::MIN);
                let mut scratch: Vec<DrawCmd> = Vec::new();
                let mut so: Vec<[egui::Pos2; 4]> = Vec::new();
                for k in 0..=16 {
                    let ts = max_time * k as f32 / 16.0;
                    scratch.clear(); so.clear();
                    for &root_idx in &roots {
                        render_node_hierarchy(
                            &mut scratch, &mut so, None, &self.texture_handles, eff, &children_map,
                            root_idx, 0, ts, 0.0, center, size, resp.rect, self.zoom,
                            self.orbit_yaw, self.orbit_pitch, ParentFrame::IDENTITY,
                            &self.hidden_nodes, instance_seed(root_idx, 0, 0, 0), self.force_billboard,
                        );
                    }
                    for d in &scratch {
                        for (p, _) in &d.vertices {
                            mnx = mnx.min(p.x); mny = mny.min(p.y);
                            mxx = mxx.max(p.x); mxy = mxy.max(p.y);
                        }
                    }
                }
                if mxx > mnx {
                    let (bw, bh) = ((mxx - mnx).max(1.0), (mxy - mny).max(1.0));
                    let fit = (resp.rect.width() / bw).min(resp.rect.height() / bh) * 0.85;
                    if fit.is_finite() && fit > 0.0 {
                        self.zoom = (self.zoom * fit).clamp(0.001, 20.0);
                    }
                }
                self.fit_pending = false;
                ctx.request_repaint();
            }

            // Sort by Z: higher Z drawn last = on top
            draws.sort_by_key(|d| d.z);
            for d in &draws {
                painter.add(crate::gl_render::make_blend_quad(d.vertices, d.tex_id, d.blend_byte, d.tint, d.add, d.draw_rect));
            }

            // Red outline for selected node
            for corners in &selected_outline {
                let red = egui::Stroke::new(2.0, Color32::RED);
                for i in 0..4 {
                    painter.line_segment([corners[i], corners[(i+1)%4]], red);
                }
            }

            // GIF recording: capture this frame, advance time, encode at the end
            if self.gif_recording {
                let step = 1.0 / 30.0;
                if (self.gif_frame_time as f32) > max_time {
                    self.gif_recording = false;
                    let frames = self.gif_captured.lock().unwrap();
                    if !frames.is_empty() {
                        let (w, h, _) = frames[0];
                        let tmp = std::env::temp_dir().join(format!("eff_preview_{}.gif", std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs()));
                        if let Ok(f) = std::fs::File::create(&tmp) {
                            let mut encoder = gif::Encoder::new(f, w, h, &[]).unwrap();
                            encoder.set_repeat(gif::Repeat::Infinite).ok();
                            for (fw, fh, frame_data) in frames.iter() {
                                if *fw != w || *fh != h { continue; }
                                let mut pixels = frame_data.clone();
                                let mut gif_frame = gif::Frame::from_rgba_speed(w, h, &mut pixels, 10);
                                gif_frame.delay = 3; // units of 10 ms -> ~30 fps
                                encoder.write_frame(&gif_frame).ok();
                            }
                            drop(encoder);
                            // Put the FILE on the clipboard (CF_HDROP) so it pastes into
                            // Explorer/Discord; a text path pastes nowhere useful.
                            #[cfg(windows)]
                            {
                                use std::os::windows::process::CommandExt;
                                let _ = std::process::Command::new("powershell")
                                    .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
                                    .args(["-NoProfile", "-Command",
                                        &format!("Set-Clipboard -Path '{}'", tmp.display())])
                                    .spawn();
                            }
                            self.status = format!("GIF copied to clipboard (file): {}", tmp.display());
                        }
                    }
                } else {
                    painter.add(crate::gl_render::make_capture(resp.rect, self.gif_captured.clone()));
                    self.anim_time += step;
                    self.gif_frame_time += step;
                    ui.ctx().request_repaint();
                }
            }

            // Display info for selected segment

            if let Some(idx) = self.selected_segment {

                if let Some(seg) = eff.segments.get(idx) {

                    let delay = compute_delay_to(eff, &children_map, idx);

                    let lt = (t - delay).max(0.0);

                    // Values of the first spawned instance (seed as in render_node_hierarchy).
                    let seed = instance_seed(idx, 0, 0, 0);
                    let pos = eval_track48(&seg.data_09, lt, [0.0; 4], seed ^ 0x09);
                    let rot = eval_track48(&seg.data_0a, lt, [0.0; 4], seed ^ 0x0a);
                    let scale = eval_track48(&seg.data_0b, lt, [1.0; 4], seed ^ 0x0b);
                    let color1 = eval_track48(&seg.data_0d, lt, [1.0; 4], seed ^ 0x0d);
                    let color2 = eval_track48(&seg.data_0e, lt, [0.0; 4], seed ^ 0x0e);

                    let lifetime = seg.data_04[4];

                    ui.separator();

                    ui.label(RichText::new(format!("[{}] {}  t={:.3}", idx, seg.name, t)).small());

                    ui.label(format!("spawn: {:.3}s  life: {:.3}s  alive: {}",

                        delay, lifetime,

                        if t >= delay && (lifetime <= 0.0 || t <= delay + lifetime) { "YES" } else { "no" }));

                    // Projectile physics (data_04[8..12])
                    let g = seg.data_04[10];
                    if g != 0.0 || seg.data_04[8] != 0.0 || seg.data_04[9] != 0.0 {
                        ui.label(format!("physics: v0=[{:.2}..{:.2}] gravity={:.2} bounce={:.2}",
                            seg.data_04[8], seg.data_04[9], g, seg.data_04[11]));
                    }

                    // Base orientation (data_06[5..8] = def+0xa0..0xac)
                    let d6 = &seg.data_06;
                    if d6[5] != 0.0 || d6[6] != 0.0 || d6[7] != 0.0 || d6[8] != 0.0 {
                        ui.label(format!("base-orient: euler=({:.1},{:.1},{:.1})° Ytrans={:.2}  ([6] Y-axis approx'd)",
                            d6[5], d6[6], d6[7], d6[8]));
                    }

                    // Spawn descriptors of this segment
                    for rec in seg.data_14.iter() {
                        let b0 = rec.floats[0].to_bits();
                        let trig = (rec.floats[1].to_bits() & 0xFF) as u8;
                        let interval = f32::from_bits(rec.ints[0]);
                        ui.label(format!("spawns seg{} x({} bursts x {}/burst) trig={} delay={:.3}s int={:.3}s",
                            (b0 >> 8) & 0xFF, (b0 >> 16) & 0xFF, (b0 >> 24) & 0xFF,
                            trig, rec.floats[8], interval));
                    }

                    ui.label(format!("pos: ({:.2}, {:.2})  rot: {:.1}  scale: ({:.2}, {:.2})",

                        pos[0], pos[1], rot[2], scale[0], scale[1]));

                    // Scale mode
                    let sm_rec = seg.data_0b.iter()
                        .filter(|r| r.floats[8] <= lt).last()
                        .or_else(|| seg.data_0b.first());
                    if let Some(r) = sm_rec {
                        let sm = kf_flags(r);
                        let mut desc = String::new();
                        if sm & 1 != 0 { desc.push_str(" +add"); }
                        if sm & 2 != 0 { desc.push_str(" uniform"); }
                        if sm & 4 != 0 {
                            desc.push_str(&format!(" random[{:.2}..{:.2}]", r.floats[0], r.floats[4]));
                        }
                        if sm & 0x10 != 0 { desc.push_str(" loop-start"); }
                        if sm & 0x20 != 0 { desc.push_str(" loop-end"); }
                        ui.label(format!("scale mode: 0x{:02x}{}", sm, desc));
                    }

                    // Render flags: d02[1] (+0x34, orientation basis) and d02[2]
                    // (+0x38 -> runtime +0x20 master flags). Bitfields, not enums.
                    let f0 = seg.data_02[0];
                    if f0 != 0 {
                        let tag = if f0 & 1 != 0 { " container(quad not drawn)" } else { "" };
                        ui.label(format!("d02[0] flags: 0x{:08x}{}", f0, tag));
                    }

                    let f1 = seg.data_02[1];
                    let mut d1 = String::new();
                    if f1 & 0x1 == 0 { d1.push_str(" DISABLED(bit0!)"); }
                    if f1 & 0x4 != 0 { d1.push_str(" orient-enable"); }
                    if f1 & 0x8 != 0 { d1.push_str(" orient=velocity/camera"); }
                    if f1 & 0x10 != 0 { d1.push_str(" cam-billboard"); }
                    if f1 & 0x80 != 0 { d1.push_str(" cb-gated"); }
                    if f1 & 0x1C == 0 { d1.push_str(" NO-ORIENT(invisible in-game)"); }
                    ui.label(format!("d02[1] flags: 0x{:08x}{}", f1, d1));

                    let f2 = seg.data_02[2];
                    let mut d2 = String::new();
                    if f2 & 0x400 != 0 { d2.push_str(" vel-preroll"); }
                    if f2 & 0x1000 != 0 { d2.push_str(" pos=parent-live"); }
                    if f2 & 0x2000 != 0 { d2.push_str(" attach-ext"); }
                    if f2 & 0x8000 != 0 { d2.push_str(" pos=spawn-frozen"); }
                    if f2 & 0x4000 != 0 { d2.push_str(" scale=parent-live"); }
                    if f2 & 0x2_0000 != 0 { d2.push_str(" scale=spawn-frozen"); }
                    if f2 & 0x1_0000 != 0 { d2.push_str(" rot=parent@spawn"); }
                    if f2 & 0x8_0000 != 0 { d2.push_str(" follow-trajectory"); }
                    if f2 & 0x10_0000 != 0 { d2.push_str(" lockX"); }
                    if f2 & 0x20_0000 != 0 { d2.push_str(" lockY"); }
                    if f2 & 0x40_0000 != 0 { d2.push_str(" lockZ"); }
                    if f2 & 0x80_0000 != 0 { d2.push_str(" no-env-scale"); }
                    ui.label(format!("d02[2] flags: 0x{:08x}{}", f2, d2));

                    // Color Start (d0D)
                    ui.horizontal(|ui| {
                        let cr = color1[0]; let cg = color1[1];
                        let cb = color1[2]; let ca = color1[3];
                        let (rect, _) = ui.allocate_exact_size(egui::Vec2::new(12.0, 12.0), egui::Sense::hover());
                        ui.painter().rect_filled(rect, 1.0, Color32::from_rgba_unmultiplied((cr.clamp(0.,1.)*255.) as u8, (cg.clamp(0.,1.)*255.) as u8, (cb.clamp(0.,1.)*255.) as u8, (ca.clamp(0.,1.)*255.) as u8));
                        ui.label(format!("start: ({:.3}, {:.3}, {:.3}, {:.3})", cr, cg, cb, ca));
                    });
                    // Color Add (d0E)
                    ui.horizontal(|ui| {
                        let cr = color2[0]; let cg = color2[1];
                        let cb = color2[2]; let ca = color2[3];
                        let (rect, _) = ui.allocate_exact_size(egui::Vec2::new(12.0, 12.0), egui::Sense::hover());
                        ui.painter().rect_filled(rect, 1.0, Color32::from_rgba_unmultiplied((cr.clamp(0.,1.)*255.) as u8, (cg.clamp(0.,1.)*255.) as u8, (cb.clamp(0.,1.)*255.) as u8, (ca.clamp(0.,1.)*255.) as u8));
                        ui.label(format!("add:   ({:.3}, {:.3}, {:.3}, {:.3})", cr, cg, cb, ca));
                    });
                }

            }

        });

    }

}



struct DrawCmd {
    z: u8,
    vertices: [(egui::Pos2, egui::Pos2); 4],
    tex_id: egui::TextureId,
    blend_byte: u8,
    tint: [f32; 4],
    add: [f32; 4],
    draw_rect: egui::Rect,
}

/// Procedural geometry for non-quad shapes (def+0x40), matching the game's mesh
/// builders (copie_le_mesh dispatch): cross=FUN_00556120, cylinder=FUN_00555bf0,
/// sphere/dome=FUN_00556580. The game draws a pre-built mesh (def+0x90); we can't
/// recover the vertex buffers, so we regenerate the exact geometry from d08 and
/// the segment counts (na = def+0x3f, nb = def+0x3e = data_02[3] bytes 3,2).
/// Returns a list of faces: (4 local-space positions, 4 surface UVs in 0..1).
/// The UVs wrap the crop texture once across the whole surface (so a revolution
/// shape doesn't repeat the texture per segment -> no radial hatching).
type MeshFace = ([[f32; 3]; 4], [[f32; 2]; 4]);
fn shape_mesh(shape: u8, d: &[f32; 8], na: u8, nb: u8) -> Vec<MeshFace> {
    use std::f32::consts::{PI, TAU};
    let lerp = |a: f32, b: f32, t: f32| a + (b - a) * t;
    let full = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
    match shape {
        0x05 => {
            // Cross (FUN_00556120): two UNIT-width perpendicular quads (XY + ZY),
            // Y from d08[1] to d08[5]. X/Z span is a fixed [-0.5, 0.5] (not d08).
            let (y0, y1) = (d[1], d[5]);
            vec![
                ([[-0.5, y0, 0.0], [0.5, y0, 0.0], [0.5, y1, 0.0], [-0.5, y1, 0.0]], full),
                ([[0.0, y0, -0.5], [0.0, y0, 0.5], [0.0, y1, 0.5], [0.0, y1, -0.5]], full),
            ]
        }
        0x02 | 0x01 | 0x08 => {
            // Cone/cylinder of revolution: r0=d08[0]->r1=d08[4], h0=d08[1]->h1=d08[5].
            // 0x02 (FUN_00555bf0) = na radial x nb stacks, full circle.
            // 0x01/0x08 (FUN_00553c40) = 2-ring strip (1 stack); 0x08 = HALF
            // cylinder (sweep pi), the "HalfCyl" shape. U wraps the sweep, V the stacks.
            let nrad = if na == 0 { 8 } else { (na as usize).min(32) };
            let nstk = if shape == 0x02 { if nb == 0 { 2 } else { nb as usize } } else { 1 };
            let sweep = if shape == 0x08 { PI } else { TAU };
            let (r0, r1, h0, h1) = (d[0], d[4], d[1], d[5]);
            let mut q = Vec::with_capacity(nrad * nstk);
            for s in 0..nstk {
                let (v0, v1) = (s as f32 / nstk as f32, (s + 1) as f32 / nstk as f32);
                let (rs0, hs0) = (lerp(r0, r1, v0), lerp(h0, h1, v0));
                let (rs1, hs1) = (lerp(r0, r1, v1), lerp(h0, h1, v1));
                for i in 0..nrad {
                    let (u0, u1) = (i as f32 / nrad as f32, (i + 1) as f32 / nrad as f32);
                    let (t0, t1) = (sweep * u0, sweep * u1);
                    let (s0, c0) = t0.sin_cos(); let (s1, c1) = t1.sin_cos();
                    q.push((
                        [[s0 * rs0, hs0, c0 * rs0], [s1 * rs0, hs0, c1 * rs0],
                         [s1 * rs1, hs1, c1 * rs1], [s0 * rs1, hs1, c0 * rs1]],
                        [[u0, v0], [u1, v0], [u1, v1], [u0, v1]],
                    ));
                }
            }
            q
        }
        0x04 | 0x06 => {
            // Ellipsoid sphere / dome (FUN_00556580): horizontal radius d08[0],
            // vertical radius d08[1]. x=sinφ·sinθ·rh, z=sinφ·cosθ·rh, y=cosφ·rv.
            // 0x04 = full sphere (φ 0..π); 0x06 = upper hemisphere/dome (φ 0..π/2).
            let nlon = if na == 0 { 8 } else { (na as usize).min(32) };
            let nlat = (if nb == 0 { nlon / 2 } else { nb as usize }).max(2);
            let (rh, rv) = (if d[0] != 0.0 { d[0] } else { 0.5 }, if d[1] != 0.0 { d[1] } else { d[0] });
            let phi_max = if shape == 0x06 { PI / 2.0 } else { PI };
            let sph = |p: f32, t: f32| {
                let (sp, cp) = p.sin_cos(); let (st, ct) = t.sin_cos();
                [sp * st * rh, cp * rv, sp * ct * rh]
            };
            let mut q = Vec::with_capacity(nlat * nlon);
            for j in 0..nlat {
                let (v0, v1) = (j as f32 / nlat as f32, (j + 1) as f32 / nlat as f32);
                let (p0, p1) = (phi_max * v0, phi_max * v1);
                for i in 0..nlon {
                    let (u0, u1) = (i as f32 / nlon as f32, (i + 1) as f32 / nlon as f32);
                    let (t0, t1) = (TAU * u0, TAU * u1);
                    q.push((
                        [sph(p0, t0), sph(p0, t1), sph(p1, t1), sph(p1, t0)],
                        [[u0, v0], [u1, v0], [u1, v1], [u0, v1]],
                    ));
                }
            }
            q
        }
        _ => vec![([[d[0], d[1], 0.0], [d[4], d[1], 0.0], [d[4], d[5], 0.0], [d[0], d[5], 0.0]], full)],
    }
}

/// Transform basis a parent exposes to its children. The game stores both the
/// parent's live state and a snapshot taken when the child spawned; which one
/// a child uses (or neither) is gated by its d02[2] flags.
#[derive(Clone, Copy)]
struct ParentFrame {
    live_pos: egui::Pos2,
    live_scale: [f32; 2],
    live_rot: f32,
    spawn_pos: egui::Pos2,
    spawn_scale: [f32; 2],
    spawn_rot: f32,
    // Full 3D world rotation of the parent (columns), for mesh children that
    // inherit orientation from a 3-axis-rotated parent (atk011 slash roots).
    rot3: [[f32; 3]; 3],
}

impl ParentFrame {
    const IDENTITY: ParentFrame = ParentFrame {
        live_pos: egui::pos2(0.0, 0.0),
        live_scale: [1.0, 1.0],
        live_rot: 0.0,
        spawn_pos: egui::pos2(0.0, 0.0),
        spawn_scale: [1.0, 1.0],
        spawn_rot: 0.0,
        rot3: [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
    };
}

/// Apply an Euler rotation (X then Y then Z, degrees-as-radians) to a point.
fn euler_apply(p: [f32; 3], r: [f32; 3]) -> [f32; 3] {
    let (sx, cx) = r[0].sin_cos(); let (sy, cy) = r[1].sin_cos(); let (sz, cz) = r[2].sin_cos();
    let (x, y, z) = (p[0], p[1], p[2]);
    let (y1, z1) = (y * cx - z * sx, y * sx + z * cx);
    let (x2, z2) = (x * cy + z1 * sy, -x * sy + z1 * cy);
    let (x3, y3) = (x2 * cz - y1 * sz, x2 * sz + y1 * cz);
    [x3, y3, z2]
}
/// 3x3 rotation matrix (columns) equivalent to euler_apply.
fn euler_mat3(r: [f32; 3]) -> [[f32; 3]; 3] {
    [euler_apply([1.0, 0.0, 0.0], r), euler_apply([0.0, 1.0, 0.0], r), euler_apply([0.0, 0.0, 1.0], r)]
}
fn mat3_vec(m: [[f32; 3]; 3], v: [f32; 3]) -> [f32; 3] {
    [m[0][0] * v[0] + m[1][0] * v[1] + m[2][0] * v[2],
     m[0][1] * v[0] + m[1][1] * v[1] + m[2][1] * v[2],
     m[0][2] * v[0] + m[1][2] * v[1] + m[2][2] * v[2]]
}
fn mat3_mul(a: [[f32; 3]; 3], b: [[f32; 3]; 3]) -> [[f32; 3]; 3] {
    [mat3_vec(a, b[0]), mat3_vec(a, b[1]), mat3_vec(a, b[2])]
}

fn render_node_hierarchy(

    draws: &mut Vec<DrawCmd>,
    selected_outline: &mut Vec<[egui::Pos2; 4]>,
    selected_idx: Option<usize>,

    handles: &std::collections::HashMap<String, egui::TextureHandle>,

    eff: &EffFile,

    children_map: &std::collections::HashMap<usize, Vec<usize>>,

    idx: usize,

    depth: usize,

    global_time: f32,

    parent_delay: f32,

    center: egui::Pos2,

    size: f32,

    draw_rect: egui::Rect,

    zoom: f32,

    orbit_yaw: f32,

    orbit_pitch: f32,

    frame: ParentFrame,
    hidden_nodes: &std::collections::HashSet<usize>,
    seed: u32,
    force_billboard: bool,

) {
    if depth > 128 { return; } // guard against a cyclic/malformed hierarchy
    if hidden_nodes.contains(&idx) { return; }

    let seg = &eff.segments[idx];

    // Parent inheritance gates (lapellant, runtime flags +0x20 = d02[2]):
    // position: 0x1000 = parent live, 0x2000 = attach (approx. live),
    //           0x8000 = parent frozen at spawn (+0x1bc), none = effect origin.
    // scale:    0x4000 = parent live, 0x20000 = frozen at spawn (+0x1dc), none = own.
    // rotation: 0x10000 = frozen at spawn (+0x1cc), 0x2000 = live, none = own.
    let f2 = seg.data_02[2];
    let parent_pos = if f2 & 0x3000 != 0 { frame.live_pos }
        else if f2 & 0x8000 != 0 { frame.spawn_pos }
        else { egui::pos2(0.0, 0.0) };
    let parent_scale = if f2 & 0x4000 != 0 || f2 & 0x2000 != 0 { frame.live_scale }
        else if f2 & 0x2_0000 != 0 { frame.spawn_scale }
        else { [1.0, 1.0] };
    let parent_rot_z = if f2 & 0x1_0000 != 0 { frame.spawn_rot }
        else if f2 & 0x2000 != 0 { frame.live_rot }
        else { 0.0 };

    let tracks = [&seg.data_09, &seg.data_0a, &seg.data_0b, &seg.data_0c, &seg.data_0d, &seg.data_0e];
    if tracks.iter().all(|a| a.is_empty()) { return; }

    let local_t = (global_time - parent_delay).max(0.0);

    let pos = eval_track48(&seg.data_09, local_t, [0.0; 4], seed ^ 0x09);
    let rot = eval_track48(&seg.data_0a, local_t, [0.0; 4], seed ^ 0x0a);
    let scale = eval_track48(&seg.data_0b, local_t, [1.0; 4], seed ^ 0x0b);
    let unk = eval_track48(&seg.data_0c, local_t, [0.0; 4], seed ^ 0x0c);
    let color1 = eval_track48(&seg.data_0d, local_t, [1.0; 4], seed ^ 0x0d);
    let color2 = eval_track48(&seg.data_0e, local_t, [0.0; 4], seed ^ 0x0e);

    // Death rule from the game (lapellant): normalized life t / lifetime > 1.0
    // -> dead, only when lifetime (d04[4] = def+0x60) is non-zero.
    let lifetime = seg.data_04[4];

    let is_alive = global_time >= parent_delay && (lifetime <= 0.0 || local_t <= lifetime);

    let scale_x = scale[0] * parent_scale[0];
    let scale_y = scale[1] * parent_scale[1];

    // d0C is a SECOND Euler rotation track (degrees), composed into the world
    // matrix like d0A. Its Z component rotates the node's local frame — this
    // scatters particles radially (random d0C + straight d09 motion along X).
    let unk_rot = -unk[2].to_radians();

    // Local frame rotation applied to this node's own position offset.
    let off_ang = -(parent_rot_z + unk_rot);
    let cos_or = off_ang.cos(); let sin_or = off_ang.sin();

    // d02[2] bit 0x80000: compose the d0C Euler into the quad matrices
    // (+0x68/+0xe8 in lapellant) — the quad turns with its own (d0C-rotated)
    // trajectory, which reads as "oriented along the movement". Without it,
    // d0C still rotates the trajectory but the quad keeps d0A alone.
    let orient_follow = (seg.data_02[2] & 0x0008_0000) != 0;

    let rot_z = -rot[2].to_radians()
        + if orient_follow { unk_rot } else { 0.0 }
        + parent_rot_z + orbit_yaw
        - seg.data_06[7].to_radians(); // base Z (in-plane) orientation

    // Base orientation matrix (data_06[5..8] = runtime def+0xa0..0xac), built at
    // init as Rotation(Euler data_06[5,6,7] degrees) then Translation(0,
    // data_06[8], 0) — confirmed: FUN_0044d1f0 = *π/180, FUN_00516e70 = translation
    // matrix. Lays the quad down / offsets it (runhorse [5]=90, mk_lp_vomi 地面親
    // [5]=-90, rain/mk_talk [8]=0.4-0.5 = Y offset). data_06[5] (X pitch) and [7]
    // (Z in-plane) fold into the 2D rotations; [8] into the Y offset; [6] (Y-axis)
    // needs true 3D corners (TODO).
    let base_pitch = seg.data_06[5].to_radians();
    let base_ty = seg.data_06[8];

    let rot_x = unk[0].to_radians() + base_pitch + orbit_pitch;

    // Projectile physics (lapellant, gated on gravity g = data_04[10] != 0):
    // y += v0*t - 0.5*g*t², with v0 = rand(data_04[8], data_04[9]) rolled once
    // per instance at spawn (FUN_0044cd20). Positive y is up in local space, so
    // v0>0 launches upward and g pulls it back down (e.g. mk_lp_vomi splash).
    // data_04[11] = floor bounce coefficient (needs runtime floor height; unused).
    let gravity = seg.data_04[10];
    let v0 = {
        let (a, b) = (seg.data_04[8], seg.data_04[9]);
        a + (b - a) * kf_rand01(seed, 0xF1, 0)
    };
    let phys_y = |t: f32| if gravity != 0.0 { v0 * t - 0.5 * gravity * t * t } else { 0.0 };

    // Child position rotated by the local frame (parent rotation + d0C).
    // The own offset is multiplied by the inherited scale: lapellant scales
    // pos.xyz together with scale.xyz in the same parent-scale block.

    let px = pos[0] * parent_scale[0] * size * zoom * 0.5;

    let py = (pos[1] + phys_y(local_t) + base_ty) * parent_scale[1] * size * zoom * 0.5;

    // Depth (pos[2]) projected through the orbit camera. Additive so it's zero
    // at front view (orbit=0, no 2D regression) and revealed by orbiting — many
    // effects move mainly in Z (e.g. mk_lp_vomi projectile flies forward).
    let pz = pos[2] * parent_scale[0] * size * zoom * 0.5;

    let tx = parent_pos.x + (px * cos_or - py * sin_or) + pz * orbit_yaw.sin();

    let ty = parent_pos.y - (px * sin_or + py * cos_or) - pz * orbit_pitch.sin();

    let node_center = center + egui::vec2(tx, ty);



    // One track/world unit in screen pixels. Positions and quad sizes share the
    // same unit in-game (default d08 corners ±0.5 -> quad width 1.0 = one unit).
    let unit = size * zoom * 0.5;
    let pitch = rot_x.cos().abs().max(0.1);

    // A flat quad (shape 0x00, trails 0x0f/0x14/0x15) is ALWAYS a unit square in-game
    // — d08 is never read for the quad path (confirmed in Ghidra on runhorse/mk_lp; the
    // earlier "d08 = quad corners" reading was wrong). Its size/aspect comes entirely
    // from the Scale track (d0B). (Mesh shapes DO use d08 as mesh params, but those go
    // through shape_mesh() below, not this code.)
    let shape = (seg.data_02[4] & 0xFF) as u8;
    let (c0, c1) = ([-0.5f32, 0.5f32], [0.5f32, -0.5f32]);
    let qax = c0[0] * scale_x * unit;
    let qay = -c0[1] * scale_y * unit * pitch;
    let qbx = c1[0] * scale_x * unit;
    let qby = -c1[1] * scale_y * unit * pitch;



    let has_texture = !seg.fn_name_1.is_empty();

    let tex_handle = if has_texture { handles.get(&seg.fn_name_1) } else { None };



    // Draw only if alive and has texture

    // d02[0] bit0: container/marker segment — registered on the 0x200 render
    // pass at init (0x211 instead of 0x11), its own quad is not drawn in-game
    // (e.g. mk_lp seg7 文字まとめ, root). Children still render.
    let hidden_quad = (seg.data_02[0] & 1) != 0;

    // d02[1] without any orientation bit (0x4/0x8 = orientation matrix,
    // 0x10 = camera billboard): the quad stays in the effect's base plane,
    // seen edge-on -> effectively invisible in-game (vanilla mk_lp sparks
    // have 0x01 and don't show; every visible segment carries 0x10).
    // The spawn context can force billboarding for the whole effect
    // (lapellant: (def+0x34 & 0x10) || (ctx+0x158 & 0x10)); the UI toggle
    // reproduces that for UI effects like mk_talk (dialog bubble).
    let no_orient = !force_billboard && (seg.data_02[1] & 0x1C) == 0;

    // Rotated quad corners in screen space (computed before culling so the
    // selected-segment outline is always drawn — diagnostic for "nothing shows").
    let base_euler = [seg.data_06[5], seg.data_06[6], seg.data_06[7]];
    let has_base_rot = base_euler.iter().any(|&a| a != 0.0);

    // 3D projection of a local point: base Euler rotation, THEN the animated d0A
    // rotation (rot[0..3] = X/Y/Z degrees — meshes like atk011's 半円柱 orient via
    // d0A[1], the Y spin, which the quad path only handles as screen-Z), -> WORLD
    // scale (scale[2] stretches the axis a rotation moved into Z, e.g. runhorse)
    // -> orbit camera. Column-major M*v order (FUN_0042eb20).
    let base_r = [base_euler[0].to_radians(), base_euler[1].to_radians(), base_euler[2].to_radians()];
    let d0a_r = [rot[0].to_radians(), rot[1].to_radians(), rot[2].to_radians()];
    let base_mat = euler_mat3(base_r);
    // Node's own local rotation = base then animated d0A; world rotation composes
    // the parent's full 3D rotation on top (atk011 slash roots rotate on 3 axes,
    // inherited by the mesh children -> the "rotated vs quads" offset).
    let own_mat = mat3_mul(euler_mat3(d0a_r), base_mat);
    let world_mat = mat3_mul(frame.rot3, own_mat);
    let mesh_shape = matches!(shape, 0x01 | 0x02 | 0x04 | 0x05 | 0x06 | 0x08);
    let s3 = [scale_x, scale_y, scale[2] * parent_scale[0]];
    let (syaw, cyaw) = orbit_yaw.sin_cos();
    let (spit, cpit) = orbit_pitch.sin_cos();
    let project_rot = |p: [f32; 3], m: [[f32; 3]; 3]| -> egui::Pos2 {
        let v = mat3_vec(m, p);
        let w = [v[0] * s3[0], v[1] * s3[1], v[2] * s3[2]];
        let sxs = w[0] * cyaw + w[2] * syaw;
        let szs = -w[0] * syaw + w[2] * cyaw;
        let sys = w[1] * cpit - szs * spit;
        node_center + egui::vec2(sxs * unit, -sys * unit)
    };
    // Meshes use the full world rotation (parent + d0A + base); base-oriented
    // quads (runhorse) keep base-only (their d0A is a Z-spin we don't apply).
    let project3d = |p: [f32; 3]| -> egui::Pos2 {
        project_rot(p, if mesh_shape { world_mat } else { base_mat })
    };

    // Screen faces to draw. Non-quad shapes (0x02/0x04/0x05/0x06) build a
    // procedural mesh (multiple quads) via project3d — zero regression on the
    // quad-based majority. Base-oriented quads use project3d too; plain quads
    // keep the validated 2D path (with d0A screen rotation + pitch foreshorten).
    let is_mesh = matches!(shape, 0x01 | 0x02 | 0x04 | 0x05 | 0x06 | 0x08);
    // Each face = (4 screen corners, 4 surface UVs in 0..1). The UV wraps the
    // crop once across the whole surface (no per-segment texture repeat).
    let uv_full = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
    let faces: Vec<([egui::Pos2; 4], [[f32; 2]; 4])> = if is_mesh {
        // Segment counts: na = def+0x3f, nb = def+0x3e = data_02[3] bytes 3,2.
        let na = ((seg.data_02[3] >> 24) & 0xFF) as u8;
        let nb = ((seg.data_02[3] >> 16) & 0xFF) as u8;
        shape_mesh(shape, &seg.data_08, na, nb).iter()
            .map(|(p, uv)| ([project3d(p[0]), project3d(p[1]), project3d(p[2]), project3d(p[3])], *uv))
            .collect()
    } else if has_base_rot {
        vec![([
            project3d([c0[0], c0[1], 0.0]), project3d([c1[0], c0[1], 0.0]),
            project3d([c1[0], c1[1], 0.0]), project3d([c0[0], c1[1], 0.0]),
        ], uv_full)]
    } else {
        let cs = [egui::pos2(qax, qay), egui::pos2(qbx, qay), egui::pos2(qbx, qby), egui::pos2(qax, qby)];
        let cos_r = rot_z.cos(); let sin_r = rot_z.sin();
        let m = |c: egui::Pos2| node_center + egui::vec2(c.x * cos_r - c.y * sin_r, c.x * sin_r + c.y * cos_r);
        vec![([m(cs[0]), m(cs[1]), m(cs[2]), m(cs[3])], uv_full)]
    };

    // The selected segment's outline is drawn even when culled
    // (hidden/no-orient/off-screen), so selecting always shows where it is.
    if selected_idx == Some(idx) {
        for (f, _) in &faces { selected_outline.push(*f); }
    }

    if tex_handle.is_some() && is_alive && !hidden_quad && !no_orient {

        let tex_id = tex_handle.unwrap().id();

        let blend_byte = ((seg.data_02[4] >> 8) & 0xFF) as u8;

        // Crop (data_04[0..4]) used directly as UVs. Raw values allow flipped
        // crops (cl>cr / ct>cb -> mirrored texture, e.g. runhorse) and tiling
        // crops (>1, e.g. rain, needs GL_REPEAT). Only an all-zero crop means
        // "no crop -> full texture"; a zero-width crop (cl==cr, e.g. gameover
        // seg23) is a thin 1-texel line, NOT the whole atlas.
        let (cl, ct, cr, cb) = (seg.data_04[0], seg.data_04[1], seg.data_04[2], seg.data_04[3]);
        let (u0, v0, u1, v1) = if cl == 0.0 && ct == 0.0 && cr == 0.0 && cb == 0.0 {
            (0.0, 0.0, 1.0, 1.0)
        } else {
            (cl, ct, cr, cb)
        };
        // Map a face's 0..1 surface UV into the crop rect.
        let crop_uv = |uv: [f32; 2]| egui::pos2(u0 + (u1 - u0) * uv[0], v0 + (v1 - v0) * uv[1]);

        // d0D = color multiply, d0E = color add (shader u_add).
        let tint = [color1[0], color1[1], color1[2], color1[3]];
        let add = [color2[0], color2[1], color2[2], 0.0];
        let z = ((seg.data_02[3] >> 8) & 0xFF) as u8;

        for (f, uv) in &faces {
            let vertices = [(f[0], crop_uv(uv[0])), (f[1], crop_uv(uv[1])), (f[2], crop_uv(uv[2])), (f[3], crop_uv(uv[3]))];
            draws.push(DrawCmd { z, vertices, tex_id, blend_byte, tint, add, draw_rect });
        }
    }



    // Spawn descriptors (d14), reversed from lapellant's emission loop.
    // floats[0] bytes: [0] origin mode, [1] target segment, [2] burst count
    // (0xff = infinite), [3] particles per burst. floats[1] low byte = trigger
    // (0 = time, 1 = parent death / floor hit, 2 = loop wrap). floats[8] =
    // delay (+0x20), ints[0] = re-fire interval as f32 bits (+0x24).
    for rec in seg.data_14.iter() {

        let b0 = rec.floats[0].to_bits();
        let child_idx = ((b0 >> 8) & 0xFF) as usize;
        if child_idx >= eff.segments.len() || child_idx == idx { continue; }
        let bursts_raw = (b0 >> 16) & 0xFF;
        let per_burst = ((b0 >> 24) & 0xFF).min(64);
        let trigger = (rec.floats[1].to_bits() & 0xFF) as u8;

        let mut interval = f32::from_bits(rec.ints[0]);
        if !(interval > 0.0) { interval = 1.0 / 30.0; }
        // Infinite emitters (0xff) are capped for the preview.
        let bursts = if bursts_raw == 0xFF { 30 } else { bursts_raw.min(32) };
        // Non-time triggers fire at the end of the parent's life.
        let base_delay = if trigger != 0 && lifetime > 0.0 { lifetime } else { rec.floats[8] };

        for b in 0..bursts {

            // This node's state at the child's spawn time, for the frozen-at-spawn
            // inheritance modes (grandparent basis approximated as live).
            let spawn_lt = base_delay + b as f32 * interval;
            let s_pos = eval_track48(&seg.data_09, spawn_lt, [0.0; 4], seed ^ 0x09);
            let s_rot = eval_track48(&seg.data_0a, spawn_lt, [0.0; 4], seed ^ 0x0a);
            let s_scale = eval_track48(&seg.data_0b, spawn_lt, [1.0; 4], seed ^ 0x0b);
            let s_unk = eval_track48(&seg.data_0c, spawn_lt, [0.0; 4], seed ^ 0x0c);
            let s_unk_rot = -s_unk[2].to_radians();
            let s_off = -(parent_rot_z + s_unk_rot);
            let spx = s_pos[0] * parent_scale[0] * size * zoom * 0.5;
            let spy = (s_pos[1] + phys_y(spawn_lt) + base_ty) * parent_scale[1] * size * zoom * 0.5;
            let spz = s_pos[2] * parent_scale[0] * size * zoom * 0.5;
            let stx = parent_pos.x + (spx * s_off.cos() - spy * s_off.sin()) + spz * orbit_yaw.sin();
            let sty = parent_pos.y - (spx * s_off.sin() + spy * s_off.cos()) - spz * orbit_pitch.sin();
            let s_rot_z = -s_rot[2].to_radians()
                + if orient_follow { s_unk_rot } else { 0.0 }
                + parent_rot_z + orbit_yaw;

            let child_frame = ParentFrame {
                live_pos: egui::pos2(tx, ty),
                live_scale: [scale_x, scale_y],
                live_rot: rot_z,
                spawn_pos: egui::pos2(stx, sty),
                spawn_scale: [s_scale[0] * parent_scale[0], s_scale[1] * parent_scale[1]],
                spawn_rot: s_rot_z,
                rot3: world_mat, // parent's full 3D rotation, inherited by mesh children
            };

            for p in 0..per_burst {

                render_node_hierarchy(

                    draws, selected_outline, selected_idx,
                    handles, eff, children_map,

                    child_idx, depth + 1, global_time, parent_delay + base_delay + b as f32 * interval,

                    center, size, draw_rect, zoom, orbit_yaw, orbit_pitch,

                    child_frame,
                    hidden_nodes,
                    instance_seed(child_idx, b, p, seed),
                    force_billboard,

                );

            }

        }

    }

}

#[cfg(test)]
mod track_tests {
    use super::*;

    fn kf(t: f32, flags: u16, val: [f32; 4], rnd: [f32; 4]) -> eff_core::core::ArrayRecord48 {
        eff_core::core::ArrayRecord48 {
            floats: [val[0], val[1], val[2], val[3], rnd[0], rnd[1], rnd[2], rnd[3], t],
            ints: [flags as u32, 0],
            trailing: 0.0,
        }
    }

    // d0B of mk_lp.eff segment 2 「門(影)」: 1.0 uniform, then +0.4 and -0.1 additive.
    #[test]
    fn additive_uniform_scale() {
        let track = vec![
            kf(0.0, 0x02, [1.0, 1.0, 1.0, 1.0], [1.0; 4]),
            kf(0.2, 0x03, [0.4, 1.0, 1.0, 1.0], [1.0; 4]),
            kf(0.6, 0x03, [-0.1, 1.0, 1.0, 1.0], [1.0; 4]),
        ];
        let s = |t: f32| eval_track48(&track, t, [1.0; 4], 0)[0];
        assert!((s(0.0) - 1.0).abs() < 1e-5);
        assert!((s(0.1) - 1.2).abs() < 1e-5);   // rising toward 1.0 + 0.4
        assert!((s(0.2) - 1.4).abs() < 1e-5);
        assert!((s(0.4) - 1.35).abs() < 1e-5);  // falling toward 1.4 - 0.1
        assert!((s(0.6) - 1.3).abs() < 1e-5);
        assert!((s(2.0) - 1.3).abs() < 1e-5);   // hold after last
    }

    #[test]
    fn loop_absolute() {
        let track = vec![
            kf(0.0, 0x00, [0.0; 4], [0.0; 4]),
            kf(1.0, 0x10, [5.0, 0.0, 0.0, 0.0], [0.0; 4]), // loop start
            kf(2.0, 0x20, [9.0, 0.0, 0.0, 0.0], [0.0; 4]), // loop end
        ];
        let s = |t: f32| eval_track48(&track, t, [0.0; 4], 0)[0];
        assert!((s(0.5) - 2.5).abs() < 1e-5);
        assert!((s(2.5) - 7.0).abs() < 1e-4);   // wrapped to tt=1.5
        assert!((s(10.7) - 7.8).abs() < 1e-3);  // tt = 1.7
    }

    #[test]
    fn loop_additive_accumulates() {
        let track = vec![
            kf(0.0, 0x00, [0.0; 4], [0.0; 4]),
            kf(1.0, 0x11, [1.0, 0.0, 0.0, 0.0], [0.0; 4]), // loop start, +1
            kf(2.0, 0x21, [2.0, 0.0, 0.0, 0.0], [0.0; 4]), // loop end, +2
        ];
        let s = |t: f32| eval_track48(&track, t, [0.0; 4], 0)[0];
        assert!((s(1.5) - 2.0).abs() < 1e-5);   // pass 1: lerp(1, 3)
        assert!((s(2.5) - 5.0).abs() < 1e-5);   // pass 2: lerp(4, 6)
        assert!((s(3.5) - 8.0).abs() < 1e-4);   // pass 3: lerp(7, 9)
    }

    #[test]
    fn random_bounded_and_deterministic() {
        // seg 1 d0A of mk_lp: random rotation between +30 and -30
        let track = vec![kf(0.0, 0x04, [0.0, 0.0, 30.0, 0.0], [0.0, 0.0, -30.0, 0.0])];
        let mut distinct = false;
        let mut prev = None;
        for seed in [1u32, 42, 999, 123456] {
            let v = eval_track48(&track, 0.0, [0.0; 4], seed);
            assert!(v[2] >= -30.0 - 1e-3 && v[2] <= 30.0 + 1e-3, "out of range: {}", v[2]);
            // same seed, same frame -> same roll (no flicker)
            let v2 = eval_track48(&track, 0.0, [0.0; 4], seed);
            assert_eq!(v[2], v2[2]);
            if let Some(p) = prev { if (v[2] - p as f32).abs() > 1e-3 { distinct = true; } }
            prev = Some(v[2]);
        }
        assert!(distinct, "different seeds should give different rolls");
    }

    #[test]
    fn uniform_random_single_roll() {
        // uniform + random: one roll broadcast to xyz
        let track = vec![kf(0.0, 0x06, [1.0, 5.0, 9.0, 0.0], [2.0, 6.0, 10.0, 0.0])];
        let v = eval_track48(&track, 0.0, [0.0; 4], 7);
        assert!(v[0] >= 1.0 && v[0] <= 2.0);
        assert_eq!(v[0], v[1]);
        assert_eq!(v[0], v[2]);
    }
}



