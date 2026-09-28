"""METCO / Joliet West High School (FR510-10) sheet set.

Third Sollega-convention job. This drawing arrives as Model plus a single
"36 x 24" template tab â€” no hand-built sheets to strip first, so it goes
straight into the generator.

The engineer asked only for the viewports and explicitly did not ask for
titleblock work, so the known unresolved template FIELDs (scale bar, "SCALE:",
sheet number) are left alone.

  python aps_joliet.py plan   # PZSESHEETS -- report only
  python aps_joliet.py make   # single-shot; use aps_joliet_chunked.py if it
                              # trips the ~100s processing limit
"""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

SRC = Path(__file__).parent / "_scratch" / "joliet_src.dwg"

PARAMS = {
    "floorIn": 0.125,
    "gapFt": 6.0,
    "maxPerSheet": 2,
    "template": "36 x 24",
    "countLayers": "*Block Count*",
    "padIn": 0.10,
    # No PDF_* layers in this drawing â€” nothing to strip.
    "stripUnderlay": False,
    "plotStyle": "monochrome.ctb",
    # Tab names carry the array: "S1.7_R1-A04".
    "sheetNameArrays": True,
    # Titleblock sheet number: key plan S1.0, array sheets S1.1, S1.2 ...
    "sheetNumberText": "S1.0",
    # The template's module-details window repeats on every sheet and lands on
    # the array; keep it on the first page only.
    "legendOnFirstOnly": True,
    "fastFreezeLayers": "a_*,b_*,c_*",
    "parkOnModel": True,
    # Measured 2026-08-28: the site is 3,183 x 398ft, an 8:1 band in a 1.34:1
    # window, so a whole-site key plan wastes most of the page vertically and
    # lands at 1:1301 - every module 0.034in of paper, i.e. a smear. Packing the
    # clusters into rows uses the height and gets 1:416 (module 0.107in), which
    # is the biggest gain of any drawing so far. 43% of the width is empty, in
    # gaps of 492 / 347 / 203 ft - scattered, not one band like San Diego.
    "keyCompact": True,
    "keyGroupGapFt": 150.0,
    # No vendor copy worth framing: unlike San Diego, Joliet's a_* layers sit on
    # TOP of the analysis geometry rather than off in their own region.
    "keySourceLayers": None,
    # Sheet-number tag above each array.
    "keyLabels": True,
    "keyLabelBandIn": 0.0,
    # Joliet's template carries NO module-details legend, so nothing to clear.
    "keyBottomInsetIn": 0.0,
}

IN_OBJ = "joliet_in.dwg"

if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "plan"
    aps_run.IN_OBJ = IN_OBJ
    aps_run.OUT_OBJ = "joliet_sheetset.dwg"

    if "--skip-upload" not in sys.argv:
        tok = get_token()[1]["access_token"]
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, aps_run.BUCKET, aps_run.IN_OBJ, SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])

    if mode == "plan":
        aps_run.run("PZSESHEETS\n", params=PARAMS, want_output=False)
    else:
        aps_run.run("PZSEMAKE\nPZSESLIM\nPZSESAVE\n", params=PARAMS,
                    want_output=True)
