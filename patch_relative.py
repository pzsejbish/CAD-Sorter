"""Use roof-relative layout for both grouping and placement, and tighten margin.

Keeping arrays where they belong relative to each other matters more than
squeezing the last inch, so RelativePack replaces ShelfPack in both places.
Margin drops 12% -> 5% because the tallest array pins the scale, so most of that
padding was simply making every array smaller than it needed to be.
"""
from pathlib import Path

p = Path("appbundle/PzseTable/Commands.cs")
s = p.read_text(encoding="utf-8")
before = s

# Grouping: does the array still fit once laid out roof-relative?
s = s.replace(
    "                    var packed = ShelfPack(\n"
    "                        trial.Select(z => (z.W / baseRatio, z.H / baseRatio)).ToList(),\n"
    "                        vpW, vpH, gap / baseRatio);",
    "                    var packed = RelativePack(\n"
    "                        trial.Select(z => ((z.X0 + z.X1) / 2, (z.Y0 + z.Y1) / 2,\n"
    "                                           z.W / baseRatio, z.H / baseRatio)).ToList(),\n"
    "                        vpW, vpH, gap / baseRatio);",
    1)

# Placement: same layout, so the plan and the sheet agree.
s = s.replace(
    "            var sizes = boxes.Select(b => (b.Pw * ratio / baseRatio,\n"
    "                                           b.Ph * ratio / baseRatio)).ToList();\n"
    "            var slots = ShelfPack(sizes, winW, winH, gapPaper);",
    "            var sizes = boxes.Select(b => (b.Pw * ratio / baseRatio,\n"
    "                                           b.Ph * ratio / baseRatio)).ToList();\n"
    "            var slots = RelativePack(\n"
    "                boxes.Select(b => (b.Cx, b.Cy, b.Pw * ratio / baseRatio,\n"
    "                                   b.Ph * ratio / baseRatio)).ToList(),\n"
    "                winW, winH, gapPaper);",
    1)

# Less padding: the tallest array sets the scale, so margin costs every sheet.
s = s.replace("                const double MARGIN = 0.12;",
              "                const double MARGIN = 0.05;", 1)
s = s.replace("                double margin = 0.12;",
              "                double margin = 0.05;", 1)

if s == before:
    print("MISSED — no substitutions applied")
else:
    p.write_text(s, encoding="utf-8")
    print("patched roof-relative layout + tighter margin")
