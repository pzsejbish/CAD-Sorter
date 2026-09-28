"""THE test: can real AutoCAD, via APS, write into IronRidge's ACAD_TABLE?

Full loop — upload the extracted TITLETABLE, define an activity that runs a
LISP one-liner through accoreconsole, submit a workitem, download the result.

The LISP is the same shape that failed on LT only because LT lacks ActiveX.
APS runs full AutoCAD, so this is the environment that decides it.
"""
import json
import time
from pathlib import Path

from aps_client import BASE, get_token, request
import aps_oss

DA = f"{BASE}/da/us-east/v3"
HERE = Path(__file__).parent
SRC_DWG = HERE / "_scratch" / "titletable.dwg"
OUT_DWG = HERE / "_scratch" / "titletable_from_aps.dwg"

BUCKET = "pzse-titleblock-proof-2026"
IN_OBJ = "titletable.dwg"
OUT_OBJ = "titletable_out.dwg"

ENGINE = "Autodesk.AutoCAD+26_0"
ACTIVITY_ID = "PZSETableWrite"
ALIAS = "test"

# One-line LISP (a .scr feeds one line at a time, so it must not wrap), then
# SAVEAS to the output localName. Reads two cells, writes two, reads back.
LISP = (
    '(progn (vl-load-com)'
    ' (setq ss (ssget "_X" (list (cons 0 "ACAD_TABLE"))))'
    ' (princ (strcat "\\n@@@ tables=" (if ss (itoa (sslength ss)) "0")))'
    ' (setq ob (vl-catch-all-apply (quote vlax-ename->vla-object) (list (ssname ss 0))))'
    ' (princ (strcat "\\n@@@ vlaobj=" (if (vl-catch-all-error-p ob)'
    ' (vl-catch-all-error-message ob) "OK")))'
    ' (setq rr (vl-catch-all-apply (quote vla-get-Rows) (list ob)))'
    ' (princ (strcat "\\n@@@ rows=" (if (vl-catch-all-error-p rr)'
    ' (vl-catch-all-error-message rr) (itoa rr))))'
    ' (setq v (vl-catch-all-apply (quote vla-GetText) (list ob 1 1)))'
    ' (princ (strcat "\\n@@@ r1c1=" (if (vl-catch-all-error-p v)'
    ' (vl-catch-all-error-message v) v)))'
    ' (setq v (vl-catch-all-apply (quote vla-GetText) (list ob 1 2)))'
    ' (princ (strcat "\\n@@@ r1c2-before=" (if (vl-catch-all-error-p v)'
    ' (vl-catch-all-error-message v) v)))'
    ' (setq w (vl-catch-all-apply (quote vla-SetText) (list ob 1 2 "ALLIED FACILITY PARTNERS")))'
    ' (princ (strcat "\\n@@@ write1=" (if (vl-catch-all-error-p w)'
    ' (vl-catch-all-error-message w) "WRITE-OK")))'
    ' (setq w (vl-catch-all-apply (quote vla-SetText) (list ob 9 2 "25")))'
    ' (princ (strcat "\\n@@@ write2=" (if (vl-catch-all-error-p w)'
    ' (vl-catch-all-error-message w) "WRITE-OK")))'
    ' (setq v (vl-catch-all-apply (quote vla-GetText) (list ob 1 2)))'
    ' (princ (strcat "\\n@@@ r1c2-after=" (if (vl-catch-all-error-p v)'
    ' (vl-catch-all-error-message v) v)))'
    ' (princ "\\n@@@ DONE") (princ))'
)
SCRIPT = LISP + "\nFILEDIA\n0\n_.SAVEAS\n2018\noutput.dwg\nY\nFILEDIA\n1\n"


def stage(msg):
    print(f"\n{'=' * 66}\n{msg}\n{'=' * 66}")


def main():
    st, body = get_token()
    assert st == 200, f"auth failed: {body}"
    tok = body["access_token"]
    print("auth OK")

    nickname = request("GET", f"{DA}/forgeapps/me", token=tok)[1]
    qualified = f"{nickname}.{ACTIVITY_ID}+{ALIAS}"

    stage("1. BUCKET + UPLOAD INPUT")
    st, b = aps_oss.ensure_bucket(tok, BUCKET)
    print(f"bucket -> HTTP {st} {str(b)[:120]}")
    assert st in (200, 201), b
    print(f"uploading {SRC_DWG.name} ({SRC_DWG.stat().st_size/1024:.0f} KB)...")
    st, b = aps_oss.upload(tok, BUCKET, IN_OBJ, SRC_DWG)
    print(f"upload -> HTTP {st}")
    assert st in (200, 201), str(b)[:400]

    stage("2. CREATE ACTIVITY")
    activity = {
        "id": ACTIVITY_ID,
        "commandLine": [
            '$(engine.path)\\accoreconsole.exe /i "$(args[inputFile].path)" '
            '/s "$(settings[script].path)"'
        ],
        "parameters": {
            "inputFile": {"verb": "get", "required": True, "localName": "input.dwg"},
            "outputFile": {"verb": "put", "required": True, "localName": "output.dwg"},
        },
        "settings": {"script": SCRIPT},
        "engine": ENGINE,
        "description": "Write values into an existing ACAD_TABLE (proof)",
    }
    st, b = request("POST", f"{DA}/activities", token=tok, data=activity)
    print(f"create activity -> HTTP {st}")
    if st == 409:
        print("  already exists; updating with a new version")
        st, b = request("POST", f"{DA}/activities/{ACTIVITY_ID}/versions",
                        token=tok, data=activity)
        print(f"  new version -> HTTP {st} {str(b)[:200]}")
        ver = b.get("version") if isinstance(b, dict) else None
        st2, b2 = request("PATCH", f"{DA}/activities/{ACTIVITY_ID}/aliases/{ALIAS}",
                          token=tok, data={"version": ver})
        print(f"  alias patch -> HTTP {st2} {str(b2)[:200]}")
    elif st in (200, 201):
        st2, b2 = request("POST", f"{DA}/activities/{ACTIVITY_ID}/aliases",
                          token=tok, data={"id": ALIAS, "version": 1})
        print(f"  alias create -> HTTP {st2} {str(b2)[:200]}")
    else:
        print("  body:", json.dumps(b, indent=2)[:900] if isinstance(b, dict) else str(b)[:900])
        return

    stage("3. SIGNED URLS")
    st, in_url = aps_oss.signed_download_url(tok, BUCKET, IN_OBJ)
    assert st == 200, in_url
    print("input download url  : ok")
    st, (out_url, out_key) = aps_oss.signed_upload_url(tok, BUCKET, OUT_OBJ)
    assert st == 200, out_url
    print("output upload url   : ok")

    stage("4. SUBMIT WORKITEM")
    st, wi = request("POST", f"{DA}/workitems", token=tok, data={
        "activityId": qualified,
        "arguments": {
            "inputFile": {"url": in_url},
            "outputFile": {"url": out_url, "verb": "put"},
        },
    })
    print(f"submit -> HTTP {st}")
    if st not in (200, 201):
        print(json.dumps(wi, indent=2)[:900] if isinstance(wi, dict) else str(wi)[:900])
        return
    wid = wi["id"]
    print(f"workitem id: {wid}")

    stage("5. POLL")
    report_url = None
    for i in range(60):
        time.sleep(5)
        st, s = request("GET", f"{DA}/workitems/{wid}", token=tok)
        status = s.get("status") if isinstance(s, dict) else "?"
        print(f"  [{i * 5:>3}s] {status}")
        if status not in ("pending", "inprogress"):
            report_url = s.get("reportUrl")
            print("\nfinal:", json.dumps(s, indent=2)[:700])
            break

    stage("6. REPORT (accoreconsole stdout)")
    if report_url:
        try:
            import urllib.request
            with urllib.request.urlopen(report_url, timeout=120) as r:
                log = r.read().decode("utf-8", "replace")
            for line in log.splitlines():
                if "@@@" in line or "error" in line.lower() or "ERROR" in line:
                    print("  ", line.strip()[:160])
        except Exception as e:
            print("  could not fetch report:", e)

    stage("7. DOWNLOAD RESULT")
    aps_oss.finalize_upload(tok, BUCKET, OUT_OBJ, out_key)
    st, url = aps_oss.signed_download_url(tok, BUCKET, OUT_OBJ)
    if st == 200:
        n = aps_oss.download_to(url, OUT_DWG)
        print(f"downloaded {n/1024:.0f} KB -> {OUT_DWG}")
    else:
        print(f"no output object: HTTP {st} {str(url)[:300]}")


if __name__ == "__main__":
    main()
