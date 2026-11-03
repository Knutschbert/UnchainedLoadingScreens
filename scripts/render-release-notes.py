#!/usr/bin/env python3
"""Render human-readable notes from registry metadata and inspection reports."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def report_for(report_dir: Path, release_id: str) -> dict | None:
    name = hashlib.sha256(release_id.encode("utf-8")).hexdigest()[:16] + ".json"
    path = report_dir / name
    return json.loads(path.read_text()) if path.exists() else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--reports", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--registry-sha", default="unknown")
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text())
    included: list[tuple[str, dict]] = []
    ignored: list[tuple[str, dict]] = []
    source_notes: list[tuple[str, str, str]] = []

    for release in manifest.get("releases", []):
        release_id = release["id"]
        if release.get("release_notes_markdown"):
            source_notes.append((release_id, release.get("release_url") or "", release["release_notes_markdown"]))
        report = report_for(args.reports, release_id)
        if not report:
            continue
        for marker in report.get("inspection", {}).get("markers", []):
            for entry in marker.get("validation", {}).get("entries", []):
                (included if entry.get("accepted") else ignored).append((release_id, entry))

    lines = [
        "# UnchainedLoadingScreens update",
        "",
        f"Generated from C2ModRegistry commit `{args.registry_sha}`.",
        "",
        "## Loading-screen entries included",
        "",
    ]
    if included:
        for release_id, entry in included:
            lines.append(
                f"- **{entry.get('categoryName') or '(unnamed)'}** — "
                f"{entry.get('description') or '(no description)'} "
                f"(source: `{release_id}`, image: `{entry.get('imageSource')}`, "
                f"status: `{entry.get('imageStatus')}`)"
            )
    else:
        lines.append("- No new loading-screen entries were included.")

    lines.extend(["", "## Ignored variants", ""])
    if ignored:
        for release_id, entry in ignored:
            lines.append(
                f"- `{entry.get('categoryName') or '(unnamed)'}` from `{release_id}` — "
                f"{entry.get('ignoredReason') or 'not accepted'}"
            )
    else:
        lines.append("- None.")

    lines.extend(["", "## Source release notes", ""])
    if source_notes:
        for release_id, url, notes in source_notes:
            lines.append(f"### {release_id}")
            if url:
                lines.append(f"Source release: {url}")
            lines.extend(["", notes.strip(), ""])
    else:
        lines.append("No source release notes were provided.")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text("\n".join(lines).rstrip() + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
