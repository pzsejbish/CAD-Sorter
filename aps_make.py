"""Generate the sheet set: key plan + one sheet per array group."""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

SRC = Path(__file__).parent / "_scratch" / "mchenry_filled_new.dwg"

PARAMS = {
    "floorIn": 0.125,
    "gapFt": 6.0,
    "maxPerSheet": 4,
    "template": "SR100 (2)",
    "countLayers": "*Block Count*",
    # Paper inches of clearance per side, so edge ballast trays are not clipped
    # by the viewport window.
    "padIn": 0.10,
    # The traced-PDF underlay is ~1.4M entities that no sheet displays. Full
    # fidelity is retained in the INPUT drawing; the deliverable does not need
    # a second copy. Set false for a job where the underlay IS the base plan.
    "stripUnderlay": True,
    "underlayLayers": "PDF*",
    # The engineer wants the printed sheet all black. A colour-dependent plot
    # style does that at plot time, so the drawing stays in colour on screen.
    "plotStyle": "monochrome.ctb",
}

if __name__ == "__main__":
    aps_run.IN_OBJ = "mchenry_plan_in.dwg"
    aps_run.OUT_OBJ = "mchenry_sheetset.dwg"

    if "--skip-upload" not in sys.argv:
        tok = get_token()[1]["access_token"]
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, aps_run.BUCKET, aps_run.IN_OBJ, SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])

    # SLIM runs after the last MAKE: a chunked run still needs the template tab
    # to clone from, so pruning cannot happen mid-run.
    aps_run.run("PZSEMAKE\nPZSESLIM\nPZSESAVE\n", params=PARAMS, want_output=True)
