#!/usr/bin/env python3
"""Discover latest C2ModRegistry releases and run the local poller.

With --registry-dir this is completely offline and is suitable for tests. With
--registry-repo it reads the public registry from raw.githubusercontent.com
and downloads only releases selected by registry marker metadata.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import urllib.parse
import urllib.request
from datetime import datetime
from pathlib import Path


def read_text(url: str, token: str | None = None) -> str:
    request = urllib.request.Request(url)
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read().decode("utf-8")


def sha512(path: Path) -> str:
    digest = hashlib.sha512()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def latest_release(package: dict) -> dict | None:
    releases = package.get("releases", [])
    if not releases:
        return None
    return max(releases, key=lambda release: datetime.fromisoformat(release["release_date"]))


def is_map_info_release(release: dict) -> bool:
    for marker in release.get("manifest", {}).get("markers", []):
        object_class = str(marker.get("object_class", "")).lower()
        if "da_mapinfo" in object_class:
            return True
    return False


def release_download_url(repo_url: str, release: dict) -> str:
    return f"{repo_url.rstrip('/')}/releases/download/{urllib.parse.quote(release['tag'])}/{urllib.parse.quote(release['pak_file_name'])}"


def local_package_files(registry: Path) -> list[tuple[str, Path]]:
    index = registry / "package_db/mod_list_index.txt"
    result = []
    for package_id in index.read_text().splitlines():
        package_id = package_id.strip()
        if not package_id or "/" not in package_id:
            continue
        result.append((package_id, registry / "package_db/packages" / f"{package_id}.json"))
    return result


def remote_package_files(repository: str, ref: str, token: str | None) -> list[tuple[str, str]]:
    base = f"https://raw.githubusercontent.com/{repository}/{urllib.parse.quote(ref)}/"
    index = read_text(base + "package_db/mod_list_index.txt", token)
    return [
        (package_id, base + "package_db/packages/" + "/".join(urllib.parse.quote(part) for part in package_id.split("/")) + ".json")
        for package_id in index.splitlines()
        if package_id.strip() and "/" in package_id
    ]


def cache_release(package_id: str, release: dict, package_file: Path | None, package_url: str | None, package_repo_url: str | None, cache: Path, token: str | None) -> Path:
    expected = release.get("hash", "").lower()
    if len(expected) != 128:
        expected = ""
    target = cache / (expected or f"{package_id.replace('/', '_')}-{release['tag']}.pak")
    if target.exists() and (not expected or sha512(target) == expected):
        return target

    local_pak = release.get("local_pak")
    if local_pak and package_file is not None:
        source = (package_file.parent / local_pak).resolve()
        shutil.copy2(source, target)
    else:
        if package_url is None:
            raise RuntimeError(f"No download source for {package_id} {release['tag']}")
        release_info = release.get("info") if isinstance(release.get("info"), dict) else {}
        repo_url = release_info.get("repo_url") or release.get("repo_url") or package_repo_url
        if not repo_url:
            raise RuntimeError(f"Release has no repo_url: {package_id} {release['tag']}")
        download = release_download_url(repo_url, release)
        request = urllib.request.Request(download)
        if token:
            request.add_header("Authorization", f"Bearer {token}")
        with urllib.request.urlopen(request, timeout=300) as response, target.open("wb") as output:
            shutil.copyfileobj(response, output)

    if expected and sha512(target) != expected:
        target.unlink(missing_ok=True)
        raise RuntimeError(f"SHA-512 mismatch for {package_id} {release['tag']}")
    return target


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--registry-dir", type=Path)
    source.add_argument("--registry-repo", default="Chiv2-Community/C2ModRegistry")
    parser.add_argument("--registry-ref", default="main")
    parser.add_argument("--cache-dir", required=True, type=Path)
    parser.add_argument("--manifest-out", required=True, type=Path)
    parser.add_argument("--manifest-filter", action="store_true", help="Use registry marker metadata to skip releases without an apparent DA_MapInfo marker")
    parser.add_argument("--token", default=None)
    parser.add_argument("--config-asset", required=True, type=Path)
    parser.add_argument("--tool", required=True, type=Path)
    parser.add_argument("--state", required=True, type=Path)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--report-dir", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    args.cache_dir = args.cache_dir.resolve()
    args.cache_dir.mkdir(parents=True, exist_ok=True)
    packages = local_package_files(args.registry_dir) if args.registry_dir else remote_package_files(args.registry_repo, args.registry_ref, args.token)
    manifest_releases = []
    for package_id, package_source in packages:
        package_file = package_source if isinstance(package_source, Path) else None
        package_json = json.loads(package_source.read_text() if package_file else read_text(package_source, args.token))
        release = latest_release(package_json)
        if release is None or (args.manifest_filter and not is_map_info_release(release)):
            continue
        package_repo_url = package_json.get("latest_release_info", {}).get("repo_url")
        pak = cache_release(package_id, release, package_file, None if package_file else package_source, package_repo_url, args.cache_dir, args.token)
        manifest_releases.append({"id": f"{package_id}@{release['tag']}", "pak": str(pak)})

    manifest = {"releases": manifest_releases}
    args.manifest_out.parent.mkdir(parents=True, exist_ok=True)
    args.manifest_out.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"selected {len(manifest_releases)} release pak(s)")

    command = [
        sys.executable,
        str(Path(__file__).with_name("poll-release-manifest.py")),
        "--manifest", str(args.manifest_out),
        "--config-asset", str(args.config_asset),
        "--tool", str(args.tool),
        "--state", str(args.state),
        "--work-dir", str(args.work_dir),
        "--output-dir", str(args.output_dir),
    ]
    if args.report_dir is not None:
        command.extend(["--report-dir", str(args.report_dir)])
    if args.dry_run:
        command.append("--dry-run")
    subprocess.run(command, check=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (KeyError, RuntimeError, json.JSONDecodeError, FileNotFoundError, urllib.error.URLError, subprocess.CalledProcessError) as error:
        print(f"registry poll failed: {error}", file=sys.stderr)
        raise SystemExit(1)
