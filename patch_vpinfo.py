"""Let PZSEVPINFO report the generated S1.* sheets, not just PZ-*/SR100*."""
from pathlib import Path

p = Path("appbundle/PzseTable/Commands.cs")
s = p.read_text(encoding="utf-8")

old = 'bool interesting = lay.LayoutName.StartsWith("PZ-")'
new = ('bool interesting = lay.LayoutName.StartsWith("S1.")\n'
       '                                       || lay.LayoutName.StartsWith("PZ-")')

if old in s:
    p.write_text(s.replace(old, new, 1), encoding="utf-8")
    print("patched")
else:
    print("MISSED — pattern not found")
