"""Attribute-driven title block for the ballast DXF export.

The panel is a BLOCK whose value cells are ATTDEFs, so engineers can correct a
value in CAD without touching geometry. Values that overrun their column wrap
and the row grows to fit — matching IronRidge's ACAD_TABLE behaviour, whose own
row heights read 0.25" base and ~0.317" on the rows that wrapped.

Because row heights depend on content, the block is laid out per export rather
than being a fixed hand-drafted template. Layout is driven by the FIELDS table
and the column constants below.

Geometry mirrors IronRidge's TITLETABLE, read out of their DWG:
    3 columns @ 0.3125" / 1.744" / 1.633"   (3.69" total)
    rows       0.25" minimum, growing per wrapped line
"""

from typing import Callable, NamedTuple, Optional

import ezdxf
from ezdxf.enums import TextEntityAlignment
from ezdxf.fonts import fonts
from ezdxf.tools.text import text_wrap

# ── Grid, from IronRidge's table ──────────────────────────────────────────────
COL_SECTION = 0.3125
COL_LABEL = 1.74446324707199
COL_VALUE = 1.63293315505598
PANEL_W = COL_SECTION + COL_LABEL + COL_VALUE

ROW_H_MIN = 0.25          # base row height, IronRidge's 0.25"
# 0.090" cap height: the widest label, "EQUIPMENT WEIGHT [LB]", measures 1.544"
# against a 1.644" label cell — the largest size where all 40 labels fit on one
# line, as IronRidge's do, with ~0.1" to spare. At 0.100" three labels overran.
TEXT_H = 0.090
# 1.15 reproduces IronRidge's grown-row height: a 2-line row comes out
# 2*(0.090*1.15) + 2*0.055 = 0.317", against the 0.3174" measured in their DWG.
LINE_SPACING = 1.15       # multiple of TEXT_H between wrapped lines
PAD_X = 0.05              # horizontal cell padding
PAD_Y = 0.055             # vertical padding, top and bottom

FONT_FILE = "arial.ttf"

BLOCK_NAME = "PZSE_TITLE_PANEL"
LAYER = "TITLEBLOCK"

# ── The field vocabulary ──────────────────────────────────────────────────────
# (section, label, tag). Label text matches IronRidge's sheet verbatim, including
# their "**" convention marking installer-review fields. The tag is our stable
# contract — a different manufacturer's block reuses the tags and re-lays-out the
# labels, so exporter code never changes.
FIELDS = [
    ("PROJECT", "PROJECT REF. NO.",      "PROJECT_REF_NO"),
    ("PROJECT", "INSTALLER",             "INSTALLER"),
    ("PROJECT", "PROJECT OWNER",         "PROJECT_OWNER"),
    ("PROJECT", "CODE SET",              "CODE_SET"),
    ("PROJECT", "SYSTEM SIZE [KW DC]",   "SYSTEM_SIZE_KWDC"),

    ("SITE", "SITE ADDRESS",             "SITE_ADDRESS"),
    ("SITE", "CITY",                     "CITY"),
    ("SITE", "STATE",                    "STATE"),
    ("SITE", "ZIP",                      "ZIP"),
    ("SITE", "**SNOW LOAD [PSF]",        "SNOW_LOAD_PSF"),
    ("SITE", "**WIND SPEED [MPH]",       "WIND_SPEED_MPH"),
    ("SITE", "**WIND EXPOSURE CAT.",     "WIND_EXPOSURE_CAT"),
    ("SITE", "SEISMIC DESIGN CAT.",      "SEISMIC_DESIGN_CAT"),
    ("SITE", "SEISMIC SdS",              "SEISMIC_SDS"),

    ("BUILDING", "**RISK CATEGORY",      "RISK_CATEGORY"),
    ("BUILDING", "**DECK HEIGHT [FT]",   "DECK_HEIGHT_FT"),
    ("BUILDING", "**PARAPET HEIGHT [IN]", "PARAPET_HEIGHT_IN"),
    ("BUILDING", "**ROOF SLOPE [DEG.]",  "ROOF_SLOPE_DEG"),
    ("BUILDING", "**ROOFING MATERIAL",   "ROOFING_MATERIAL"),

    ("MODULE", "**MANUFACTURER",         "MODULE_MANUFACTURER"),
    ("MODULE", "**MODEL",                "MODULE_MODEL"),
    ("MODULE", "**LENGTH [IN]",          "MODULE_LENGTH_IN"),
    ("MODULE", "**WIDTH [IN]",           "MODULE_WIDTH_IN"),
    ("MODULE", "**THICKNESS [IN]",       "MODULE_THICKNESS_IN"),
    ("MODULE", "**WEIGHT [LB]",          "MODULE_WEIGHT_LB"),
    ("MODULE", "**WATTAGE CLASS [W]",    "MODULE_WATTAGE_W"),

    ("SYSTEM", "ARRAY AZIMUTH [DEG.]",   "ARRAY_AZIMUTH_DEG"),
    ("SYSTEM", "**TILT ANGLE [DEG.]",    "TILT_ANGLE_DEG"),
    ("SYSTEM", "ROW SPACING [IN]",       "ROW_SPACING_IN"),
    ("SYSTEM", "INTERROW SPACING [IN]",  "INTERROW_SPACING_IN"),
    ("SYSTEM", "**CMU WEIGHT [LB]",      "CMU_WEIGHT_LB"),
    ("SYSTEM", "**CMU DIM.'S [NOM. IN]", "CMU_DIMS_IN"),
    ("SYSTEM", "EQUIPMENT WEIGHT [LB]",  "EQUIPMENT_WEIGHT_LB"),
    ("SYSTEM", "BALLAST WEIGHT [LB]",    "BALLAST_WEIGHT_LB"),
    ("SYSTEM", "TOTAL WEIGHT [LB]",      "TOTAL_WEIGHT_LB"),
    ("SYSTEM", "**DEAD LOAD [PSF]",      "DEAD_LOAD_PSF"),

    ("TOTALS", "**MODULE COUNT",         "MODULE_COUNT"),
    ("TOTALS", "**CHASSIS COUNT",        "CHASSIS_COUNT"),
    ("TOTALS", "BALLAST COUNT",          "BALLAST_COUNT"),
    ("TOTALS", "ANCHOR COUNT",           "ANCHOR_COUNT"),
]

TAGS = [t for _, _, t in FIELDS]


class Row(NamedTuple):
    """One laid-out row: wrapped label/value lines and the height they need."""
    section: str
    label_lines: list
    value_lines: list
    tag: str
    height: float


def _font(cap_height: float = TEXT_H):
    return fonts.make_font(FONT_FILE, cap_height=cap_height)


def _wrap(text: str, box_w: float, width_of: Callable[[str], float]) -> list:
    if not text:
        return [""]
    return text_wrap(text, box_w, width_of) or [""]


def layout_rows(values: Optional[dict] = None) -> list:
    """Wrap every cell and compute each row's height.

    A row is as tall as its tallest cell needs, floored at ROW_H_MIN, so short
    rows stay on IronRidge's 0.25" rhythm and only overrun rows grow.
    """
    values = values or {}
    font = _font()
    label_w = COL_LABEL - 2 * PAD_X
    value_w = COL_VALUE - 2 * PAD_X
    line_h = TEXT_H * LINE_SPACING

    rows = []
    for section, label, tag in FIELDS:
        label_lines = _wrap(label, label_w, font.text_width)
        value_lines = _wrap(str(values.get(tag, "")), value_w, font.text_width)
        n = max(len(label_lines), len(value_lines))
        height = max(ROW_H_MIN, n * line_h + 2 * PAD_Y)
        rows.append(Row(section, label_lines, value_lines, tag, height))
    return rows


def _row_tops(rows: list) -> list:
    """Y of each row's top edge, panel origin at bottom-left."""
    total = sum(r.height for r in rows)
    tops, y = [], total
    for r in rows:
        tops.append(y)
        y -= r.height
    return tops


def build_template_block(doc, rows: Optional[list] = None,
                         name: str = BLOCK_NAME):
    """Define the title panel block. Origin = bottom-left of the panel.

    `rows` comes from layout_rows(values); pass the same list to
    insert_title_block so geometry and attribute placement agree.
    """
    rows = rows if rows is not None else layout_rows()
    if name in doc.blocks:
        del doc.blocks[name]
    if LAYER not in doc.layers:
        doc.layers.new(LAYER, dxfattribs={"color": 7})

    blk = doc.blocks.new(name=name)
    tops = _row_tops(rows)
    height = sum(r.height for r in rows)
    line_h = TEXT_H * LINE_SPACING
    x_label = COL_SECTION
    x_value = COL_SECTION + COL_LABEL

    # Outer frame + column rules
    blk.add_lwpolyline(
        [(0, 0), (PANEL_W, 0), (PANEL_W, height), (0, height)],
        close=True, dxfattribs={"layer": LAYER},
    )
    for x in (x_label, x_value):
        blk.add_line((x, 0), (x, height), dxfattribs={"layer": LAYER})

    def place_lines(lines, x, top, row_h):
        """Top-align a wrapped stack within its cell."""
        block_h = len(lines) * line_h
        y = top - (row_h - block_h) / 2 - line_h / 2
        for ln in lines:
            yield ln, (x, y)
            y -= line_h

    for row, top in zip(rows, tops):
        if top < height:  # rule above every row but the first
            blk.add_line((x_label, top), (PANEL_W, top),
                         dxfattribs={"layer": LAYER})

        for ln, pos in place_lines(row.label_lines, x_label + PAD_X, top, row.height):
            blk.add_text(ln, height=TEXT_H, dxfattribs={"layer": LAYER}) \
               .set_placement(pos, align=TextEntityAlignment.MIDDLE_LEFT)

        # One ATTDEF per row. Multi-line values carry an embedded MTEXT so the
        # cell stays a single editable attribute rather than stacked text.
        n_val = len(row.value_lines)
        block_h = n_val * line_h
        y_first = top - (row.height - block_h) / 2 - line_h / 2
        attdef = blk.add_attdef(
            tag=row.tag, text="", height=TEXT_H,
            dxfattribs={"layer": LAYER},
        )
        attdef.set_placement((x_value + PAD_X, y_first),
                             align=TextEntityAlignment.MIDDLE_LEFT)

    # Section labels: rotated, centred on the rows they span
    i, n = 0, len(rows)
    while i < n:
        section = rows[i].section
        j = i
        while j < n and rows[j].section == section:
            j += 1
        y_top = tops[i]
        y_bot = tops[j - 1] - rows[j - 1].height
        if y_top < height:
            blk.add_line((0, y_top), (COL_SECTION, y_top),
                         dxfattribs={"layer": LAYER})
        blk.add_text(
            section, height=TEXT_H * 0.85,
            dxfattribs={"layer": LAYER, "rotation": 90},
        ).set_placement((COL_SECTION / 2, (y_top + y_bot) / 2),
                        align=TextEntityAlignment.MIDDLE_CENTER)
        i = j

    return blk


def insert_title_block(layout, values: dict, rows: Optional[list] = None,
                       insert=(0.0, 0.0), name: str = BLOCK_NAME):
    """Insert the panel and fill its attributes.

    Values that wrapped get an embedded MTEXT with the column width set, so CAD
    renders the break and the cell remains one attribute.
    """
    rows = rows if rows is not None else layout_rows(values)
    ref = layout.add_blockref(name, insert, dxfattribs={"layer": LAYER})
    ref.add_auto_attribs({t: str(values.get(t, "")) for t in TAGS})

    by_tag = {r.tag: r for r in rows}
    line_h = TEXT_H * LINE_SPACING
    for attrib in ref.attribs:
        row = by_tag.get(attrib.dxf.tag)
        n = len(row.value_lines) if row else 1
        if not row or n < 2:
            continue
        # The ATTDEF sits at the centre of the FIRST line, which is what a
        # single-line value wants. A middle-anchored MTEXT instead wants the
        # centre of the whole stack, so drop it by half the extra leading —
        # otherwise the wrapped text rides high and leaves a gap beneath.
        x, y, z = attrib.dxf.insert
        insert = (x, y - (n - 1) * line_h / 2.0, z)
        mtext = layout.add_mtext(
            r"\P".join(row.value_lines),
            dxfattribs={
                "layer": LAYER,
                "char_height": TEXT_H,
                "width": COL_VALUE - 2 * PAD_X,
                "line_spacing_factor": LINE_SPACING / 1.6,
                "insert": insert,
                "attachment_point": 4,  # middle-left
            },
        )
        attrib.set_mtext(mtext)
        mtext.destroy()
    return ref
