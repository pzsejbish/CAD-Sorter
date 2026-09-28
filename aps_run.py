"""Create/refresh the activity that loads the AppBundle, then run a workitem."""
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
IN_OBJ = "titletable.dwg"
PARAMS_OBJ = "params.json"
OUT_OBJ = "titletable_out.dwg"

ENGINE = "Autodesk.AutoCAD+26_0"
APPBUNDLE_ID = "PzseTable"
ACTIVITY_ID = "PzseTableRun"
ALIAS = "test"


def stage(m):
    print(f"\n{'=' * 66}\n{m}\n{'=' * 66}")


def _params_match(cur, want):
    """Compare only the fields we set â€” APS fills in defaults of its own."""
    if set((cur or {}).keys()) != set(want.keys()):
        return False
    for k, w in want.items():
        c = (cur or {}).get(k) or {}
        for f in ("verb", "localName", "required"):
            if f in w and c.get(f, False if f == "required" else None) != w[f]:
                return False
    return True


def unchanged(cur, want):
    """Is the aliased activity already exactly what we would post?

    Every run used to POST a new version, and APS caps an activity at 100 â€”
    which blocked generation mid-chunk twice. The script is compared exactly,
    so a stale script can never be reused silently; the worst case of a
    too-strict comparison is one wasted version, as before.
    """
    if not isinstance(cur, dict):
        return False
    if cur.get("commandLine") != want["commandLine"]:
        return False
    if cur.get("engine") != want["engine"]:
        return False
    if list(cur.get("appbundles") or []) != list(want["appbundles"]):
        return False
    if not _params_match(cur.get("parameters"), want["parameters"]):
        return False
    stored = (cur.get("settings") or {}).get("script")
    if isinstance(stored, dict):
        stored = stored.get("value")
    return stored == want["settings"]["script"]


def upsert_activity(tok, nickname, script):
    activity = {
        "id": ACTIVITY_ID,
        "commandLine": [
            '$(engine.path)\\accoreconsole.exe /i "$(args[inputFile].path)" '
            f'/al "$(appbundles[{APPBUNDLE_ID}].path)" '
            '/s "$(settings[script].path)"'
        ],
        "parameters": {
            "inputFile": {"verb": "get", "required": True, "localName": "input.dwg"},
            "params": {"verb": "get", "required": False, "localName": "params.json"},
            "outputFile": {"verb": "put", "required": False, "localName": "output.dwg"},
        },
        "settings": {"script": script},
        "engine": ENGINE,
        # The default cut this job off at ~90s, mid-way through stripping the
        # underlay. Generating 14 sheets alone costs ~40s of regen.
        "limitProcessingTimeSec": 1800,
        "appbundles": [f"{nickname}.{APPBUNDLE_ID}+{ALIAS}"],
        "description": "Load PzseTable bundle; dump/fill ACAD_TABLE cells",
    }
    qualified = f"{nickname}.{ACTIVITY_ID}+{ALIAS}"
    st, cur = request("GET", f"{DA}/activities/{ACTIVITY_ID}+{ALIAS}", token=tok)
    if st == 200 and unchanged(cur, activity):
        print(f"  reusing v{cur.get('version')} (unchanged)")
        return qualified

    st, b = request("POST", f"{DA}/activities", token=tok, data=activity)
    if st == 409:
        # The versions endpoint rejects 'id' â€” it is implied by the URL. Sending
        # it 400s, which silently leaves the alias on the previous version and
        # runs a stale script.
        ver_spec = {k: v for k, v in activity.items() if k != "id"}
        st, b = request("POST", f"{DA}/activities/{ACTIVITY_ID}/versions",
                        token=tok, data=ver_spec)
        print(f"  new version -> HTTP {st}")
        if st not in (200, 201):
            print("   ", json.dumps(b, indent=2)[:400] if isinstance(b, dict) else str(b)[:400])
            raise SystemExit(1)
        ver = b.get("version") if isinstance(b, dict) else None
        st2, b2 = request("PATCH", f"{DA}/activities/{ACTIVITY_ID}/aliases/{ALIAS}",
                          token=tok, data={"version": ver})
        if st2 == 404:
            st2, b2 = request("POST", f"{DA}/activities/{ACTIVITY_ID}/aliases",
                              token=tok, data={"id": ALIAS, "version": ver})
        print(f"  alias -> HTTP {st2} {str(b2)[:120]}")
    else:
        print(f"  create -> HTTP {st}")
        if st in (200, 201):
            st2, b2 = request("POST", f"{DA}/activities/{ACTIVITY_ID}/aliases",
                              token=tok, data={"id": ALIAS, "version": 1})
            print(f"  alias -> HTTP {st2} {str(b2)[:120]}")
        else:
            print(json.dumps(b, indent=2)[:800] if isinstance(b, dict) else str(b)[:800])
            raise SystemExit(1)
    return f"{nickname}.{ACTIVITY_ID}+{ALIAS}"


def run(script, params=None, want_output=True, show=("@@@",)):
    st, body = get_token()
    assert st == 200, body
    tok = body["access_token"]
    nickname = request("GET", f"{DA}/forgeapps/me", token=tok)[1]

    stage("ACTIVITY")
    qualified = upsert_activity(tok, nickname, script)
    print(f"  {qualified}")

    stage("ARGUMENTS")
    args = {}
    st, in_url = aps_oss.signed_download_url(tok, BUCKET, IN_OBJ)
    assert st == 200, in_url
    args["inputFile"] = {"url": in_url}
    print("  inputFile: ok")

    if params is not None:
        p = SCRATCH / "params.json"
        p.write_text(json.dumps(params), encoding="utf-8")
        st, _ = aps_oss.upload(tok, BUCKET, PARAMS_OBJ, p)
        st, purl = aps_oss.signed_download_url(tok, BUCKET, PARAMS_OBJ)
        assert st == 200, purl
        args["params"] = {"url": purl}
        print(f"  params: ok ({len(params.get('cells', []))} cells)")

    out_key = None
    if want_output:
        st, (out_url, out_key) = aps_oss.signed_upload_url(tok, BUCKET, OUT_OBJ)
        assert st == 200, out_url
        args["outputFile"] = {"url": out_url, "verb": "put"}
        print("  outputFile: ok")

    stage("WORKITEM")
    st, wi = request("POST", f"{DA}/workitems", token=tok,
                     data={"activityId": qualified, "arguments": args})
    print(f"  submit -> HTTP {st}")
    if st not in (200, 201):
        print(json.dumps(wi, indent=2)[:800] if isinstance(wi, dict) else str(wi)[:800])
        return None
    wid = wi["id"]

    report_url, status = None, "?"
    for i in range(60):
        time.sleep(4)
        st, s = request("GET", f"{DA}/workitems/{wid}", token=tok)
        status = s.get("status") if isinstance(s, dict) else "?"
        if status not in ("pending", "inprogress"):
            report_url = s.get("reportUrl")
            break
    print(f"  status: {status}")

    stage("REPORT")
    log = ""
    if report_url:
        try:
            with urllib.request.urlopen(report_url, timeout=120) as r:
                log = r.read().decode("utf-8", "replace")
        except Exception as e:
            print("  report fetch failed:", e)
    for line in log.splitlines():
        if any(k in line for k in show) or "rror" in line:
            print("  ", line.strip()[:200])

    if want_output and out_key:
        stage("OUTPUT")
        aps_oss.finalize_upload(tok, BUCKET, OUT_OBJ, out_key)
        st, url = aps_oss.signed_download_url(tok, BUCKET, OUT_OBJ)
        if st == 200:
            # Name the local file after the output object, and fall back if it is
            # locked (open in CAD) rather than losing the run.
            dest = SCRATCH / OUT_OBJ
            try:
                n = aps_oss.download_to(url, dest)
            except PermissionError:
                dest = SCRATCH / (dest.stem + "_new" + dest.suffix)
                n = aps_oss.download_to(url, dest)
                print("  (original was locked; wrote a copy)")
            print(f"  downloaded {n/1024/1024:.1f} MB -> {dest.name}")
        else:
            print(f"  no output: HTTP {st}")
    return log


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "dump"
    if mode == "dump":
        run("PZSEDUMP\n", want_output=False)

