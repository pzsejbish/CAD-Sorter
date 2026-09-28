"""Package the .NET plugin as an AutoCAD .bundle and upload it to APS."""
import json
import shutil
import urllib.request
import uuid
import zipfile
from pathlib import Path

from aps_client import BASE, get_token, request

DA = f"{BASE}/da/us-east/v3"
HERE = Path(__file__).parent
BUILD = HERE / "appbundle" / "PzseTable" / "bin" / "Release" / "net8.0-windows"
STAGE = HERE / "appbundle" / "stage"
ZIP_PATH = HERE / "appbundle" / "PzseTable.zip"

APPBUNDLE_ID = "PzseTable"
ALIAS = "test"
ENGINE = "Autodesk.AutoCAD+26_0"

# AutoCAD 2025 changed the Autoloader: for a .NET ComponentEntry it now demands
#   1. AppType=".Net" on the entry
#   2. RuntimeRequirements INSIDE the ComponentEntry, not beside it
#   3. BOTH SeriesMin and SeriesMax present
# Get any of those wrong and the plugin is skipped silently — no load error, and
# the command simply reports "Unknown command". All three were wrong here.
# Series range spans 2025..2026 so it covers the engine whichever numbering
# AutoCAD 2026 uses (R25.1 or R26.0).
PACKAGE_CONTENTS = """<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" Version="1.0" ProductCode="{%s}"
                    Name="PzseTable" AppVersion="1.0.0"
                    Description="PZSE title-table automation" Author="PZSE">
  <CompanyDetails Name="PZSE" />
  <Components>
    <ComponentEntry AppName="PzseTable" Version="1.0"
                    ModuleName="./Contents/PzseTable.dll"
                    AppDescription="Read and write ACAD_TABLE cells"
                    AppType=".Net"
                    LoadOnAutoCADStartup="True">
      <RuntimeRequirements OS="Win64" Platform="AutoCAD*"
                           SeriesMin="R25.0" SeriesMax="R26.1" />
      <!-- Every command must be declared here, not just defined in C#. With
           LoadOnCommandInvocation the loader only recognises what this block
           lists; an undeclared command reports "Unknown command". -->
      <Commands GroupName="PZSE">
        <Command Global="PZSEDUMP" Local="PZSEDUMP" />
        <Command Global="PZSEFILL" Local="PZSEFILL" />
        <Command Global="PZSESAVE" Local="PZSESAVE" />
        <Command Global="PZSESTRIP" Local="PZSESTRIP" />
        <Command Global="PZSEVPOFF" Local="PZSEVPOFF" />
        <Command Global="PZSEPLAN" Local="PZSEPLAN" />
        <Command Global="PZSETILE" Local="PZSETILE" />
        <Command Global="PZSEVPINFO" Local="PZSEVPINFO" />
        <Command Global="PZSELAYERS" Local="PZSELAYERS" />
        <Command Global="PZSESHEETS" Local="PZSESHEETS" />
        <Command Global="PZSEMAKE" Local="PZSEMAKE" />
        <Command Global="PZSEFREEZE" Local="PZSEFREEZE" />
        <Command Global="PZSESLIM" Local="PZSESLIM" />
        <Command Global="PZSEFAST" Local="PZSEFAST" />
        <Command Global="PZSETITLE" Local="PZSETITLE" />
        <Command Global="PZSEKEYLAB" Local="PZSEKEYLAB" />
        <Command Global="PZSEPROFILE" Local="PZSEPROFILE" />
      </Commands>
    </ComponentEntry>
  </Components>
</ApplicationPackage>
""" % str(uuid.uuid4()).upper()


def package() -> Path:
    bundle = STAGE / "PzseTable.bundle"
    if STAGE.exists():
        shutil.rmtree(STAGE)
    (bundle / "Contents").mkdir(parents=True)
    (bundle / "PackageContents.xml").write_text(PACKAGE_CONTENTS, encoding="utf-8")

    dll = BUILD / "PzseTable.dll"
    if not dll.exists():
        raise SystemExit(f"build output missing: {dll}")
    # Ship deps.json / runtimeconfig.json alongside the DLL â€” a .NET 8 plugin
    # needs them to resolve its framework, and omitting them was the second
    # reason nothing loaded.
    for name in ("PzseTable.dll", "PzseTable.deps.json",
                 "PzseTable.runtimeconfig.json"):
        src = BUILD / name
        if src.exists():
            shutil.copy2(src, bundle / "Contents" / name)

    if ZIP_PATH.exists():
        ZIP_PATH.unlink()
    with zipfile.ZipFile(ZIP_PATH, "w", zipfile.ZIP_DEFLATED) as z:
        for p in sorted(bundle.rglob("*")):
            if p.is_file():
                z.write(p, p.relative_to(STAGE))
    with zipfile.ZipFile(ZIP_PATH) as z:
        print("  zip contents:")
        for n in z.namelist():
            print("    ", n)
    return ZIP_PATH


def multipart_post(url: str, fields: dict, file_field: str,
                   filename: str, content: bytes) -> int:
    """APS appbundle upload is a plain multipart form POST to S3."""
    boundary = "----pzse" + uuid.uuid4().hex
    parts = []
    for k, v in fields.items():
        parts.append(
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n"
            .encode())
    parts.append(
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"{file_field}\"; "
        f"filename=\"{filename}\"\r\nContent-Type: application/octet-stream\r\n\r\n"
        .encode())
    parts.append(content)
    parts.append(f"\r\n--{boundary}--\r\n".encode())
    body = b"".join(parts)

    req = urllib.request.Request(
        url, data=body, method="POST",
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.status
    except urllib.error.HTTPError as e:
        print("  upload error body:", e.read().decode("utf-8", "replace")[:400])
        return e.code


def main():
    st, body = get_token()
    assert st == 200, body
    tok = body["access_token"]

    print("packaging...")
    z = package()
    print(f"  {z.name}  {z.stat().st_size/1024:.1f} KB")

    print("\ncreating appbundle record...")
    spec = {"id": APPBUNDLE_ID, "engine": ENGINE,
            "description": "PZSE title-table automation"}
    st, b = request("POST", f"{DA}/appbundles", token=tok, data=spec)
    print(f"  create -> HTTP {st}")
    if st == 409:
        print("  exists; posting a new version")
        # The versions endpoint rejects 'id' â€” it is implied by the URL.
        ver_spec = {k: v for k, v in spec.items() if k != "id"}
        st, b = request("POST", f"{DA}/appbundles/{APPBUNDLE_ID}/versions",
                        token=tok, data=ver_spec)
        print(f"  version -> HTTP {st}")
    if st not in (200, 201):
        print(json.dumps(b, indent=2)[:600] if isinstance(b, dict) else str(b)[:600])
        return

    version = b.get("version")
    up = b.get("uploadParameters") or {}
    endpoint = up.get("endpointURL")
    form = up.get("formData") or {}
    print(f"  version={version}  upload endpoint={'ok' if endpoint else 'MISSING'}")

    print("\nuploading zip...")
    code = multipart_post(endpoint, form, "file", z.name, z.read_bytes())
    print(f"  upload -> HTTP {code}")
    if code not in (200, 201, 204):
        return

    print("\nsetting alias...")
    st, b = request("POST", f"{DA}/appbundles/{APPBUNDLE_ID}/aliases",
                    token=tok, data={"id": ALIAS, "version": version})
    if st == 409:
        st, b = request("PATCH", f"{DA}/appbundles/{APPBUNDLE_ID}/aliases/{ALIAS}",
                        token=tok, data={"version": version})
    print(f"  alias -> HTTP {st} {str(b)[:160]}")

    nickname = request("GET", f"{DA}/forgeapps/me", token=tok)[1]
    print(f"\nqualified appbundle id: {nickname}.{APPBUNDLE_ID}+{ALIAS}")


if __name__ == "__main__":
    main()
