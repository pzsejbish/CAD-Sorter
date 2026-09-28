using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(PzseTable.Commands))]

namespace PzseTable
{
    public class Commands
    {
        private static void Say(string msg)
        {
            // The APS report captures the AutoCAD command-line stream, not the
            // process stdout â€” Console.WriteLine goes nowhere. Write through the
            // editor and keep stdout only as a fallback.
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                          .DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage("\n@@@ " + msg);
                    return;
                }
            }
            catch { /* fall through */ }
            Console.WriteLine("@@@ " + msg);
        }

        /// <summary>
        /// Dump every ACAD_TABLE's shape and cell text. Run this first against a
        /// new sheet to learn the row indices before mapping any data to them.
        /// </summary>
        [CommandMethod("PZSEDUMP")]
        public static void PzseDump()
        {
            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                int found = 0;
                foreach (ObjectId id in ms)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Table tbl) continue;
                    found++;
                    Say($"TABLE {found}: rows={tbl.Rows.Count} cols={tbl.Columns.Count}");
                    for (int r = 0; r < tbl.Rows.Count; r++)
                    {
                        var parts = new List<string>();
                        for (int c = 0; c < tbl.Columns.Count; c++)
                        {
                            string s;
                            try { s = tbl.Cells[r, c].TextString ?? ""; }
                            catch (System.Exception ex) { s = "<" + ex.GetType().Name + ">"; }
                            parts.Add(s.Replace("\n", " ").Trim());
                        }
                        Say($"  T{found} r{r}: " + string.Join(" | ", parts));
                    }
                }
                Say($"TOTAL TABLES={found}");
                tr.Commit();
            }
            catch (System.Exception ex)
            {
                Say("DUMP-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Normalise a label for matching: collapse space, upper-case.</summary>
        private static string Norm(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var parts = s.Replace("\n", " ").Split(
                new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts).ToUpperInvariant();
        }

        /// <summary>
        /// Fill table values by matching the LABEL column, not by row index â€” so
        /// it works on any sheet regardless of table order or added rows, and
        /// fills every table that carries the labels.
        ///
        /// params.json: { "labelCol": 1, "valueCol": 2,
        ///                "byLabel": { "INSTALLER": "ACME", ... } }
        /// </summary>
        [CommandMethod("PZSEFILL")]
        public static void PzseFill()
        {
            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
                if (!File.Exists(path))
                {
                    Say("FILL-SKIPPED no params.json at " + path);
                    return;
                }
                using var pdoc = JsonDocument.Parse(File.ReadAllText(path));
                var root = pdoc.RootElement;
                int labelCol = root.TryGetProperty("labelCol", out var lc) ? lc.GetInt32() : 1;
                int valueCol = root.TryGetProperty("valueCol", out var vc) ? vc.GetInt32() : 2;

                var want = new Dictionary<string, string>();
                foreach (var kv in root.GetProperty("byLabel").EnumerateObject())
                    want[Norm(kv.Name)] = kv.Value.GetString() ?? "";
                Say($"labels to fill={want.Count} labelCol={labelCol} valueCol={valueCol}");

                using var tr = db.TransactionManager.StartTransaction();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                var tableIds = new List<ObjectId>();
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                        if (tr.GetObject(id, OpenMode.ForRead) is Table) tableIds.Add(id);
                }
                Say($"tables in drawing={tableIds.Count}");

                int totalSet = 0, totalFail = 0, tablesTouched = 0;
                for (int ti = 0; ti < tableIds.Count; ti++)
                {
                    var tbl = (Table)tr.GetObject(tableIds[ti], OpenMode.ForWrite);
                    if (tbl.Columns.Count <= valueCol) continue;

                    int setHere = 0;
                    for (int r = 0; r < tbl.Rows.Count; r++)
                    {
                        string label;
                        try { label = Norm(tbl.Cells[r, labelCol].TextString); }
                        catch { continue; }
                        if (label.Length == 0 || !want.TryGetValue(label, out var v)) continue;

                        try
                        {
                            var cell = tbl.Cells[r, valueCol];
                            // The cell holds a FIELD; its unresolved value renders
                            // as #### and wins over any text set underneath. Clear
                            // the field first, THEN write literal text.
                            bool hadField = !cell.FieldId.IsNull;
                            if (hadField) cell.FieldId = ObjectId.Null;
                            cell.TextString = v;
                            setHere++; totalSet++;
                            if (totalSet <= 6)
                                Say($"  T{ti} r{r} [{label}] field={(hadField ? "cleared" : "none")} <- {v}");
                        }
                        catch (System.Exception ex)
                        {
                            totalFail++;
                            Say($"  FAIL T{ti} r{r} [{label}]: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    if (setHere > 0)
                    {
                        tablesTouched++;
                        // GenerateLayout alone leaves the rendered block stale, so
                        // viewers keep drawing the old ####. Recompute it too.
                        try { tbl.RecomputeTableBlock(true); }
                        catch (System.Exception ex) { Say($"  recompute T{ti} failed: {ex.Message}"); }
                        try { tbl.GenerateLayout(); } catch { }
                        Say($"  table {ti}: {setHere} cells set");
                    }
                }
                tr.Commit();
                Say($"FILL-DONE set={totalSet} fail={totalFail} tables={tablesTouched}");
            }
            catch (System.Exception ex)
            {
                Say("FILL-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Erase every model-space entity, leaving paper space untouched.
        ///
        /// The title block, border, legend, logo and drawing info all live in
        /// paper space; the array geometry (~1.3M polylines on this project)
        /// lives in model space and is only seen through a viewport. Plotting
        /// the sheet intact exceeds the Design Automation time limit purely on
        /// regen, so strip model space first when the goal is to look at the
        /// sheet furniture. Destructive â€” run it on a copy.
        /// </summary>
        [CommandMethod("PZSESTRIP")]
        public static void PzseStrip()
        {
            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int erased = 0, failed = 0;
                foreach (ObjectId id in ms)
                {
                    try
                    {
                        var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        ent.Erase();
                        erased++;
                    }
                    catch { failed++; }
                }
                tr.Commit();
                Say($"STRIP-DONE erased={erased} failed={failed}");
            }
            catch (System.Exception ex)
            {
                Say("STRIP-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Switch off every paper-space viewport, so layouts plot their own
        /// furniture without regenerating model space through the window.
        ///
        /// Cheaper than PZSESTRIP by orders of magnitude: a handful of viewport
        /// objects rather than ~674k entities, which matters because the whole
        /// workitem has to finish inside the processing-time limit.
        /// </summary>
        [CommandMethod("PZSEVPOFF")]
        public static void PzseVpOff()
        {
            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                int off = 0, skipped = 0;
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (!btr.IsLayout) continue;
                    if (btr.Name.Equals(BlockTableRecord.ModelSpace,
                                        StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (ObjectId id in btr)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp) continue;
                        // Viewport number 1 is the paper-space "sheet" itself;
                        // switching it off would blank the whole layout.
                        if (vp.Number == 1) { skipped++; continue; }
                        var w = (Viewport)tr.GetObject(id, OpenMode.ForWrite);
                        w.On = false;   // property, not a method
                        off++;
                    }
                }
                tr.Commit();
                Say($"VPOFF-DONE off={off} skipped(sheet)={skipped}");
            }
            catch (System.Exception ex)
            {
                Say("VPOFF-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // â”€â”€ Sheet tiling â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // Standard architectural scales as ratios (model units per paper unit).
        // 3/16" = 1'-0" is 1:64; 3/32" is 1:128.
        private static readonly (string Name, double Ratio)[] ArchScales =
        {
            ("3\" = 1'", 4), ("1-1/2\" = 1'", 8), ("1\" = 1'", 12),
            ("3/4\" = 1'", 16), ("1/2\" = 1'", 24), ("3/8\" = 1'", 32),
            ("1/4\" = 1'", 48), ("3/16\" = 1'", 64), ("1/8\" = 1'", 96),
            ("3/32\" = 1'", 128), ("1/16\" = 1'", 192), ("3/64\" = 1'", 256),
            ("1/32\" = 1'", 384),
        };

        /// <summary>Largest ratio (most coverage) that still prints text at or
        /// above the legibility floor. Snapping DOWN never makes text smaller
        /// than asked for.</summary>
        private static (string Name, double Ratio) SnapScale(double modelTextH,
                                                             double floorIn)
        {
            double maxRatio = modelTextH / floorIn;
            (string, double) best = ArchScales[0];
            foreach (var s in ArchScales)
                if (s.Ratio <= maxRatio && s.Ratio > best.Item2) best = s;
            return best;
        }

        /// <summary>
        /// AutoCAD-style comma-separated wildcard match, e.g. "a_*,b_*,c_*".
        /// Needed where a name is tested directly rather than handed to a
        /// SelectionFilter, which is the only wildcard matcher elsewhere here.
        /// </summary>
        private static bool WildMatch(string name, string patterns)
        {
            if (string.IsNullOrWhiteSpace(patterns) || name == null) return false;
            foreach (var raw in patterns.Split(','))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                string rx = "^" + System.Text.RegularExpressions.Regex.Escape(p)
                                      .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                if (System.Text.RegularExpressions.Regex.IsMatch(name, rx,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;
            }
            return false;
        }

        private static SelectionFilter LayerFilter(string pattern) =>
            new SelectionFilter(new[] { new TypedValue((int)DxfCode.LayerName, pattern) });

        /// <summary>Select by layer wildcard; empty result is not an error.</summary>
        private static ObjectId[] SelectByLayer(Editor ed, string pattern)
        {
            var res = ed.SelectAll(LayerFilter(pattern));
            return res.Status == PromptStatus.OK ? res.Value.GetObjectIds()
                                                 : System.Array.Empty<ObjectId>();
        }

        /// <summary>
        /// Work out the sheet plan WITHOUT changing the drawing: measured
        /// block-count text height, the scale that keeps it legible, the extent
        /// of the PV geometry, and the resulting tile grid.
        /// </summary>
        [CommandMethod("PZSEPLAN")]
        public static void PzsePlan()
        {
            try
            {
                var plan = BuildPlan(out string err);
                if (plan == null) { Say("PLAN-FAILED " + err); return; }
                foreach (var line in plan.Report()) Say(line);
                File.WriteAllText(
                    Path.Combine(Directory.GetCurrentDirectory(), "plan.json"),
                    plan.ToJson());
                Say("PLAN-DONE wrote plan.json");
            }
            catch (System.Exception ex)
            {
                Say("PLAN-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private class SheetPlan
        {
            public double TextHeight, FloorIn, Ratio;
            public string ScaleName;
            public double MinX, MinY, MaxX, MaxY;     // model units (inches)
            public double VpW, VpH;                    // paper inches
            public double CovW, CovH;                  // model units per sheet
            public int Nx, Ny;                         // areas, total sheets
            public string Template;
            public List<(int Col, int Row, double Cx, double Cy)> Tiles = new();
            public List<(int No, double X0, double Y0, double X1, double Y1,
                         int Nx, int Ny, int Bins)> Areas = new();

            public IEnumerable<string> Report()
            {
                yield return $"text height    : {TextHeight:0.##}\" (floor {FloorIn:0.###}\")";
                yield return $"scale          : {ScaleName}  (1:{Ratio:0})";
                yield return $"viewport       : {VpW:0.##} x {VpH:0.##} in paper";
                yield return $"sheet covers   : {CovW / 12:0} x {CovH / 12:0} ft";
                yield return $"template       : {Template}";
                yield return $"pv areas       : {Areas.Count}";
                yield return $"TOTAL SHEETS   : {Ny}";
                foreach (var a in Areas.OrderByDescending(a => a.Nx * a.Ny).Take(12))
                    yield return
                        $"  area {a.No,-3} {(a.X1 - a.X0) / 12,6:0} x {(a.Y1 - a.Y0) / 12,5:0} ft" +
                        $"  at ({a.X0 / 12,8:0}, {a.Y0 / 12,8:0})" +
                        $"  {a.Nx}x{a.Ny}={a.Nx * a.Ny} sheets";
            }

            public string ToJson()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"scale\":\"").Append(ScaleName).Append("\",\"ratio\":")
                  .Append(Ratio).Append(",\"textHeight\":").Append(TextHeight)
                  .Append(",\"nx\":").Append(Nx).Append(",\"ny\":").Append(Ny)
                  .Append(",\"tiles\":[");
                for (int i = 0; i < Tiles.Count; i++)
                {
                    var t = Tiles[i];
                    if (i > 0) sb.Append(',');
                    sb.Append($"{{\"col\":{t.Col},\"row\":{t.Row}," +
                              $"\"cx\":{t.Cx:0.###},\"cy\":{t.Cy:0.###}}}");
                }
                return sb.Append("]}").ToString();
            }
        }

        private static SheetPlan BuildPlan(out string err)
        {
            err = null;
            var db = HostApplicationServices.WorkingDatabase;
            var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application
                     .DocumentManager.MdiActiveDocument.Editor;

            // Options, with defaults matching the agreed spec.
            double floorIn = 0.125, vpW = 0, vpH = 0;
            string template = "SR100";
            string pvPattern = "*Module*,*Bay*,BX,*Chassis*,*Ballast*";
            string countPattern = "*Block Count*";

            string pfile = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
            if (File.Exists(pfile))
            {
                using var pdoc = JsonDocument.Parse(File.ReadAllText(pfile));
                var r = pdoc.RootElement;
                if (r.TryGetProperty("floorIn", out var f)) floorIn = f.GetDouble();
                if (r.TryGetProperty("template", out var t)) template = t.GetString();
                if (r.TryGetProperty("pvLayers", out var p)) pvPattern = p.GetString();
                if (r.TryGetProperty("countLayers", out var cl)) countPattern = cl.GetString();
            }

            var plan = new SheetPlan { FloorIn = floorIn, Template = template };

            using var tr = db.TransactionManager.StartTransaction();

            // 1. Measured block-count text height â€” the legibility constraint.
            var counts = SelectByLayer(ed, countPattern);
            var seenH = new Dictionary<double, int>();
            foreach (var id in counts)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not DBText txt) continue;
                double h = System.Math.Round(txt.Height, 2);
                seenH[h] = seenH.TryGetValue(h, out var n) ? n + 1 : 1;
            }
            if (seenH.Count == 0) { err = $"no text on layers {countPattern}"; return null; }
            double modal = 0; int best = -1;
            foreach (var kv in seenH) if (kv.Value > best) { best = kv.Value; modal = kv.Key; }
            plan.TextHeight = modal;
            Say($"  block-count labels={counts.Length} heights={seenH.Count} modal={modal}\"");

            var scale = SnapScale(modal, floorIn);
            plan.ScaleName = scale.Name;
            plan.Ratio = scale.Ratio;

            // 2. Extent of the PV geometry (not $EXTMIN/$EXTMAX, which stray
            //    geometry inflates by an order of magnitude on this drawing).
            var pv = SelectByLayer(ed, pvPattern);
            if (pv.Length == 0) { err = $"no geometry on layers {pvPattern}"; return null; }
            double minx = double.MaxValue, miny = double.MaxValue;
            double maxx = double.MinValue, maxy = double.MinValue;
            int measured = 0;
            foreach (var id in pv)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
                try
                {
                    var ext = e.GeometricExtents;
                    if (ext.MinPoint.X < minx) minx = ext.MinPoint.X;
                    if (ext.MinPoint.Y < miny) miny = ext.MinPoint.Y;
                    if (ext.MaxPoint.X > maxx) maxx = ext.MaxPoint.X;
                    if (ext.MaxPoint.Y > maxy) maxy = ext.MaxPoint.Y;
                    measured++;
                }
                catch { /* entities without extents */ }
            }
            plan.MinX = minx; plan.MinY = miny; plan.MaxX = maxx; plan.MaxY = maxy;

            // 3. Viewport size from the template. NOT simply the largest â€” the
            //    sheet also carries 1:1 detail viewports bigger than the page
            //    itself. Take the largest one that actually fits the sheet.
            var lm = LayoutManager.Current;
            if (!LayoutExists(tr, db, template)) { err = $"layout {template} not found"; return null; }
            var lay = (Layout)tr.GetObject(lm.GetLayoutId(template), OpenMode.ForRead);
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            const double SHEET_W = 36.0, SHEET_H = 24.0;
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Viewport v) continue;
                if (v.Number == 1) continue;
                if (v.Width > SHEET_W || v.Height > SHEET_H) continue;   // detail vp
                if (v.Width * v.Height > vpW * vpH) { vpW = v.Width; vpH = v.Height; }
            }
            if (vpW <= 0) { err = $"no sheet-sized viewport on {template}"; return null; }
            plan.VpW = vpW; plan.VpH = vpH;
            plan.CovW = vpW * plan.Ratio;
            plan.CovH = vpH * plan.Ratio;

            // 4. Cluster the PV geometry into distinct arrays. Tiling one
            //    bounding box over the whole model would cover thousands of feet
            //    of empty space between unrelated arrays.
            double cell = 50.0 * 12.0;                 // 50 ft bins
            var bins = new Dictionary<(int, int), int>();
            var binBox = new Dictionary<(int, int), double[]>();
            int oversize = 0;
            foreach (var id in pv)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
                Extents3d ext;
                try { ext = e.GeometricExtents; } catch { continue; }

                // Skip site outlines and boundaries that live on module-named
                // layers. A module is roughly 7 x 4 ft; anything wider than a
                // whole sheet stretches its cluster across the entire model and
                // turns three real arrays into one 3,300 ft phantom.
                if (ext.MaxPoint.X - ext.MinPoint.X > plan.CovW ||
                    ext.MaxPoint.Y - ext.MinPoint.Y > plan.CovH)
                {
                    oversize++;
                    continue;
                }

                double cx0 = (ext.MinPoint.X + ext.MaxPoint.X) / 2.0;
                double cy0 = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0;
                var key = ((int)System.Math.Floor(cx0 / cell),
                           (int)System.Math.Floor(cy0 / cell));
                bins[key] = bins.TryGetValue(key, out var bn) ? bn + 1 : 1;
                if (!binBox.TryGetValue(key, out var bb))
                    binBox[key] = new[] { ext.MinPoint.X, ext.MinPoint.Y,
                                          ext.MaxPoint.X, ext.MaxPoint.Y };
                else
                {
                    if (ext.MinPoint.X < bb[0]) bb[0] = ext.MinPoint.X;
                    if (ext.MinPoint.Y < bb[1]) bb[1] = ext.MinPoint.Y;
                    if (ext.MaxPoint.X > bb[2]) bb[2] = ext.MaxPoint.X;
                    if (ext.MaxPoint.Y > bb[3]) bb[3] = ext.MaxPoint.Y;
                }
            }

            var live = new HashSet<(int, int)>();
            foreach (var kv in bins) if (kv.Value >= 3) live.Add(kv.Key);

            // Collect areas first, then order them, then number the sheets.
            // Walking the HashSet directly gave non-deterministic ordering, so
            // sheet numbers could shuffle between identical runs.
            var found = new List<(double X0, double Y0, double X1, double Y1, int Bins)>();
            var seenBin = new HashSet<(int, int)>();
            foreach (var start in live)
            {
                if (!seenBin.Add(start)) continue;
                var stack = new Stack<(int, int)>();
                stack.Push(start);
                var blob = new List<(int, int)>();
                while (stack.Count > 0)
                {
                    var c0 = stack.Pop();
                    blob.Add(c0);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var nb = (c0.Item1 + dx, c0.Item2 + dy);
                            if (live.Contains(nb) && seenBin.Add(nb)) stack.Push(nb);
                        }
                }

                double ax0 = double.MaxValue, ay0 = double.MaxValue;
                double ax1 = double.MinValue, ay1 = double.MinValue;
                foreach (var c0 in blob)
                {
                    var bb = binBox[c0];
                    if (bb[0] < ax0) ax0 = bb[0];
                    if (bb[1] < ay0) ay0 = bb[1];
                    if (bb[2] > ax1) ax1 = bb[2];
                    if (bb[3] > ay1) ay1 = bb[3];
                }

                // Drop degenerate areas â€” stray geometry that cleared the
                // entity threshold but is not an array (a 30 x 1 ft sliver, or
                // a zero-extent point) would otherwise each claim a sheet.
                const double MIN_SIDE = 20.0 * 12.0;      // 20 ft
                if (ax1 - ax0 < MIN_SIDE && ay1 - ay0 < MIN_SIDE)
                {
                    Say($"  skipping degenerate area at ({ax0 / 12:0}, {ay0 / 12:0}) "
                        + $"{(ax1 - ax0) / 12:0}x{(ay1 - ay0) / 12:0} ft");
                    continue;
                }
                found.Add((ax0, ay0, ax1, ay1, blob.Count));
            }

            // Areas in reading order too: topmost first, then left to right.
            // Banded on the sheet height so two areas at roughly the same
            // height are read across rather than one being pushed after the
            // other by a few feet of difference.
            found.Sort((a, b) =>
            {
                int bandA = (int)System.Math.Floor(-a.Y1 / plan.CovH);
                int bandB = (int)System.Math.Floor(-b.Y1 / plan.CovH);
                if (bandA != bandB) return bandA.CompareTo(bandB);
                return a.X0.CompareTo(b.X0);
            });

            int areaNo = 0, totalSheets = 0;
            foreach (var a in found)
            {
                int nx = System.Math.Max(1, (int)System.Math.Ceiling((a.X1 - a.X0) / plan.CovW));
                int ny = System.Math.Max(1, (int)System.Math.Ceiling((a.Y1 - a.Y0) / plan.CovH));
                totalSheets += nx * ny;

                // Reading order within the area: left to right, top to bottom.
                for (int row = 0; row < ny; row++)
                    for (int col = 0; col < nx; col++)
                        plan.Tiles.Add((col, row,
                                        a.X0 + (col + 0.5) * plan.CovW,
                                        a.Y1 - (row + 0.5) * plan.CovH));

                plan.Areas.Add((areaNo, a.X0, a.Y0, a.X1, a.Y1, nx, ny, a.Bins));
                areaNo++;
            }

            Say($"  pv entities={pv.Length} measured={measured} oversize-skipped={oversize}");
            plan.Nx = plan.Areas.Count;      // areas, not columns, once clustered
            plan.Ny = totalSheets;
            tr.Commit();
            return plan;
        }

        /// <summary>
        /// Generate one sheet per tile: clone the template layout and aim its
        /// plan viewport at that tile's patch of model space.
        ///
        /// params.json may carry "maxSheets" to cap a trial run and
        /// "sheetPrefix" to control naming (default PZ-001, PZ-002, ...).
        /// </summary>
        [CommandMethod("PZSETILE")]
        public static void PzseTile()
        {
            try
            {
                var plan = BuildPlan(out string err);
                if (plan == null) { Say("TILE-FAILED " + err); return; }
                foreach (var line in plan.Report()) Say(line);

                int maxSheets = int.MaxValue;
                string prefix = "PZ-";
                string pfile = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
                if (File.Exists(pfile))
                {
                    using var pdoc = JsonDocument.Parse(File.ReadAllText(pfile));
                    var r = pdoc.RootElement;
                    if (r.TryGetProperty("maxSheets", out var m)) maxSheets = m.GetInt32();
                    if (r.TryGetProperty("sheetPrefix", out var s)) prefix = s.GetString();
                }

                var db = HostApplicationServices.WorkingDatabase;
                var lm = LayoutManager.Current;
                int made = 0, failed = 0;
                int n = System.Math.Min(plan.Tiles.Count, maxSheets);
                Say($"generating {n} of {plan.Tiles.Count} sheets, prefix {prefix}");

                for (int i = 0; i < n; i++)
                {
                    var tile = plan.Tiles[i];
                    string name = prefix + (i + 1).ToString("000");
                    try
                    {
                        lm.CloneLayout(plan.Template, name, i + 1);
                        AimViewport(db, lm, name, tile.Cx, tile.Cy, plan.CovH);
                        made++;
                        if (made <= 5 || made % 20 == 0)
                            Say($"  {name}  centre ({tile.Cx / 12:0}, {tile.Cy / 12:0}) ft"
                                + $"  [area tile c{tile.Col} r{tile.Row}]");
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        if (failed <= 5)
                            Say($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                Say($"TILE-DONE made={made} failed={failed}");
            }
            catch (System.Exception ex)
            {
                Say("TILE-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Point a cloned layout's plan viewport at one tile.</summary>
        private static void AimViewport(Database db, LayoutManager lm, string layoutName,
                                        double cx, double cy, double viewHeight)
        {
            // A cloned layout's viewports come back unrealised â€” Number is -1
            // and they will not display until the layout has been current.
            // Activating it before we edit is what makes On settable at all
            // (setting it from another layout throws eNotApplicable).
            try { lm.CurrentLayout = layoutName; } catch { }

            using var tr = db.TransactionManager.StartTransaction();
            var lay = (Layout)tr.GetObject(lm.GetLayoutId(layoutName), OpenMode.ForRead);
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);

            // Aim EVERY sheet-sized viewport, not merely the largest. The
            // template carries more than one big window â€” a 21 x 20 in one
            // pointed at a different roof â€” and leaving it alone made all ten
            // sheets render the same view regardless of the tile.
            // Anything bigger than the page is a detail viewport; anything tiny
            // is a callout. Both are left as drawn.
            const double SHEET_W = 36.0, SHEET_H = 24.0, MIN_AREA = 100.0;
            var targets = new List<ObjectId>();
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Viewport v) continue;
                if (v.Number == 1) continue;
                if (v.Width > SHEET_W || v.Height > SHEET_H) continue;
                if (v.Width * v.Height < MIN_AREA) continue;
                targets.Add(id);
            }
            if (targets.Count == 0)
                throw new InvalidOperationException("no sheet-sized viewport in clone");

            foreach (var id in targets)
            {
                var vp = (Viewport)tr.GetObject(id, OpenMode.ForWrite);
                // Scale from THIS viewport's own paper height, so a smaller
                // window still shows the tile at the same 1:64.
                vp.ViewCenter = new Autodesk.AutoCAD.Geometry.Point2d(cx, cy);
                vp.ViewHeight = viewHeight * (vp.Height / 22.36);
                vp.TwistAngle = 0.0;             // template windows carry a twist
                try { vp.On = true; } catch { }  // unrealised clones come back off
            }
            tr.Commit();
        }

        /// <summary>
        /// Report every layout's viewports: on/off, aim, twist, and â€” the thing
        /// most likely to make sheets look identical â€” which layers are frozen
        /// in that viewport. AutoCAD freezes newly created layers in existing
        /// viewports, so layers added after a template was drawn are invisible
        /// through it, and cloning propagates that to every sheet.
        /// </summary>
        [CommandMethod("PZSEVPINFO")]
        public static void PzseVpInfo()
        {
            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

                foreach (DBDictionaryEntry de in dict)
                {
                    var lay = (Layout)tr.GetObject(de.Value, OpenMode.ForRead);
                    if (lay.LayoutName.Equals("Model", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Keep the output inside the log limit.
                    bool interesting = lay.LayoutName.StartsWith("S1.")
                                       || lay.LayoutName.StartsWith("PZ-")
                                       || lay.LayoutName.StartsWith("SR100");
                    if (!interesting) continue;

                    var btr = (BlockTableRecord)tr.GetObject(
                        lay.BlockTableRecordId, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp) continue;
                        if (vp.Number == 1) continue;

                        ObjectIdCollection frozen = null;
                        try { frozen = vp.GetFrozenLayers(); } catch { }
                        int nf = frozen?.Count ?? 0;

                        // Name a few frozen layers, favouring ours.
                        var names = new List<string>();
                        int ours = 0;
                        if (frozen != null)
                            foreach (ObjectId lid in frozen)
                            {
                                if (tr.GetObject(lid, OpenMode.ForRead) is not LayerTableRecord lt)
                                    continue;
                                if (lt.Name.StartsWith("Roof ", StringComparison.OrdinalIgnoreCase))
                                {
                                    ours++;
                                    if (names.Count < 3) names.Add(lt.Name);
                                }
                            }

                        Say($"{lay.LayoutName,-10} vp#{vp.Number,-3} on={vp.On,-5} "
                            + $"{vp.Width:0.0}x{vp.Height:0.0}in "
                            + $"centre=({vp.ViewCenter.X / 12:0},{vp.ViewCenter.Y / 12:0})ft "
                            + $"viewH={vp.ViewHeight / 12:0}ft twist={vp.TwistAngle:0.###} "
                            + $"frozen={nf} ofOurs={ours}");
                        if (names.Count > 0)
                            Say($"             e.g. {string.Join(", ", names)}");
                    }
                }
                tr.Commit();
                Say("VPINFO-DONE");
            }
            catch (System.Exception ex)
            {
                Say("VPINFO-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Global layer state for our output â€” off/frozen/no-plot
        /// layers are invisible through every viewport regardless of aim.</summary>
        [CommandMethod("PZSELAYERS")]
        public static void PzseLayers()
        {
            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                int ours = 0, off = 0, frozen = 0, noplot = 0;
                var samples = new List<string>();
                foreach (ObjectId id in lt)
                {
                    var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!l.Name.StartsWith("Roof ", StringComparison.OrdinalIgnoreCase))
                        continue;
                    ours++;
                    if (l.IsOff) off++;
                    if (l.IsFrozen) frozen++;
                    if (!l.IsPlottable) noplot++;
                    if (samples.Count < 8)
                        samples.Add($"{l.Name} off={l.IsOff} frozen={l.IsFrozen} "
                                    + $"plot={l.IsPlottable} color={l.Color.ColorIndex} "
                                    + $"vpdflt-frozen={l.ViewportVisibilityDefault}");
                }
                Say($"Roof* layers={ours}  off={off}  frozen={frozen}  non-plotting={noplot}");
                foreach (var s in samples) Say("  " + s);
                tr.Commit();
                Say("LAYERS-DONE");
            }
            catch (System.Exception ex)
            {
                Say("LAYERS-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // â”€â”€ Array-driven sheet planning â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // Replaces the spatial clustering. The .scr names every layer
        // "Roof <n> Arr <m> <thing>", so array identity is explicit and there
        // is no threshold to tune. Sheets are then packed the way Sollega's
        // engineer does it: at most two arrays, stacked, biggest alone.

        private static readonly System.Text.RegularExpressions.Regex ArrLayer =
            new(@"^Roof\s+(\d+)\s+Arr\s+(\d+)\s+(.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Everything the .scr actually plots. "description" is excluded because
        // that layer is created non-plotting; "anchor" is included because
        // anchors sit on the array perimeter and leaving them out let arrays
        // overflow their window and clip.
        private static readonly HashSet<string> DrawnKinds =
            new(StringComparer.OrdinalIgnoreCase)
            { "bay", "block count", "module", "anchor" };

        private static readonly System.Text.RegularExpressions.Regex ArrKey =
            new(@"^Roof\s+(\d+)\s+Arr\s+(\d+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Compact identifier for a tab name: "R1-A01b", or "R1-A01a-2of4" for
        /// one tile of a split array. "S1.7" alone says nothing about which array
        /// is on the page, so finding a given array meant opening tabs until one
        /// matched.
        /// </summary>
        private static string Token(ArrayInfo a)
        {
            string suffix = a.Key != null && a.Name.Length > a.Key.Length
                ? a.Name.Substring(a.Key.Length) : "";
            var m = ArrKey.Match(a.Key ?? a.Name ?? "");
            string core = m.Success
                ? $"R{int.Parse(m.Groups[1].Value)}-A{int.Parse(m.Groups[2].Value):00}{suffix}"
                : (a.Name ?? "?");
            return a.TileCount > 1 ? $"{core}-{a.Tile}of{a.TileCount}" : core;
        }

        private class ArrayInfo
        {
            public string Name;
            /// <summary>
            /// The layer-name key this body came from ("Roof 1 Arr 01"), which is
            /// NOT Name once Cluster has split it ("Roof 1 Arr 01a"). Viewport
            /// isolation matches layers against this; matching against Name froze
            /// a split array's own layers and produced an empty window.
            /// </summary>
            public string Key;
            public double X0 = double.MaxValue, Y0 = double.MaxValue;
            public double X1 = double.MinValue, Y1 = double.MinValue;
            public int Ents;
            /// <summary>
            /// Module manufacturer, e.g. "HELIENE". Derived from the BLOCK NAME
            /// on the vendor module layer - the array layers are all one colour
            /// and carry no make at all. Null when not resolved.
            /// </summary>
            public string Make;
            /// <summary>1-based tile index; TileCount &lt;= 1 means not tiled.</summary>
            public int Tile, TileCount;
            public double W => X1 - X0;
            public double H => Y1 - Y0;

            /// <summary>What the sheet calls this: "Roof 1 Arr 01a (2 of 4)".</summary>
            public string Display => TileCount > 1
                ? $"{Name} ({Tile} of {TileCount})" : Name;
        }

        /// <summary>One entity's model-space footprint.</summary>
        private struct Box
        {
            public double X0, Y0, X1, Y1;
        }

        /// <summary>
        /// Split the entities sharing one array layer into spatially connected
        /// bodies, joining two whose boxes come within <paramref name="gap"/>.
        ///
        /// The layer name does not define an array. Client drawings copy
        /// geometry between roofs and do not always re-layer it: on the San
        /// Diego airport job four "Roof 1 Arr N" layers each carried a second
        /// body ~2,500ft east over Roof 3, so the union of their extents spanned
        /// the whole site and the packer tiled them into 251 sheets against the
        /// engineer's 24. Grouping by proximity as well as by name is what makes
        /// the plan survive a drawing nobody cleaned up first.
        ///
        /// Single-linkage union-find, bucketed into a grid of cell size
        /// <paramref name="gap"/> so linkage stays near-linear. Entities bigger
        /// than one sheet are already filtered out by the caller, so a box spans
        /// at most a couple of cells and the 3x3 neighbour sweep cannot miss a
        /// pair that ought to join.
        /// </summary>
        private static List<List<int>> Cluster(List<Box> boxes, double gap)
        {
            int n = boxes.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
                return i;
            }

            var grid = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < n; i++)
            {
                long cx0 = (long)System.Math.Floor(boxes[i].X0 / gap);
                long cx1 = (long)System.Math.Floor(boxes[i].X1 / gap);
                long cy0 = (long)System.Math.Floor(boxes[i].Y0 / gap);
                long cy1 = (long)System.Math.Floor(boxes[i].Y1 / gap);
                for (long cx = cx0; cx <= cx1; cx++)
                    for (long cy = cy0; cy <= cy1; cy++)
                    {
                        if (!grid.TryGetValue((cx, cy), out var l))
                            grid[(cx, cy)] = l = new List<int>();
                        l.Add(i);
                    }
            }

            foreach (var kv in grid)
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        if (!grid.TryGetValue((kv.Key.Item1 + dx, kv.Key.Item2 + dy),
                                              out var other)) continue;
                        foreach (int i in kv.Value)
                            foreach (int j in other)
                            {
                                if (i >= j) continue;
                                if (Find(i) == Find(j)) continue;
                                var a = boxes[i]; var b = boxes[j];
                                if (a.X0 - b.X1 > gap || b.X0 - a.X1 > gap) continue;
                                if (a.Y0 - b.Y1 > gap || b.Y0 - a.Y1 > gap) continue;
                                parent[Find(j)] = Find(i);
                            }
                    }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var l)) groups[r] = l = new List<int>();
                l.Add(i);
            }
            return groups.Values.ToList();
        }


        /// <summary>One array's spot on a sheet, in paper inches from the
        /// window's bottom-left corner.</summary>
        private struct Slot
        {
            public double X, Y, W, H;
        }

        /// <summary>
        /// Lay arrays out preserving their arrangement on the roof, with the
        /// empty roof between them squeezed out.
        ///
        /// Shelf packing wrapped wherever the page width ran out, which could
        /// put two arrays side by side on paper that are nowhere near each other
        /// in reality. Here a page row IS a roof row: arrays whose model extents
        /// overlap vertically stay on the same row, ordered west to east, and
        /// rows run north to south. Reading the sheet then tells you something
        /// true about where things are.
        /// </summary>
        private static List<Slot> RelativePack(
            List<(double Cx, double Cy, double W, double H)> items,
            double winW, double winH, double gap)
        {
            if (items.Count == 0) return new List<Slot>();

            // Band by model Y: an array joins the current row if it overlaps it
            // vertically by more than half its own height.
            var order = items.Select((it, i) => (it, i))
                             .OrderByDescending(t => t.it.Cy).ToList();
            var rows = new List<List<(int Idx, double Cx, double Cy, double W, double H)>>();
            foreach (var (it, idx) in order)
            {
                var row = rows.LastOrDefault();
                bool joins = false;
                if (row != null)
                {
                    double rowTop = row.Max(r => r.Cy + r.H / 2);
                    double rowBot = row.Min(r => r.Cy - r.H / 2);
                    double top = it.Cy + it.H / 2, bot = it.Cy - it.H / 2;
                    double overlap = System.Math.Min(rowTop, top) - System.Math.Max(rowBot, bot);
                    joins = overlap > it.H * 0.5;
                }
                if (!joins) { row = new(); rows.Add(row); }
                row.Add((idx, it.Cx, it.Cy, it.W, it.H));
            }

            double totalH = rows.Sum(r => r.Max(z => z.H)) + gap * (rows.Count - 1);
            if (totalH > winH) return null;
            foreach (var r in rows)
                if (r.Sum(z => z.W) + gap * (r.Count - 1) > winW) return null;

            var slots = new Slot[items.Count];
            double y = winH;
            foreach (var row in rows)
            {
                double rowH = row.Max(z => z.H);
                double x = 0;
                foreach (var z in row.OrderBy(z => z.Cx))     // west to east
                {
                    slots[z.Idx] = new Slot { X = x, Y = y - rowH + (rowH - z.H) / 2,
                                              W = z.W, H = z.H };
                    x += z.W + gap;
                }
                y -= rowH + gap;
            }
            return slots.ToList();
        }

        /// <summary>
        /// Centre a packed layout in the window: each row centred across, the
        /// block of rows centred down, and short items centred within their
        /// row's band. Packing builds from the top-left corner, which leaves
        /// everything hanging off the north-west; this is a pure post-process
        /// so it cannot change what went on which sheet.
        /// </summary>
        private static void CentreSlots(List<Slot> slots, double winW, double winH)
        {
            if (slots == null || slots.Count == 0) return;

            // Items packed into the same row share a top edge.
            var rows = slots.GroupBy(s => System.Math.Round(s.Y + s.H, 4)).ToList();

            double blockTop = rows.Max(r => r.Key);
            double blockBottom = slots.Min(s => s.Y);
            double dy = (winH - (blockTop - blockBottom)) / 2.0 - blockBottom;

            foreach (var row in rows)
            {
                var items = row.ToList();
                double left = items.Min(s => s.X);
                double right = items.Max(s => s.X + s.W);
                double dx = (winW - (right - left)) / 2.0 - left;
                double rowH = items.Max(s => s.H);
                double rowTop = row.Key;

                foreach (var s in items)
                {
                    int k = slots.FindIndex(z => z.X == s.X && z.Y == s.Y
                                                 && z.W == s.W && z.H == s.H);
                    if (k < 0) continue;
                    var v = slots[k];
                    v.X += dx;
                    // Centre within the row band so a short array beside a tall
                    // one does not hang from the top edge.
                    v.Y = (rowTop - rowH) + (rowH - v.H) / 2.0 + dy;
                    slots[k] = v;
                }
            }
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

        private class SheetSet
        {
            public string ScaleName, KeyName, Template;
            public double Ratio, KeyRatio, VpW, VpH, GapPaper, BaseRatio;
            public double PadPaper;                    // breathing room per side
            public double SiteCx, SiteCy, SiteW, SiteH;
            /// <summary>
            /// One entry per key-plan window when the key plan is compacted.
            /// Empty means the old behaviour: a single window over the whole
            /// site. Never one entry - a lone group is no compaction at all.
            /// </summary>
            public List<ArrayInfo> KeyBoxes = new();
            public double KeyGapPaper;
            public double KeyBottomInset;
            /// <summary>Every array body, for key-plan callouts.</summary>
            public List<ArrayInfo> KeyArrays = new();
            public double KeyLabelBand;
            public List<List<ArrayInfo>> Sheets = new();
        }

        /// <summary>Report the plan without touching the drawing.</summary>
        [CommandMethod("PZSESHEETS")]
        public static void PzseSheets()
        {
            var plan = PlanSheets(out string err);
            if (plan == null) { Say("SHEETS-FAILED " + err); return; }
            Say($"scale {plan.ScaleName} (1:{plan.Ratio:0})  "
                + $"window {plan.VpW:0.##}x{plan.VpH:0.##}in");
            Say($"key plan {plan.KeyName} for "
                + $"{plan.SiteW / 12:0}x{plan.SiteH / 12:0}ft site");
            Say($"SHEETS: 1 key plan + {plan.Sheets.Count} array sheets "
                + $"= {plan.Sheets.Count + 1}");
            for (int i = 0; i < plan.Sheets.Count; i++)
                Say($"  S1.{i + 1,-3} " + string.Join(" + ", plan.Sheets[i]
                    .Select(a => $"{a.Display} [{Token(a)}] "
                               + $"({a.W / 12:0}x{a.H / 12:0}ft)")));
            Say("SHEETS-DONE");
        }

        /// <summary>Shared planning used by both the report and the generator.</summary>
        private static SheetSet PlanSheets(out string err)
        {
            err = null;
            try
            {
                double floorIn = 0.125, gapFt = 6.0;
                int maxPerSheet = 2;
                string template = "SR100 (2)";
                string countPattern = "*Block Count*";
                // Paper inches of clearance on each side of an array. The array
                // extents are the extents of its geometry, so a window sized
                // exactly to them clips whatever sits on the boundary â€” which in
                // practice is the edge ballast trays.
                double padIn = 0.10;
                // Two bodies sharing one array layer but further apart than this
                // are not one array â€” see Cluster().
                double clusterGapFt = 100.0;
                // Below this an isolated body is drafting debris, not an array.
                int minPartEnts = 8;
                // Give each cluster of arrays its own key-plan window instead of
                // one window over the whole site. OFF by default: it is a remedy
                // for a site spread far wider than the ground it covers, and a
                // site that fits the page honestly should be drawn as it sits.
                bool keyCompact = false;
                // Arrays further apart than this start a new key-plan group.
                // Deliberately coarser than clusterGapFt: that one decides what
                // is one array, this one decides what shares a window.
                double keyGroupGapFt = 200.0;
                // Paper inches along the bottom of the key-plan window to keep
                // clear. The template's module-details legend sits inside the
                // drawing area, bottom-left, and is template content the placer
                // does not erase. Uncompacted it never mattered - the arrays
                // were too small to reach it. Compacted they land right on it.
                double keyBottomInsetIn = 0.0;
                // Draw the engineer's per-array callouts on the key plan.
                bool keyLabels = false;
                // Paper inches reserved ABOVE the packed groups for label rows.
                // The band below costs nothing - it reuses keyBottomInsetIn,
                // which is already being given up to clear the legend.
                double keyLabelBandIn = 2.0;
                // Where the module manufacturer lives. The array layers are all
                // one colour (ACI 5 here), so the make is NOT in the layer
                // scheme; it is the block name on the vendor's module layer.
                string makeLayer = "a_Modules";
                // Block name -> manufacturer, e.g. {"H":"HELIENE","T":"TALESUN"}.
                // Verified against the titleblock counts before trusting it.
                var makeNames = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                // Layers whose geometry the KEY PLAN frames, when the drawing
                // carries a second, already-coherent copy of the site. See the
                // key plan source note below. Empty = frame the analysis arrays.
                string keySourceLayers = null;

                string pfile = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
                if (File.Exists(pfile))
                {
                    using var pdoc = JsonDocument.Parse(File.ReadAllText(pfile));
                    var r = pdoc.RootElement;
                    if (r.TryGetProperty("floorIn", out var f)) floorIn = f.GetDouble();
                    if (r.TryGetProperty("gapFt", out var g)) gapFt = g.GetDouble();
                    if (r.TryGetProperty("maxPerSheet", out var m)) maxPerSheet = m.GetInt32();
                    if (r.TryGetProperty("template", out var t)) template = t.GetString();
                    if (r.TryGetProperty("countLayers", out var c)) countPattern = c.GetString();
                    if (r.TryGetProperty("padIn", out var pd0)) padIn = pd0.GetDouble();
                    if (r.TryGetProperty("clusterGapFt", out var cg)) clusterGapFt = cg.GetDouble();
                    if (r.TryGetProperty("minPartEnts", out var mp)) minPartEnts = mp.GetInt32();
                    if (r.TryGetProperty("keyCompact", out var kc)) keyCompact = kc.GetBoolean();
                    if (r.TryGetProperty("keyGroupGapFt", out var kg)) keyGroupGapFt = kg.GetDouble();
                    if (r.TryGetProperty("keyBottomInsetIn", out var kb0)) keyBottomInsetIn = kb0.GetDouble();
                    if (r.TryGetProperty("keyLabels", out var kl)) keyLabels = kl.GetBoolean();
                    if (r.TryGetProperty("keyLabelBandIn", out var klb)) keyLabelBandIn = klb.GetDouble();
                    if (r.TryGetProperty("makeLayer", out var ml)) makeLayer = ml.GetString();
                    if (r.TryGetProperty("makeNames", out var mn))
                        foreach (var pr in mn.EnumerateObject())
                            makeNames[pr.Name] = pr.Value.GetString();
                    if (r.TryGetProperty("keySourceLayers", out var ksl)) keySourceLayers = ksl.GetString();
                }

                var db = HostApplicationServices.WorkingDatabase;
                var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application
                         .DocumentManager.MdiActiveDocument.Editor;
                using var tr = db.TransactionManager.StartTransaction();

                // 1. Scale from the measured block-count text.
                var counts = SelectByLayer(ed, countPattern);
                var heights = new Dictionary<double, int>();
                foreach (var id in counts)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not DBText t2) continue;
                    double h = System.Math.Round(t2.Height, 2);
                    heights[h] = heights.TryGetValue(h, out var n) ? n + 1 : 1;
                }
                if (heights.Count == 0) { err = "no block-count text"; return null; }
                double modal = 0; int best = -1;
                foreach (var kv in heights) if (kv.Value > best) { best = kv.Value; modal = kv.Key; }
                var scale = SnapScale(modal, floorIn);
                Say($"text {modal}\" / floor {floorIn}\" -> {scale.Name} (1:{scale.Ratio:0})");

                // 2. The template's plan window.
                var lm = LayoutManager.Current;
                if (!LayoutExists(tr, db, template))
                { err = $"layout {template} not found"; return null; }
                var lay = (Layout)tr.GetObject(lm.GetLayoutId(template), OpenMode.ForRead);
                var lbtr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                double vpW = 0, vpH = 0;
                foreach (ObjectId id in lbtr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Viewport v) continue;
                    if (v.Number == 1 || v.Width > 36.0 || v.Height > 24.0) continue;
                    if (v.Width * v.Height > vpW * vpH) { vpW = v.Width; vpH = v.Height; }
                }
                if (vpW <= 0) { err = "no plan window"; return null; }
                double covW = vpW * scale.Ratio, covH = vpH * scale.Ratio;
                Say($"window {vpW:0.##}x{vpH:0.##}in = {covW / 12:0}x{covH / 12:0}ft");

                // 3. Size each array from its own layers. Collect the boxes per
                //    layer name first; the name alone does not define an array
                //    (see Cluster), so the grouping happens afterwards.
                var boxesByKey = new Dictionary<string, List<Box>>();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
                    var m2 = ArrLayer.Match(e.Layer);
                    if (!m2.Success || !DrawnKinds.Contains(m2.Groups[3].Value.Trim())) continue;
                    Extents3d ext;
                    try { ext = e.GeometricExtents; } catch { continue; }
                    // A single entity wider than a sheet is a stray outline.
                    if (ext.MaxPoint.X - ext.MinPoint.X > covW ||
                        ext.MaxPoint.Y - ext.MinPoint.Y > covH) continue;

                    string key = $"Roof {int.Parse(m2.Groups[1].Value)} " +
                                 $"Arr {int.Parse(m2.Groups[2].Value):00}";
                    if (!boxesByKey.TryGetValue(key, out var bl))
                        boxesByKey[key] = bl = new List<Box>();
                    bl.Add(new Box
                    {
                        X0 = ext.MinPoint.X, Y0 = ext.MinPoint.Y,
                        X1 = ext.MaxPoint.X, Y1 = ext.MaxPoint.Y,
                    });
                }

                // Split each layer name into its spatially connected bodies.
                var arrays = new List<ArrayInfo>();
                double clusterGap = clusterGapFt * 12.0;
                foreach (var kv in boxesByKey.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var parts = Cluster(kv.Value, clusterGap)
                        .Select(c =>
                        {
                            var a = new ArrayInfo
                            { Name = kv.Key, Key = kv.Key, Ents = c.Count };
                            foreach (int i in c)
                            {
                                var b = kv.Value[i];
                                if (b.X0 < a.X0) a.X0 = b.X0;
                                if (b.Y0 < a.Y0) a.Y0 = b.Y0;
                                if (b.X1 > a.X1) a.X1 = b.X1;
                                if (b.Y1 > a.Y1) a.Y1 = b.Y1;
                            }
                            return a;
                        })
                        // Largest body first, so a split array's "a" is its main one.
                        .OrderByDescending(a => a.Ents).ToList();

                    // Never drop a body silently â€” a dropped body is geometry that
                    // reaches no sheet, which is the one failure nobody would spot.
                    foreach (var d in parts.Where(p => p.Ents < minPartEnts))
                        Say($"  NOTE {kv.Key}: ignoring {d.Ents}-entity body at "
                            + $"({d.X0 / 12:0},{d.Y0 / 12:0})ft "
                            + $"(under minPartEnts={minPartEnts})");
                    parts = parts.Where(p => p.Ents >= minPartEnts).ToList();

                    if (parts.Count > 1)
                    {
                        for (int i = 0; i < parts.Count; i++)
                            parts[i].Name = kv.Key + (char)('a' + i);
                        Say($"  SPLIT {kv.Key} -> {parts.Count} bodies: "
                            + string.Join(", ", parts.Select(
                                p => $"{p.Name} {p.W / 12:0}x{p.H / 12:0}ft "
                                   + $"@({p.X0 / 12:0},{p.Y0 / 12:0})ft")));
                    }
                    arrays.AddRange(parts);
                }

                // Order by POSITION, not array number. The .scr numbering has no
                // spatial meaning, so packing in that order paired arrays from
                // opposite ends of the roof and made the sheet sequence zigzag.
                // Band top-to-bottom by sheet height, then left-to-right within
                // a band â€” the way a drafter walks a roof.
                var unordered = arrays.Where(a => a.Ents > 0).ToList();
                if (unordered.Count == 0) { err = "no arrays"; return null; }
                double topY = unordered.Max(a => a.Y1);
                var list = unordered
                    .OrderBy(a => (int)System.Math.Floor((topY - a.Y1) / covH))
                    .ThenBy(a => a.X0)
                    .ToList();
                Say($"arrays: {list.Count} (ordered by position)");

                // 3b. Manufacturer per array, by majority of the module blocks
                //     standing inside it. The make is not in the layer scheme -
                //     every "Roof N Arr M Module" layer is the same colour - but
                //     the vendor's module layer carries one block per module and
                //     the block NAME is the make ("H" / "T"). Counting those
                //     against the titleblock is what proves the mapping.
                if (keyLabels && makeNames.Count > 0)
                {
                    var tally = new Dictionary<ArrayInfo, Dictionary<string, int>>();
                    int placed = 0, orphan = 0;
                    var msr = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in msr)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br) continue;
                        if (!string.Equals(br.Layer, makeLayer, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!makeNames.TryGetValue(br.Name, out string make)) continue;
                        double px = br.Position.X, py = br.Position.Y;
                        ArrayInfo owner = null;
                        foreach (var a in list)
                            if (px >= a.X0 && px <= a.X1 && py >= a.Y0 && py <= a.Y1)
                            { owner = a; break; }
                        if (owner == null) { orphan++; continue; }
                        if (!tally.TryGetValue(owner, out var d))
                            tally[owner] = d = new Dictionary<string, int>();
                        d[make] = d.TryGetValue(make, out var c0) ? c0 + 1 : 1;
                        placed++;
                    }
                    foreach (var kv in tally)
                        kv.Key.Make = kv.Value.OrderByDescending(z => z.Value).First().Key;
                    var totals = new Dictionary<string, int>();
                    foreach (var d in tally.Values)
                        foreach (var kv in d)
                            totals[kv.Key] = totals.TryGetValue(kv.Key, out var t0) ? t0 + kv.Value : kv.Value;
                    Say($"makes: {string.Join(", ", totals.Select(z => $"{z.Key}={z.Value}"))}"
                        + $"  placed={placed} outside-any-array={orphan}"
                        + $"  unresolved={list.Count(a => a.Make == null)}");
                }

                // 4. Key plan: shrink the whole site to fill the window.
                //
                //    No standard scale, no clamp. The array sheets are what gets
                //    read and measured, so they hold to a legible architectural
                //    scale; the key plan is only an overview of the installation
                //    and nobody scales off it. Snapping it to ArchScales worked
                //    only while a site happened to fit one — San Diego needs
                //    1:1522 and the table stops at 1:384, so it fell back to a
                //    scale four times too coarse and the window came out 120x50in
                //    on a 36x24 sheet, showing one corner of the site.
                double sx0 = list.Min(a => a.X0), sy0 = list.Min(a => a.Y0);
                double sx1 = list.Max(a => a.X1), sy1 = list.Max(a => a.Y1);

                // 4a. KEY PLAN SOURCE. Some client drawings carry the site
                //     TWICE: the analysis geometry on "Roof N Arr M" layers,
                //     exploded roof by roof so each array can own a sheet, and
                //     the vendor's own site layout, which is already one
                //     coherent plan. San Diego is exactly this - Sollega's copy
                //     on a_* sits compactly at x 6,351..8,255 / y 1,571..1,812
                //     (1,904 x 241ft) while the analysis copy is spread over
                //     3,859 x 1,588ft with a 1,447ft hole in the middle.
                //
                //     Framing the vendor copy IS what the engineer's stamped key
                //     plan does - his viewport (centre 7096,1376, viewH 1458ft,
                //     1:768) lands squarely on it and holds 4,443 of its module
                //     blocks. Nothing is compacted and nothing is moved; the
                //     coherent plan was in the drawing all along.
                //
                //     Largest cluster wins, because the layer also carries
                //     strays elsewhere in model space.
                bool keyFromSource = false;
                if (!string.IsNullOrWhiteSpace(keySourceLayers))
                {
                    var srcBoxes = new List<Box>();
                    var msk = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in msk)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
                        if (!WildMatch(e.Layer, keySourceLayers)) continue;
                        Extents3d ex3;
                        try { ex3 = e.GeometricExtents; } catch { continue; }
                        srcBoxes.Add(new Box
                        {
                            X0 = ex3.MinPoint.X, Y0 = ex3.MinPoint.Y,
                            X1 = ex3.MaxPoint.X, Y1 = ex3.MaxPoint.Y
                        });
                    }
                    if (srcBoxes.Count == 0)
                        Say($"key plan source '{keySourceLayers}': nothing matched, using arrays");
                    else
                    {
                        var sg = Cluster(srcBoxes, keyGroupGapFt * 12.0);
                        var win = sg.OrderByDescending(z => z.Count).First();
                        sx0 = win.Min(i => srcBoxes[i].X0);
                        sy0 = win.Min(i => srcBoxes[i].Y0);
                        sx1 = win.Max(i => srcBoxes[i].X1);
                        sy1 = win.Max(i => srcBoxes[i].Y1);
                        keyFromSource = true;
                        Say($"key plan source '{keySourceLayers}': {sg.Count} cluster(s), "
                            + $"largest {win.Count}/{srcBoxes.Count} entities, "
                            + $"{(sx1 - sx0) / 12:0}x{(sy1 - sy0) / 12:0}ft");
                    }
                }
                // Fit against the window LESS the pad, or the padded slot no
                // longer fits the window it was sized to.
                const double KEYMARGIN = 0.03;
                double keyRatio = System.Math.Max(
                    (sx1 - sx0) / System.Math.Max(0.1, vpW - 2 * padIn),
                    (sy1 - sy0) / System.Math.Max(0.1, vpH - 2 * padIn))
                    * (1.0 + KEYMARGIN);
                Say($"site {(sx1 - sx0) / 12:0}x{(sy1 - sy0) / 12:0}ft -> "
                    + $"key plan fits at 1:{keyRatio:0}");

                // 4b. Compacted key plan.
                //
                //     One window over the whole site spends the page on ground
                //     rather than on arrays. San Diego's extents are
                //     3,859x1,588ft but carry a single 1,447ft empty band, so
                //     every module lands on 0.03in of paper and the arrays plot
                //     as solid black. Grouping the arrays by proximity and
                //     giving each group its OWN window squeezes that ground out
                //     WITHOUT moving any geometry: each window still shows true
                //     model space, and they all share one scale, so the sheet
                //     stays measurable. RelativePack keeps the groups in their
                //     real arrangement - rows north to south, west to east
                //     inside a row - so the plan still reads as the site.
                //
                //     This is what the engineer did by hand on the stamped San
                //     Diego set, at 1:768 against our 1:1577.
                const double KEYGAPIN = 0.20;      // paper gap between groups
                var keyBoxes = new List<ArrayInfo>();
                // Compaction is a remedy for a spread-out ANALYSIS layout. When
                // the vendor's coherent plan is available there is nothing to
                // remedy, so the two are mutually exclusive.
                if (keyCompact && keyFromSource)
                    Say("key plan: source layers supplied, skipping compaction");
                if (keyCompact && !keyFromSource && list.Count > 1)
                {
                    var groups = Cluster(list.Select(a => new Box
                    {
                        X0 = a.X0, Y0 = a.Y0, X1 = a.X1, Y1 = a.Y1
                    }).ToList(), keyGroupGapFt * 12.0);

                    foreach (var g in groups)
                    {
                        var kb = new ArrayInfo { Name = $"key{keyBoxes.Count + 1}" };
                        foreach (int i in g)
                        {
                            var a = list[i];
                            if (a.X0 < kb.X0) kb.X0 = a.X0;
                            if (a.Y0 < kb.Y0) kb.Y0 = a.Y0;
                            if (a.X1 > kb.X1) kb.X1 = a.X1;
                            if (a.Y1 > kb.Y1) kb.Y1 = a.Y1;
                        }
                        kb.Ents = g.Count;
                        keyBoxes.Add(kb);
                    }

                    // Does the whole set of groups pack at this scale? Sized the
                    // same way PlaceViewports will size them, or the plan would
                    // promise a fit the sheet cannot honour.
                    // Pack against the SAME usable height the placer will use, or
                    // the plan promises a scale the sheet cannot honour and
                    // PlaceViewports drops to its stacking fallback.
                    // The band ABOVE the groups is bought with scale; the band
                    // BELOW is free, because keyBottomInsetIn is already being
                    // surrendered to clear the legend.
                    double keyUsableH = vpH - keyBottomInsetIn
                                      - (keyLabels ? keyLabelBandIn : 0.0);
                    bool Packs(double r) => RelativePack(
                        keyBoxes.Select(z => ((z.X0 + z.X1) / 2.0, (z.Y0 + z.Y1) / 2.0,
                                              z.W / r + 2 * padIn,
                                              z.H / r + 2 * padIn)).ToList(),
                        vpW, keyUsableH, KEYGAPIN) != null;

                    if (keyBoxes.Count < 2)
                    {
                        Say("key plan: one group, nothing to compact");
                        keyBoxes.Clear();
                    }
                    else
                    {
                        // The uncompacted ratio fits the site, so it very nearly
                        // always packs; grow it rather than assume.
                        double hi = keyRatio, lo = 1.0;
                        for (int k = 0; k < 8 && !Packs(hi); k++) hi *= 1.5;
                        if (!Packs(hi))
                        {
                            Say("key plan: groups would not pack, using whole-site window");
                            keyBoxes.Clear();
                        }
                        else
                        {
                            for (int k = 0; k < 50; k++)
                            {
                                double mid = (lo + hi) / 2.0;
                                if (Packs(mid)) hi = mid; else lo = mid;
                            }
                            double packed = hi * (1.0 + KEYMARGIN);
                            Say($"key plan compacted: {keyBoxes.Count} groups "
                                + string.Join(" ", keyBoxes.Select(
                                    z => $"[{z.W / 12:0}x{z.H / 12:0}ft]"))
                                + $" -> 1:{packed:0} (whole-site was 1:{keyRatio:0})");
                            keyRatio = packed;
                        }
                    }
                }

                // 5. One scale for the set, fixed by the largest array â€” it
                //    gets a page to itself and everything else works to it.
                //    Model units per paper inch; bigger number = zoomed out.
                const double MARGIN = 0.05;
                double fitRatio = list.Max(
                    a => System.Math.Max(a.W / vpW, a.H / vpH)) * (1.0 + MARGIN);
                var biggest = list.OrderByDescending(a => a.W * a.H).First();

                // Never draw coarser than the legibility scale. fitRatio zooms
                // out until the LARGEST array fits one page, but covW/covH â€” what
                // decides that an array needs tiling at all â€” are computed from
                // `scale`. Letting fitRatio win meant the page budget was set at
                // one scale and the drawing made at another: on San Diego one
                // 304ft array pushed the set to 1:126 while tiling was still
                // being judged at 1:64, so every sheet drew at half the size it
                // was planned for and an array filled 18% of its window.
                // Capping re-couples the two. It binds only when one array is far
                // larger than the rest â€” McHenry's fitRatio is 1:62 against a 1:64
                // floor, so that job is unaffected.
                double baseRatio = System.Math.Min(fitRatio, scale.Ratio);
                Say($"largest array {biggest.Name} "
                    + $"{biggest.W / 12:0}x{biggest.H / 12:0}ft needs 1:{fitRatio:0}");
                if (baseRatio < fitRatio)
                    Say($"  capped to the legibility scale 1:{baseRatio:0} "
                        + $"({biggest.Name} exceeds one page and is tiled)");
                Say($"set scale 1:{baseRatio:0}");

                // Pack: at most two arrays, stacked, gap between.
                double gap = gapFt * 12.0;
                var sheets = new List<List<ArrayInfo>>();
                var cur = new List<ArrayInfo>();
                int tiled = 0;
                foreach (var a in list)
                {
                    // An array too big to stay legible on one page genuinely has
                    // to run across several, the way the engineer would split it
                    // by hand. Measure against the coverage LEFT once the window
                    // pad is taken off, or a tile sized to the raw coverage no
                    // longer fits once padding is added and the packer bails.
                    double effW = (vpW - 2 * padIn) * scale.Ratio;
                    double effH = (vpH - 2 * padIn) * scale.Ratio;
                    if (a.W > effW || a.H > effH)
                    {
                        if (cur.Count > 0) { sheets.Add(cur); cur = new(); }
                        int nx = System.Math.Max(1, (int)System.Math.Ceiling(a.W / effW));
                        int ny = System.Math.Max(1, (int)System.Math.Ceiling(a.H / effH));
                        // One ArrayInfo PER TILE, each carrying its own slice of
                        // the extents. Pushing the same instance nx*ny times aimed
                        // every tile at the whole array's centre, so the pages came
                        // out identical instead of adjacent.
                        double tw = a.W / nx, th = a.H / ny;
                        for (int ry = 0; ry < ny; ry++)      // north to south
                            for (int cx = 0; cx < nx; cx++)  // west to east
                            {
                                sheets.Add(new List<ArrayInfo>
                                {
                                    new ArrayInfo
                                    {
                                        Name = a.Name,
                                        Key = a.Key,
                                        Ents = a.Ents,
                                        Tile = ry * nx + cx + 1,
                                        TileCount = nx * ny,
                                        X0 = a.X0 + cx * tw,
                                        X1 = a.X0 + (cx + 1) * tw,
                                        Y0 = a.Y1 - (ry + 1) * th,
                                        Y1 = a.Y1 - ry * th,
                                    }
                                });
                            }
                        Say($"  TILED {a.Name} {a.W / 12:0}x{a.H / 12:0}ft "
                            + $"-> {nx}x{ny} = {nx * ny} sheets");
                        tiled++;
                        continue;
                    }
                    // Only pair arrays that are actually neighbours. Two arrays
                    // that fit on a page but sit at opposite ends of the roof
                    // do not belong on the same sheet.
                    bool near = cur.Count == 0 ||
                                (System.Math.Abs(a.X0 - cur[0].X0) <= covW &&
                                 System.Math.Abs(a.Y1 - cur[0].Y1) <= covH);
                    // Does it still fit once everything is shelf-packed?
                    var trial = new List<ArrayInfo>(cur) { a };
                    // Pad here too, or a group proved to fit unpadded gets to the
                    // sheet and no longer does.
                    var packed = RelativePack(
                        trial.Select(z => ((z.X0 + z.X1) / 2, (z.Y0 + z.Y1) / 2,
                                           z.W / baseRatio + 2 * padIn,
                                           z.H / baseRatio + 2 * padIn)).ToList(),
                        vpW, vpH, gap / baseRatio);
                    if (near && packed != null && trial.Count <= maxPerSheet)
                    { cur = trial; }
                    else
                    { if (cur.Count > 0) sheets.Add(cur); cur = new List<ArrayInfo> { a }; }
                }
                if (cur.Count > 0) sheets.Add(cur);
                if (tiled > 0) Say($"  {tiled} array(s) exceeded one sheet and were tiled");

                tr.Commit();
                return new SheetSet
                {
                    ScaleName = scale.Name,
                    Ratio = scale.Ratio,
                    KeyName = $"1:{keyRatio:0}",
                    KeyRatio = keyRatio,
                    Template = template,
                    VpW = vpW,
                    VpH = vpH,
                    GapPaper = gap / baseRatio,
                    BaseRatio = baseRatio,
                    PadPaper = padIn,
                    SiteCx = (sx0 + sx1) / 2.0,
                    SiteCy = (sy0 + sy1) / 2.0,
                    SiteW = sx1 - sx0,
                    SiteH = sy1 - sy0,
                    KeyBoxes = keyBoxes,
                    KeyGapPaper = KEYGAPIN,
                    KeyBottomInset = keyBottomInsetIn,
                    KeyArrays = list,
                    KeyLabelBand = keyLabels ? keyLabelBandIn : 0.0,
                    Sheets = sheets,
                };
            }
            catch (System.Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Generate the sheet set: a key plan plus one sheet per array group,
        /// each array getting its own viewport sized to it at the uniform scale.
        ///
        /// A two-array sheet gets two viewports, stacked with the gap and
        /// centred in the template's plan window â€” which is why the template's
        /// own plan viewport is resized rather than merely re-aimed.
        /// </summary>
        [CommandMethod("PZSEMAKE")]
        public static void PzseMake()
        {
            try
            {
                var plan = PlanSheets(out string err);
                if (plan == null) { Say("MAKE-FAILED " + err); return; }
                Say($"scale {plan.ScaleName} (1:{plan.Ratio:0}), "
                    + $"key plan {plan.KeyName}, "
                    + $"{plan.Sheets.Count} array sheets");

                // Chunking window, so one run fits the processing limit. The plan
                // is deterministic, so sheet N is the same sheet in every chunk
                // and the pieces compose.
                int startAt = 0, count = int.MaxValue;
                // Breathing room around each array inside its window.
                double margin = 0.05;
                // Put the array identity in the tab name ("S1.7 R1-A04a") so the
                // set can be navigated without opening tabs to find one. Default
                // off: it renames every tab, and the IronRidge job shares this
                // bundle and its existing S1.N names.
                bool nameArrays = false;
                string pf = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
                if (File.Exists(pf))
                {
                    using var pd = JsonDocument.Parse(File.ReadAllText(pf));
                    if (pd.RootElement.TryGetProperty("startAt", out var s0))
                        startAt = s0.GetInt32();
                    if (pd.RootElement.TryGetProperty("count", out var c0))
                        count = c0.GetInt32();
                    if (pd.RootElement.TryGetProperty("marginPct", out var mg))
                        margin = mg.GetDouble();
                    if (pd.RootElement.TryGetProperty("sheetNameArrays", out var na))
                        nameArrays = na.GetBoolean();
                }
                int stopBefore = count == int.MaxValue
                    ? int.MaxValue
                    : startAt + count;
                Say($"chunk: sheets [{startAt}, "
                    + (stopBefore == int.MaxValue ? "end" : stopBefore.ToString()) + ")");

                var db = HostApplicationServices.WorkingDatabase;
                var lm = LayoutManager.Current;
                int made = 0, failed = 0, skipped = 0;

                // Activating each layout forces a regen, and this drawing carries
                // ~500k entities of traced PDF underlay â€” 73% of it, and nothing
                // our sheets show. Freeze those layers for the duration and the
                // regen has almost nothing to chew on. Restored before saving.
                var thawAfter = new List<ObjectId>();
                using (var ltr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)ltr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId id in lt)
                    {
                        var l = (LayerTableRecord)ltr.GetObject(id, OpenMode.ForRead);
                        if (!l.Name.StartsWith("PDF", StringComparison.OrdinalIgnoreCase)
                            || l.IsFrozen) continue;
                        var w = (LayerTableRecord)ltr.GetObject(id, OpenMode.ForWrite);
                        w.IsFrozen = true;
                        thawAfter.Add(id);
                    }
                    ltr.Commit();
                }
                Say($"froze {thawAfter.Count} underlay layer(s) for the run");

                // Key plan is index 0 in the chunk numbering.
                if (startAt <= 0 && stopBefore > 0)
                try
                {
                    lm.CloneLayout(plan.Template, "S1", 1);
                    // Name null throughout: a key-plan window isolates nothing,
                    // it shows whatever of the site falls inside it.
                    var keyVps = new List<(double, double, double, double, string, string)>();
                    if (plan.KeyBoxes.Count > 1)
                        foreach (var kb in plan.KeyBoxes)
                            keyVps.Add(((kb.X0 + kb.X1) / 2.0, (kb.Y0 + kb.Y1) / 2.0,
                                        kb.W / plan.KeyRatio, kb.H / plan.KeyRatio,
                                        null, null));
                    else
                        keyVps.Add((plan.SiteCx, plan.SiteCy,
                                    plan.SiteW / plan.KeyRatio,
                                    plan.SiteH / plan.KeyRatio, null, null));
                    PlaceViewports(db, lm, "S1", keyVps,
                        plan.KeyRatio, plan.VpW, plan.VpH,
                        plan.KeyBoxes.Count > 1 ? plan.KeyGapPaper : plan.GapPaper,
                        margin, plan.KeyRatio, plan.PadPaper,
                        plan.KeyBoxes.Count > 1 ? plan.KeyBottomInset : 0.0);
                    made++;
                    Say($"  S1  key plan, site {plan.SiteW / 12:0}x{plan.SiteH / 12:0}ft"
                        + (plan.KeyBoxes.Count > 1
                           ? $", {plan.KeyBoxes.Count} compacted windows" : ""));
                }
                catch (System.Exception ex)
                {
                    failed++;
                    Say($"  FAIL S1: {ex.GetType().Name}: {ex.Message}");
                }

                for (int i = 0; i < plan.Sheets.Count; i++)
                {
                    int idx = i + 1;                     // key plan occupies 0
                    if (idx < startAt || idx >= stopBefore) { skipped++; continue; }
                    var group = plan.Sheets[i];
                    string name = $"S1.{i + 1}";
                    // Underscore, never a space. A .scr feeds prompts line by
                    // line and a space reads as Enter, so a layout name with one
                    // splits across two prompts and breaks -PLOT; ssget's 410
                    // filter mishandles them too.
                    if (nameArrays && group.Count > 0)
                        name += "_" + string.Join("+", group.Select(Token));
                    try
                    {
                        lm.CloneLayout(plan.Template, name, i + 2);
                        // Each array: model centre, and the paper size it needs
                        // at the uniform scale.
                        var boxes = group.Select(a => (
                            (a.X0 + a.X1) / 2.0,
                            (a.Y0 + a.Y1) / 2.0,
                            a.W / plan.Ratio,
                            a.H / plan.Ratio,
                            // Display, not Name: the label under a tiled window
                            // has to say which tile it is.
                            a.Display,
                            a.Key ?? a.Name)).ToList();
                        PlaceViewports(db, lm, name, boxes, plan.Ratio,
                                       plan.VpW, plan.VpH, plan.GapPaper, margin,
                                       plan.BaseRatio, plan.PadPaper);
                        made++;
                        if (made <= 6 || made % 6 == 0)
                            Say($"  {name}  " + string.Join(" + ",
                                group.Select(a => a.Name)));
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        if (failed <= 5)
                            Say($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                // Put the underlay back before anything is written out.
                if (thawAfter.Count > 0)
                {
                    using var ltr = db.TransactionManager.StartTransaction();
                    foreach (var id in thawAfter)
                        ((LayerTableRecord)ltr.GetObject(id, OpenMode.ForWrite))
                            .IsFrozen = false;
                    ltr.Commit();
                    Say($"thawed {thawAfter.Count} underlay layer(s)");
                }
                Say($"MAKE-DONE made={made} failed={failed} skipped={skipped}");
            }
            catch (System.Exception ex)
            {
                Say("MAKE-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Report what a new engineer drawing actually contains, and which key
        /// plan config it implies. Read-only: it changes nothing.
        ///
        /// Every client drawing has arrived with its own shape, and the cost of
        /// not knowing has been hours each time:
        ///   McHenry    - analysis geometry, coherent, nothing special.
        ///   San Diego  - the site drawn TWICE. Analysis copy exploded to
        ///                3,859x1,588ft with a 1,447ft hole; the vendor's own
        ///                copy a compact band off at x 6,351..8,255. Wants
        ///                keySourceLayers.
        ///   Joliet     - one copy, an 8:1 band 3,183x398ft, vendor layers ON
        ///                TOP of it rather than elsewhere. Whole-site key plan
        ///                is 1:1301; packing the clusters gets 1:416. Wants
        ///                keyCompact.
        /// Those three needed three different answers, and the drawing knew
        /// which one all along.
        ///
        /// Reports, never decides: it prints the measurements next to the config
        /// they imply, and a human sets params.json.
        /// </summary>
        [CommandMethod("PZSEPROFILE")]
        public static void PzseProfile()
        {
            try
            {
                string countPattern = "*Block Count*";
                string vendorPattern = "a_*,b_*,c_*";
                double groupGapFt = 150.0;
                string pfile = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
                if (File.Exists(pfile))
                {
                    using var pdoc = JsonDocument.Parse(File.ReadAllText(pfile));
                    var r0 = pdoc.RootElement;
                    if (r0.TryGetProperty("countLayers", out var c0)) countPattern = c0.GetString();
                    if (r0.TryGetProperty("fastFreezeLayers", out var f0)) vendorPattern = f0.GetString();
                    if (r0.TryGetProperty("keyGroupGapFt", out var g0)) groupGapFt = g0.GetDouble();
                }

                var db = HostApplicationServices.WorkingDatabase;
                using var tr = db.TransactionManager.StartTransaction();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var analysis = new List<Box>();
                var vendor = new List<Box>();
                var vendorBlocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var arrLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ObjectId id in ms)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
                    bool isArr = WildMatch(e.Layer, countPattern);
                    bool isVen = WildMatch(e.Layer, vendorPattern);
                    if (!isArr && !isVen) continue;
                    Extents3d ex;
                    try { ex = e.GeometricExtents; } catch { continue; }
                    var b = new Box
                    {
                        X0 = ex.MinPoint.X, Y0 = ex.MinPoint.Y,
                        X1 = ex.MaxPoint.X, Y1 = ex.MaxPoint.Y
                    };
                    if (isArr) { analysis.Add(b); arrLayers.Add(e.Layer); }
                    else
                    {
                        vendor.Add(b);
                        // Histogram, NOT a first-sample: a first sample returns
                        // whatever anonymous block happens to come first
                        // (*U48/*U54) and hides the real names. San Diego's make
                        // is H/T on exactly these inserts.
                        if (e is BlockReference br)
                            vendorBlocks[br.Name] = vendorBlocks.TryGetValue(br.Name, out var n0) ? n0 + 1 : 1;
                    }
                }

                Say($"PROFILE arrays: {arrLayers.Count} layer(s), {analysis.Count} entities");
                if (analysis.Count == 0) { Say("PROFILE-DONE no analysis geometry"); return; }
                ReportSpread("analysis", analysis, groupGapFt);

                if (vendor.Count > 0)
                {
                    Say($"PROFILE vendor '{vendorPattern}': {vendor.Count} entities");
                    ReportSpread("vendor  ", vendor, groupGapFt);
                    var top = vendorBlocks.OrderByDescending(z => z.Value).Take(6);
                    Say("  block names: " + string.Join(", ", top.Select(z => $"{z.Key}={z.Value}")));
                }
                else Say("PROFILE vendor: none");

                tr.Commit();
                Say("PROFILE-DONE");
            }
            catch (System.Exception ex)
            {
                Say("PROFILE-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Extents, dominant cluster and packed-vs-whole scale for one body of
        /// geometry. The packed figure is what compaction would buy.
        /// </summary>
        private static void ReportSpread(string label, List<Box> boxes, double gapFt)
        {
            double x0 = boxes.Min(b => b.X0), y0 = boxes.Min(b => b.Y0);
            double x1 = boxes.Max(b => b.X1), y1 = boxes.Max(b => b.Y1);
            double w = x1 - x0, h = y1 - y0;
            Say($"  {label} extents {w / 12:0}x{h / 12:0}ft  aspect {(h > 0 ? w / h : 0):0.00}:1");

            var groups = Cluster(boxes, gapFt * 12.0);
            var big = groups.OrderByDescending(g => g.Count).First();
            double bx0 = big.Min(i => boxes[i].X0), by0 = big.Min(i => boxes[i].Y0);
            double bx1 = big.Max(i => boxes[i].X1), by1 = big.Max(i => boxes[i].Y1);
            Say($"  {label} {groups.Count} cluster(s) at {gapFt:0}ft; largest holds "
                + $"{100.0 * big.Count / boxes.Count:0}% of entities and is "
                + $"{(bx1 - bx0) / 12:0}x{(by1 - by0) / 12:0}ft");

            // Report BOTH windows, because they answer different questions and
            // quoting only the first is misleading: keySourceLayers frames the
            // dominant CLUSTER, not the whole extent, and on San Diego those two
            // differ by nearly 3x. 30.24x22.58in is the usable plan window less
            // its pad on every template so far.
            const double PW = 30.24, PH = 22.58;
            double whole = System.Math.Max(w / PW, h / PH) * 1.03;
            double clust = System.Math.Max((bx1 - bx0) / PW, (by1 - by0) / PH) * 1.03;
            Say($"  {label} whole extent -> 1:{whole:0} (module {44.65 / whole:0.000}in)"
                + $"   largest cluster -> 1:{clust:0} (module {44.65 / clust:0.000}in)");
        }

        /// <summary>Layer carrying the viewport borders, created non-plotting.</summary>
        private const string VpLayer = "PZSE-VPORT";

        /// <summary>Layer carrying the key-plan callouts. Plots, unlike VpLayer.</summary>
        private const string LabLayer = "PZSE-KEYLAB";

        // The engineer's own callout, measured off his stamped sheet: a bordered
        // box 2.23 x 0.67in holding two centred lines - manufacturer, then
        // "ROOF n ARRAY m" - with a plain leader dropped from the box edge to
        // the array. No arrowhead.
        private const double LabW = 2.23, LabH = 0.67, LabTxt = 0.15, LabPad = 0.10;

        /// <summary>Text height of the small per-array sheet tag.</summary>
        private const double TagTxt = 0.13;

        /// <summary>
        /// Draw the per-array callouts on the key plan.
        ///
        /// Separate from PZSEMAKE on purpose: it only reads the finished S1 tab,
        /// so it can be re-run on a built drawing while the label layout is
        /// being tuned, instead of regenerating all 30 sheets to move a box.
        ///
        /// Labels go in the band ABOVE the packed groups and the band BELOW
        /// them, whichever is nearer the array, so leaders stay short and mostly
        /// do not cross. Within a band they are ordered by paper X for the same
        /// reason. Nothing is drawn with a solid fill: the sheets plot through
        /// monochrome.ctb, which would turn a filled box solid black and swallow
        /// the text. The engineer's green/purple fills come from a colour plot.
        /// </summary>
        [CommandMethod("PZSEKEYLAB")]
        public static void PzseKeyLab()
        {
            try
            {
                var plan = PlanSheets(out string err);
                if (plan == null) { Say("KEYLAB-FAILED " + err); return; }
                if (plan.KeyLabelBand <= 0 && plan.KeyArrays.Count == 0)
                { Say("KEYLAB: keyLabels off"); return; }

                var db = HostApplicationServices.WorkingDatabase;
                var lm = LayoutManager.Current;
                ObjectId layId;
                try { layId = lm.GetLayoutId("S1"); }
                catch { Say("KEYLAB-FAILED no S1 tab"); return; }

                using var tr = db.TransactionManager.StartTransaction();
                var lay = (Layout)tr.GetObject(layId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(
                    lay.BlockTableRecordId, OpenMode.ForWrite);

                // 1. The group windows, read back rather than recomputed - this
                //    is the placement PZSEMAKE actually produced.
                var wins = new List<(double Px, double Py, double Pw, double Ph,
                                     double Cx, double Cy, double Scale)>();
                var details = new List<(double X0, double Y0, double X1, double Y1)>();
                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Viewport v) continue;
                    if (v.Number == 1 || v.Height <= 0) continue;
                    if (string.Equals(v.Layer, VpLayer, StringComparison.OrdinalIgnoreCase))
                        wins.Add((v.CenterPoint.X, v.CenterPoint.Y, v.Width, v.Height,
                                  v.ViewCenter.X, v.ViewCenter.Y, v.ViewHeight / v.Height));
                    // Skip anything bigger than the sheet. The template carries a
                    // 47.6x27.0in paper-space viewport that covers the whole page;
                    // treating it as an obstacle made EVERY tag collide and the
                    // nudge walked them all off the top edge. PlaceViewports
                    // guards the same entity for the same reason.
                    else if (v.Width <= 36.0 && v.Height <= 24.0)
                        details.Add((v.CenterPoint.X - v.Width / 2, v.CenterPoint.Y - v.Height / 2,
                                     v.CenterPoint.X + v.Width / 2, v.CenterPoint.Y + v.Height / 2));
                }
                if (wins.Count == 0) { Say("KEYLAB-FAILED no plan windows on S1"); return; }

                double blockL = wins.Min(w => w.Px - w.Pw / 2);
                double blockR = wins.Max(w => w.Px + w.Pw / 2);
                double blockB = wins.Min(w => w.Py - w.Ph / 2);
                double blockT = wins.Max(w => w.Py + w.Ph / 2);
                double midY = (blockB + blockT) / 2.0;

                // 2. Where each array lands on paper, through its own window.
                //    Top edge, not centre: the tag sits just above the array.
                var pts = new List<(ArrayInfo A, double X, double Top)>();
                int lost = 0;
                foreach (var a in plan.KeyArrays)
                {
                    double mx = (a.X0 + a.X1) / 2.0, my = (a.Y0 + a.Y1) / 2.0;
                    var w = wins.FirstOrDefault(z =>
                        mx >= z.Cx - z.Pw * z.Scale / 2 && mx <= z.Cx + z.Pw * z.Scale / 2 &&
                        my >= z.Cy - z.Ph * z.Scale / 2 && my <= z.Cy + z.Ph * z.Scale / 2);
                    if (w.Scale <= 0) { lost++; continue; }
                    pts.Add((a, w.Px + (mx - w.Cx) / w.Scale,
                                w.Py + (a.Y1 - w.Cy) / w.Scale));
                }
                if (lost > 0) Say($"  {lost} array(s) fell outside every window, not labelled");

                var labLayer = PlotLayer(db, tr, LabLayer);

                // Which sheet shows each array. This is the whole point of the
                // callout: the key plan's job is to say which page to turn to.
                // It is also the one identity we can state with certainty - we
                // assigned it - unlike the manufacturer, which lives only in the
                // vendor copy of the site.
                // Keyed by NAME, not by object: an array too big for one page is
                // split into one record PER TILE, so the tiles are not the same
                // instances as the arrays in KeyArrays and an identity lookup
                // silently missed them - they fell back to the raw token.
                // A tiled array spans several sheets, so carry the range.
                var sheetsOf = new Dictionary<string, List<int>>();
                for (int s = 0; s < plan.Sheets.Count; s++)
                    foreach (var a in plan.Sheets[s])
                    {
                        string k = a.Name ?? a.Key ?? "";
                        if (!sheetsOf.TryGetValue(k, out var l)) sheetsOf[k] = l = new List<int>();
                        if (!l.Contains(s + 1)) l.Add(s + 1);
                    }
                string SheetLabel(ArrayInfo a)
                {
                    if (!sheetsOf.TryGetValue(a.Name ?? a.Key ?? "", out var l) || l.Count == 0)
                        return null;
                    l.Sort();
                    return l.Count == 1 ? $"S1.{l[0]}" : $"S1.{l[0]}-{l[l.Count - 1]}";
                }

                // 3. A small tag directly above each array, no leader.
                //
                //    The engineer's big boxed callouts with leaders work because
                //    HIS arrays are a thin band with clear page above and below.
                //    Ours is a packed block filling the sheet, so the same
                //    treatment stacked labels into a column and fanned 33
                //    leaders across the drawing. At 1:601 the arrays are large
                //    enough to be labelled where they stand, which needs no
                //    leader, no reserved band, and costs no scale.
                //
                //    Nudge upward on collision, tallest-first, so a tag never
                //    lands on one already placed or on the legend.
                var taken = new List<(double X0, double Y0, double X1, double Y1)>(details);
                int drawn = 0, unmapped = 0;
                foreach (var it in pts.OrderByDescending(p => p.Top))
                {
                    string sn = SheetLabel(it.A);
                    if (sn == null) unmapped++;
                    string text = sn ?? Token(it.A);
                    double bw = System.Math.Max(0.46, text.Length * TagTxt * 0.72 + 2 * 0.05);
                    double bh = TagTxt + 2 * 0.045;
                    double px = it.X - bw / 2, py0 = it.Top + 0.05, py = py0;
                    // Never wander far from the array it names, and never off the
                    // sheet: past the limit, sit on the array rather than vanish.
                    for (int guard = 0; guard < 24; guard++)
                    {
                        bool hit = false;
                        foreach (var r in taken)
                            if (px < r.X1 && px + bw > r.X0 && py < r.Y1 && py + bh > r.Y0)
                            { hit = true; break; }
                        if (!hit) break;
                        py += bh + 0.03;
                        if (py > py0 + 1.6 || py + bh > 23.5) { py = py0; break; }
                    }
                    taken.Add((px, py, px + bw, py + bh));
                    DrawTag(btr, tr, labLayer, px, py, bw, bh, text);
                    drawn++;
                }
                Say($"  tagged {drawn} array(s)"
                    + (unmapped > 0 ? $", {unmapped} with NO sheet number" : ""));

                tr.Commit();
                Say($"KEYLAB-DONE {pts.Count} callout(s), "
                    + $"{pts.Count(p => p.A.Make != null)} with a make");
            }
            catch (System.Exception ex)
            {
                Say("KEYLAB-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>A plotting layer, unlike <see cref="NoPlotLayer"/>.</summary>
        private static ObjectId PlotLayer(Database db, Transaction tr, string name)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            var rec = new LayerTableRecord { Name = name, IsPlottable = true };
            var id = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }

        /// <summary>
        /// A small boxed sheet tag in paper space. Outline and text only - these
        /// plot through monochrome.ctb, where a solid fill would go black and
        /// take the text with it.
        /// </summary>
        private static void DrawTag(
            BlockTableRecord btr, Transaction tr, ObjectId layer,
            double x, double y, double w, double h, string text)
        {
            var box = new Polyline();
            box.AddVertexAt(0, new Autodesk.AutoCAD.Geometry.Point2d(x, y), 0, 0, 0);
            box.AddVertexAt(1, new Autodesk.AutoCAD.Geometry.Point2d(x + w, y), 0, 0, 0);
            box.AddVertexAt(2, new Autodesk.AutoCAD.Geometry.Point2d(x + w, y + h), 0, 0, 0);
            box.AddVertexAt(3, new Autodesk.AutoCAD.Geometry.Point2d(x, y + h), 0, 0, 0);
            box.Closed = true;
            box.LayerId = layer;
            btr.AppendEntity(box);
            tr.AddNewlyCreatedDBObject(box, true);

            var txt = new MText
            {
                Contents = text,
                TextHeight = TagTxt,
                Attachment = AttachmentPoint.MiddleCenter,
                Location = new Autodesk.AutoCAD.Geometry.Point3d(x + w / 2, y + h / 2, 0),
                Width = 0,
                LayerId = layer,
            };
            btr.AppendEntity(txt);
            tr.AddNewlyCreatedDBObject(txt, true);
        }

        /// <summary>One boxed callout plus its leader, in paper space.</summary>
        private static void DrawLabel(
            BlockTableRecord btr, Transaction tr, ObjectId layer,
            double x, double y, ArrayInfo a, double tx, double ty, bool up,
            string sheetNo)
        {
            var box = new Polyline();
            box.AddVertexAt(0, new Autodesk.AutoCAD.Geometry.Point2d(x, y), 0, 0, 0);
            box.AddVertexAt(1, new Autodesk.AutoCAD.Geometry.Point2d(x + LabW, y), 0, 0, 0);
            box.AddVertexAt(2, new Autodesk.AutoCAD.Geometry.Point2d(x + LabW, y + LabH), 0, 0, 0);
            box.AddVertexAt(3, new Autodesk.AutoCAD.Geometry.Point2d(x, y + LabH), 0, 0, 0);
            box.Closed = true;
            box.LayerId = layer;
            btr.AppendEntity(box);
            tr.AddNewlyCreatedDBObject(box, true);

            var m = ArrKey.Match(a.Key ?? a.Name ?? "");
            string ident = m.Success
                ? $"ROOF {int.Parse(m.Groups[1].Value)} ARRAY {int.Parse(m.Groups[2].Value)}"
                  + (a.Name != null && a.Key != null && a.Name.Length > a.Key.Length
                     ? a.Name.Substring(a.Key.Length).ToUpperInvariant() : "")
                : (a.Name ?? "").ToUpperInvariant();

            // Top line is the SHEET, because that is what the reader wants from
            // a key plan. The engineer puts the manufacturer there; we cannot,
            // without a copy-A-to-copy-B mapping nobody has.
            string head = sheetNo ?? a.Make;
            var txt = new MText
            {
                Contents = head != null ? head + "\\P" + ident : ident,
                TextHeight = LabTxt,
                Attachment = AttachmentPoint.MiddleCenter,
                Location = new Autodesk.AutoCAD.Geometry.Point3d(x + LabW / 2, y + LabH / 2, 0),
                Width = LabW - 2 * LabPad,
                LayerId = layer,
            };
            btr.AppendEntity(txt);
            tr.AddNewlyCreatedDBObject(txt, true);

            // Leader from the box edge facing the array. No arrowhead - his has
            // none, and at this scale one would cover the array it points at.
            var from = new Autodesk.AutoCAD.Geometry.Point3d(x + LabW / 2, up ? y : y + LabH, 0);
            var line = new Line(from, new Autodesk.AutoCAD.Geometry.Point3d(tx, ty, 0)) { LayerId = layer };
            btr.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }

        /// <summary>
        /// Ensure the non-plotting viewport layer exists. Idempotent, so it is
        /// safe to call once per layout.
        /// </summary>
        private static ObjectId NoPlotLayer(Database db, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(VpLayer)) return lt[VpLayer];
            lt.UpgradeOpen();
            var rec = new LayerTableRecord { Name = VpLayer, IsPlottable = false };
            var id = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }

        /// <summary>
        /// Fit viewports onto one layout. Each entry is (model cx, model cy,
        /// paper width, paper height); they are stacked vertically, centred in
        /// the template's plan window. Extra sheet-sized viewports inherited
        /// from the template are switched off so they cannot draw a stale view.
        /// </summary>
        private static void PlaceViewports(
            Database db, LayoutManager lm, string layoutName,
            List<(double Cx, double Cy, double Pw, double Ph,
                  string Name, string Key)> boxes,
            double ratio, double winW, double winH, double gapPaper,
            double margin, double baseRatio, double pad,
            double bottomInset = 0.0)
        {
            // Activating the layout is REQUIRED, not an optimisation to drop.
            // It realises the viewports â€” a resized or cloned viewport keeps
            // Number = -1 and renders nothing until its layout has been current.
            // Removing this to save the ~6s regen produced sheets with correct
            // viewport geometry and no content at all. The cost is real, so the
            // caller chunks the run instead.
            try { lm.CurrentLayout = layoutName; } catch { }

            using var tr = db.TransactionManager.StartTransaction();
            var lay = (Layout)tr.GetObject(lm.GetLayoutId(layoutName), OpenMode.ForRead);
            var btr = (BlockTableRecord)tr.GetObject(
                lay.BlockTableRecordId, OpenMode.ForWrite);

            // The template carries more than one page-sized window and there is
            // no reliable way to read which of them is actually displaying â€”
            // Viewport.On lies from a non-current layout. Picking one and
            // switching the other off produced sheets that drew borders and no
            // content, i.e. the live one was the one being switched off.
            // So: take the geometry of the largest as the page window, erase
            // every page-sized window, and build fresh ones we fully control.
            double winCx = 0, winCy = 0, bestArea = 0;
            var doomed = new List<ObjectId>();
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Viewport v) continue;
                if (v.Number == 1) continue;
                if (v.Width > 36.0 || v.Height > 24.0) continue;
                if (v.Width * v.Height < 100.0) continue;      // detail bubble
                if (v.Width * v.Height > bestArea)
                {
                    bestArea = v.Width * v.Height;
                    winCx = v.CenterPoint.X; winCy = v.CenterPoint.Y;
                }
                doomed.Add(id);
            }
            if (doomed.Count == 0)
                throw new InvalidOperationException("no plan window on " + layoutName);
            foreach (var id in doomed)
                ((Viewport)tr.GetObject(id, OpenMode.ForWrite)).Erase();

            int n = boxes.Count;
            // Pad the window so boundary geometry â€” the edge ballast trays â€” is
            // inside the viewport instead of clipped by it. The pad goes on the
            // SLOT, not on ViewHeight alone: scale is ViewHeight / vp.Height, so
            // growing both together leaves the uniform scale untouched, whereas
            // growing ViewHeight by itself would silently rescale each array.
            var sizes = boxes.Select(b => (b.Pw * ratio / baseRatio + 2 * pad,
                                           b.Ph * ratio / baseRatio + 2 * pad)).ToList();
            // Lay out the same way the planner did, so the sheet matches the plan
            // that was validated. ShelfPack wraps on page width and could put two
            // arrays side by side that sit at opposite ends of the roof.
            // Keep the bottom band clear when asked: pack into a shorter box AND
            // raise its floor by the same amount. Shrinking alone is not enough,
            // because CentreSlots would just centre the block in the full window
            // and put it straight back over the legend.
            double usableH = winH - bottomInset;
            var slots = RelativePack(
                boxes.Select((b, i) => (b.Cx, b.Cy, sizes[i].Item1, sizes[i].Item2))
                     .ToList(),
                winW, usableH, gapPaper);
            // The planner already proved this group packs, so a failure here is a
            // bug. Fall back to a plain stack at the SAME scale rather than the
            // old winH/n split, which silently shrank arrays to make them fit.
            if (slots == null)
            {
                Say($"  !! {layoutName}: pack failed, stacking at base scale");
                slots = new List<Slot>();
                double yCursor = usableH;
                foreach (var z in sizes)
                {
                    yCursor -= z.Item2;
                    slots.Add(new Slot { X = 0, Y = yCursor, W = z.Item1, H = z.Item2 });
                    yCursor -= gapPaper;
                }
            }
            // Packing decides membership and order; centring only moves things.
            CentreSlots(slots, winW, usableH);
            double left = winCx - winW / 2.0;
            double bottom = winCy - winH / 2.0 + bottomInset;
            double slotH = winH / System.Math.Max(1, n);
            double top = winCy + winH / 2.0;

            NoPlotLayer(db, tr);

            for (int i = 0; i < n; i++)
            {
                var b = boxes[i];
                var vp = new Viewport();
                btr.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                // The border around each array is a drafting aid, not drawing
                // content. On a non-plotting layer it stays visible on screen
                // and disappears from the sheet.
                vp.Layer = VpLayer;

                // Model size of this array.
                double mw = b.Pw * ratio, mh = b.Ph * ratio;

                // One scale for the whole set, fixed by the largest array.
                // Per-sheet scaling made every page a different zoom, which
                // is exactly what the engineer does not want; he sizes the
                // biggest array to its own page and works to that.
                // NO per-sheet refitting. An earlier clamp re-fitted each array
                // to winH/n, so putting three on a page shrank a tall array to a
                // third of the height and its block counts stopped being legible.
                // The scale is fixed for the whole set; whether an array fits is
                // the packer's decision, not something to solve by zooming out.
                double r = baseRatio;

                // Size the WINDOW to the array's own proportions. Stretching it
                // to the full page width made a short wide slot, and filling that
                // dragged in neighbouring arrays either side.
                var slot = slots[i];
                vp.Width = slot.W;
                vp.Height = slot.H;
                vp.CenterPoint = new Autodesk.AutoCAD.Geometry.Point3d(
                    left + slot.X + slot.W / 2.0,
                    bottom + slot.Y + slot.H / 2.0, 0);

                vp.ViewCenter = new Autodesk.AutoCAD.Geometry.Point2d(b.Cx, b.Cy);
                vp.ViewHeight = vp.Height * r;
                vp.TwistAngle = 0.0;
                vp.On = true;     // fresh viewports come in off

                // Show only this array. Freezing the other arrays' layers in
                // THIS viewport isolates it without touching model space â€”
                // which is what the manual workflow achieves by dragging
                // arrays apart.
                if (b.Name != null)
                {
                    var hide = new ObjectIdCollection();
                    var lt2 = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId lid in lt2)
                    {
                        var l2 = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        var m3 = ArrLayer.Match(l2.Name);
                        if (!m3.Success) continue;
                        string owner = $"Roof {int.Parse(m3.Groups[1].Value)} "
                                     + $"Arr {int.Parse(m3.Groups[2].Value):00}";
                        // Match the LAYER key, not the display name. A split body
                        // is called "Roof 1 Arr 01a" but still lives on the
                        // "Roof 1 Arr 01" layers, so comparing against Name froze
                        // its own geometry and drew an empty window. Two bodies
                        // sharing a key cannot be separated by layer freezing â€”
                        // they are separated by where the viewport is aimed.
                        if (!string.Equals(owner, b.Key, StringComparison.Ordinal))
                            hide.Add(lid);
                    }
                    if (hide.Count > 0) vp.FreezeLayersInViewport(hide.GetEnumerator());

                    // With several arrays to a page, each window has to say
                    // which one it is.
                    // Below the array and centred under it. It used to hang off
                    // the top-left of the viewport border, which no longer plots
                    // â€” so the label had nothing left to belong to.
                    const double LBLH = 0.18, LBLGAP = 0.16;
                    var lbl = new DBText
                    {
                        TextString = b.Name.ToUpperInvariant(),
                        Height = LBLH,
                        HorizontalMode = TextHorizontalMode.TextCenter,
                        VerticalMode = TextVerticalMode.TextTop,
                        // Colour 7 is the drafting default: black on a light
                        // background, white on a dark one. Inheriting the current
                        // layer's colour plotted these yellow.
                        ColorIndex = 7,
                    };
                    // Justified DBText reads AlignmentPoint and ignores Position,
                    // so set Position first and mirror it into AlignmentPoint â€”
                    // setting only Position leaves justified text at the origin.
                    lbl.Position = new Autodesk.AutoCAD.Geometry.Point3d(
                        left + slot.X + slot.W / 2.0,
                        bottom + slot.Y - LBLGAP, 0);
                    lbl.AlignmentPoint = lbl.Position;
                    btr.AppendEntity(lbl);
                    tr.AddNewlyCreatedDBObject(lbl, true);
                }
            }
            tr.Commit();
        }

        /// <summary>
        /// Freeze the traced-PDF underlay layers and leave them frozen.
        ///
        /// Plotting this drawing times out because those ~500k entities have to
        /// be regenerated; they are also invisible on our sheets. Run before a
        /// plot so the page can actually be produced.
        /// </summary>
        [CommandMethod("PZSEFREEZE")]
        public static void PzseFreeze()
        {
            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                int n = 0;
                foreach (ObjectId id in lt)
                {
                    var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!l.Name.StartsWith("PDF", StringComparison.OrdinalIgnoreCase)
                        || l.IsFrozen) continue;
                    ((LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite)).IsFrozen = true;
                    n++;
                }
                tr.Commit();
                Say($"FREEZE-DONE froze {n} underlay layer(s)");
            }
            catch (System.Exception ex)
            {
                Say("FREEZE-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool LayoutExists(Transaction tr, Database db, string name)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            return dict.Contains(name);
        }

        /// <summary>
        /// Minimal glob: supports a leading and/or trailing '*', which is all the
        /// layer patterns in this project actually use. Comma-separated patterns
        /// match if any one matches.
        /// </summary>
        private static bool Glob(string s, string patterns)
        {
            if (string.IsNullOrEmpty(patterns) || s == null) return false;
            foreach (var raw in patterns.Split(','))
            {
                var pat = raw.Trim();
                if (pat.Length == 0) continue;
                if (pat == "*") return true;
                bool lead = pat.StartsWith("*"), trail = pat.EndsWith("*");
                var core = pat.Trim('*');
                if (core.Length == 0) continue;
                const StringComparison IC = StringComparison.OrdinalIgnoreCase;
                bool hit = lead && trail ? s.IndexOf(core, IC) >= 0
                         : trail ? s.StartsWith(core, IC)
                         : lead ? s.EndsWith(core, IC)
                         : string.Equals(s, core, IC);
                if (hit) return true;
            }
            return false;
        }

        /// <summary>Sort key for our own tabs: S1 first, then S1.1 .. S1.N.</summary>
        private static int SheetKey(string name)
        {
            if (string.Equals(name, "S1", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name == null
                || !name.StartsWith("S1.", StringComparison.OrdinalIgnoreCase))
                return int.MaxValue;
            // Read only the leading digit run. The tab may carry an array token
            // after the number ("S1.7_R1-A04a"); parsing the whole remainder
            // failed, returned int.MaxValue, and had SLIM delete every sheet the
            // run had just generated â€” with MAKE still reporting failed=0.
            int i = 3;
            while (i < name.Length && char.IsDigit(name[i])) i++;
            return i > 3 && int.TryParse(name.Substring(3, i - 3), out int k)
                ? k : int.MaxValue;
        }

        /// <summary>
        /// Post-process the generated set into something an engineer can open:
        /// drop the tabs we did not author, order the ones we did, strip the
        /// traced-PDF underlay, and purge what that leaves unreferenced.
        ///
        /// Run AFTER the last PZSEMAKE chunk and BEFORE PZSESAVE. It is not part
        /// of PZSEMAKE because a chunked run still needs the template tab to
        /// clone from â€” pruning mid-run would break every later chunk.
        ///
        /// The underlay is ~1.4M entities that no sheet displays (PZSEMAKE
        /// freezes them to plot, then thaws them again before saving), so they
        /// are pure weight in the delivered file. The full-fidelity copy is the
        /// retained INPUT drawing, which is where the underlay came from.
        ///
        /// params.json: { "stripUnderlay": true, "underlayLayers": "PDF*" }
        /// </summary>
        [CommandMethod("PZSESLIM")]
        public static void PzseSlim()
        {
            var db = HostApplicationServices.WorkingDatabase;
            bool strip = true;
            string underlay = "PDF*";
            string plotStyle = "monochrome.ctb";
            try
            {
                string pf = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
                if (File.Exists(pf))
                {
                    using var pd = JsonDocument.Parse(File.ReadAllText(pf));
                    if (pd.RootElement.TryGetProperty("stripUnderlay", out var s))
                        strip = s.GetBoolean();
                    if (pd.RootElement.TryGetProperty("underlayLayers", out var u))
                        underlay = u.GetString();
                    if (pd.RootElement.TryGetProperty("plotStyle", out var ps))
                        plotStyle = ps.GetString();
                }
            }
            catch (System.Exception ex)
            {
                Say("SLIM params ignored: " + ex.Message);
            }

            // ---- 1. tabs -------------------------------------------------
            try
            {
                var lm = LayoutManager.Current;
                var keep = new List<string>();
                var drop = new List<string>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var dict = (DBDictionary)tr.GetObject(
                        db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry e in dict)
                    {
                        if (string.Equals(e.Key, "Model", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (SheetKey(e.Key) != int.MaxValue) keep.Add(e.Key);
                        else drop.Add(e.Key);
                    }
                    tr.Commit();
                }

                // Deleting the current layout throws, so park on one we keep.
                if (keep.Count > 0)
                {
                    keep.Sort((a, b) => SheetKey(a).CompareTo(SheetKey(b)));
                    try { lm.CurrentLayout = keep[0]; } catch { }
                }

                int gone = 0;
                foreach (var name in drop)
                {
                    try { lm.DeleteLayout(name); gone++; }
                    catch (System.Exception ex)
                    { Say($"  keep {name}: {ex.GetType().Name}"); }
                }
                Say($"SLIM tabs: kept {keep.Count}, deleted {gone}");

                // Contiguous tab order, S1 then S1.1 .. S1.N.
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    for (int i = 0; i < keep.Count; i++)
                    {
                        var lay = (Layout)tr.GetObject(
                            lm.GetLayoutId(keep[i]), OpenMode.ForWrite);
                        lay.TabOrder = i + 1;
                    }
                    tr.Commit();
                }
                if (keep.Count > 0)
                    Say($"  order: {string.Join(", ", keep.Take(6))}"
                        + (keep.Count > 6 ? $", ... ({keep.Count} tabs)" : ""));
            }
            catch (System.Exception ex)
            {
                Say("SLIM-TABS-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }

            // ---- 2. underlay ---------------------------------------------
            if (strip)
            {
                // A single transaction over ~1.4M entities does not finish inside
                // the processing limit â€” the undo record alone sinks it. So:
                // undo recording off, ids gathered in one cheap read pass, then
                // erased through the open/close API with no transaction at all.
                db.DisableUndoRecording(true);
                try
                {
                    var doomed = new List<ObjectId>();
                    var kill = new List<ObjectId>();

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        // A locked or frozen layer refuses the erase, and PZSEMAKE
                        // leaves these frozen on the way through.
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        foreach (ObjectId lid in lt)
                        {
                            var l = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                            if (!Glob(l.Name, underlay)) continue;
                            var w = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForWrite);
                            w.IsFrozen = false;
                            w.IsLocked = false;
                            doomed.Add(lid);
                        }

                        // Walk every block definition, not just model space â€”
                        // imported PDF geometry is routinely nested in blocks.
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        foreach (ObjectId bid in bt)
                        {
                            var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                            foreach (ObjectId eid in btr)
                            {
                                if (tr.GetObject(eid, OpenMode.ForRead) is not Entity ent)
                                    continue;
                                if (Glob(ent.Layer, underlay)) kill.Add(eid);
                            }
                        }
                        tr.Commit();
                    }
                    Say($"SLIM underlay: {kill.Count:N0} entities on "
                        + $"{doomed.Count} layer(s) matching '{underlay}'");

                    int erased = 0;
                    foreach (var eid in kill)
                    {
                        try
                        {
                            var e = (Entity)eid.Open(OpenMode.ForWrite);
                            e.Erase();
                            e.Close();
                            erased++;
                        }
                        catch { }
                        if (erased % 250000 == 0 && erased > 0)
                            Say($"  erased {erased:N0} / {kill.Count:N0}");
                    }
                    Say($"SLIM underlay: erased {erased:N0}");
                }
                catch (System.Exception ex)
                {
                    Say("SLIM-UNDERLAY-FAILED " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    db.DisableUndoRecording(false);
                }
            }
            else Say("SLIM underlay: kept (stripUnderlay=false)");

            // ---- 3. purge ------------------------------------------------
            try
            {
                int total = 0;
                // Purging frees nested references a round at a time: an emptied
                // block can only be purged once its last insert is gone.
                for (int round = 0; round < 5; round++)
                {
                    var cand = new ObjectIdCollection();
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in (LayerTable)tr.GetObject(
                                     db.LayerTableId, OpenMode.ForRead)) cand.Add(id);
                        foreach (ObjectId id in (BlockTable)tr.GetObject(
                                     db.BlockTableId, OpenMode.ForRead)) cand.Add(id);
                        foreach (ObjectId id in (LinetypeTable)tr.GetObject(
                                     db.LinetypeTableId, OpenMode.ForRead)) cand.Add(id);
                        foreach (ObjectId id in (TextStyleTable)tr.GetObject(
                                     db.TextStyleTableId, OpenMode.ForRead)) cand.Add(id);
                        foreach (ObjectId id in (DimStyleTable)tr.GetObject(
                                     db.DimStyleTableId, OpenMode.ForRead)) cand.Add(id);
                        tr.Commit();
                    }

                    db.Purge(cand);            // filters to what is truly unreferenced
                    if (cand.Count == 0) break;

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in cand)
                        {
                            try
                            {
                                var o = tr.GetObject(id, OpenMode.ForWrite);
                                if (!o.IsErased) { o.Erase(); total++; }
                            }
                            catch { }
                        }
                        tr.Commit();
                    }
                }
                Say($"SLIM purge: removed {total} unreferenced record(s)");
            }
            catch (System.Exception ex)
            {
                Say("SLIM-PURGE-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }

            // ---- 4. plot style -------------------------------------------
            // The sheet prints black while the drawing stays in colour on screen:
            // a colour-dependent plot style maps every pen to black at plot time
            // and changes no entity. Applied to the layouts, so a plot the
            // engineer runs from LT comes out the same as one we run here.
            if (!string.IsNullOrEmpty(plotStyle))
            {
                try
                {
                    if (!db.PlotStyleMode && plotStyle.EndsWith(
                            ".ctb", StringComparison.OrdinalIgnoreCase))
                        Say($"SLIM plot style: WARNING drawing uses named styles "
                            + $"(STB); '{plotStyle}' may not apply");

                    // PlotSettings.CurrentStyleSheet is read-only; the validator
                    // is the only supported way to set it.
                    var psv = PlotSettingsValidator.Current;
                    try
                    {
                        var avail = psv.GetPlotStyleSheetList();
                        var names = new List<string>();
                        foreach (string s in avail) names.Add(s);
                        if (!names.Any(s => string.Equals(
                                s, plotStyle, StringComparison.OrdinalIgnoreCase)))
                            Say($"SLIM plot style: '{plotStyle}' not installed; "
                                + $"available: {string.Join(", ", names.Take(12))}");
                    }
                    catch { }

                    int set = 0;
                    using var tr = db.TransactionManager.StartTransaction();
                    var dict = (DBDictionary)tr.GetObject(
                        db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry e in dict)
                    {
                        try
                        {
                            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForWrite);
                            psv.SetCurrentStyleSheet(lay, plotStyle);
                            set++;
                        }
                        catch (System.Exception ex2)
                        { Say($"  {e.Key}: {ex2.GetType().Name}: {ex2.Message}"); }
                    }
                    tr.Commit();
                    Say($"SLIM plot style: '{plotStyle}' on {set} layout(s) "
                        + $"(mode={(db.PlotStyleMode ? "CTB" : "STB")})");
                }
                catch (System.Exception ex)
                {
                    Say("SLIM-PLOTSTYLE-FAILED " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Say("SLIM-DONE");
        }

        /// <summary>
        /// Post-process the finished set so AutoCAD can open and page through it.
        /// Output-only: it changes nothing about how sheets are planned or made,
        /// and is not part of PZSEMAKE.
        ///
        /// 1. Freeze the vendor's working-copy layers IN THE PLAN VIEWPORTS.
        ///    Sollega drawings carry a second, complete copy of the racking on
        ///    a_*/b_*/c_* alongside the "Roof N Arr M" layers the sheets actually
        ///    draw from â€” 32% of the entities on Joliet, 62% on San Diego â€” and
        ///    nothing frozen them, so every viewport rendered them.
        ///
        ///    Per-viewport, NOT a global layer freeze. Freezing them globally
        ///    also blanked the module swatches in the MODULE DETAILS legend,
        ///    which is drawn from that same geometry and shown through one of the
        ///    titleblock viewports. Only the plan windows need them gone.
        ///
        /// 2. Park the drawing on Model, so opening it does not immediately
        ///    regenerate a layout.
        ///
        /// params.json: { "fastFreezeLayers": "a_*,b_*,c_*", "parkOnModel": true }
        /// </summary>
        [CommandMethod("PZSEFAST")]
        public static void PzseFast()
        {
            string pattern = "a_*,b_*,c_*";
            bool park = true;
            // When the key plan is drawn FROM these layers, freezing them in its
            // viewport empties the sheet. The key plan is always "S1".
            bool sparePlan = false;
            try
            {
                string pf = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
                if (File.Exists(pf))
                {
                    using var pd = JsonDocument.Parse(File.ReadAllText(pf));
                    if (pd.RootElement.TryGetProperty("fastFreezeLayers", out var p))
                        pattern = p.GetString();
                    if (pd.RootElement.TryGetProperty("parkOnModel", out var k))
                        park = k.GetBoolean();
                    if (pd.RootElement.TryGetProperty("keySourceLayers", out var ks))
                        sparePlan = !string.IsNullOrWhiteSpace(ks.GetString());
                }
            }
            catch (System.Exception ex) { Say("FAST params ignored: " + ex.Message); }

            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                // Never touch an array layer, whatever the pattern says â€” those
                // are the sheets' content.
                var hide = new ObjectIdCollection();
                var names = new List<string>();
                foreach (ObjectId lid in lt)
                {
                    var l = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    if (ArrLayer.IsMatch(l.Name)) continue;
                    if (!WildMatch(l.Name, pattern)) continue;
                    hide.Add(lid);
                    names.Add(l.Name);
                }
                if (hide.Count == 0) { Say("FAST: no layers matched " + pattern); }
                else Say($"FAST freezing {hide.Count} layer(s) in plan viewports: "
                         + string.Join(", ", names.Take(6))
                         + (names.Count > 6 ? $", +{names.Count - 6} more" : ""));

                int vps = 0;
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry de in dict)
                {
                    var lay = (Layout)tr.GetObject(de.Value, OpenMode.ForRead);
                    if (lay.LayoutName.Equals("Model", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (sparePlan && lay.LayoutName.Equals("S1", StringComparison.OrdinalIgnoreCase))
                    { Say("FAST: S1 spared, it is drawn from those layers"); continue; }
                    var btr = (BlockTableRecord)tr.GetObject(
                        lay.BlockTableRecordId, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is not Viewport v) continue;
                        // Only OUR plan windows. The titleblock's own viewports
                        // display the legend and must keep the geometry.
                        if (!string.Equals(v.Layer, VpLayer, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (hide.Count > 0)
                        {
                            var w = (Viewport)tr.GetObject(id, OpenMode.ForWrite);
                            w.FreezeLayersInViewport(hide.GetEnumerator());
                        }
                        vps++;
                    }
                }
                Say($"FAST: {vps} plan viewport(s) updated");
                tr.Commit();
            }
            catch (System.Exception ex)
            {
                Say($"FAST-FAILED {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (park)
            {
                try
                {
                    LayoutManager.Current.CurrentLayout = "Model";
                    Say("FAST: parked on Model");
                }
                catch (System.Exception ex) { Say("FAST park failed: " + ex.Message); }
            }
            Say("FAST-DONE");
        }

        /// <summary>
        /// Fill in the titleblock per sheet, and stop the template's legend
        /// window repeating over every array.
        ///
        /// 1. SHEET NUMBER. The Sollega titleblock carries a hard-coded "S1.0"
        ///    bottom-right â€” it is plain MTEXT, not a field, so every cloned
        ///    sheet read S1.0. Set it from the tab's own number: the key plan
        ///    becomes S1.0 and the array sheets S1.1, S1.2, ... This is the
        ///    number that belongs in the titleblock; the array token in the tab
        ///    name is a drafting convenience and deliberately stays out of it.
        ///
        /// 2. LEGEND. The template's module-details window (8.23x4.76in) sits
        ///    inside the drawing area, so once arrays were scaled up to fill the
        ///    page it landed on top of them. Any template viewport overlapping
        ///    our plan window is removed from every sheet but the first â€” which
        ///    is the general statement of the problem, rather than matching that
        ///    one window by size.
        ///
        /// params.json: { "sheetNumberText": "S1.0", "legendOnFirstOnly": true }
        /// </summary>
        [CommandMethod("PZSETITLE")]
        public static void PzseTitle()
        {
            string placeholder = "S1.0";
            bool legendFirstOnly = true;
            try
            {
                string pf = Path.Combine(Directory.GetCurrentDirectory(), "params.json");
                if (File.Exists(pf))
                {
                    using var pd = JsonDocument.Parse(File.ReadAllText(pf));
                    if (pd.RootElement.TryGetProperty("sheetNumberText", out var p))
                        placeholder = p.GetString();
                    if (pd.RootElement.TryGetProperty("legendOnFirstOnly", out var l))
                        legendFirstOnly = l.GetBoolean();
                }
            }
            catch (System.Exception ex) { Say("TITLE params ignored: " + ex.Message); }

            var db = HostApplicationServices.WorkingDatabase;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

                // Editing anything on a locked layer throws eOnLockedLayer and
                // takes the whole transaction with it — Joliet ships locked
                // layers and numbered zero sheets because of it. Unlock for the
                // duration and put the locks back.
                var relock = new List<ObjectId>();
                var ltab = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in ltab)
                {
                    var l = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    if (!l.IsLocked) continue;
                    ((LayerTableRecord)tr.GetObject(lid, OpenMode.ForWrite)).IsLocked = false;
                    relock.Add(lid);
                }
                if (relock.Count > 0)
                    Say($"TITLE temporarily unlocked {relock.Count} layer(s)");

                var sheets = new List<(int Key, string Name, ObjectId Id)>();
                foreach (DBDictionaryEntry e in dict)
                {
                    if (string.Equals(e.Key, "Model", StringComparison.OrdinalIgnoreCase))
                        continue;
                    sheets.Add((SheetKey(e.Key), e.Key, e.Value));
                }
                sheets.Sort((a, b) => a.Key.CompareTo(b.Key));

                // The drawing area is wherever we put plan windows, unioned over
                // the whole set. Testing the legend against ONE sheet's plan
                // window missed the paired sheets, whose windows are short and
                // never reach down to it.
                double dx0 = double.MaxValue, dy0 = double.MaxValue;
                double dx1 = double.MinValue, dy1 = double.MinValue;
                foreach (var s in sheets)
                {
                    var l = (Layout)tr.GetObject(s.Id, OpenMode.ForRead);
                    var b = (BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead);
                    foreach (ObjectId eid in b)
                    {
                        if (tr.GetObject(eid, OpenMode.ForRead) is not Viewport v) continue;
                        if (!string.Equals(v.Layer, VpLayer, StringComparison.OrdinalIgnoreCase))
                            continue;
                        // The key plan's window is 120x50in — larger than the
                        // sheet and hanging off it. Letting that into the union
                        // made the "drawing area" -44..77in, which swallowed the
                        // whole titleblock and erased 145 windows.
                        if (v.Width > 36.0 || v.Height > 24.0) continue;
                        dx0 = System.Math.Min(dx0, v.CenterPoint.X - v.Width / 2);
                        dy0 = System.Math.Min(dy0, v.CenterPoint.Y - v.Height / 2);
                        dx1 = System.Math.Max(dx1, v.CenterPoint.X + v.Width / 2);
                        dy1 = System.Math.Max(dy1, v.CenterPoint.Y + v.Height / 2);
                    }
                }
                if (dx1 > dx0)
                    Say($"TITLE drawing area {dx0:0.##},{dy0:0.##} .. {dx1:0.##},{dy1:0.##} in");

                int numbered = 0, dropped = 0, missing = 0, frame = 0;
                for (int i = 0; i < sheets.Count; i++)
                {
                    var lay = (Layout)tr.GetObject(sheets[i].Id, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(
                        lay.BlockTableRecordId, OpenMode.ForRead);

                    string number = sheets[i].Key == 0 ? "S1.0" : $"S1.{sheets[i].Key}";

                    var candidates = new List<ObjectId>();
                    foreach (ObjectId eid in btr)
                    {
                        if (tr.GetObject(eid, OpenMode.ForRead) is not Viewport v) continue;
                        if (string.Equals(v.Layer, VpLayer, StringComparison.OrdinalIgnoreCase))
                            continue;
                        candidates.Add(eid);
                    }

                    // The sheet number, and any template window over the drawing.
                    bool found = false;
                    foreach (ObjectId eid in btr)
                    {
                        var obj = tr.GetObject(eid, OpenMode.ForRead);
                        if (obj is MText mt && mt.Text.Trim() == placeholder)
                        {
                            var w = (MText)tr.GetObject(eid, OpenMode.ForWrite);
                            // Rewrite inside the formatting run so the font,
                            // weight and height survive.
                            w.Contents = w.Contents.Replace(placeholder, number);
                            found = true; numbered++;
                        }
                        else if (obj is DBText dt && dt.TextString.Trim() == placeholder)
                        {
                            var w = (DBText)tr.GetObject(eid, OpenMode.ForWrite);
                            w.TextString = number;
                            found = true; numbered++;
                        }
                    }
                    if (!found) missing++;

                    if (legendFirstOnly && i > 0 && dx1 > dx0)
                    {
                        var gone = new List<(double X0, double Y0, double X1, double Y1)>();
                        foreach (var eid in candidates)
                        {
                            var v = (Viewport)tr.GetObject(eid, OpenMode.ForRead);
                            double x0 = v.CenterPoint.X - v.Width / 2;
                            double y0 = v.CenterPoint.Y - v.Height / 2;
                            double x1 = v.CenterPoint.X + v.Width / 2;
                            double y1 = v.CenterPoint.Y + v.Height / 2;
                            // CONTAINED in the drawing area, not merely touching
                            // it. The paper-space viewport always spills past the
                            // area (Joliet's is 33.4x18.9 centred off-sheet, San
                            // Diego's 47.6x27.0), so containment excludes it
                            // without needing to identify it — which mattered,
                            // because both Number and a size threshold failed and
                            // erasing it blanked the titleblock's SITE box.
                            if (!(x0 >= dx0 - 0.01 && x1 <= dx1 + 0.01 &&
                                  y0 >= dy0 - 0.01 && y1 <= dy1 + 0.01)) continue;
                            // Erase rather than switch off: an off viewport still
                            // leaves its border on a plotting layer.
                            ((Viewport)tr.GetObject(eid, OpenMode.ForWrite)).Erase();
                            gone.Add((x0, y0, x1, y1));
                            dropped++;
                        }

                        // The legend's frame is separate paper-space geometry, so
                        // erasing the window alone left an empty box on the sheet.
                        // Take what sits INSIDE the window's footprint with it.
                        const double TOL = 0.06;
                        foreach (ObjectId eid in btr)
                        {
                            var obj = tr.GetObject(eid, OpenMode.ForRead);
                            if (obj is Viewport) continue;
                            if (obj is not Entity ent || ent.IsErased) continue;
                            // Never the array label we drew.
                            if (ArrLayer.IsMatch(ent.Layer)) continue;
                            Extents3d ex;
                            try { ex = ent.GeometricExtents; } catch { continue; }
                            foreach (var g in gone)
                            {
                                if (ex.MinPoint.X >= g.X0 - TOL && ex.MaxPoint.X <= g.X1 + TOL &&
                                    ex.MinPoint.Y >= g.Y0 - TOL && ex.MaxPoint.Y <= g.Y1 + TOL)
                                {
                                    ((Entity)tr.GetObject(eid, OpenMode.ForWrite)).Erase();
                                    frame++;
                                    break;
                                }
                            }
                        }
                    }
                }
                foreach (var lid in relock)
                    ((LayerTableRecord)tr.GetObject(lid, OpenMode.ForWrite)).IsLocked = true;

                Say($"TITLE numbered {numbered} sheet(s)"
                    + (missing > 0 ? $", {missing} had no '{placeholder}' text" : ""));
                if (legendFirstOnly)
                    Say($"TITLE removed {dropped} legend window(s) + {frame} frame item(s) from the drawing area");
                tr.Commit();
            }
            catch (System.Exception ex)
            {
                Say($"TITLE-FAILED {ex.GetType().Name}: {ex.Message}");
                return;
            }
            Say("TITLE-DONE");
        }

        /// <summary>Save the working database to output.dwg in the working dir.</summary>
        [CommandMethod("PZSESAVE")]
        public static void PzseSave()
        {
            try
            {
                var db = HostApplicationServices.WorkingDatabase;
                string outPath = Path.Combine(Directory.GetCurrentDirectory(), "output.dwg");
                db.SaveAs(outPath, DwgVersion.Current);
                Say("SAVED " + outPath + " (" + new FileInfo(outPath).Length + " bytes)");
            }
            catch (System.Exception ex)
            {
                Say("SAVE-FAILED " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}

