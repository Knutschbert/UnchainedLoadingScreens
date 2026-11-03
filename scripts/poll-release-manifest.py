#!/usr/bin/env python3
"""Offline stand-in for the registry polling workflow.

The manifest format is intentionally close to the data the GitHub adapter will
eventually produce. Each release points to a local pak while developing.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run_tool(dotnet: str, tool: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [dotnet, str(tool), *arguments],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--config-asset", required=True, type=Path)
    parser.add_argument("--tool", required=True, type=Path)
    parser.add_argument("--state", required=True, type=Path)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--report-dir", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text())
    releases = manifest.get("releases", [])
    state = json.loads(args.state.read_text()) if args.state.exists() else {"processed": {}}
    processed: dict[str, str] = state.setdefault("processed", {})
    config_asset = args.config_asset.resolve()
    args.work_dir.mkdir(parents=True, exist_ok=True)

    changed = 0
    for release in releases:
        release_id = release["id"]
        pak = (args.manifest.parent / release["pak"]).resolve()
        if not pak.exists():
            raise FileNotFoundError(f"release {release_id}: pak does not exist: {pak}")

        digest = sha256(pak)
        if processed.get(release_id) == digest:
            print(f"skip {release_id}: already processed")
            continue

        inspection = run_tool(args.dotnet, args.tool, "inspect", "--pak", str(pak))
        report = json.loads(inspection.stdout)
        if args.report_dir is not None:
            args.report_dir.mkdir(parents=True, exist_ok=True)
            report_name = hashlib.sha256(release_id.encode("utf-8")).hexdigest()[:16] + ".json"
            (args.report_dir / report_name).write_text(json.dumps({
                "release_id": release_id,
                "pak": str(pak),
                "inspection": report,
            }, indent=2) + "\n")
        markers = report["markers"]
        accepted = sum(marker.get("validation", {}).get("acceptedCount", 0) for marker in markers)
        print(f"check {release_id}: {len(markers)} marker(s), {accepted} accepted map(s)")

        if args.dry_run or not markers or accepted == 0:
            if not args.dry_run:
                processed[release_id] = digest
            continue

        release_work = args.work_dir / release_id.replace("/", "_")
        release_work.mkdir(parents=True, exist_ok=True)
        run_tool(
            args.dotnet,
            args.tool,
            "merge",
            "--marker-pak",
            str(pak),
            "--config-asset",
            str(config_asset),
            "--out-dir",
            str(release_work),
            "--pak-out",
            str(release_work / "UnchainedLoadingScreens.pak"),
        )
        config_asset = (release_work / args.config_asset.name).resolve()
        processed[release_id] = digest
        changed += 1

    if not args.dry_run and changed:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        for suffix in (".uasset", ".uexp", ".ubulk"):
            source = config_asset.with_suffix(suffix)
            if source.exists():
                shutil.copy2(source, args.output_dir / source.name)
        pak = config_asset.parent / "UnchainedLoadingScreens.pak"
        if pak.exists():
            shutil.copy2(pak, args.output_dir / pak.name)
        state["processed"] = processed
        args.state.parent.mkdir(parents=True, exist_ok=True)
        args.state.write_text(json.dumps(state, indent=2) + "\n")
        print(f"wrote final config files to {args.output_dir}")
    elif not args.dry_run:
        print("no new releases required a config update")

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (KeyError, json.JSONDecodeError, FileNotFoundError, subprocess.CalledProcessError) as error:
        print(f"poll failed: {error}", file=sys.stderr)
        raise SystemExit(1)
