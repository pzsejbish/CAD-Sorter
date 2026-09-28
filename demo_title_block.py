"""End-to-end proof: payload -> populated title block -> DXF -> PNG.

Uses the real McHenry West numbers so the output can be eyeballed against
IronRidge's actual sheet (W:\\2026 PV Projects\\McHenry West26_02068).

Run:  python demo_title_block.py
Outputs title_block_demo.dxf (open in AutoCAD LT) and title_block_demo.png.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ezdxf
from ezdxf.addons.drawing import RenderContext, Frontend
from ezdxf.addons.drawing.matplotlib import MatplotlibBackend
import matplotlib.pyplot as plt

from title_block import (build_template_block, insert_title_block, layout_rows,
                         TAGS, PANEL_W, FIELDS, ROW_H_MIN)
from title_block_data import title_block_values, GAPS

OUT = os.path.dirname(os.path.abspath(__file__))

# A payload shaped exactly like runBallastCalc.ts emits, with McHenry values.
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

# A stand-in optimizer result in the shape build_dxf already parses, carrying
# the psf_breakdown that UnifiedDataManager.calculate_system_psf emits.
# Quantities are McHenry's; tray_weight 4.75 lb reproduces IronRidge's total.
TRAY_WEIGHT = 4.75
result_geojson = {"features": [
    {"properties": {
        "type": "optimizer_result",
        "psf": 9.8,
        "psf_breakdown": {
            "num_panels": 1547,
            "num_trays": 2601,
            "num_supp_bays": 0,
            "num_blocks": 16545,
            "num_anchors": 0,
            "module_weight": 1547 * 70.55,
            "tray_weight": 2601 * TRAY_WEIGHT,
            "block_weight": 16545 * 15.5,
            "anchor_weight": 0,
        },
    }},
]}

values = title_block_values(payload, result_geojson)

filled = [t for t in TAGS if values.get(t)]
blank = [t for t in TAGS if not values.get(t)]
print(f"tags total : {len(TAGS)}")
print(f"populated  : {len(filled)}")
print(f"blank      : {len(blank)}")
print("\n--- populated ---")
for _sec, label, tag in FIELDS:
    if values.get(tag):
        print(f"  {label:26} {values[tag]}")
print("\n--- blank (reason) ---")
for t in blank:
    print(f"  {t:22} {GAPS.get(t, '(no source)')}")

# Cross-check against IronRidge's published sheet for McHenry West.
EXPECTED = {
    "EQUIPMENT_WEIGHT_LB": "121,496",
    "BALLAST_WEIGHT_LB":   "256,448",
    "TOTAL_WEIGHT_LB":     "377,943",
    "DEAD_LOAD_PSF":       "9.8 (MAX)",
    "MODULE_COUNT":        "1,547",
    "CHASSIS_COUNT":       "2,601",
    "BALLAST_COUNT":       "16,545",
    "ANCHOR_COUNT":        "0",
}
print("\n--- cross-check vs IronRidge sheet ---")
bad = 0
for tag, want in EXPECTED.items():
    got = values.get(tag, "")
    mark = "OK " if got == want else "BAD"
    if got != want:
        bad += 1
    print(f"  {mark} {tag:22} got={got!r:14} want={want!r}")
print(f"  => {len(EXPECTED) - bad}/{len(EXPECTED)} match")

# Lay out rows against the actual values, so overrun rows grow.
#
# Casing matters here and is an open decision: IronRidge sets values in caps,
# which is what pushed INSTALLER and SITE ADDRESS onto two lines on their sheet.
# Show both so the wrap path is exercised either way.
def report_growth(vals, note):
    rs = layout_rows(vals)
    grown = [(r.tag, len(r.value_lines), round(r.height, 4))
             for r in rs if r.height > ROW_H_MIN + 1e-9]
    print(f"\n--- rows that grew, {note} ({len(grown)} of {len(rs)}) ---")
    for tag, lines, h in grown:
        print(f"  {tag:22} value lines={lines}  height={h}\"")
    if not grown:
        print("  (none — every value fits on one line)")
    print(f"  panel height: {sum(r.height for r in rs):.3f}\" "
          f"(uniform would be {len(rs) * ROW_H_MIN:.3f}\")")
    return rs


report_growth(values, "as entered (mixed case)")
upper = {k: (v.upper() if isinstance(v, str) else v) for k, v in values.items()}
rows = report_growth(upper, "UPPERCASED, as IronRidge sets them")

# Build the sheet from the uppercased values: that's how IronRidge sets theirs,
# and it exercises the wrapping / embedded-MTEXT path. Casing is still an open
# decision — flip this line to `values` for as-entered.
values = upper

# Build the DXF.
# R2018 (AC1032), not R2013: multi-line attributes are stored as an MTEXT
# embedded in the ATTRIB, which is an AC1032 feature. On R2013 the embedded
# entity is dropped on export and wrapped values silently truncate to their
# first line. IronRidge's own DWG is AC1032, so this matches what they use.
doc = ezdxf.new(dxfversion="R2018", setup=True)
build_template_block(doc, rows)
psp = doc.layout("Layout1")
psp.page_setup(size=(36, 24), margins=(1, 1, 1, 1), units="inch")
insert_title_block(psp, values, rows, insert=(31.0, 11.5))
dxf_path = os.path.join(OUT, "title_block_demo.dxf")
try:
    doc.saveas(dxf_path)
except PermissionError:
    # Usually means the file is open in AutoCAD. Fall back rather than fail the
    # whole run, so the checks above still report.
    dxf_path = os.path.join(OUT, "title_block_demo_new.dxf")
    doc.saveas(dxf_path)
    print("\n!! title_block_demo.dxf is locked (open in CAD?) — wrote a copy")
print(f"\nwrote {dxf_path}")

# Round-trip check: reopen and read the attributes back out. Multi-line values
# come back via the embedded MTEXT, so compare on plain text with breaks removed.
doc2 = ezdxf.readfile(dxf_path)
ref2 = doc2.layout("Layout1").query("INSERT").first
back = {}
for a in ref2.attribs:
    if a.has_embedded_mtext_entity:
        back[a.dxf.tag] = " ".join(a.plain_mtext().split())
    else:
        back[a.dxf.tag] = a.dxf.text
mismatch = [t for t in TAGS
            if " ".join(back.get(t, "").split()) != " ".join(str(values.get(t, "")).split())]
print(f"\nround-trip attributes match: {not mismatch}  "
      f"({len(back)} attribs read back)")
if mismatch:
    for t in mismatch:
        print(f"  MISMATCH {t}: got={back.get(t)!r} want={values.get(t)!r}")
embedded = [a.dxf.tag for a in ref2.attribs if a.has_embedded_mtext_entity]
print(f"attribs carrying embedded MTEXT: {embedded}")

# Render just the panel to PNG
panel_h = sum(r.height for r in rows)
fig = plt.figure(figsize=(5.5, 14))
ax = fig.add_axes([0, 0, 1, 1])
Frontend(RenderContext(doc), MatplotlibBackend(ax)).draw_layout(
    doc.layout("Layout1"), finalize=True)
ax.set_xlim(30.8, 31.2 + PANEL_W)
ax.set_ylim(11.3, 11.6 + panel_h)
png_path = os.path.join(OUT, "title_block_demo.png")
try:
    fig.savefig(png_path, dpi=170)
except PermissionError:
    png_path = os.path.join(OUT, "title_block_demo_new.png")
    fig.savefig(png_path, dpi=170)
print(f"wrote {png_path}")
