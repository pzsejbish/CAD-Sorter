"""Rework sheet generation to the engineer's spec:

  1. Uniform scale, set by the largest array (which gets its own page).
  2. Each viewport shows ONLY its array — per-viewport layer freezing rather
     than moving geometry apart in model space.
"""
from pathlib import Path

p = Path("appbundle/PzseTable/Commands.cs")
s = p.read_text(encoding="utf-8")
before = s

# --- carry the array name through to the viewport placer --------------------
s = s.replace(
    "            List<(double Cx, double Cy, double Pw, double Ph)> boxes,\n"
    "            double ratio, double winW, double winH, double gapPaper,\n"
    "            double margin)",
    "            List<(double Cx, double Cy, double Pw, double Ph, string Name)> boxes,\n"
    "            double ratio, double winW, double winH, double gapPaper,\n"
    "            double margin, double baseRatio)",
    1)

s = s.replace(
    "                        var boxes = group.Select(a => (\n"
    "                            (a.X0 + a.X1) / 2.0,\n"
    "                            (a.Y0 + a.Y1) / 2.0,\n"
    "                            a.W / plan.Ratio,\n"
    "                            a.H / plan.Ratio)).ToList();\n"
    "                        PlaceViewports(db, lm, name, boxes, plan.Ratio,\n"
    "                                       plan.VpW, plan.VpH, plan.GapPaper, margin);",
    "                        var boxes = group.Select(a => (\n"
    "                            (a.X0 + a.X1) / 2.0,\n"
    "                            (a.Y0 + a.Y1) / 2.0,\n"
    "                            a.W / plan.Ratio,\n"
    "                            a.H / plan.Ratio,\n"
    "                            a.Name)).ToList();\n"
    "                        PlaceViewports(db, lm, name, boxes, plan.Ratio,\n"
    "                                       plan.VpW, plan.VpH, plan.GapPaper, margin,\n"
    "                                       plan.BaseRatio);",
    1)

# The key plan is deliberately NOT isolated and NOT on the uniform scale.
s = s.replace(
    "                        new List<(double, double, double, double)>\n"
    "                        {\n"
    "                            (plan.SiteCx, plan.SiteCy,\n"
    "                             plan.SiteW / plan.KeyRatio, plan.SiteH / plan.KeyRatio)\n"
    "                        },\n"
    "                        plan.KeyRatio, plan.VpW, plan.VpH, plan.GapPaper, margin);",
    "                        new List<(double, double, double, double, string)>\n"
    "                        {\n"
    "                            (plan.SiteCx, plan.SiteCy,\n"
    "                             plan.SiteW / plan.KeyRatio, plan.SiteH / plan.KeyRatio,\n"
    "                             null)\n"
    "                        },\n"
    "                        plan.KeyRatio, plan.VpW, plan.VpH, plan.GapPaper, margin,\n"
    "                        plan.KeyRatio);",
    1)

# --- uniform scale, and isolation, inside PlaceViewports --------------------
s = s.replace(
    "                // Model size of this array.\n"
    "                double mw = b.Pw * ratio, mh = b.Ph * ratio;\n"
    "\n"
    "                // Largest scale at which the array fits its slot, with margin.\n"
    "                // Expressed as model units per paper inch, so smaller = zoomed in.\n"
    "                double r = System.Math.Max(mw / winW, mh / slotH) * (1.0 + margin);",
    "                // Model size of this array.\n"
    "                double mw = b.Pw * ratio, mh = b.Ph * ratio;\n"
    "\n"
    "                // One scale for the whole set, fixed by the largest array.\n"
    "                // Per-sheet scaling made every page a different zoom, which\n"
    "                // is exactly what the engineer does not want; he sizes the\n"
    "                // biggest array to its own page and works to that.\n"
    "                double r = baseRatio;\n"
    "                // Never let an array overflow its slot even so.\n"
    "                double fit = System.Math.Max(mw / winW, mh / slotH) * (1.0 + margin);\n"
    "                if (fit > r) r = fit;",
    1)

s = s.replace(
    "                vp.ViewCenter = new Autodesk.AutoCAD.Geometry.Point2d(b.Cx, b.Cy);\n"
    "                vp.ViewHeight = vp.Height * r;\n"
    "                vp.TwistAngle = 0.0;\n"
    "                vp.On = true;     // fresh viewports come in off",
    "                vp.ViewCenter = new Autodesk.AutoCAD.Geometry.Point2d(b.Cx, b.Cy);\n"
    "                vp.ViewHeight = vp.Height * r;\n"
    "                vp.TwistAngle = 0.0;\n"
    "                vp.On = true;     // fresh viewports come in off\n"
    "\n"
    "                // Show only this array. Freezing the other arrays' layers in\n"
    "                // THIS viewport isolates it without touching model space —\n"
    "                // which is what the manual workflow achieves by dragging\n"
    "                // arrays apart.\n"
    "                if (b.Name != null)\n"
    "                {\n"
    "                    var hide = new ObjectIdCollection();\n"
    "                    var lt2 = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);\n"
    "                    foreach (ObjectId lid in lt2)\n"
    "                    {\n"
    "                        var l2 = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);\n"
    "                        var m3 = ArrLayer.Match(l2.Name);\n"
    "                        if (!m3.Success) continue;\n"
    "                        string owner = $\"Roof {int.Parse(m3.Groups[1].Value)} \"\n"
    "                                     + $\"Arr {int.Parse(m3.Groups[2].Value):00}\";\n"
    "                        if (!string.Equals(owner, b.Name, StringComparison.Ordinal))\n"
    "                            hide.Add(lid);\n"
    "                    }\n"
    "                    if (hide.Count > 0) vp.FreezeLayersInViewport(hide.GetEnumerator());\n"
    "                }",
    1)

if s == before:
    print("MISSED — no substitutions applied")
else:
    p.write_text(s, encoding="utf-8")
    print("patched isolation + uniform scale")
