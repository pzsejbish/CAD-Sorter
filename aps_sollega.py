"""Sollega / San Diego Intl Airport (26_02881) sheet set.

Same pipeline as the IronRidge McHenry job â€” this drawing happens to use the
identical "Roof N Arr M <kind>" layer convention, so nothing in the AppBundle
needed changing. What differs is the template tab and the underlay layers.

Input is the Rev7 layout drawing reduced to its Model tab plus ONE paper tab
kept as the titleblock template ("36 x 24"). The engineer's own 24 hand-built
sheets were deleted; they are the benchmark this run is measured against.

  python aps_sollega.py plan   # PZSESHEETS -- report only, no output drawing
  python aps_sollega.py make   # PZSEMAKE + SLIM + SAVE -- writes the sheet set
"""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

SRC = Path(__file__).parent / "_scratch" / "sollega_sdia_rev7_run.dwg"

PARAMS = {
    "floorIn": 0.125,
    "gapFt": 6.0,
    # The reference engineer stacks at most two arrays on a sheet.
    "maxPerSheet": 2,
    # The one paper tab kept from Rev7; its viewport #3 (30.44x22.78in) is the
    # plan window the whole set is scaled to.
    "template": "36 x 24",
    "countLayers": "*Block Count*",
    "padIn": 0.10,
    # PDF_Solid Fills alone is 25,193 of the 104,795 model entities and no
    # sheet displays it.
    "stripUnderlay": True,
    "underlayLayers": "PDF*",
    "plotStyle": "monochrome.ctb",
    # Tab names carry the array: "S1.7 R1-A04a". 29 sheets of "S1.N" gave no way
    # to find a given array without opening tabs one by one.
    "sheetNameArrays": True,
    # Titleblock sheet number: key plan S1.0, array sheets S1.1, S1.2 ...
    "sheetNumberText": "S1.0",
    # The template's module-details window repeats on every sheet and lands on
    # the array; keep it on the first page only.
    "legendOnFirstOnly": True,
    "fastFreezeLayers": "a_*,b_*,c_*",
    "parkOnModel": True,
    # The arrays here sit inside 3,859x1,588ft of extents that carry a single
    # 1,447ft empty band, so one window over the whole site draws every module
    # at 0.03in and the arrays plot as solid black. Give each cluster its own
    # window instead: no geometry moves, every window is still true model space
    # at one shared scale, and the dead ground stops paying for page.
    #
    # The engineer's own stamped Rev7 key plan is compacted the same way, by
    # hand, at 1:768. This is a large-site remedy and stays OFF elsewhere.
    # This drawing carries the site TWICE. The "Roof N Arr M" analysis geometry
    # is exploded roof by roof (3,859 x 1,588ft with a 1,447ft hole); Sollega's
    # own site layout on a_* is already one coherent plan (1,904 x 241ft at
    # x 6,351..8,255). The engineer's stamped key plan frames the SECOND one --
    # verified: his viewport holds 4,443 of its module blocks and its aspect is
    # 6.24:1 against his plotted 6.22:1. So aim at it rather than compact ours.
    # ⚠ The two key-plan styles are mutually exclusive, and the choice is a real
    # trade-off:
    #   keySourceLayers -> the VENDOR plan. Looks like the engineer's sheet
    #       (1:781 vs his 1:768) but CANNOT be labelled: our sheet numbers
    #       belong to the analysis copy and nothing in the file ties the two
    #       copies together.
    #   keyCompact      -> the ANALYSIS copy, packed. Less like his picture, but
    #       every array can be labelled with the sheet that shows it, because we
    #       assigned those numbers ourselves.
    # Labelled wins by default: a key plan exists to say which page to turn to.
    "keySourceLayers": None,
    "keyCompact": True,
    "keyGroupGapFt": 300.0,
    # Keep the bottom of the plan window clear of the module-details legend,
    # which is template content the placer does not erase. Uncompacted the
    # arrays never reached it; compacted they plot straight over it.
    #
    # Measured, not guessed: the legend viewport is 8.23x4.76in with its TOP at
    # paper y=7.90in, and the plan window floor is y=0.335in, so the pack has to
    # start 7.57in up. 7.7 gives a little margin. Costs no scale at all - this
    # pack is width-limited, and it only degrades past ~8.5in (1:621).
    "keyBottomInsetIn": 7.7,
    # Per-array callouts on the key plan, the way the engineer draws them:
    # a bordered box with the manufacturer over "ROOF n ARRAY m", and a plain
    # leader to the array. Only the band ABOVE the arrays costs scale; the band
    # below reuses the legend inset we were already giving up.
    # OFF pending the array-identity match. The callouts currently aim at the
    # ANALYSIS geometry, which is nowhere near the vendor plan the key sheet now
    # frames, so every leader would run off the sheet. Labelling the vendor copy
    # needs a PZSE-array -> vendor-array correspondence first.
    "keyLabels": True,
    # 0: the tags sit directly above their arrays, so no band is reserved and
    # the key plan keeps its full 1:601.
    "keyLabelBandIn": 0.0,
    # The make is NOT in the array layers - all 29 are ACI 5. It is the BLOCK
    # NAME on the vendor module layer. Verified against the titleblock:
    # H = 2,492 vs Heliene 2,491, T = 2,154 vs Talesun 2,152.
    "makeLayer": "a_Modules",
    "makeNames": {"H": "HELIENE", "T": "TALESUN"},
}

if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "plan"
    aps_run.IN_OBJ = "sollega_sdia_in.dwg"
    aps_run.OUT_OBJ = "sollega_sdia_sheetset.dwg"

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
