# Flowhub Print Interceptor

A Windows 11 tray application that safely controls a Star printer-connected cash drawer for Flowhub Maui.

It observes the original PDF that Flowhub creates and the completed Windows print event. A job is classified only when Flowhub's internal filename type and configured visible PDF header agree:

- transaction receipt: open the drawer automatically;
- fulfillment ticket: keep the drawer locked without prompting;
- unknown, missing, malformed, mismatched, test, or unrelated print: ask the operator.

Closing or ignoring a prompt keeps the drawer locked. The application never records receipt contents or customer details.

## Install

Download the latest [PrintInterceptorSetup.exe](https://github.com/joegullo63/flowhub-print-interceptor/releases/latest/download/PrintInterceptorSetup.exe) on each POS station and run it as the signed-in POS user.

The guided installer:

1. detects installed Windows printers and asks which receipt queue to watch;
2. detects Flowhub's receipt and fulfillment selections, or requires manual confirmation;
3. opens the selected printer's properties and requires confirmation that both Star **Peripheral Unit Timing** values are **None**;
4. asks for the transaction and fulfillment PDF headers;
5. detects Flowhub's live PDF directory;
6. enables the Windows PrintService Operational log, installs the tray app, and registers startup.

The installer is currently unsigned, so Windows SmartScreen may show an unrecognized-app warning. Review the GitHub Release checksum before choosing **More info > Run anyway**. Code signing is recommended before broad deployment.

Do not install JavaPOS. The existing Windows queue can carry the documented StarPRNT drawer pulse as a five-byte RAW job.

## Safe acceptance test

1. Print a real fulfillment ticket. It must print without opening the drawer or prompting.
2. Print a real transaction receipt. The drawer should open exactly once without prompting.
3. Print an unrelated document to the selected receipt queue. It should prompt and default to **Keep Locked** after 30 seconds.
4. Confirm both outcomes in `%LOCALAPPDATA%\PrintInterceptor\logs`.

Flowhub's test receipt/fulfillment assets intentionally follow the unknown-job prompt path; test with real workflow documents.

## Updates

The tray app checks this repository's latest public GitHub Release at startup and every six hours. When a newer version exists, it asks the operator before downloading. The installer and its published SHA-256 checksum must match before Windows is asked for administrator approval.

Updates preserve the terminal's printer, PDF folder, receipt markers, pulse timing, and timeout configuration.

## Build

No third-party development packages are required on Windows 11. The build uses the installed .NET Framework 4.8 compiler:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
```

Outputs:

- `dist\PrintInterceptor.exe`
- `dist\PrintInterceptorSetup.exe` - a single-file portable installer containing the tray application

The build runs a non-hardware self-test covering configuration, Windows event parsing, compressed PDF extraction, update metadata parsing, and drawer-command generation. It sends no printer bytes and cannot open the drawer.

## Release

Update the version in `src/Version.cs`, commit it, and push a matching tag such as `v1.1.0`. GitHub Actions builds the installer, creates its SHA-256 file, and publishes both to a GitHub Release using the repository-scoped `GITHUB_TOKEN`.

## Architecture and safety boundary

Flowhub continues printing normally through its selected Windows queue. The Star driver's automatic drawer timing must remain disabled. Print Interceptor watches Flowhub's source PDFs and Windows PrintService Event 307, then submits the documented StarPRNT primary-peripheral command only after classification or operator approval.

The interceptor fails closed with respect to its own actions: capture, parsing, correlation, configuration, and update failures do not authorize a drawer pulse. It is not a boundary against malicious software or an administrator running as the same Windows user; see [SECURITY.md](SECURITY.md).

For research into the locally installed Flowhub wrapper and why its bundled PDF files do not control live receipt appearance, see [Flowhub desktop integration notes](docs/FLOWHUB-DESKTOP-NOTES.md).

## License

MIT
