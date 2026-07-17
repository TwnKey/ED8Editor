use eframe::glow::{self, HasContext as _};
use egui::epaint::PaintCallbackInfo;
use std::sync::{Arc, OnceLock};

static GL_STATE: OnceLock<(glow::Program, glow::VertexArray)> = OnceLock::new();

fn get_program(gl: &glow::Context) -> (glow::Program, glow::VertexArray) {
    unsafe { let s = GL_STATE.get_or_init(|| init_gl(gl)); (s.0, s.1) }
}

unsafe fn init_gl(gl: &glow::Context) -> (glow::Program, glow::VertexArray) {
    let vs = gl.create_shader(glow::VERTEX_SHADER).unwrap();
    gl.shader_source(vs, "#version 300 es\nprecision mediump float;\nuniform vec2 u_screen;\nuniform vec2 u_offset;\nin vec2 a_pos;\nin vec2 a_uv;\nout vec2 v_uv;\nvoid main(){vec2 p=a_pos-u_offset;vec2 c=(p/u_screen)*2.0-1.0;gl_Position=vec4(c.x,-c.y,0.0,1.0);v_uv=a_uv;}");
    gl.compile_shader(vs);
    let fs = gl.create_shader(glow::FRAGMENT_SHADER).unwrap();
    // Premultiplied output. The multiply term (tex*d0D) is occluding: its alpha
    // ma = tex.a*d0D.a drives blending. The additive term (d0E) is a glow gated
    // only by texture alpha, NOT by d0D.a -- so segments with d0D.a==0 but a
    // non-zero color-add still show (e.g. mk_lp_vomi houbutu2, green trail).
    // Texture is premultiplied at load (t.rgb already *= t.a). Multiply term
    // therefore only takes an extra *d0D.a (u_tint.a); its blend alpha is
    // ma = t.a*d0D.a. Additive term (d0E) is a glow gated only by texture
    // alpha, NOT by d0D.a -- so d0D.a==0 segments with a color-add still show
    // (e.g. mk_lp_vomi houbutu2, green trail).
    gl.shader_source(fs, "#version 300 es\nprecision mediump float;\nuniform sampler2D u_tex;\nuniform vec4 u_tint;\nuniform vec4 u_add;\nin vec2 v_uv;\nout vec4 frag;\nvoid main(){vec4 t=texture(u_tex,v_uv);float ma=t.a*u_tint.a;vec3 mul=t.rgb*u_tint.rgb*u_tint.a;vec3 add=u_add.rgb*t.a;frag=vec4(mul+add,ma);}");
    gl.compile_shader(fs);
    let p = gl.create_program().unwrap();
    gl.attach_shader(p, vs); gl.attach_shader(p, fs); gl.link_program(p);
    gl.delete_shader(vs); gl.delete_shader(fs);
    (p, gl.create_vertex_array().unwrap())
}

/// `draw_rect`: the full draw area rect (used as PaintCallback rect for correct viewport).
pub fn make_blend_quad(
    v: [(egui::Pos2, egui::Pos2); 4],
    tex: egui::TextureId,
    blend: u8,
    tint: [f32; 4],
    add: [f32; 4],
    draw_rect: egui::Rect,
) -> egui::Shape {
    let cb = egui_glow::CallbackFn::new(move |info: PaintCallbackInfo, painter: &egui_glow::Painter| {
        let gl = painter.gl();
        unsafe {
            let (prog, vao) = get_program(gl);
            gl.use_program(Some(prog)); gl.bind_vertex_array(Some(vao));
            // Source is premultiplied. Alpha-over = ONE, 1-SRC_A; additive and
            // subtractive add the premultiplied src fully (ONE, ONE).
            match blend {
                0x02 => { gl.blend_equation(glow::FUNC_ADD); gl.blend_func(glow::ONE, glow::ONE); }
                0x04 => { gl.blend_equation(glow::FUNC_REVERSE_SUBTRACT); gl.blend_func(glow::ONE, glow::ONE); }
                _ => { gl.blend_equation(glow::FUNC_ADD); gl.blend_func(glow::ONE, glow::ONE_MINUS_SRC_ALPHA); }
            }
            // Debug: print viewport and screen info once per second
            {
                use std::sync::atomic::{AtomicU64, Ordering};
                static LAST_DBG: AtomicU64 = AtomicU64::new(0);
                let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs();
                if LAST_DBG.load(Ordering::Relaxed) != now {
                    LAST_DBG.store(now, Ordering::Relaxed);
                    eprintln!("[gl] viewport=({:.0},{:.0})-({:.0},{:.0}) sz=({:.0},{:.0}) screen_px={:?} ppp={:.2} blend=0x{:02X} quad_rect=({:.0},{:.0})-({:.0},{:.0})",
                        info.viewport.min.x, info.viewport.min.y,
                        info.viewport.max.x, info.viewport.max.y,
                        info.viewport.width(), info.viewport.height(),
                        info.screen_size_px, info.pixels_per_point, blend,
                        v[0].0.x, v[0].0.y, v[2].0.x, v[2].0.y);
                }
            }
            let vp = &info.viewport;
            if let Some(l) = gl.get_uniform_location(prog, "u_screen") { gl.uniform_2_f32(Some(&l), vp.width(), vp.height()); }
            if let Some(l) = gl.get_uniform_location(prog, "u_offset") { gl.uniform_2_f32(Some(&l), vp.min.x, vp.min.y); }
            if let Some(l) = gl.get_uniform_location(prog, "u_tex") { gl.uniform_1_i32(Some(&l), 0); }
            if let Some(l) = gl.get_uniform_location(prog, "u_tint") { gl.uniform_4_f32(Some(&l), tint[0], tint[1], tint[2], tint[3]); }
            if let Some(l) = gl.get_uniform_location(prog, "u_add") { gl.uniform_4_f32(Some(&l), add[0], add[1], add[2], add[3]); }
            gl.active_texture(glow::TEXTURE0);
            if let Some(gl_tex) = painter.texture(tex) { gl.bind_texture(glow::TEXTURE_2D, Some(gl_tex)); }
            else { gl.bind_texture(glow::TEXTURE_2D, None); }
            // Tiling: crops with UV > 1 (rain streaks etc.) need REPEAT wrap.
            gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_WRAP_S, glow::REPEAT as i32);
            gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_WRAP_T, glow::REPEAT as i32);
            let d: [f32; 16] = [v[0].0.x, v[0].0.y, v[0].1.x, v[0].1.y, v[1].0.x, v[1].0.y, v[1].1.x, v[1].1.y, v[2].0.x, v[2].0.y, v[2].1.x, v[2].1.y, v[3].0.x, v[3].0.y, v[3].1.x, v[3].1.y];
            let vbo = gl.create_buffer().unwrap();
            gl.bind_buffer(glow::ARRAY_BUFFER, Some(vbo));
            gl.buffer_data_u8_slice(glow::ARRAY_BUFFER, std::slice::from_raw_parts(d.as_ptr() as *const u8, 64), glow::DYNAMIC_DRAW);
            let pl = gl.get_attrib_location(prog, "a_pos").unwrap() as u32;
            let ul = gl.get_attrib_location(prog, "a_uv").unwrap() as u32;
            gl.vertex_attrib_pointer_f32(pl, 2, glow::FLOAT, false, 16, 0); gl.enable_vertex_attrib_array(pl);
            gl.vertex_attrib_pointer_f32(ul, 2, glow::FLOAT, false, 16, 8); gl.enable_vertex_attrib_array(ul);
            gl.draw_arrays(glow::TRIANGLE_FAN, 0, 4);
            gl.delete_buffer(vbo);
            // Restore CLAMP so egui's own use of this texture (thumbnail) is unaffected.
            gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_WRAP_S, glow::CLAMP_TO_EDGE as i32);
            gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_WRAP_T, glow::CLAMP_TO_EDGE as i32);
            gl.bind_vertex_array(None); gl.bind_texture(glow::TEXTURE_2D, None); gl.use_program(None);
            gl.blend_equation(glow::FUNC_ADD);
            gl.blend_func_separate(glow::ONE, glow::ONE_MINUS_SRC_ALPHA, glow::ONE, glow::ONE_MINUS_SRC_ALPHA);
        }
    });
    egui::Shape::Callback(egui::PaintCallback { rect: draw_rect, callback: Arc::new(cb) })
}

/// Capture the current framebuffer region (for GIF recording).
/// Frames are pushed as (width_px, height_px, rgba).
pub fn make_capture(
    capture_rect: egui::Rect,
    out: std::sync::Arc<std::sync::Mutex<Vec<(u16, u16, Vec<u8>)>>>,
) -> egui::Shape {
    let cb = egui_glow::CallbackFn::new(move |info: PaintCallbackInfo, painter: &egui_glow::Painter| {
        let gl = painter.gl();
        unsafe {
            // read_pixels works in physical pixels with a bottom-left origin;
            // capture_rect is in logical points with a top-left origin.
            let ppp = info.pixels_per_point;
            let sw = info.screen_size_px[0] as i32;
            let sh = info.screen_size_px[1] as i32;
            let x = ((capture_rect.left() * ppp).round() as i32).clamp(0, sw);
            let y_top = ((capture_rect.top() * ppp).round() as i32).clamp(0, sh);
            let w = ((capture_rect.width() * ppp).round() as i32).min(sw - x).max(0);
            let h = ((capture_rect.height() * ppp).round() as i32).min(sh - y_top).max(0);
            let y = sh - y_top - h;
            if w <= 0 || h <= 0 { return; }
            let mut pixels = vec![0u8; (w * h * 4) as usize];
            gl.read_pixels(x, y, w, h, glow::RGBA, glow::UNSIGNED_BYTE, glow::PixelPackData::Slice(&mut pixels));
            // OpenGL reads bottom-up, flip vertically
            let stride = (w * 4) as usize;
            let mut flipped = vec![0u8; pixels.len()];
            for row in 0..h as usize {
                let src = &pixels[(h as usize - 1 - row) * stride..(h as usize - row) * stride];
                let dst = &mut flipped[row * stride..(row + 1) * stride];
                dst.copy_from_slice(src);
            }
            if let Ok(mut frames) = out.lock() {
                frames.push((w as u16, h as u16, flipped));
            }
        }
    });
    egui::Shape::Callback(egui::PaintCallback { rect: capture_rect, callback: Arc::new(cb) })
}
