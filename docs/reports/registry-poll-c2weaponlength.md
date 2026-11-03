# Registry poll failure: C2WeaponLength download source

## Symptom

The scheduled workflow failed during registry polling:

```text
registry poll failed: No download source for ARoese/C2WeaponLength v2.0.1
```

## Evidence

The affected registry package is `package_db/packages/ARoese/C2WeaponLength.json`.
Its package metadata identifies the repository as:

```text
https://github.com/ARoese/C2WeaponLength
```

The release contains `tag: v2.0.1` and `pak_file_name: weaponLength.pak`.
Registry records are not guaranteed to include a release-level `info.repo_url`
or `repo_url` field in every generated checkout/schema variant.

## Root cause

The adapter only searched `release.info.repo_url` and `release.repo_url` when
building a download URL. It did not fall back to the package-level
`latest_release_info.repo_url`.

## Fix status

Fixed in `scripts/poll-registry.py`. The URL source order is now:

1. `release.info.repo_url`;
2. `release.repo_url`;
3. `latest_release_info.repo_url`.

The offline registry fixture now omits release-level `info` and the integration
test verifies the release URL builder and full local polling path.

## Red herrings

- The GitHub runner, .NET build, and Actions cache were not the failure source.
- The affected release has no map-info marker, but that is evaluated only after
  the pak download source is resolved.

## Open questions

None for this failure. A live workflow rerun is still required to verify the
full public-registry download path after the fix.
