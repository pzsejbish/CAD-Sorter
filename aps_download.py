"""Pull an already-produced output object out of the bucket (no re-run)."""
import sys
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run

obj = sys.argv[1] if len(sys.argv) > 1 else "mchenry_filled.dwg"
dest = Path(__file__).parent / "_scratch" / obj

tok = get_token()[1]["access_token"]
st, url = aps_oss.signed_download_url(tok, aps_run.BUCKET, obj)
if st != 200:
    raise SystemExit(f"no such object {obj!r}: HTTP {st} {str(url)[:300]}")

try:
    n = aps_oss.download_to(url, dest)
except PermissionError:
    # Usually means it is open in CAD; write alongside rather than fail.
    dest = dest.with_name(dest.stem + "_v2" + dest.suffix)
    n = aps_oss.download_to(url, dest)
    print("  (original locked; wrote a copy)")
print(f"downloaded {n/1024/1024:.1f} MB -> {dest}")
