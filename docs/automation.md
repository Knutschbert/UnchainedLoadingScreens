# Loading-screen automation

The loading-screen builder is intentionally designed to run locally before it
runs in GitHub Actions. A local run should use exactly the same inputs and
steps as CI; GitHub should only provide the trigger, release download, and
release upload.

## Planned input

Put a fixture pak in `fixtures/` while developing. The first fixture should
contain one mod marker data asset and all files needed to read it. Keep the
fixture outside Git if it is large or proprietary, or use Git LFS if it is
appropriate for this repository.

The marker asset must be accompanied by its sidecar files when extracted:

```text
Marker.uasset
Marker.uexp          # required when present in the source package
Marker.ubulk         # only when present/referenced
```

The target config asset follows the same rule. Never copy only a `.uasset`
when a `.uexp` or `.ubulk` exists.

## Local execution contract

The CLI supports this local form:

```sh
dotnet run --project tools/LoadingScreensPatcher -- \
  inspect --pak fixtures/ExampleMod.pak
```

Marker discovery scans `.uasset` files and checks their exported class and
properties; it does not rely on the marker filename containing `MapInfo`.
The current merge command writes a replacement config pak under `build/`.

The CLI should be deterministic and should not need GitHub credentials. The
GitHub workflow will call the same CLI after downloading the source release.

## Validation rules

For each `DA_MapInfo` marker, validate:

1. at least one `.uasset` exports `DA_MapInfo_C` with the required marker
   properties;
2. the CDO can be read;
3. `Map Variants` can be read;
4. each variant's category name identifies a map asset whose `<name>.umap`
   exists somewhere in the source pak; variants without a matching map are
   ignored;
5. the description is taken from the matching variant description;
6. the image is taken from the matching variant image when present, otherwise
   from `Default Image`;
7. image references outside `/Game/Mods` are considered valid even when the
   referenced file is not in the source pak;
8. an existing config entry is updated by stable map identifier rather than
   duplicated; and
9. the generated package contains both sides of every input package that had
   sidecars.

Whether a texture must physically exist in the marker pak is a separate rule:
the reference may intentionally resolve to a base-game asset.

The validator reports `imageSource` and `imageStatus` for every accepted or
ignored variant. Current statuses are `external-reference`, `in-mod-present`,
`in-mod-missing`, and `missing`. Non-default image-like properties are listed
in `imageProperties` so an unfamiliar schema is visible rather than silently
treated as equivalent to the default-only schema.

## Tool responsibilities

- **CUE4Parse:** inspect and validate cooked exports/properties;
- **UAssetAPI:** modify and save the cooked package;
- **repak:** extract and create pak files;
- **GitHub Actions:** trigger the process and publish its output, not contain
  asset-format logic.

The exact property/export names are confirmed by the first real marker
fixture; the local writer prototype now uses them directly.

## Current fixture findings

The dummy pak is a version-9 pak mounted at
`../../../TBL/Content/Mods/AgMods/KendoPlus/`. Its marker is
`KendoPlus_MapInfo.uasset`, class `DA_MapInfo_C`, with these relevant fields:

```text
DefaultImage       SoftObjectProperty
VariantDescriptions Array<AchievementCategoryTuple>
```

Each description tuple contains `CategoryName` and `SubcategoryName`. In this
fixture `TDM_KendoPlus_Dev` is accepted because `TDM_KendoPlus_Dev.umap` is
present; `TDM_KP_Quarry` is ignored because no matching `.umap` exists. The
fixture has no separate per-variant image property, so both accepted and
future image handling currently use `DefaultImage` unless another marker
schema exposes such a property.

## C2ModRegistry polling source

The registry commit that added `Knutschbert/Hitboxes` confirms the registry
layout and should be treated as the polling contract:

```text
package_db/mod_list_index.txt
package_db/packages/<owner>/<package>.json
```

The package JSON contains a `releases` array. Each release provides:

- `tag`;
- `pak_file_name`;
- a 128-character `hash` (SHA-512);
- `release_date`; and
- a generated `manifest`, including marker paths and `object_class`.

The first-run registry adapter should therefore:

1. read `package_db/mod_list_index.txt` from the registry's `main` branch;
2. fetch each listed package JSON;
3. select the newest release by `release_date`;
4. use `release.info.repo_url`, `tag`, and `pak_file_name` to form the GitHub
   release download URL;
5. verify the downloaded pak against the registry SHA-512 hash;
6. optionally use `manifest.markers` as a fast filter for apparent
   `DA_MapInfo` entries, while retaining the pak scanner as the authoritative
   validation step; and
7. pass only candidate paks to the local polling/merge pipeline.

This avoids guessing repository names and avoids scanning unrelated plugin
releases. The registry's `main` branch is the source; its `db` branch is a
published database artifact and is not needed for this workflow.
