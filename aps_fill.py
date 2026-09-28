"""Fill IronRidge's real ACAD_TABLE with our mapped data, via APS.

The PZSEDUMP run confirmed the table is 40 rows x 3 cols in exactly the order of
FIELDS, so tag position == row index and the value column is 2.
"""
from title_block import FIELDS, TAGS
from title_block_data import title_block_values
from aps_run import run

VALUE_COL = 2

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

# Uppercase to match how IronRidge sets their sheet.
cells = []
for row, (_section, label, tag) in enumerate(FIELDS):
    v = str(values.get(tag, "")).upper()
    if v:
        cells.append({"row": row, "col": VALUE_COL, "value": v})

print(f"mapping {len(cells)} of {len(TAGS)} tags into table rows")
for c in cells[:6]:
    print(f"  r{c['row']:>2} <- {c['value']}")
print("  ...")

params = {"table": 0, "cells": cells}
run("PZSEFILL\nPZSESAVE\n", params=params, want_output=True)
