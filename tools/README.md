# Asset pipeline tools

`LoadingScreensPatcher` is a headless .NET CLI. The first command is an
offline fixture inspector; it extracts `DA_MapInfo` candidates from a pak and
reports their exports/properties plus the `.umap` files available for variant
validation.

Run locally after installing the .NET 8 SDK:

```sh
dotnet run --project tools/LoadingScreensPatcher -- \
  inspect \
  --pak test-assets/dummy/KendoPlus.pak \
  --out build/kendoplus-inspection.json
```

The project deliberately uses UAssetAPI's pak interop so local inspection and
the eventual GitHub Actions implementation do not depend on a globally
installed `repak` executable. A standalone `repak` binary can still be used
for packaging if needed by a later workflow.

## Local merge prototype

The current fixture can be merged into the extracted config asset offline:

```sh
dotnet run --project tools/LoadingScreensPatcher -- \
  merge \
  --marker-pak test-assets/dummy/KendoPlus.pak \
  --config-asset test-assets/dummy/LoadingScreensConfig.uasset \
  --out-dir build/merged
```

The prototype currently updates or adds one `Configs` entry per accepted map.
It uses the marker's `DefaultImage`, writes the resulting `.uasset` and
`.uexp` pair, and packs them at the game’s config virtual path. It
produces `build/merged/UnchainedLoadingScreens.pak` by default and deliberately
does not overwrite the checked-in fixture. Use `--pak-out` only when a custom
output path is needed.

## Local polling simulation

`scripts/poll-release-manifest.py` simulates the first-run/polling workflow
without GitHub. The manifest can contain every release discovered on an
initial scan; the state file makes later runs process only new or changed
release paks.

```sh
dotnet build tools/LoadingScreensPatcher/LoadingScreensPatcher.csproj -c Release
python3 scripts/poll-release-manifest.py \
  --manifest test-assets/dummy/releases.json \
  --config-asset test-assets/dummy/LoadingScreensConfig.uasset \
  --tool tools/LoadingScreensPatcher/bin/Release/net8.0/LoadingScreensPatcher.dll \
  --state build/poll-state.json \
  --work-dir build/poll-work \
  --output-dir build/poll-output
```

Add `--dry-run` to inspect all manifest paks without writing the config or
state. The future GitHub polling adapter only needs to replace the local
manifest with release metadata/downloads; the asset processing remains the
same.

Packages with no `DA_MapInfo_C` marker are recorded as processed and skipped;
they do not fail the polling run.

Pass `--report-dir build/registry-reports` to save one JSON inspection report
per processed release. Reports include pak file lists, marker exports,
accepted/ignored variants, descriptions, image source/status, and skip reasons.
The GitHub workflow uploads these reports as the
`loading-screens-report-<run-number>` artifact.

## Offline golden-fixture test

Run the complete local round-trip test:

```sh
python3 scripts/test-offline-pipeline.py
```

It checks class-based marker discovery, accepted/ignored variants, generated
pak paths, output naming, and repeated-merge idempotency.

## Deterministic plan / dry run

Inspect planned changes without writing assets or a pak:

```sh
dotnet run --project tools/LoadingScreensPatcher -- \
  plan \
  --marker-pak test-assets/dummy/KendoPlus.pak \
  --config-asset test-assets/dummy/LoadingScreensConfig.uasset \
  --out build/loading-screens-plan.json
```

The equivalent merge form is:

```sh
dotnet run --project tools/LoadingScreensPatcher -- \
  merge \
  --marker-pak test-assets/dummy/KendoPlus.pak \
  --config-asset test-assets/dummy/LoadingScreensConfig.uasset \
  --out-dir build/plan-output \
  --dry-run \
  --plan-out build/loading-screens-plan.json
```

The JSON plan reports `add`, `update`, and `ignore` decisions, image source
and status, and the reason for ignored variants.

## Registry adapter

The registry adapter can be run against a local registry checkout or fixture:

```sh
python3 scripts/poll-registry.py \
  --registry-dir test-assets/registry \
  --cache-dir build/registry-cache \
  --manifest-out build/registry-manifest.json \
  --config-asset test-assets/dummy/LoadingScreensConfig.uasset \
  --tool tools/LoadingScreensPatcher/bin/Release/net8.0/LoadingScreensPatcher.dll \
  --state build/registry-state.json \
  --work-dir build/registry-work \
  --output-dir build/registry-output
```

For the live public registry, replace `--registry-dir` with
`--registry-repo Chiv2-Community/C2ModRegistry`. The adapter scans every latest
release by default because registry manifests can label a `DA_MapInfo` asset as
generic `PrimaryDataAsset`. Downloaded paks are cached by their registry
SHA-512 and are never committed to the repository. Use `--manifest-filter` only
as an optional optimization when the registry metadata is known to be reliable.
