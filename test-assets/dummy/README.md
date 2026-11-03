# Dummy fixture

`KendoPlus.pak` is a local development fixture and is ignored by Git because
of its size. It contains a `DA_MapInfo` marker and map assets for exercising
the marker validator.

The extracted target package is:

```text
LoadingScreensConfig.uasset
LoadingScreensConfig.uexp
```

Keep the pak and extracted files from the same fixture revision. The eventual
offline test should extract the marker from the pak rather than relying only
on a separately copied marker asset.
