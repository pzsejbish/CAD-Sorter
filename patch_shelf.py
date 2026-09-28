"""Pack arrays across the page as well as down it, and label each one.

Arrays here are tall and narrow while the page is wide and short, so a purely
vertical stack leaves ~two thirds of every sheet empty. Shelf packing places
them left to right, wrapping to a new row, which uses the width and lets more
arrays share a page without changing the scale.
"""
from pathlib import Path

p = Path("appbundle/PzseTable/Commands.cs")
s = p.read_text(encoding="utf-8")
before = s

# ---- shelf packer ----------------------------------------------------------
helper = '''
        /// <summary>One array's spot on a sheet, in paper inches from the
        /// window's bottom-left corner.</summary>
        private struct Slot
        {
            public double X, Y, W, H;
        }

        /// <summary>
        /// Place items left-to-right, wrapping to a new row when the width runs
        /// out. Returns null if they do not all fit, so the caller can start a
        /// fresh sheet. Rows are laid from the top down, matching reading order.
        /// </summary>
        private static List<Slot> ShelfPack(
            List<(double W, double H)> items, double winW, double winH, double gap)
        {
            var slots = new List<Slot>();
            double rowY = winH, rowH = 0, x = 0;
            foreach (var it in items)
            {
                if (it.W > winW || it.H > winH) return null;      // never fits
                if (x > 0 && x + gap + it.W > winW)               // wrap
                {
                    rowY -= rowH + gap;
                    rowH = 0;
                    x = 0;
                }
                double px = x > 0 ? x + gap : 0;
                if (rowY - it.H < -1e-9) return null;             // out of height
                slots.Add(new Slot { X = px, Y = rowY - it.H, W = it.W, H = it.H });
                x = px + it.W;
                if (it.H > rowH) rowH = it.H;
            }
            return slots;
        }

'''
s = s.replace("        private class SheetSet", helper + "        private class SheetSet", 1)

# ---- grouping now asks the packer whether the array still fits -------------
s = s.replace(
    "                    double paperH = a.H / baseRatio;\n"
    "                    double gapPaperH = gap / baseRatio;\n"
    "                    double need = cur.Count == 0 ? paperH : paperH + gapPaperH;\n"
    "                    if (near && curH + need <= vpH && cur.Count < maxPerSheet)\n"
    "                    { cur.Add(a); curH += need; }\n"
    "                    else\n"
    "                    { if (cur.Count > 0) sheets.Add(cur); cur = new List<ArrayInfo> { a }; curH = a.H / baseRatio; }",
    "                    // Does it still fit once everything is shelf-packed?\n"
    "                    var trial = new List<ArrayInfo>(cur) { a };\n"
    "                    var packed = ShelfPack(\n"
    "                        trial.Select(z => (z.W / baseRatio, z.H / baseRatio)).ToList(),\n"
    "                        vpW, vpH, gap / baseRatio);\n"
    "                    if (near && packed != null && trial.Count <= maxPerSheet)\n"
    "                    { cur = trial; }\n"
    "                    else\n"
    "                    { if (cur.Count > 0) sheets.Add(cur); cur = new List<ArrayInfo> { a }; }",
    1)

# curH is no longer meaningful.
s = s.replace(
    "                var cur = new List<ArrayInfo>();\n"
    "                double curH = 0;\n",
    "                var cur = new List<ArrayInfo>();\n",
    1)

# ---- placement uses the packer, and labels each window ---------------------
s = s.replace(
    "            // Fill the page: split the window between the arrays rather than\n"
    "            // sizing each to its own extents, which left small arrays as tiny\n"
    "            // windows adrift in a mostly empty sheet.\n"
    "            int n = boxes.Count;\n"
    "            double slotH = (winH - gapPaper * (n - 1)) / n;\n"
    "            double top = winCy + winH / 2.0;",
    "            // Shelf-pack across the page, not merely down it.\n"
    "            int n = boxes.Count;\n"
    "            var sizes = boxes.Select(b => (b.Pw * ratio / baseRatio,\n"
    "                                           b.Ph * ratio / baseRatio)).ToList();\n"
    "            var slots = ShelfPack(sizes, winW, winH, gapPaper)\n"
    "                        ?? sizes.Select((z, k) => new Slot {\n"
    "                               X = 0, Y = winH - (k + 1) * (winH / n),\n"
    "                               W = z.Item1, H = z.Item2 }).ToList();\n"
    "            double left = winCx - winW / 2.0, bottom = winCy - winH / 2.0;\n"
    "            double slotH = winH / System.Math.Max(1, n);\n"
    "            double top = winCy + winH / 2.0;",
    1)

s = s.replace(
    "                double pw = mw / r, ph = mh / r;\n"
    "                vp.Width = System.Math.Min(pw, winW);\n"
    "                vp.Height = System.Math.Min(ph, slotH);\n"
    "                vp.CenterPoint = new Autodesk.AutoCAD.Geometry.Point3d(\n"
    "                    winCx, top - (i * (slotH + gapPaper) + slotH / 2.0), 0);",
    "                var slot = slots[i];\n"
    "                vp.Width = slot.W;\n"
    "                vp.Height = slot.H;\n"
    "                vp.CenterPoint = new Autodesk.AutoCAD.Geometry.Point3d(\n"
    "                    left + slot.X + slot.W / 2.0,\n"
    "                    bottom + slot.Y + slot.H / 2.0, 0);",
    1)

# Label above each window so a shared sheet says which array is which.
s = s.replace(
    "                    if (hide.Count > 0) vp.FreezeLayersInViewport(hide.GetEnumerator());\n"
    "                }",
    "                    if (hide.Count > 0) vp.FreezeLayersInViewport(hide.GetEnumerator());\n"
    "\n"
    "                    // With several arrays to a page, each window has to say\n"
    "                    // which one it is.\n"
    "                    var lbl = new DBText\n"
    "                    {\n"
    "                        TextString = b.Name.ToUpperInvariant(),\n"
    "                        Height = 0.18,\n"
    "                        Position = new Autodesk.AutoCAD.Geometry.Point3d(\n"
    "                            left + slot.X, bottom + slot.Y + slot.H + 0.08, 0),\n"
    "                    };\n"
    "                    btr.AppendEntity(lbl);\n"
    "                    tr.AddNewlyCreatedDBObject(lbl, true);\n"
    "                }",
    1)

if s == before:
    print("MISSED — no substitutions applied")
else:
    p.write_text(s, encoding="utf-8")
    print("patched shelf packing + labels")
