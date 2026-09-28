"""What does ezdxf give us for (a) measuring text width and (b) multiline ATTRIBs?"""
import ezdxf

print("ezdxf", ezdxf.__version__)

# --- (a) font measurement -----------------------------------------------------
print("\n--- font metrics API ---")
try:
    from ezdxf import fonts
    print("  ezdxf.fonts: OK")
    ff = fonts.FontFace(family="Arial")
    font = fonts.make_font(ff, cap_height=0.10)
    print("  make_font ->", type(font).__name__)
    for s in ["ALLIED FACILITY PARTNERS", "4716 W Crystal Lake Rd", "IL", "121,496"]:
        print(f"    {s!r:28} width={font.text_width(s):.4f}")
except Exception as e:
    print("  ezdxf.fonts failed:", type(e).__name__, e)

try:
    from ezdxf.tools import text_size
    print("  ezdxf.tools.text_size: OK ->", [n for n in dir(text_size) if not n.startswith('_')][:8])
except Exception as e:
    print("  ezdxf.tools.text_size:", type(e).__name__, e)

# --- (b) multiline / MTEXT attributes ----------------------------------------
print("\n--- ATTRIB multiline support ---")
doc = ezdxf.new(dxfversion="R2013", setup=True)
blk = doc.blocks.new("T")
ad = blk.add_attdef(tag="X", text="", height=0.1)
print("  attdef has set_mtext:", hasattr(ad, "set_mtext"))
print("  attdef has embedded mtext flag:", hasattr(ad, "has_embedded_mtext_entity"))
print("  attdef mtext-ish members:", [n for n in dir(ad) if "mtext" in n.lower()])

msp = doc.modelspace()
ref = msp.add_blockref("T", (0, 0))
ref.add_auto_attribs({"X": "one"})
at = ref.attribs[0]
print("  attrib mtext-ish members:", [n for n in dir(at) if "mtext" in n.lower()])

# Can an ATTRIB hold an embedded MTEXT with a wrap width?
try:
    m = at.set_mtext(msp.add_mtext("ALLIED FACILITY PARTNERS"))
    print("  set_mtext() succeeded:", m is not None or True)
    print("  now has_embedded_mtext_entity:", at.has_embedded_mtext_entity)
except Exception as e:
    print("  set_mtext failed:", type(e).__name__, e)

# --- (c) MTEXT wrapping helper ------------------------------------------------
print("\n--- MTEXT wrap helpers ---")
try:
    from ezdxf.tools import text as text_tools
    names = [n for n in dir(text_tools) if "wrap" in n.lower() or "split" in n.lower()]
    print("  ezdxf.tools.text wrap-ish:", names)
except Exception as e:
    print("  ezdxf.tools.text:", type(e).__name__, e)
