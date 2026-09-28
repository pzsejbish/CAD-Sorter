"""Raise the per-sheet cap now that arrays pack across the page as well as down."""
from pathlib import Path

for name in ("aps_make.py", "aps_sheets.py"):
    p = Path(name)
    s = p.read_text(encoding="utf-8")
    s2 = s.replace('"maxPerSheet": 2,', '"maxPerSheet": 4,')
    p.write_text(s2, encoding="utf-8")
    print(f"{name}: {'updated' if s2 != s else 'unchanged'}")
