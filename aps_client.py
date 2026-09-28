"""Minimal APS (Autodesk Platform Services) client — stdlib only.

Credentials come from .env (gitignored); nothing is hardcoded here.
"""
import json
import os
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

BASE = "https://developer.api.autodesk.com"
ENV_PATH = Path(__file__).with_name(".env")


def load_env(path: Path = ENV_PATH) -> dict:
    env = {}
    if path.exists():
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            env[k.strip()] = v.strip()
    # Real env vars win, so this works on Render too.
    for k in ("APS_CLIENT_ID", "APS_CLIENT_SECRET"):
        if os.environ.get(k):
            env[k] = os.environ[k]
    return env


def request(method: str, url: str, *, token: str = None, data=None,
            headers: dict = None, form: bool = False):
    """Return (status, parsed_body_or_text). Never raises on HTTP error."""
    hdrs = dict(headers or {})
    if token:
        hdrs["Authorization"] = f"Bearer {token}"

    body = None
    if data is not None:
        if form:
            body = urllib.parse.urlencode(data).encode()
            hdrs.setdefault("Content-Type", "application/x-www-form-urlencoded")
        elif isinstance(data, (bytes, bytearray)):
            body = data
        else:
            body = json.dumps(data).encode()
            hdrs.setdefault("Content-Type", "application/json")

    req = urllib.request.Request(url, data=body, headers=hdrs, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            raw = resp.read()
            status = resp.status
    except urllib.error.HTTPError as e:
        raw = e.read()
        status = e.code
    except urllib.error.URLError as e:
        return 0, f"network error: {e.reason}"

    text = raw.decode("utf-8", "replace")
    try:
        return status, json.loads(text)
    except json.JSONDecodeError:
        return status, text


def get_token(scopes: str = "code:all data:read data:write data:create "
                            "bucket:create bucket:read bucket:delete") -> tuple:
    """Two-legged (client_credentials) token. Returns (status, body)."""
    env = load_env()
    cid = env.get("APS_CLIENT_ID")
    sec = env.get("APS_CLIENT_SECRET")
    if not cid or not sec:
        raise RuntimeError("APS_CLIENT_ID / APS_CLIENT_SECRET missing from .env")

    import base64
    basic = base64.b64encode(f"{cid}:{sec}".encode()).decode()
    return request(
        "POST", f"{BASE}/authentication/v2/token",
        data={"grant_type": "client_credentials", "scope": scopes},
        headers={"Authorization": f"Basic {basic}"},
        form=True,
    )


if __name__ == "__main__":
    env = load_env()
    cid = env.get("APS_CLIENT_ID", "")
    print(f"client id: {cid[:6]}...{cid[-4:]}  (len {len(cid)})")
    print(f"secret   : {'set' if env.get('APS_CLIENT_SECRET') else 'MISSING'}")

    status, body = get_token()
    print(f"\ntoken request -> HTTP {status}")
    if status == 200 and isinstance(body, dict):
        tok = body.get("access_token", "")
        print(f"  access_token : {tok[:18]}... ({len(tok)} chars)")
        print(f"  token_type   : {body.get('token_type')}")
        print(f"  expires_in   : {body.get('expires_in')}s")
        print("\nAUTH OK")
    else:
        print("  body:", json.dumps(body, indent=2)[:900]
              if isinstance(body, dict) else str(body)[:900])
        print("\nAUTH FAILED")
