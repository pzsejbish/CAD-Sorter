"""What Design Automation access does this APS app have?

Checks: engine list (which AutoCAD versions), the app nickname (needed before
you can own activities/appbundles), and any existing activities.
"""
import json

from aps_client import BASE, get_token, request

DA = f"{BASE}/da/us-east/v3"

status, body = get_token()
assert status == 200, f"auth failed: {status} {body}"
tok = body["access_token"]

print("=" * 66)
print("ENGINES AVAILABLE")
print("=" * 66)
st, engines = request("GET", f"{DA}/engines?page=", token=tok)
print(f"HTTP {st}")
if isinstance(engines, dict):
    items = engines.get("data", [])
    acad = sorted([e for e in items if "AutoCAD" in e])
    print(f"total engines: {len(items)}")
    print(f"AutoCAD engines ({len(acad)}):")
    for e in acad:
        print("   ", e)
    others = sorted(set(i.split(".")[1] for i in items if "." in i))
    print("engine families:", others)
else:
    print(json.dumps(engines, indent=2)[:600] if isinstance(engines, dict) else str(engines)[:600])

print("\n" + "=" * 66)
print("APP NICKNAME  (must be set before owning activities/appbundles)")
print("=" * 66)
st, nick = request("GET", f"{DA}/forgeapps/me", token=tok)
print(f"HTTP {st} -> {nick!r}")

print("\n" + "=" * 66)
print("EXISTING ACTIVITIES OWNED BY THIS APP")
print("=" * 66)
st, acts = request("GET", f"{DA}/activities", token=tok)
print(f"HTTP {st}")
if isinstance(acts, dict):
    data = acts.get("data", [])
    mine = [a for a in data if not a.startswith("Autodesk.")]
    print(f"total visible: {len(data)}   owned-by-me: {len(mine)}")
    print("sample of shared/public:", data[:5])
    if mine:
        print("mine:", mine)
else:
    print(str(acts)[:400])

print("\n" + "=" * 66)
print("QUOTA / USAGE SIGNAL")
print("=" * 66)
# There is no public quota endpoint; a cheap probe is to list workitems perms.
st, wi = request("GET", f"{DA}/workitems/does-not-exist", token=tok)
print(f"workitem probe -> HTTP {st} ({'reachable' if st in (404, 200) else 'check'})")
if st not in (404, 200):
    print("  body:", str(wi)[:400])
