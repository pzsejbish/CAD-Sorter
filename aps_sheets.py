"""Plan the sheet set the way the reference engineer composes it.

One array per group (identified by layer name), at most two per sheet stacked
vertically, uniform scale from the legibility floor, plus a key plan.
"""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

SRC = Path(__file__).parent / "_scratch" / "mchenry_filled_new.dwg"

PARAMS = {
    "floorIn": 0.125,        # minimum legible printed block-count height
    "gapFt": 6.0,            # space between two arrays on one sheet
    "maxPerSheet": 4,        # Sollega never puts more than two
    "template": "SR100 (2)",
    "countLayers": "*Block Count*",
}

if __name__ == "__main__":
    aps_run.IN_OBJ = "mchenry_plan_in.dwg"
    aps_run.OUT_OBJ = "sheets_out.dwg"

    if "--skip-upload" not in sys.argv:
        tok = get_token()[1]["access_token"]
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, aps_run.BUCKET, aps_run.IN_OBJ, SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])

    aps_run.run("PZSESHEETS\n", params=PARAMS, want_output=False)
