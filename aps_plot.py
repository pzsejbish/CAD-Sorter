"""Plot a layout to PDF on APS rather than locally.

The local plot went pathological â€” this drawing carries ~1.3M polylines and
accoreconsole ballooned past 11 GB without finishing. Design Automation runs on
provisioned hardware and enforces a wall-clock limit, so it either returns or
fails cleanly instead of eating the workstation.
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
IN_OBJ = "mchenry_filled_in.dwg"      # the FILLED drawing, uploaded below
OUT_OBJ = "sr100.pdf"

ENGINE = "Autodesk.AutoCAD+26_0"
ACTIVITY_ID = "PzsePlot"
ALIAS = "test"

LAYOUT = sys.argv[1] if len(sys.argv) > 1 else "SR100"

# -PLOT with detailed configuration OFF uses the layout's own saved page setup,
# which IronRidge already configured (there are plotted PDFs in the project
# folder), so paper size / scale / plot style all come along correctly.
SCRIPT = (
    # Make the target layout current first — a viewport cannot be switched off
    # from another layout (that was the eNotApplicable). Then MVIEW OFF hides
    # the model-space window so the plot only has to regen the sheet furniture,
    # which is all we changed anyway.
    f'(setvar "CTAB" "{LAYOUT}")\n'
    "MVIEW\n"
    "OFF\n"
    "ALL\n"
    "\n"
    "FILEDIA\n0\n"
    "-PLOT\n"
    "N\n"                 # detailed plot configuration? no
    f"{LAYOUT}\n"         # layout name
    "DWG To PDF.pc3\n"    # output device
    "Y\n"                 # write the plot to a file
    "output.pdf\n"        # file name (localName of the output parameter)
    "N\n"                 # save changes to page setup
    "Y\n"                 # proceed with plot
    "FILEDIA\n1\n"
)


def main():
    tok = get_token()[1]["access_token"]
    nickname = request("GET", f"{DA}/forgeapps/me", token=tok)[1]

    # The 61 MB input rarely changes between attempts; skip re-uploading it
    # while iterating on the script.
    if "--skip-upload" not in sys.argv:
        src = SCRATCH / "mchenry_filled_new.dwg"
        if not src.exists():
            raise SystemExit(f"missing {src}")
        print(f"uploading {src.name} ({src.stat().st_size/1024/1024:.1f} MB) ...")
        st, b = aps_oss.upload(tok, BUCKET, IN_OBJ, src)
        print(f"  upload -> HTTP {st}")
        if st not in (200, 201):
            raise SystemExit(str(b)[:400])
    else:
        print("skipping upload (--skip-upload)")

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
        # Default is 60s, and regenerating this layout alone blew through that.
        # Raise it; the tier may cap the value but it will not go lower.
        "limitProcessingTimeSec": 900,
        "description": "Plot a layout to PDF",
    }
    st, b = request("POST", f"{DA}/activities", token=tok, data=activity)
    if st == 409:
        ver_spec = {k: v for k, v in activity.items() if k != "id"}
        st, b = request("POST", f"{DA}/activities/{ACTIVITY_ID}/versions",
                        token=tok, data=ver_spec)
        ver = b.get("version") if isinstance(b, dict) else None
        st2, _ = request("PATCH", f"{DA}/activities/{ACTIVITY_ID}/aliases/{ALIAS}",
                         token=tok, data={"version": ver})
        if st2 == 404:
            request("POST", f"{DA}/activities/{ACTIVITY_ID}/aliases",
                    token=tok, data={"id": ALIAS, "version": ver})
        print(f"  activity version {ver}")
    else:
        request("POST", f"{DA}/activities/{ACTIVITY_ID}/aliases",
                token=tok, data={"id": ALIAS, "version": 1})
        print(f"  activity created -> HTTP {st}")

    # 60 is the ceiling OSS accepts for signed-URL expiry; asking for more
    # fails the request rather than clamping.
    st, in_url = aps_oss.signed_download_url(tok, BUCKET, IN_OBJ, minutes=60)
    if st != 200:
        raise SystemExit(f"input url failed: {st} {str(in_url)[:300]}")
    st, res = aps_oss.signed_upload_url(tok, BUCKET, OUT_OBJ, minutes=60)
    if st != 200:
        raise SystemExit(f"output url failed: {st} {str(res)[:300]}")
    out_url, out_key = res

    print(f"submitting plot of layout {LAYOUT!r} ...")
    st, wi = request("POST", f"{DA}/workitems", token=tok, data={
        "activityId": f"{nickname}.{ACTIVITY_ID}+{ALIAS}",
        "arguments": {
            "inputFile": {"url": in_url},
            "outputFile": {"url": out_url, "verb": "put"},
        },
    })
    if st not in (200, 201):
        raise SystemExit(json.dumps(wi, indent=2)[:800])
    wid = wi["id"]

    status, report_url = "?", None
    for i in range(90):
        time.sleep(5)
        st, s = request("GET", f"{DA}/workitems/{wid}", token=tok)
        status = s.get("status") if isinstance(s, dict) else "?"
        if i % 6 == 0:
            print(f"  [{i*5:>4}s] {status}")
        if status not in ("pending", "inprogress"):
            report_url = s.get("reportUrl")
            break
    print(f"  final: {status}")

    if report_url:
        try:
            with urllib.request.urlopen(report_url, timeout=120) as r:
                log = r.read().decode("utf-8", "replace")
            started = False
            for line in log.splitlines():
                if "standard output dump" in line:
                    started = True
                if started:
                    s = line.strip()
                    if s:
                        print("   ", s[:170])
        except Exception as e:
            print("  report fetch failed:", e)

    aps_oss.finalize_upload(tok, BUCKET, OUT_OBJ, out_key)
    st, url = aps_oss.signed_download_url(tok, BUCKET, OUT_OBJ)
    if st == 200:
        dest = SCRATCH / f"{LAYOUT.replace(' ', '_')}_filled.pdf"
        n = aps_oss.download_to(url, dest)
        print(f"  downloaded {n/1024/1024:.2f} MB -> {dest.name}")
    else:
        print(f"  no PDF produced: HTTP {st}")


if __name__ == "__main__":
    main()
