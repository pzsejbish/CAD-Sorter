"""Convert PlanSheets' remaining early returns from Say/return to err/null."""
from pathlib import Path

p = Path("appbundle/PzseTable/Commands.cs")
s = p.read_text(encoding="utf-8")

subs = [
    ('{ Say($"SHEETS-FAILED layout {template} not found"); return; }',
     '{ err = $"layout {template} not found"; return null; }'),
    ('if (vpW <= 0) { Say("SHEETS-FAILED no plan window"); return; }',
     'if (vpW <= 0) { err = "no plan window"; return null; }'),
    ('if (unordered.Count == 0) { Say("SHEETS-FAILED no arrays"); return; }',
     'if (unordered.Count == 0) { err = "no arrays"; return null; }'),
]
for a, b in subs:
    if a in s:
        s = s.replace(a, b)
        print("patched:", a[:64])
    else:
        print("MISSED :", a[:64])

p.write_text(s, encoding="utf-8")
