with open("src/gui/app.rs", "r", encoding="utf-8") as f:
    lines = f.readlines()

# Fix ensure_snapshot: replace the broken } chain (lines 160-165, 0-based)
# Current:
#   line 160:                     }
#   line 161:     }
#   line 162:                 }
#   line 163:             }
#   line 164:         }
#   line 165:     }
#   line 166:     fn undo

# Find the exact lines
for i, l in enumerate(lines):
    if "fn ensure_snapshot" in l:
        fn_start = i
    if "fn undo" in l and i > fn_start:
        undo_line = i
        break

# The broken } end at undo_line-6 through undo_line-1
# Replace lines undo_line-6 to undo_line-1 with proper } chain
proper = [
    "                    }\n",   # if a != b
    "                }\n",       # if let Ok
    "            }\n",           # if let Some
    "        }\n",               # if !auto_snapshot
    "    }\n",                   # fn ensure_snapshot
    "\n",                        # blank line
]
lines[undo_line-6:undo_line] = proper
print(f"Fixed ensure_snapshot (lines {undo_line-5}-{undo_line})")

# Now add } at 0 spaces before impl App to close first impl EffEditorApp
for i, l in enumerate(lines):
    if "impl eframe::App for EffEditorApp" in l:
        lines.insert(i, "}\n")
        lines.insert(i, "\n")
        print(f"Added impl EffEditorApp closing before line {i+1}")
        break

with open("src/gui/app.rs", "w", encoding="utf-8") as f:
    f.writelines(lines)
print(f"Total: {len(lines)} lines")
