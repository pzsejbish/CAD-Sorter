"""Plot one generated sheet to PDF so the result can actually be inspected.

Plots timed out before because the traced-PDF underlay (~500k entities) had to
regenerate. PZSEFREEZE removes it from the regen, which should bring the plot
inside the processing limit.
"""
import json
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
IN_OBJ = "sheetset_for_plot.dwg"
OUT_OBJ = "sheet.pdf"
ENGINE = "Autodesk.AutoCAD+26_0"
ACTIVITY_ID = "PzsePlotSheet"
ALIAS = "test"

LAYOUT = sys.argv[1] if len(sys.argv) > 1 else "S1.1"
SRC = SCRATCH / "mchenry_sheets_v11.dwg"

SCRIPT = (
    "PZSEFREEZE\n"
    "FILEDIA\n0\n"
    "-PLOT\n"
    "N\n"                 # detailed plot configuration?
    f"{LAYOUT}\n"         # layout name
    "\n"                  # page setup name — none; keep the layout's own.
                          # Omitting this ate the device name and shifted every
                          # later answer by one prompt, which read as a timeout.
    "DWG To PDF.pc3\n"    # output device
                          # No "write to file?" prompt here: a PDF plotter always
                          # writes to one, so the next prompt is the name itself
                          # and a stray Y would be taken as the filename.
    "output.pdf\n"        # localName of the output parameter
    "N\n"                 # save changes to page setup
    "Y\n"                 # proceed with plot
    "FILEDIA\n1\n"
)


def main():
    tok = get_token()[1]["access_token"]
    nickname = request("GET", f"{DA}/forgeapps/me", token=tok)[1]

    if "--skip-upload" not in sys.argv:
        print(f"uploading {SRC.name} ({SRC.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, BUCKET, IN_OBJ, SRC)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:300])

    activity = {
        "id": ACTIVITY_ID,
        "commandLine": [
            '$(engine.path)\\accoreconsole.exe /i "$(args[inputFile].path)" '
            '/al "$(appbundles[PzseTable].path)" '
            '/s "$(settings[script].path)"'
        ],
        "appbundles": [f"{nickname}.PzseTable+{ALIAS}"],
        "parameters": {
            "inputFile": {"verb": "get", "required": True, "localName": "input.dwg"},
            "outputFile": {"verb": "put", "required": True, "localName": "output.pdf"},
        },
        "settings": {"script": SCRIPT},
        "engine": ENGINE,
        # -PLOT was cut off at a flat 60s. Plotting a full sheet legitimately
        # takes longer than that even with the underlay gone.
        "limitProcessingTimeSec": 1800,
        "description": "Plot one layout to PDF with the underlay frozen",
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
        print(f"  activity v{ver}")
    else:
        request("POST", f"{DA}/activities/{ACTIVITY_ID}/aliases",
                token=tok, data={"id": ALIAS, "version": 1})
        print(f"  activity created -> HTTP {st}")

    st, in_url = aps_oss.signed_download_url(tok, BUCKET, IN_OBJ, minutes=60)
    st2, res = aps_oss.signed_upload_url(tok, BUCKET, OUT_OBJ, minutes=60)
    if st != 200 or st2 != 200:
        raise SystemExit("signed url failed")
    out_url, out_key = res

    print(f"plotting {LAYOUT!r} ...")
    st, wi = request("POST", f"{DA}/workitems", token=tok, data={
        "activityId": f"{nickname}.{ACTIVITY_ID}+{ALIAS}",
        "arguments": {"inputFile": {"url": in_url},
                      "outputFile": {"url": out_url, "verb": "put"}},
    })
    if st not in (200, 201):
        raise SystemExit(json.dumps(wi, indent=2)[:600])

    wid, status, report = wi["id"], "?", None
    for _ in range(40):
        time.sleep(4)
        st, s = request("GET", f"{DA}/workitems/{wid}", token=tok)
        status = s.get("status") if isinstance(s, dict) else "?"
        if status not in ("pending", "inprogress"):
            report = s.get("reportUrl")
            break
    print("  status:", status)

    if report:
        with urllib.request.urlopen(report, timeout=120) as r:
            log = r.read().decode("utf-8", "replace")
        full = "--full" in sys.argv
        for line in log.splitlines():
            if full or any(k in line for k in ("@@@", "PLOT", "Plotting",
                                               "Effective", "rror", "Cannot")):
                print("   ", line.strip()[:160])

    aps_oss.finalize_upload(tok, BUCKET, OUT_OBJ, out_key)
    st, url = aps_oss.signed_download_url(tok, BUCKET, OUT_OBJ)
    if st == 200:
        dest = SCRATCH / f"{LAYOUT.replace('.', '_')}.pdf"
        n = aps_oss.download_to(url, dest)
        print(f"  downloaded {n/1024:.0f} KB -> {dest.name}")
    else:
        print(f"  no PDF: HTTP {st}")


if __name__ == "__main__":
    main()
