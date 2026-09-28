"""Inspect the viewports on the generated sheets — why do they all look alike?"""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

SRC = Path(__file__).parent / "_scratch" / "mchenry_tiled_v2.dwg"

if __name__ == "__main__":
    aps_run.IN_OBJ = "mchenry_tiled_in.dwg"
    aps_run.OUT_OBJ = "vpinfo_out.dwg"

    if "--skip-upload" not in sys.argv:
        tok = get_token()[1]["access_token"]
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, aps_run.BUCKET, aps_run.IN_OBJ, SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])

    aps_run.run("PZSEVPINFO\n", want_output=False)
