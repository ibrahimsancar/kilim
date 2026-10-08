# Security

Kilim moves people's files, so problems that could lose, expose or overwrite a file are taken seriously.

## Reporting

Please do not open a public issue for a security problem. Use GitHub's private reporting instead: **Security** tab, **Report a vulnerability**. You will get an answer within a few days.

Helpful to include: what you did, what happened, and the Windows version.

## What Kilim does and does not do

- It moves files only when you drop them on the rug or take them out, and only with Windows' own file operation (the one Explorer uses). Its moves never overwrite: a taken name gets " (2)".
- Kilim itself never deletes files. When you drag an item out, the drop is handled by Windows exactly as in Explorer, so dropping it on the Recycle Bin deletes it.
- Its own data lives only in `%USERPROFILE%\Kilim`: `items` (the hidden files), `state.json` (rug position, pattern, where each item came from) and, after a crash, `crash.log`. Other folders are touched only to move an item in or out at the user's request. On first run the .NET runtime inside the exe unpacks itself to a temporary folder, as every single-file .NET app does.
- It has no network code and no third-party packages. It refuses files on network locations, including behind a local junction or symbolic link, whose targets it reads from the local disk before Windows would contact a server. A path whose real location Windows cannot report is refused.
- It needs no administrator rights, only reads the registry (the desktop icon size) and does not start with Windows.
- It refuses to hide a drive, its own folder, a folder containing it, or anything in the Recycle Bin, judged both by the path as written and by where junctions and links really lead, including a link to another drive's root.
- If its own folder is itself redirected by a junction or link, it refuses to start rather than put files somewhere you did not choose. Settings are written to a fresh temporary file and swapped in, never through a planted link.
- `state.json` is treated as untrusted: a "came from" path is used only if it really leads to an ordinary folder on a local drive, never a network share, mapped drive, device path or startup folder; otherwise the item goes to the desktop.

## Known limits

- If a large folder move is interrupted (cancelled, or a file inside is locked), the folder can end up partly in each place, as it can in Explorer. Nothing is lost; both parts stay visible.
- Kilim's folder is ordinary user data. A program already running as you can change it, as it could change any of your files.

## Releases

Releases are built only by the [release workflow](.github/workflows/release.yml) from a version tag, with actions pinned to commit hashes; version tags can be created only by the maintainer. Each release has `SHA256SUMS.txt` and a signed build provenance attestation, see [Verify a download](README.md#verify-a-download).
