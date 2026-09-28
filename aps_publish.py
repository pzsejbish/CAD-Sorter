"""Publish a generated sheet set to a monochrome PDF and per-sheet PNGs.

This is the deliverable step the pipeline was missing. PZSESLIM only sets
monochrome.ctb as each layout's plot STYLE — it never plots anything, so a run
produced a DWG and nothing else. This plots every sheet through that style, so
the PDF comes out black, and renders a PNG per sheet for quick review.

Batched: one workitem plots up to BATCH layouts, because a single run is cut off
near 100s. The plot script is passed as an INPUT parameter rather than living in
the activity's settings, so the activity is created once and never needs a new
version per batch — activities cap at 100 versions and a per-batch version bump
would burn through that in a couple of runs.

  python aps_publish.py joliet
  python aps_publish.py sollega
"""
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

from aps_client import BASE, get_token, request
import aps_oss

DA = f"{BASE}/da/us-east/v3"
HERE = Path(__file__).parent
SCRATCH = HERE / "_scratch"

BUCKET = "pzse-titleblock-proof-2026"
ENGINE = "Autodesk.AutoCAD+26_0"
ACTIVITY_ID = "PzsePublishSet"
ALIAS = "test"
BATCH = 3
SLOTS = [f"pdf{i+1}" for i in range(BATCH)]

JOBS = {
    "joliet": ("joliet_chain_07.dwg", "joliet"),
    "sollega": ("sollega_chain_10.dwg", "sollega_sdia"),
}


def plot_block(layout, out_name):
    """One -PLOT run. The blank line after the layout name keeps the layout's
    own page setup; omitting it eats the device name and shifts every later
    answer by one prompt."""
    return (
        "-PLOT\n"
        "N\n"
        f"{layout}\n"
        "\n"
        "DWG To PDF.pc3\n"
        f"{out_name}\n"
        "N\n"
        "Y\n"
    )


def upsert_activity(tok, nickname):
    params = {
        "inputFile": {"verb": "get", "required": True, "localName": "input.dwg"},
        "scriptFile": {"verb": "get", "required": True, "localName": "plot.scr"},
    }
    for i, s in enumerate(SLOTS):
        params[s] = {"verb": "put", "required": False,
                     "localName": f"sheet{i+1}.pdf"}
    activity = {
        "id": ACTIVITY_ID,
        "commandLine": [
            '$(engine.path)\\accoreconsole.exe /i "$(args[inputFile].path)" '
            '/al "$(appbundles[PzseTable].path)" '
            '/s "$(args[scriptFile].path)"'
        ],
        "appbundles": [f"{nickname}.PzseTable+{ALIAS}"],
        "parameters": params,
        "engine": ENGINE,
        "limitProcessingTimeSec": 1800,
        "description": "Plot a batch of layouts to PDF",
    }
    st, b = request("POST", f"{DA}/activities", token=tok, data=activity)
    if st == 409:
        spec = {k: v for k, v in activity.items() if k != "id"}
        st, b = request("POST", f"{DA}/activities/{ACTIVITY_ID}/versions",
                        token=tok, data=spec)
        ver = b.get("version") if isinstance(b, dict) else None
        st2, _ = request("PATCH", f"{DA}/activities/{ACTIVITY_ID}/aliases/{ALIAS}",
                         token=tok, data={"version": ver})
        if st2 == 404:
            request("POST", f"{DA}/activities/{ACTIVITY_ID}/aliases",
                    token=tok, data={"id": ALIAS, "version": ver})
    else:
        request("POST", f"{DA}/activities/{ACTIVITY_ID}/aliases",
                token=tok, data={"id": ALIAS, "version": 1})
    return f"{nickname}.{ACTIVITY_ID}+{ALIAS}"


def sheet_key(name):
    """Order S1 first, then S1.N numerically — matching PZSESLIM's SheetKey."""
    if name == "S1":
        return 0
    m = re.match(r"^S1\.(\d+)", name)
    return int(m.group(1)) if m else 10 ** 6


def layouts_of(dwg: Path):
    """Read tab names straight out of the DWG with the local AutoCAD LT."""
    acc = Path(r"C:\Program Files\Autodesk\AutoCAD LT 2026\accoreconsole.exe")
    scr = SCRATCH / "_pub_tabs.scr"
    scr.write_text(
        "FILEDIA\n0\n"
        '(foreach x (dictsearch (namedobjdict) "ACAD_LAYOUT")'
        ' (if (= 3 (car x)) (princ (strcat "\\n@@@L <" (cdr x) ">"))))\n(princ)\n',
        encoding="ascii")
    import subprocess
    # accoreconsole writes wide characters, so the raw bytes carry a NUL between
    # every character. Decoding as text and regexing it finds nothing.
    raw = subprocess.run([str(acc), "/i", str(dwg), "/s", str(scr), "/l", "en-US"],
                         capture_output=True, timeout=900).stdout
    out = raw.decode("latin-1", "ignore").replace("\x00", "")
    names = re.findall(r"@@@L <([^>]*)>", out)
    # accoreconsole echoes the script line itself, so the raw matches include a
    # literal '" (cdr x) "' alongside the real tab names.
    names = [n for n in dict.fromkeys(names)
             if n and n != "Model" and '"' not in n and "(" not in n]
    return sorted(names, key=sheet_key)


def main():
    job = sys.argv[1] if len(sys.argv) > 1 else "joliet"
    if job not in JOBS:
        raise SystemExit(f"unknown job {job!r}; pick one of {list(JOBS)}")
    obj, stem = JOBS[job]
    publish(obj, stem)


def publish(obj, stem):
    """Plot every sheet of _scratch/<obj> to a merged PDF plus per-sheet PNGs."""
    src = SCRATCH / obj
    outdir = SCRATCH / f"{stem}_sheets"
    outdir.mkdir(exist_ok=True)

    print(f"reading layouts from {src.name} ...")
    tabs = layouts_of(src)
    print(f"  {len(tabs)} sheets")

    tok = get_token()[1]["access_token"]
    nickname = request("GET", f"{DA}/forgeapps/me", token=tok)[1]
    qualified = upsert_activity(tok, nickname)

    in_obj = f"{stem}_pub_in.dwg"
    print(f"uploading {src.name} ...")
    st, _ = aps_oss.upload(tok, BUCKET, in_obj, src)
    if st not in (200, 201):
        raise SystemExit(f"upload failed HTTP {st}")

    # A batch that overruns the ~100s limit is split and retried rather than
    # dropped — plot cost per sheet varies enough that a fixed batch size will
    # sometimes overshoot, and a silently missing sheet is the worst outcome.
    made = []
    queue = [tabs[i:i + BATCH] for i in range(0, len(tabs), BATCH)]
    failed = []
    while queue:
        batch = queue.pop(0)
        b0 = tabs.index(batch[0])
        script = "PZSEFREEZE\nFILEDIA\n0\n"
        for i, lay in enumerate(batch):
            script += plot_block(lay, f"sheet{i+1}.pdf")
        sp = SCRATCH / "_pub_plot.scr"
        sp.write_text(script, encoding="ascii")
        scr_obj = f"{stem}_pub_script.scr"
        aps_oss.upload(tok, BUCKET, scr_obj, sp)

        args = {}
        args["inputFile"] = {"url": aps_oss.signed_download_url(tok, BUCKET, in_obj)[1]}
        args["scriptFile"] = {"url": aps_oss.signed_download_url(tok, BUCKET, scr_obj)[1]}
        keys = {}
        for i, lay in enumerate(batch):
            o = f"{stem}_pub_{b0+i:02d}.pdf"
            st, (url, key) = aps_oss.signed_upload_url(tok, BUCKET, o)
            args[SLOTS[i]] = {"url": url, "verb": "put"}
            keys[SLOTS[i]] = (o, key, lay)

        print(f"\n### batch {b0//BATCH + 1}: {', '.join(batch)}")
        st, wi = request("POST", f"{DA}/workitems", token=tok,
                         data={"activityId": qualified, "arguments": args})
        if st not in (200, 201):
            print("  submit failed:", json.dumps(wi)[:300]); continue
        wid, status, report = wi["id"], "?", None
        for _ in range(60):
            time.sleep(4)
            st, s = request("GET", f"{DA}/workitems/{wid}", token=tok)
            status = s.get("status") if isinstance(s, dict) else "?"
            if status not in ("pending", "inprogress"):
                report = s.get("reportUrl"); break
        print(f"  status: {status}")
        if status != "success":
            if len(batch) > 1:
                mid = len(batch) // 2
                print(f"  -> splitting into {mid} + {len(batch)-mid} and retrying")
                queue.insert(0, batch[mid:])
                queue.insert(0, batch[:mid])
                continue
            failed.append(batch[0])
            if report:
                with urllib.request.urlopen(report, timeout=120) as r:
                    log = r.read().decode("utf-8", "replace")
                for line in log.splitlines():
                    if "rror" in line or "Cannot" in line:
                        print("   ", line.strip()[:160])
            continue

        for slot, (o, key, lay) in keys.items():
            aps_oss.finalize_upload(tok, BUCKET, o, key)
            st, url = aps_oss.signed_download_url(tok, BUCKET, o)
            if st != 200:
                print(f"  {lay}: no PDF"); continue
            safe = re.sub(r"[^A-Za-z0-9._+-]", "_", lay)
            dest = outdir / f"{safe}.pdf"
            n = aps_oss.download_to(url, dest)
            made.append((lay, dest))
            print(f"  {lay:28s} {n/1024:5.0f} KB")

    if not made:
        raise SystemExit("nothing plotted")

    # ---- merge + render, locally ------------------------------------------
    import fitz
    made.sort(key=lambda t: sheet_key(t[0]))
    book = fitz.open()
    for lay, p in made:
        with fitz.open(p) as one:
            book.insert_pdf(one)
    merged = SCRATCH / f"{stem}_SHEETSET.pdf"
    book.save(merged)
    print(f"\nmerged -> {merged.name}  ({len(made)} pages, "
          f"{merged.stat().st_size/1024/1024:.1f} MB)")
    # Never let a missing sheet pass as a complete set.
    if len(made) != len(tabs):
        print(f"  !! {len(tabs) - len(made)} of {len(tabs)} sheets did NOT plot")
        for f in failed:
            print(f"     missing: {f}")

    pngdir = outdir / "png"
    pngdir.mkdir(exist_ok=True)
    for i, (lay, p) in enumerate(made):
        with fitz.open(p) as one:
            safe = re.sub(r"[^A-Za-z0-9._+-]", "_", lay)
            one[0].get_pixmap(dpi=100).save(pngdir / f"{safe}.png")
    print(f"rendered {len(made)} PNGs -> {pngdir}")


if __name__ == "__main__":
    main()
