with open('src/gui/app.rs', 'r', encoding='utf-8') as f:
    c = f.read()

old = '                        kf.pos[0], kf.pos[1], kf.rot[2], kf.scale[0], kf.scale[1]));\n\n                    // Color Start (d0D)'

new = '                        kf.pos[0], kf.pos[1], kf.rot[2], kf.scale[0], kf.scale[1]));\n\n                    // Scale mode\n                    let sm = seg.data_0b.iter()\n                        .filter(|r| r.floats[8] <= t).last()\n                        .or_else(|| seg.data_0b.first())\n                        .map(|r| r.ints[0] as u16).unwrap_or(0);\n                    if sm & 4 != 0 {\n                        let rec = seg.data_0b.iter()\n                            .filter(|r| r.floats[8] <= t).last()\n                            .or_else(|| seg.data_0b.first());\n                        if let Some(r) = rec {\n                            ui.label(format!("scale mode: {}  random: [{:.2}..{:.2}]",\n                                sm, r.floats[0], r.floats[4]));\n                        }\n                    } else {\n                        ui.label(format!("scale mode: {}", sm));\n                    }\n\n                    // Color Start (d0D)'

if old in c:
    c = c.replace(old, new)
    print('FIXED')
else:
    print('NOT FOUND')

with open('src/gui/app.rs', 'w', encoding='utf-8') as f:
    f.write(c)
