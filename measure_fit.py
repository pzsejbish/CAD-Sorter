"""Pick a text height where labels fit their column, as IronRidge's do."""
from ezdxf.fonts import fonts

from title_block import FIELDS, COL_LABEL, COL_VALUE, PAD_X

label_w = COL_LABEL - 2 * PAD_X
value_w = COL_VALUE - 2 * PAD_X
print(f'label cell inner width: {label_w:.3f}"   value cell: {value_w:.3f}"')

print("\n--- label fit by cap height ---")
for h in (0.100, 0.095, 0.090, 0.085, 0.080, 0.075):
    f = fonts.make_font("arial.ttf", cap_height=h)
    over = [lbl for _s, lbl, _t in FIELDS if f.text_width(lbl) > label_w]
    widest = max(FIELDS, key=lambda r: f.text_width(r[1]))
    print(f"  cap={h:.3f}  widest={f.text_width(widest[1]):.3f}\" "
          f"({widest[1]!r})  overflowing={len(over)}")
    for lbl in over[:4]:
        print(f"      over: {lbl!r} = {f.text_width(lbl):.3f}\"")

print("\n--- value fit at candidate heights ---")
SAMPLES = [
    "Allied Facility Partners",
    "ALLIED FACILITY PARTNERS",
    "4716 W Crystal Lake Rd",
    "McHenry West",
    "SRP-590-BTA-BG",
    "377,943",
    "9.8 (MAX)",
]
for h in (0.090, 0.085, 0.080):
    f = fonts.make_font("arial.ttf", cap_height=h)
    print(f"  cap={h:.3f}")
    for s in SAMPLES:
        w = f.text_width(s)
        print(f"    {s!r:28} {w:.3f}\"  {'fits' if w <= value_w else 'WRAPS'}")
