"""Derive one scale from the largest array and pack every sheet against it."""
from pathlib import Path

p = Path("appbundle/PzseTable/Commands.cs")
s = p.read_text(encoding="utf-8")
before = s

# SheetSet gains the base scale.
s = s.replace(
    "            public double Ratio, KeyRatio, VpW, VpH, GapPaper;",
    "            public double Ratio, KeyRatio, VpW, VpH, GapPaper, BaseRatio;",
    1)

# Compute it, and pack against it rather than against the legibility scale.
s = s.replace(
    "                // 5. Pack: at most two arrays, stacked, gap between.\n"
    "                double gap = gapFt * 12.0;",
    "                // 5. One scale for the set, fixed by the largest array — it\n"
    "                //    gets a page to itself and everything else works to it.\n"
    "                //    Model units per paper inch; bigger number = zoomed out.\n"
    "                const double MARGIN = 0.12;\n"
    "                double baseRatio = list.Max(\n"
    "                    a => System.Math.Max(a.W / vpW, a.H / vpH)) * (1.0 + MARGIN);\n"
    "                var biggest = list.OrderByDescending(a => a.W * a.H).First();\n"
    "                Say($\"largest array {biggest.Name} \"\n"
    "                    + $\"{biggest.W / 12:0}x{biggest.H / 12:0}ft sets the scale: \"\n"
    "                    + $\"1:{baseRatio:0}\");\n"
    "\n"
    "                // Pack: at most two arrays, stacked, gap between.\n"
    "                double gap = gapFt * 12.0;",
    1)

# Packing now measures paper height at the base scale against the page, not
# model height against the legibility-scale coverage.
s = s.replace(
    "                    double need = cur.Count == 0 ? a.H : a.H + gap;\n"
    "                    if (near && curH + need <= covH && cur.Count < maxPerSheet)\n"
    "                    { cur.Add(a); curH += need; }",
    "                    double paperH = a.H / baseRatio;\n"
    "                    double gapPaperH = gap / baseRatio;\n"
    "                    double need = cur.Count == 0 ? paperH : paperH + gapPaperH;\n"
    "                    if (near && curH + need <= vpH && cur.Count < maxPerSheet)\n"
    "                    { cur.Add(a); curH += need; }",
    1)
s = s.replace(
    "                    { if (cur.Count > 0) sheets.Add(cur); cur = new List<ArrayInfo> { a }; curH = a.H; }",
    "                    { if (cur.Count > 0) sheets.Add(cur); cur = new List<ArrayInfo> { a }; curH = a.H / baseRatio; }",
    1)

# Publish it on the returned plan.
s = s.replace(
    "                    GapPaper = gap / scale.Ratio,",
    "                    GapPaper = gap / baseRatio,\n"
    "                    BaseRatio = baseRatio,",
    1)

if s == before:
    print("MISSED — no substitutions applied")
else:
    p.write_text(s, encoding="utf-8")
    print("patched base scale")
