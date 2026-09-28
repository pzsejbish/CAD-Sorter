"""Generate the sheet set in chunks, chaining each run's output into the next.

Activating a layout is required for its viewports to render, and that regen
costs ~6s on this drawing. Seventeen sheets therefore cannot finish inside the
free tier's 60s limit, so the run is split and the intermediate DWG stays in the
bucket between chunks — no download/upload round trip.
"""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

SRC = Path(__file__).parent / "_scratch" / "mchenry_filled_new.dwg"
CHUNK = int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 6
TOTAL = 17                      # key plan + 16 array sheets

BASE = {
    "floorIn": 0.125,
    "gapFt": 6.0,
    "maxPerSheet": 2,
    "template": "SR100 (2)",
    "countLayers": "*Block Count*",
}

if __name__ == "__main__":
    tok = get_token()[1]["access_token"]

    if "--skip-upload" not in sys.argv:
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, aps_run.BUCKET, "sheetchain_00.dwg", SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])

    start = 0
    step = 0
    while start < TOTAL:
        n = min(CHUNK, TOTAL - start)
        aps_run.IN_OBJ = f"sheetchain_{step:02d}.dwg"
        aps_run.OUT_OBJ = f"sheetchain_{step+1:02d}.dwg"
        params = dict(BASE, startAt=start, count=n)
        print(f"\n### chunk {step+1}: sheets [{start}, {start+n}) "
              f"{aps_run.IN_OBJ} -> {aps_run.OUT_OBJ}")
        log = aps_run.run("PZSEMAKE\nPZSESAVE\n", params=params,
                          want_output=True, show=("MAKE-DONE", "FAIL", "chunk:"))
        if not log or "MAKE-DONE" not in log:
            raise SystemExit(f"chunk {step+1} did not complete; stopping")
        start += n
        step += 1

    print(f"\nfinal object: sheetchain_{step:02d}.dwg")
