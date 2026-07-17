with open("src/gui/app.rs", "r", encoding="utf-8") as f:
    content = f.read()

old = """    let is_alive = if auto_die {
        global_time >= node_start && global_time < parent_delay + 0.001
    } else {
        global_time >= node_start
    };"""

new = """    let node_end = parent_delay + lifetime.max(0.001);
    let is_alive = if auto_die {
        global_time >= node_start && global_time < parent_delay + 0.001
    } else {
        global_time >= node_start && global_time < node_end
    };"""

content = content.replace(old, new)
with open("src/gui/app.rs", "w", encoding="utf-8") as f:
    f.write(content)
print("Fixed: nodes now die after lifetime")
