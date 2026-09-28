"""Thread the margin setting through PzseMake's two PlaceViewports calls."""
from pathlib import Path

p = Path("appbundle/PzseTable/Commands.cs")
s = p.read_text(encoding="utf-8")
before = s

# Read the margin from params alongside the chunk window.
s = s.replace(
    "                int startAt = 0, count = int.MaxValue;\n",
    "                int startAt = 0, count = int.MaxValue;\n"
    "                // Breathing room around each array inside its window.\n"
    "                double margin = 0.12;\n",
    1)
s = s.replace(
    '                    if (pd.RootElement.TryGetProperty("count", out var c0))\n'
    "                        count = c0.GetInt32();\n",
    '                    if (pd.RootElement.TryGetProperty("count", out var c0))\n'
    "                        count = c0.GetInt32();\n"
    '                    if (pd.RootElement.TryGetProperty("marginPct", out var mg))\n'
    "                        margin = mg.GetDouble();\n",
    1)

# Both call sites gain the argument.
s = s.replace(
    "                        plan.KeyRatio, plan.VpW, plan.VpH, plan.GapPaper);",
    "                        plan.KeyRatio, plan.VpW, plan.VpH, plan.GapPaper, margin);",
    1)
s = s.replace(
    "                        PlaceViewports(db, lm, name, boxes, plan.Ratio,\n"
    "                                       plan.VpW, plan.VpH, plan.GapPaper);",
    "                        PlaceViewports(db, lm, name, boxes, plan.Ratio,\n"
    "                                       plan.VpW, plan.VpH, plan.GapPaper, margin);",
    1)

if s == before:
    print("MISSED — no substitutions applied")
else:
    p.write_text(s, encoding="utf-8")
    print("patched margin through")
