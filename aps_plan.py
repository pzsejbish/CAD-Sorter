"""Ask the drawing what the sheet plan should be — no modification.

Reports the measured block-count text height, the scale that keeps it legible at
the 0.125in floor, the PV extent, and the resulting tile grid.
"""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

# The proof bucket is 'transient', so objects vanish after 24h and the input has
# to go back up each session.
SRC = Path(__file__).parent / "_scratch" / "mchenry_filled_new.dwg"

PARAMS = {
    "floorIn": 0.125,           # minimum legible printed text height
    "template": "SR100",
    # Tile OUR output — the bay boxes and block-count numbers the .scr draws,
    # on "Roof <n> Arr <m> <thing>" layers. Tiling IronRidge's BX / a_Modules /
    # "PV Modules - *" instead spreads sheets over 4,700 ft of roof that carries
    # no ballast callouts.
    "pvLayers": "Roof*Arr*",
    "countLayers": "*Block Count*",
}

if __name__ == "__main__":
    aps_run.IN_OBJ = "mchenry_plan_in.dwg"
    aps_run.OUT_OBJ = "plan_out.dwg"

    if "--skip-upload" not in sys.argv:
        if not SRC.exists():
            raise SystemExit(f"missing {SRC}")
        tok = get_token()[1]["access_token"]
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, aps_run.BUCKET, aps_run.IN_OBJ, SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])

    aps_run.run("PZSEPLAN\n", params=PARAMS, want_output=False)
