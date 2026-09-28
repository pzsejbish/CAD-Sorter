"""OSS (object storage) helpers — upload input, get signed URLs for a workitem."""
import json
import urllib.request
from pathlib import Path

from aps_client import BASE, request

OSS = f"{BASE}/oss/v2"


def ensure_bucket(token: str, bucket: str, policy: str = "transient") -> tuple:
    """transient = objects expire in 24h, which is right for test runs."""
    st, body = request("POST", f"{OSS}/buckets", token=token,
                       data={"bucketKey": bucket, "policyKey": policy})
    if st == 409:  # already exists
        return 200, {"bucketKey": bucket, "note": "already existed"}
    return st, body


def upload(token: str, bucket: str, object_key: str, path: Path) -> tuple:
    """Signed-S3 upload flow: get URL, PUT bytes, then finalize."""
    st, body = request(
        "GET", f"{OSS}/buckets/{bucket}/objects/{object_key}/signeds3upload",
        token=token)
    if st != 200:
        return st, body
    upload_key = body["uploadKey"]
    url = body["urls"][0]

    data = path.read_bytes()
    req = urllib.request.Request(url, data=data, method="PUT")
    with urllib.request.urlopen(req, timeout=300) as resp:
        if resp.status not in (200, 201):
            return resp.status, "S3 PUT failed"

    return request(
        "POST", f"{OSS}/buckets/{bucket}/objects/{object_key}/signeds3upload",
        token=token, data={"uploadKey": upload_key})


def signed_download_url(token: str, bucket: str, object_key: str,
                        minutes: int = 60) -> tuple:
    st, body = request(
        "GET",
        f"{OSS}/buckets/{bucket}/objects/{object_key}/signeds3download"
        f"?minutesExpiration={minutes}",
        token=token)
    if st != 200:
        return st, body
    return 200, body["url"]


def signed_upload_url(token: str, bucket: str, object_key: str,
                      minutes: int = 60) -> tuple:
    """A single PUT url the workitem can write its output to."""
    st, body = request(
        "GET",
        f"{OSS}/buckets/{bucket}/objects/{object_key}/signeds3upload"
        f"?minutesExpiration={minutes}",
        token=token)
    if st != 200:
        return st, body
    return 200, (body["urls"][0], body["uploadKey"])


def finalize_upload(token: str, bucket: str, object_key: str,
                    upload_key: str) -> tuple:
    return request(
        "POST", f"{OSS}/buckets/{bucket}/objects/{object_key}/signeds3upload",
        token=token, data={"uploadKey": upload_key})


def download_to(url: str, dest: Path) -> int:
    req = urllib.request.Request(url, method="GET")
    with urllib.request.urlopen(req, timeout=300) as resp:
        data = resp.read()
    dest.write_bytes(data)
    return len(data)
