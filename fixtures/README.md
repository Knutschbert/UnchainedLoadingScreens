# Development fixtures

Place local cooked pak fixtures here while developing the asset pipeline.

Do not add a large or proprietary pak to Git accidentally. Prefer Git LFS or
an ignored local copy when appropriate. A useful first fixture is one release
pak containing exactly one marker data asset.

Expected development flow:

```text
fixtures/ExampleMod.pak
        ↓
repak extract (marker asset and sidecars)
        ↓
CUE4Parse inspection
        ↓
CDO validation
        ↓
UAssetAPI config update
        ↓
repak output
```

The current local fixture is under `test-assets/dummy/`. Its large source pak
is intentionally ignored by Git; the extracted `LoadingScreensConfig`
`.uasset`/`.uexp` pair remains available for writer tests.

The marker contract currently targets a `DA_MapInfo` data asset. Its
`Map Variants` provide the descriptions and images:

- variant category names are map asset names;
- a variant is accepted only when `<category name>.umap` exists somewhere in
  the source pak;
- a missing variant image falls back to `Default Image`;
- an image reference outside `/Game/Mods` is accepted without requiring that
  image to be present in the source pak.
