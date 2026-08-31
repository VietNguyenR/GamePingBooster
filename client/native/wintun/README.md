# wintun.dll

This file is **not committed to the repository** (see `.gitignore`). It is a third-party binary
and must be the official signed build, not a copy of unknown provenance.

## Getting it

1. Download from https://www.wintun.net/ (latest release, currently 0.14.1).
2. Extract `bin/amd64/wintun.dll`.
3. Put it in this directory. `GamePingBooster.Service.csproj` copies it next to the executable
   at build time.

## Verify the signature before using it

```powershell
Get-AuthenticodeSignature .\wintun.dll | Format-List Status, SignerCertificate
```

`Status` must be `Valid` and the signer must be **WireGuard LLC**. If either is not true, stop.
This is a driver that runs in kernel space; it is not a place to take chances.

## Notes

- Only the **amd64** build is needed. The client targets `win-x64` only.
- The `.sys` driver is embedded inside the DLL. There is no `.inf` and nothing to run `pnputil`
  against; the first `WintunCreateAdapter` call installs it.
- The calling process must run as **LocalSystem**. Administrator is not enough:
  `WintunCreateAdapter` fails with access denied. This is why the engine lives in a Windows
  Service rather than in the UI application.
- When the process exits the adapter disappears, and every route pointing at it goes with it.
  That is the safety brake: if the app crashes, the user's networking heals itself.
