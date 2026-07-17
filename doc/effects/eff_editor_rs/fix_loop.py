with open("src/gui/app.rs", "r", encoding="utf-8") as f:
    content = f.read()

# Fix 1: render_node_hierarchy - remove modulo, clamp to max_t
old1 = "    let local_t = (global_time - parent_delay).max(0.0) % max_t.max(0.001);"
new1 = "    let local_t = (global_time - parent_delay).max(0.0).min(max_t);"
content = content.replace(old1, new1)

# Fix 2: show_preview_panel - remove modulo from displayed t
old2 = "            let t = self.anim_time as f32 % max_time;"
new2 = "            let t = (self.anim_time as f32).min(max_time);"
content = content.replace(old2, new2)

# Fix 3: show_preview_panel segment info - use delay-based local_t like render
# The info display uses global t, but should show segment-local time.
# Actually the info display already uses kf from extract_keyframes which uses local_t.
# The issue is just the modulo. The info display uses `t` which is already fixed above.

with open("src/gui/app.rs", "w", encoding="utf-8") as f:
    f.write(content)
print("Fixed: no more animation looping")
