with open("src/gui/gl_render.rs", "r", encoding="utf-8") as f:
    content = f.read()

old = "void main(){frag=texture(u_tex,v_uv)*u_tint;}"
new = "void main(){frag=texture(u_tex,v_uv)*u_tint;frag.rgb*=frag.a;}"

content = content.replace(old, new)
with open("src/gui/gl_render.rs", "w", encoding="utf-8") as f:
    f.write(content)
print("Fixed: premultiplied alpha in shader")
