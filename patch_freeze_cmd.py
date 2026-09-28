"""Declare PZSEFREEZE in PackageContents.xml — undeclared commands never load."""
from pathlib import Path

p = Path("aps_appbundle.py")
s = p.read_text(encoding="utf-8")

old = '<Command Global="PZSEMAKE" Local="PZSEMAKE" />'
new = old + '\n        <Command Global="PZSEFREEZE" Local="PZSEFREEZE" />'

if 'PZSEFREEZE' in s:
    print("already declared")
elif old in s:
    p.write_text(s.replace(old, new, 1), encoding="utf-8")
    print("declared PZSEFREEZE")
else:
    print("MISSED — anchor not found")
