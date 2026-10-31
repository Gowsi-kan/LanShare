# LanShare v2.0.0

LanShare v2.0.0 brings secure, resumable transfers and optional phone sharing to a cross-platform desktop app for local networks.

## Highlights

- **Encrypted computer-to-computer transfers:** TLS-protected connections with device identity, fingerprint trust, and a short-code pairing flow.
- **Resume interrupted transfers:** large files use chunk state to resume after disconnects, cancellation, or restart.
- **Phone and browser sharing:** optionally upload from a phone to the computer or download files shared by the desktop app. The UI provides a QR link and PIN, with optional upload approval and HTTPS.
- **Faster transfers:** parallel streams, adaptive LZ4 compression, small-file bundling, a larger in-flight window, combined frame writes, and reused source-file handles.
- **Transfer controls:** pause, cancel, retry/resume, live bandwidth limits, transfer progress, and integrity checks.
- **Cross-platform desktop app:** Windows and Linux self-contained x64 builds are attached below.

## Downloads

- **Windows x64:** `LanShare-v2.0.0-win-x64.zip`
- **Linux x64:** `LanShare-v2.0.0-linux-x64.tar.gz`
- **SHA-256 checksums:** `SHA256SUMS.txt`

For Linux, extract the archive and run `chmod +x LanShare` if needed, then launch `./LanShare`. Both packages are self-contained and include the .NET runtime.

## Network requirements

- TCP `45454` for computer-to-computer transfers
- UDP `45455` for peer discovery
- TCP `45480` by default for optional phone access (the app may select another port if unavailable)

Allow the ports used by the app through the local firewall. If UDP discovery is blocked by Wi-Fi isolation or VLANs, connect using the peer's IP address.

## Security notes

Computer-to-computer transfers use TLS and device pairing. Phone/browser access is a separate service; use HTTPS when encrypted browser traffic is required. Only enable phone access on a network you trust.

The Windows build is not code-signed, so SmartScreen may show a warning.
