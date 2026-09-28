"""Generate the tiled sheets: one cloned SR100 layout per tile.

Start capped (maxSheets) so a trial run stays inside the 60s processing limit
and we can look at the result before committing to the full set.
"""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

SRC = Path(__file__).parent / "_scratch" / "mchenry_filled_new.dwg"

MAX = int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 6

PARAMS = {
    "floorIn": 0.125,
    "template": "SR100 (2)",
    "pvLayers": "Roof*Arr*",
    "countLayers": "*Block Count*",
    "sheetPrefix": "PZ-",
    "maxSheets": MAX,
}

if __name__ == "__main__":
    aps_run.IN_OBJ = "mchenry_plan_in.dwg"
    aps_run.OUT_OBJ = "mchenry_tiled.dwg"

    if "--skip-upload" not in sys.argv:
        tok = get_token()[1]["access_token"]
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, aps_run.BUCKET, aps_run.IN_OBJ, SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])

    print(f"generating up to {MAX} sheets")
    aps_run.run("PZSETILE\nPZSESAVE\n", params=PARAMS, want_output=True)
