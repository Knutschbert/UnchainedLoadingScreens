#!/usr/bin/env python3
"""Run the offline golden-fixture test for the loading-screen pipeline."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import shutil
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PAK = ROOT / "test-assets/dummy/KendoPlus.pak"
CONFIG = ROOT / "test-assets/dummy/LoadingScreensConfig.uasset"
TOOL_PROJECT = ROOT / "tools/LoadingScreensPatcher/LoadingScreensPatcher.csproj"
TOOL_DLL = ROOT / "tools/LoadingScreensPatcher/bin/Release/net8.0/LoadingScreensPatcher.dll"
BUILD = ROOT / "build/offline-test"


def load_registry_module():
    spec = importlib.util.spec_from_file_location("poll_registry", ROOT / "scripts/poll-registry.py")
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(*args: str, capture: bool = False) -> subprocess.CompletedProcess[str]:
    return subprocess.run(args, check=True, text=True, capture_output=capture)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inspect(pak: Path) -> dict:
    result = run("dotnet", str(TOOL_DLL), "inspect", "--pak", str(pak), capture=True)
    return json.loads(result.stdout)


def merge(config: Path, output: Path, pak_output: Path) -> None:
    run(
        "dotnet",
        str(TOOL_DLL),
        "merge",
        "--marker-pak",
        str(PAK),
        "--config-asset",
        str(config),
        "--out-dir",
        str(output),
        "--pak-out",
        str(pak_output),
    )


def plan(config: Path, output: Path) -> dict:
    result = run(
        "dotnet",
        str(TOOL_DLL),
        "plan",
        "--marker-pak",
        str(PAK),
        "--config-asset",
        str(config),
        "--out",
        str(output),
    )
    return json.loads(output.read_text())


def poll(manifest: Path, state: Path, work: Path, output: Path) -> str:
    result = run(
        "python3",
        str(ROOT / "scripts/poll-release-manifest.py"),
        "--manifest",
        str(manifest),
        "--config-asset",
        str(CONFIG),
        "--tool",
        str(TOOL_DLL),
        "--state",
        str(state),
        "--work-dir",
        str(work),
        "--output-dir",
        str(output),
        "--report-dir",
        str(BUILD / "poll-reports"),
        capture=True,
    )
    return result.stdout


def poll_registry(state: Path, work: Path, output: Path) -> str:
    result = run(
        "python3",
        str(ROOT / "scripts/poll-registry.py"),
        "--registry-dir",
        str(ROOT / "test-assets/registry"),
        "--cache-dir",
        str(BUILD / "registry-cache"),
        "--manifest-out",
        str(BUILD / "registry-manifest.json"),
        "--config-asset",
        str(CONFIG),
        "--tool",
        str(TOOL_DLL),
        "--state",
        str(state),
        "--work-dir",
        str(work),
        "--output-dir",
        str(output),
        "--report-dir",
        str(BUILD / "registry-reports"),
        capture=True,
    )
    return result.stdout


def main() -> int:
    if not PAK.exists():
        raise FileNotFoundError(f"fixture pak not found: {PAK}")
    if not CONFIG.exists():
        raise FileNotFoundError(f"fixture config not found: {CONFIG}")

    run("dotnet", "build", str(TOOL_PROJECT), "-c", "Release", "-warnaserror")
    if BUILD.exists():
        shutil.rmtree(BUILD)
    BUILD.mkdir(parents=True)

    report = inspect(PAK)
    assert report["markerPaths"], "class-based marker discovery found no marker"
    assert any(marker["exports"][0]["className"] == "DA_MapInfo_C" for marker in report["markers"])
    validation = report["markers"][0]["validation"]
    assert validation["acceptedCount"] == 1, validation
    assert validation["ignoredCount"] == 1, validation
    assert validation["imageHandling"] == "default-only", validation
    assert all(entry["imageSource"] == "DefaultImage" for entry in validation["entries"])
    assert all(entry["imageStatus"] == "external-reference" for entry in validation["entries"])

    plan_report = plan(CONFIG, BUILD / "plan.json")
    assert plan_report["addCount"] == 1, plan_report
    assert plan_report["updateCount"] == 0, plan_report
    assert plan_report["ignoredCount"] == 1, plan_report

    first_dir = BUILD / "first"
    first_pak = first_dir / "UnchainedLoadingScreens.pak"
    merge(CONFIG, first_dir, first_pak)
    assert first_pak.exists()
    first_asset = first_dir / CONFIG.name
    first_uexp = first_dir / CONFIG.with_suffix(".uexp").name
    assert first_asset.exists() and first_uexp.exists()

    pak_report = inspect(first_pak)
    assert pak_report["mountPoint"] == "../../../TBL/Content/"
    assert pak_report["fileCount"] == 2
    assert sorted(pak_report["files"]) == [
        "Mods/ArgonSDK/Mods/ModLoading/_Content/Config/LoadingScreensConfig.uasset",
        "Mods/ArgonSDK/Mods/ModLoading/_Content/Config/LoadingScreensConfig.uexp",
    ]

    second_dir = BUILD / "second"
    second_pak = second_dir / "UnchainedLoadingScreens.pak"
    merge(first_asset, second_dir, second_pak)
    assert sha256(first_asset) == sha256(second_dir / CONFIG.name), "repeated merge changed uasset"
    assert sha256(first_uexp) == sha256(second_dir / CONFIG.with_suffix(".uexp").name), "repeated merge changed uexp"

    manifest = BUILD / "multi-release.json"
    manifest.write_text(json.dumps({
        "releases": [
            {"id": "KendoPlus-local", "pak": str(PAK)},
            {"id": "GeneratedConfig-no-marker", "pak": str(first_pak)},
        ]
    }, indent=2) + "\n")
    poll_state = BUILD / "poll-state.json"
    poll_output = BUILD / "poll-output"
    first_poll = poll(manifest, poll_state, BUILD / "poll-work", poll_output)
    state = json.loads(poll_state.read_text())
    assert set(state["processed"]) == {"KendoPlus-local", "GeneratedConfig-no-marker"}, state
    assert "check KendoPlus-local: 1 marker(s), 1 accepted map(s)" in first_poll
    assert "check GeneratedConfig-no-marker: 0 marker(s), 0 accepted map(s)" in first_poll
    assert (poll_output / "UnchainedLoadingScreens.pak").exists()

    second_poll = poll(manifest, poll_state, BUILD / "poll-work", poll_output)
    assert "skip KendoPlus-local: already processed" in second_poll
    assert "skip GeneratedConfig-no-marker: already processed" in second_poll

    registry_state = BUILD / "registry-state.json"
    registry_output = BUILD / "registry-output"
    registry_module = load_registry_module()
    assert registry_module.release_download_url(
        "https://github.com/Fixture/Dummy",
        {"tag": "v1.0.0", "pak_file_name": "Dummy.pak"},
    ) == "https://github.com/Fixture/Dummy/releases/download/v1.0.0/Dummy.pak"
    first_registry = poll_registry(registry_state, BUILD / "registry-work", registry_output)
    registry_manifest = json.loads((BUILD / "registry-manifest.json").read_text())
    assert registry_manifest["releases"][0]["id"] == "Fixture/Dummy@v1.0.0"
    assert "check Fixture/Dummy@v1.0.0: 1 marker(s), 1 accepted map(s)" in first_registry
    assert (registry_output / "UnchainedLoadingScreens.pak").exists()
    reports = list((BUILD / "registry-reports").glob("*.json"))
    assert len(reports) == 1
    assert json.loads(reports[0].read_text())["release_id"] == "Fixture/Dummy@v1.0.0"

    second_registry = poll_registry(registry_state, BUILD / "registry-work", registry_output)
    assert "skip Fixture/Dummy@v1.0.0: already processed" in second_registry

    print("offline pipeline test passed")
    print("  marker: DA_MapInfo_C discovered by class")
    print("  variants: 1 accepted, 1 ignored")
    print("  output: UnchainedLoadingScreens.pak with uasset/uexp")
    print("  idempotency: repeated merge produced identical assets")
    print("  plan: deterministic add/ignore decisions")
    print("  polling: multi-release and no-marker skip")
    print("  registry: local package index and SHA-512 cache")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, subprocess.CalledProcessError) as error:
        print(f"offline pipeline test failed: {error}", file=sys.stderr)
        raise SystemExit(1)
