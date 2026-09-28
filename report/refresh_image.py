"""Swap the embedded render in report.html for the current one."""
import re
from pathlib import Path

HERE = Path(__file__).parent
page = HERE / "report.html"
uri = (HERE.parent / "_scratch" / "filled_result_b64.txt").read_text(
    encoding="utf-8").strip()

before = (HERE.parent / "_scratch" / "before_titleblock_b64.txt").read_text(
    encoding="utf-8").strip()

html = page.read_text(encoding="utf-8")

# The filled-table render, assigned to #shot at the end of the script block.
html, n = re.subn(
    r'(getElementById\("shot"\)\.src = ")[^"]*(")',
    lambda m: m.group(1) + uri + m.group(2), html)
if n != 1:
    raise SystemExit(f"expected 1 #shot assignment, found {n}")

# IronRidge's issued title block.
if 'getElementById("before")' not in html:
    html = html.replace(
        'document.getElementById("shot").src',
        'document.getElementById("before").src = "__BEFORE__";\n'
        '  document.getElementById("shot").src', 1)
html, n = re.subn(
    r'(getElementById\("before"\)\.src = ")[^"]*(")',
    lambda m: m.group(1) + before + m.group(2), html)
if n != 1:
    raise SystemExit(f"expected 1 #before assignment, found {n}")

page.write_text(html, encoding="utf-8")
print(f"images refreshed; page is {page.stat().st_size // 1024} KB")
