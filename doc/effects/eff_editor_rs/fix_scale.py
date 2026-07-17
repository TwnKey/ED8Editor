with open("src/gui/app.rs", "r", encoding="utf-8") as f:
    content = f.read()

old = """    let (sx, sy) = match scale_mode {

        2 => (kf.scale[0], kf.scale[0]),

        3 => (1.0/kf.scale[0].max(0.001), 1.0/kf.scale[0].max(0.001)),

        _ => (kf.scale[0], kf.scale[1]),

    };"""

new = """    // ints[0] flags: bit0=additive, bit1=uniform, bit2=random(scale[i],scale[i+4])
    let is_uniform = (scale_mode & 2) != 0;
    let is_additive = (scale_mode & 1) != 0;
    let (sx, sy) = if is_uniform {
        if is_additive { (1.0 + kf.scale[0], 1.0 + kf.scale[0]) }
        else { (kf.scale[0], kf.scale[0]) }
    } else {
        (kf.scale[0], kf.scale[1])
    };"""

if old in content:
    content = content.replace(old, new)
    with open("src/gui/app.rs", "w", encoding="utf-8") as f:
        f.write(content)
    print("OK")
else:
    print("NOT FOUND")
