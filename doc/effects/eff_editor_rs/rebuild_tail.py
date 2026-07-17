#!/usr/bin/env python3
"""Reconstruct the tail of app.rs - all functions after show_editable_segment."""
import sys

tail = r"""
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
) {
    let seg = &eff.segments[idx];
    let indent = "  ".repeat(depth);
    let label = format!("{}[{}] {}", indent, idx, seg.name);
    let resp = ui.selectable_label(*selected == Some(idx), &label);
    if resp.clicked() { *selected = Some(idx); }
    if let Some(children) = children_map.get(&idx) {
        for &child in children {
            show_segment_tree(ui, child, eff, children_map, selected, depth + 1);
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

fn interpolate_kf(kfs: &[AnimKeyframe], t: f32) -> AnimKeyframe {
    if kfs.is_empty() { return AnimKeyframe { time: 0.0, pos: [0.0;4], rot: [0.0;4], scale: [1.0;4], unk: [0.0;4], color1: [1.0;4], color2: [1.0;4] }; }
    if kfs.len() == 1 { return kfs[0].clone(); }
    let mut before = &kfs[0];
    let mut after = &kfs[kfs.len()-1];
    for i in 0..kfs.len()-1 {
        if kfs[i].time <= t && kfs[i+1].time >= t {
            before = &kfs[i]; after = &kfs[i+1]; break;
        }
    }
    let frac = if (after.time - before.time).abs() > 0.0001 {
        (t - before.time) / (after.time - before.time)
    } else { 0.0 };
    let lerp4 = |a: &[f32;4], b: &[f32;4]| -> [f32;4] {
        [a[0]+(b[0]-a[0])*frac, a[1]+(b[1]-a[1])*frac, a[2]+(b[2]-a[2])*frac, a[3]+(b[3]-a[3])*frac]
    };
    AnimKeyframe {
        time: t,
        pos: lerp4(&before.pos, &after.pos),
        rot: lerp4(&before.rot, &after.rot),
        scale: lerp4(&before.scale, &after.scale),
        unk: lerp4(&before.unk, &after.unk),
        color1: lerp4(&before.color1, &after.color1),
        color2: lerp4(&before.color2, &after.color2),
    }
}

#[derive(Clone, Copy, PartialEq)]
enum ArrayMode { Floats, Color, Hex }

fn edit_children(
    ui: &mut egui::Ui,
    label: &str,
    arr: &mut Vec<eff_core::core::ArrayRecord48>,
    modified: &mut bool,
) {
    if arr.is_empty() { return; }
    ui.collapsing(format!("{} ({} children)", label, arr.len()), |ui| {
        for i in 0..arr.len() {
            let rec = &mut arr[i];
            let u0 = rec.floats[0].to_bits();
            let child_idx = ((u0 >> 8) & 0xFF) as usize;
            ui.collapsing(format!("  [{}] delay={:.3} child=seg#{}", i, rec.floats[8], child_idx), |ui| {
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
                    ui.label("delay:");
                    if ui.add(egui::DragValue::new(&mut rec.floats[8]).speed(0.01)).changed() { *modified = true; }
                });
            });
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
    if arr.is_empty() { return; }
    let time_label = if arr.len() > 1 && (arr.last().unwrap().floats[8] - arr.first().unwrap().floats[8]).abs() > 0.001 {
        "t"
    } else { " " };
    ui.collapsing(format!("{} ({} records)", label, arr.len()), |ui| {
        for i in 0..arr.len() {
            let rec = &mut arr[i];
            let header = egui::collapsing_header::CollapsingHeader::new(format!("  [{}]  {}={:.3}", i, time_label, rec.floats[8]))
                .default_open(arr.len() <= 4);
            header.show(ui, |ui| {
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
                        if ui.add(egui::DragValue::new(&mut cr).speed(0.01).range(0.0..=1.0)).changed() { rec.floats[0] = cr; *modified = true; }
                        if ui.add(egui::DragValue::new(&mut cg).speed(0.01).range(0.0..=1.0)).changed() { rec.floats[1] = cg; *modified = true; }
                        if ui.add(egui::DragValue::new(&mut cb).speed(0.01).range(0.0..=1.0)).changed() { rec.floats[2] = cb; *modified = true; }
                        if ui.add(egui::DragValue::new(&mut ca).speed(0.01).range(0.0..=1.0)).changed() { rec.floats[3] = ca; *modified = true; }
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
                ui.horizontal(|ui| {
                    ui.label("ints:");
                    if ui.add(egui::DragValue::new(&mut rec.ints[0])).changed() { *modified = true; }
                    if ui.add(egui::DragValue::new(&mut rec.ints[1])).changed() { *modified = true; }
                });
                if (rec.trailing - 0.0).abs() > 0.0001 || rec.ints[0] != 0 || rec.ints[1] != 0 {
                    ui.horizontal(|ui| {
                        ui.label(format!("trail={:.4}", rec.trailing));
                    });
                }
            });
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

            // Calculate max animation time across all segments
            let max_time: f32 = eff.segments.iter()
                .filter_map(|s| extract_keyframes(s).last().map(|k| k.time))
                .fold(0.0f32, |a, b| a.max(b))
                .max(1.0);

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
                if ui.small_button("Reset view").clicked() { self.orbit_yaw = 0.0; self.orbit_pitch = 0.0; self.zoom = 1.0; }
                ui.add(egui::DragValue::new(&mut self.zoom).speed(0.05).range(0.1..=10.0).prefix("Zoom: "));
            });

            // Copy hex button
            if ui.small_button("Copy hex").clicked() {
                if let (Some(ref eff), Some(idx)) = (&self.effect, self.selected_segment) {
                    if let Some(seg) = eff.segments.get(idx) {
                        let mut hex = format!("data_0b for [{}] {}:\n", idx, seg.name);
                        hex.push_str(&format!("{:>4} {:>10} {:>10} {:>10} {:>10} {:>10} {:>6} {:>6} {:>10}\n", "#", "f[0]", "f[1]", "f[2]", "f[3]", "t", "i[0]", "i[1]", "trail"));
                        for (ri, r) in seg.data_0b.iter().enumerate() {
                            hex.push_str(&format!("{:>4} {:10.4} {:10.4} {:10.4} {:10.4} {:10.4} {:>6} {:>6} {:10.4}\n", ri, r.floats[0], r.floats[1], r.floats[2], r.floats[3], r.floats[8], r.ints[0], r.ints[1], r.trailing));
                            let raw = unsafe { std::slice::from_raw_parts(r as *const _ as *const u8, 48) };
                            hex.push_str(&format!("  raw: {}\n", raw.iter().map(|b| format!("{:02X}", b)).collect::<Vec<_>>().join(" ")));
                        }
                        ctx.output_mut(|o| o.copied_text = hex);
                        self.status = "Hex copied!".into();
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

            let t = self.anim_time as f32 % max_time;

            // Background
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

            // Render from root, draw ALL nodes
            if !self.texture_handles.is_empty() {
                for &root_idx in &roots {
                    render_node_hierarchy(
                        &painter, &self.texture_handles, eff, &children_map,
                        root_idx, t, 0.0,
                        center, size, resp.rect, self.zoom, self.orbit_yaw, self.orbit_pitch,
                        [1.0, 1.0, 1.0, 1.0], 0.0, egui::pos2(0.0, 0.0),
                    );
                }
            }

            // Display info for selected segment
            if let Some(idx) = self.selected_segment {
                if let Some(seg) = eff.segments.get(idx) {
                    let kfs = extract_keyframes(seg);
                    let kf = interpolate_kf(&kfs, t);
                    let delay = compute_delay_to(eff, &children_map, idx);
                    let lifetime = seg.data_04[4];
                    let auto_die = (seg.data_02[0] & 1) != 0;
                    ui.separator();
                    ui.label(RichText::new(format!("[{}] {}  t={:.3}", idx, seg.name, t)).small());
                    ui.label(format!("spawn: {:.3}s  life: {:.3}s  auto-die: {}  alive: {}",
                        delay, lifetime,
                        if auto_die { "YES" } else { "no" },
                        if t >= delay && t < if auto_die { delay + 0.001 } else { delay + lifetime.max(0.001) } { "YES" } else { "no" }));
                    ui.label(format!("pos: ({:.2}, {:.2})  rot: {:.1}  scale: ({:.2}, {:.2})",
                        kf.pos[0], kf.pos[1], kf.rot[2], kf.scale[0], kf.scale[1]));
                    if !kf.color1.iter().all(|&v| v == 0.0) {
                        ui.horizontal(|ui| {
                            let cr = kf.color1[0].clamp(0.0, 1.0); let cg = kf.color1[1].clamp(0.0, 1.0);
                            let cb = kf.color1[2].clamp(0.0, 1.0); let ca = kf.color1[3].clamp(0.0, 1.0);
                            let (rect, _) = ui.allocate_exact_size(egui::Vec2::new(16.0, 16.0), egui::Sense::hover());
                            ui.painter().rect_filled(rect, 2.0, Color32::from_rgba_unmultiplied((cr*255.0) as u8, (cg*255.0) as u8, (cb*255.0) as u8, (ca*255.0) as u8));
                            ui.label(format!("color: ({:.2}, {:.2}, {:.2}, {:.2})", cr, cg, cb, ca));
                        });
                    }
                }
            }
        });
    }
}

fn render_node_hierarchy(
    painter: &egui::Painter,
    handles: &std::collections::HashMap<String, egui::TextureHandle>,
    eff: &EffFile,
    children_map: &std::collections::HashMap<usize, Vec<usize>>,
    idx: usize,
    global_time: f32,
    parent_delay: f32,
    center: egui::Pos2,
    size: f32,
    draw_rect: egui::Rect,
    zoom: f32,
    orbit_yaw: f32,
    orbit_pitch: f32,
    parent_scale: [f32; 4],
    parent_rot_z: f32,
    parent_pos: egui::Pos2,
) {
    let seg = &eff.segments[idx];
    let kfs = extract_keyframes(seg);
    if kfs.is_empty() { return; }
    
    let max_t = kfs.last().map(|k| k.time).unwrap_or(1.0);
    let local_t = (global_time - parent_delay).max(0.0) % max_t.max(0.001);
    let kf = interpolate_kf(&kfs, local_t);
    let lifetime = seg.data_04[4];
    let auto_die = (seg.data_02[0] & 1) != 0;
    let node_start = parent_delay;
    let is_alive = if auto_die {
        global_time >= node_start && global_time < parent_delay + 0.001
    } else {
        global_time >= node_start
    };

    // Scale mode from d0B
    let scale_mode = seg.data_0b.iter()
        .filter(|r| r.floats[8] <= local_t)
        .last()
        .map(|r| r.ints[0])
        .or_else(|| seg.data_0b.first().map(|r| r.ints[0]))
        .unwrap_or(0);
    let (sx, sy) = match scale_mode {
        2 => (kf.scale[0], kf.scale[0]),
        3 => (1.0/kf.scale[0].max(0.001), 1.0/kf.scale[0].max(0.001)),
        _ => (kf.scale[0], kf.scale[1]),
    };
    let scale_x = sx * parent_scale[0];
    let scale_y = sy * parent_scale[1];
    let rot_z = -kf.rot[2].to_radians() + parent_rot_z + orbit_yaw;
    let rot_x = kf.unk[0].to_radians() + orbit_pitch;
    
    // Child position rotated by parent rotation
    let px = kf.pos[0] * size * zoom * 0.5;
    let py = kf.pos[1] * size * zoom * 0.5;
    let cos_pr = (-parent_rot_z).cos(); let sin_pr = (-parent_rot_z).sin();
    let tx = parent_pos.x + (px * cos_pr - py * sin_pr);
    let ty = parent_pos.y - (px * sin_pr + py * cos_pr);
    let node_center = center + egui::vec2(tx, ty);

    let base_w = size * zoom * 0.35 * scale_x;
    let base_h = size * zoom * 0.35 * scale_y * rot_x.cos().abs().max(0.1);

    let has_texture = !seg.fn_name_1.is_empty();
    let tex_handle = if has_texture { handles.get(&seg.fn_name_1) } else { None };

    // Draw only if alive and has texture
    if tex_handle.is_some() && is_alive {
        let tex_id = tex_handle.unwrap().id();
        let blend_byte = ((seg.data_02[4] >> 8) & 0xFF) as u8;
        
        // Draw quad
        let corners = [
            egui::pos2(-base_w, -base_h), egui::pos2(base_w, -base_h),
            egui::pos2(base_w, base_h), egui::pos2(-base_w, base_h),
        ];
        let cos_r = rot_z.cos(); let sin_r = rot_z.sin();
        let rotated: Vec<_> = corners.iter().map(|c| {
            node_center + egui::vec2(c.x * cos_r - c.y * sin_r, c.x * sin_r + c.y * cos_r)
        }).collect();

        let cl = seg.data_04[0] as f32; let ct = seg.data_04[1] as f32;
        let cr = seg.data_04[2] as f32; let cb = seg.data_04[3] as f32;
        let (u0, v0, u1, v1) = if cl < cr && ct < cb { (cl, ct, cr, cb) } else { (0.0, 0.0, 1.0, 1.0) };
        let uv = [egui::pos2(u0, v0), egui::pos2(u1, v0), egui::pos2(u1, v1), egui::pos2(u0, v1)];

        let seg_t = if max_t > 0.0 { local_t / max_t } else { 0.0 };
        let lerp = |a: f32, b: f32| a + (b - a) * seg_t;
        let cr_c = lerp(kf.color1[0], kf.color2[0]).clamp(0.0, 1.0);
        let cg = lerp(kf.color1[1], kf.color2[1]).clamp(0.0, 1.0);
        let cb_c = lerp(kf.color1[2], kf.color2[2]).clamp(0.0, 1.0);
        let ca = lerp(kf.color1[3], kf.color2[3]).clamp(0.0, 1.0);
        let tint = [cr_c, cg, cb_c, ca];

        let vertices = [(rotated[0], uv[0]), (rotated[1], uv[1]), (rotated[2], uv[2]), (rotated[3], uv[3])];
        painter.add(crate::gl_render::make_blend_quad(vertices, tex_id, blend_byte, tint, draw_rect));
    }

    // Recursively render children
    if let Some(children) = children_map.get(&idx) {
        for &child_idx in children {
            let delay = seg.data_14.iter()
                .find(|rec| {
                    let u0 = rec.floats[0].to_bits();
                    ((u0 >> 8) & 0xFF) as usize == child_idx
                })
                .map(|rec| rec.floats[8])
                .unwrap_or(0.0);
            
            render_node_hierarchy(
                painter, handles, eff, children_map,
                child_idx, global_time, parent_delay + delay,
                center, size, draw_rect, zoom, orbit_yaw, orbit_pitch,
                [scale_x, scale_y, 1.0, 1.0], rot_z, egui::pos2(tx, ty),
            );
        }
    }
}
"""

print(tail)
