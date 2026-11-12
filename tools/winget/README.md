# winget manifests

`manifests/` mirrors the layout of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), so a version folder can be copied there as is.

After a package is accepted, people can install it with:

```powershell
winget install acdclove1234-commits.Zeph-Iris
```

## Submitting a version

1. Copy `manifests/a/acdclove1234-commits/Zeph-Iris/<version>/` into a fork of microsoft/winget-pkgs at the same path.
2. For a new version: update `PackageVersion`, `InstallerUrl`, `InstallerSha256` (the release asset's sha256, upper case), `ReleaseDate` and `ReleaseNotesUrl`.
3. Open a pull request. The winget-pkgs bots validate the manifests, scan the installer and install it in a sandbox; a moderator merges it.

Notes:
- The installer is Inno Setup, installs per machine and asks for elevation itself (`ElevationRequirement: elevatesSelf`).
- A silent fresh install does not start the app; an upgrade restarts it if it was running.
- The installer adds the signed PawnIO driver when it is missing.
