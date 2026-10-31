# LanShare

[![Latest release](https://img.shields.io/badge/release-v2.0.0-blue)](https://github.com/Gowsi-kan/LanShare/releases/tag/v2.0.0)

LanShare is a .NET 10 desktop application for transferring files between computers on a local network. It supports large files, folders, and batches of small files, with resumable transfers and a graphical Avalonia UI. An optional browser-based feature lets a phone upload files to the computer or download files the computer has shared.

The current repository release is **v2.0.0**. See the [release notes](LanShare/RELEASE_NOTES_v2.0.0.md) and [GitHub release](https://github.com/Gowsi-kan/LanShare/releases/tag/v2.0.0).

Transfers happen over the local network: there is no cloud relay or account required.

## Features

- Discover other LanShare computers on the LAN, or connect by entering an IP address.
- Queue files and folders before choosing a peer, then send them once a device is selected.
- Send files and folders over an encrypted, authenticated connection. Devices pair by comparing a short code; trusted device fingerprints are saved for future transfers.
- Resume interrupted computer-to-computer transfers using per-file chunk state.
- Bundle small files, use parallel streams, and optionally compress data with adaptive LZ4.
- Pause, cancel, retry, and resume transfers; adjust the outgoing bandwidth limit.
- Review, reorder, and queue outgoing batches; queued batches run one at a time while each transfer keeps its parallel-stream tuning.
- Review incoming file names and choose to rename conflicts, keep existing files, or replace them after the new copy is complete.
- Search local transfer history and queue an in-session retry or reopen a saved destination.
- Start optional phone access to upload files from a browser or download files shared by the desktop app. The UI provides a QR link and a PIN; incoming phone uploads can require approval. HTTPS is optional.
- Check transfer integrity with XXH3 checksums.

## Repository layout

The solution and application projects live in the `LanShare/` subdirectory:

```text
README.md                         This guide
LICENSE                           MIT license
LanShare/
  LanShare.slnx                   .NET solution
  Directory.Build.props           Shared .NET build settings
  src/
    LanShare.Core/                Transfer protocol, sender/receiver, discovery, security
    LanShare.Web/                 Optional browser-based phone sharing server
    LanShare/                     Avalonia desktop app and view models
  tests/LanShare.Tests/           Protocol, transfer, resume, and security tests
  bench/LanShare.Bench/           Loopback transfer benchmark
  scripts/bench-gate.sh           Benchmark comparison helper
```

## Requirements

- .NET 10 SDK. The repository's `global.json` selects the .NET 10 SDK.
- Windows or Linux for the desktop application.
- A local network connection between devices for peer discovery and transfers.

## Build, run, and test

Run these commands from the repository root:

```bash
# Build the complete solution
dotnet build LanShare/LanShare.slnx

# Launch the desktop app
dotnet run --project LanShare/src/LanShare -c Release

# Run the test suite
dotnet test LanShare/tests/LanShare.Tests/LanShare.Tests.csproj -c Release
```

On both computers, launch LanShare, select a discovered peer (or add its IP address), choose files or folders, and start the transfer. The receiver can choose whether to accept the incoming transfer and where to save it. The default receive folder is `Downloads/LanShare`.

### Publish a self-contained single-file app

```bash
# Windows x64
dotnet publish LanShare/src/LanShare -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/win

# Linux x64
dotnet publish LanShare/src/LanShare -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux
```

## Networking and firewall

| Purpose | Protocol | Default port |
|---|---|---:|
| Computer-to-computer transfer | TCP | `45454` |
| Peer discovery | UDP | `45455` |
| Optional phone/browser access | TCP | `45480` |

If a default TCP port is unavailable, the app can bind to another available port. Allow the ports used by the app through the host firewall on the local network. LanShare includes a firewall helper in the desktop UI.

Discovery uses UDP broadcast and may not work across Wi-Fi client isolation or VLANs. In that case, use **Add by IP address**. Phone access displays its active address, port, QR link, and PIN in the app.

## How computer-to-computer transfers work

The transfer implementation is in `LanShare/src/LanShare.Core` and uses the LFS2 protocol:

1. The computers find each other through UDP beacons, or the sender connects to an entered address.
2. They establish a TLS connection and authenticate/pair devices using their certificate fingerprints and a displayed comparison code.
3. The sender offers file metadata. After acceptance, the receiver returns saved chunk state so the sender can skip data already received.
4. Files are divided into 2 MiB chunks. Parallel data streams read and hash chunks, optionally compress them, send frames, and wait for receiver acknowledgements.
5. The receiver verifies each chunk before writing it. A failed data stream reconnects and requeues unacknowledged chunks.

Large files are written to temporary `.lfspart` files, with `.lfsstate` metadata used for resuming. Small files (up to 256 KiB each) are packed into bundles and tracked in a batch journal. On completion, received files are finalized into the destination folder.

## Performance and benchmarking

- Auto mode starts with two streams and adds streams while measured throughput improves, up to eight. Stream count can also be set manually.
- Each stream can have up to eight unacknowledged frames in flight.
- Adaptive LZ4 compression backs off when data is not compressible or compression costs more than it is expected to save.
- Small files are bundled to reduce per-file network overhead.
- Receiver resume checkpoints are written periodically (about every 30 seconds during active transfers).
- The benchmark reports throughput and verifies transferred files. Its loopback results measure local CPU, storage, and protocol overhead; they are not a prediction of Wi-Fi or LAN throughput.

The desktop app stores up to 500 transfer-history records locally. History stores transfer summaries and receive destinations, not outgoing file paths; retry is available for outgoing transfers while their source files remain available in the current app session.

Run a short benchmark from the repository root:

```bash
dotnet run -c Release --project LanShare/bench/LanShare.Bench -- --quick
```

For a repeatable comparison on Linux, use the benchmark gate from the solution directory:

```bash
cd LanShare
scripts/bench-gate.sh --save  # record a baseline on an otherwise idle machine
scripts/bench-gate.sh         # run and compare against that baseline
```

The gate defaults to `/dev/shm` for a lower-variance run. Use `BENCH_MODE=disk scripts/bench-gate.sh` to benchmark against the normal filesystem. See the script comments for options such as benchmark size and directory.

## Phone/browser access

Phone access is separate from the LFS2 computer-to-computer protocol. When enabled in the desktop app, it starts a local web server and displays a QR link and PIN. A phone on the same network can use the browser to send files to the computer or download files selected for sharing. Upload approval can be enabled in the UI, and HTTPS can be selected when starting the server.

The browser server uses a temporary access token and PIN each time it starts. HTTP does not encrypt browser traffic; enable HTTPS when encryption is needed and the phone accepts the certificate.

## Security notes

- Computer-to-computer transfers use TLS and device pairing. The app persists its device identity and trusted-device fingerprints in the user's application data directory.
- Incoming transfers still require receiver acceptance unless auto-accept is enabled.
- The optional browser server is a separate service. Keep it limited to trusted networks; use HTTPS for encrypted browser connections.
- Paths supplied by a peer are validated to prevent writes outside the chosen destination folder.

## License

LanShare is released under the [MIT License](LICENSE).
