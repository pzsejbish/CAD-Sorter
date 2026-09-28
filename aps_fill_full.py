"""Fill the title table on the FULL McHenry sheet set, matching by label.

Differences from the earlier proof:
  - input is the whole IronRidge DWG, not the extracted TITLETABLE block, so the
    logo / address / FINAL LAYOUT text / DRAWING INFO are all present
  - cells are matched by their LABEL, not row index, so table order doesn't matter
  - the plugin clears each cell's FIELD before writing, and recomputes the table
    block, which is what the #### was about
"""
import json
from pathlib import Path

from aps_client import get_token
import aps_oss
import aps_run
from title_block import FIELDS
from title_block_data import title_block_values

HERE = Path(__file__).parent
FULL_DWG = HERE / "_scratch" / "mchenry.dwg"
FULL_OBJ = "mchenry_full.dwg"

payload = {"result": {
    "project_name": "McHenry West",
    "client_company_name": "Allied Facility Partners",
    "asce_version": "ASCE 7-16",
    "project_address_line1": "4716 W Crystal Lake Rd",
    "project_address_line2": "McHenry, IL, 60050",
    "project_state_code": "IL",
    "ground_snow_load": 25,
    "wind_speed": 114,
    "exposure_category": "C",
    "design_spectral_acceleration_sds": 0.114,
    "risk_category": "III",
    "building_height": 50,
    "parapet_height": 12,
    "highest_roof_pitch": 1,
    "panel_length": 89.69,
    "panel_width": 44.65,
    "pv_module_weight": 70.55,
    "block_weight": 15.5,
}}

TRAY_WEIGHT = 4.75
result_geojson = {"features": [
    {"properties": {
        "type": "optimizer_result",
        "psf": 9.8,
        "psf_breakdown": {
            "num_panels": 1547, "num_trays": 2601, "num_supp_bays": 0,
            "num_blocks": 16545, "num_anchors": 0,
            "module_weight": 1547 * 70.55,
            "tray_weight": 2601 * TRAY_WEIGHT,
            "block_weight": 16545 * 15.5,
            "anchor_weight": 0,
        },
    }},
]}

values = title_block_values(payload, result_geojson)

# Match on IronRidge's own label text, which our FIELDS list mirrors verbatim.
by_label = {}
for _section, label, tag in FIELDS:
    v = str(values.get(tag, "")).upper()
    if v:
        by_label[label] = v

print(f"filling {len(by_label)} labelled rows")

if not FULL_DWG.exists():
    raise SystemExit(f"missing {FULL_DWG} — re-copy from the W: drive")

st, body = get_token()
tok = body["access_token"]
size_mb = FULL_DWG.stat().st_size / 1024 / 1024
print(f"uploading full sheet set: {FULL_DWG.name} ({size_mb:.1f} MB) ...")
st, b = aps_oss.upload(tok, aps_run.BUCKET, FULL_OBJ, FULL_DWG)
print(f"  upload -> HTTP {st}")
if st not in (200, 201):
    raise SystemExit(str(b)[:400])

# Point the runner at the full drawing.
aps_run.IN_OBJ = FULL_OBJ
aps_run.OUT_OBJ = "mchenry_filled.dwg"

params = {"labelCol": 1, "valueCol": 2, "byLabel": by_label}
aps_run.run("PZSEFILL\nPZSESAVE\n", params=params, want_output=True)
