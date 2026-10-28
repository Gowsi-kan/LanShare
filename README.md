<div align="center">

# 📡 LanShare

**Fast, resumable large-file transfer over your local network.**

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Avalonia 11](https://img.shields.io/badge/Avalonia-11-8B44AC)
![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-blue)
![Protocol](https://img.shields.io/badge/protocol-LFS2-orange)

</div>

LanShare is a cross-platform desktop app for moving big files and huge folders between machines on the same LAN, with no cloud, no accounts and no USB stick. It uses a custom TCP protocol (**LFS2**) with parallel streams, adaptive compression and crash-safe resume, so a pulled cable or a cancelled transfer never means starting over.

**Built with:** .NET 10 · Avalonia 11 · MVVM (CommunityToolkit.Mvvm) · raw async TCP · UDP discovery

---

## ✨ Features

- **Auto-tuned parallelism**: starts with 2 streams and adds more while throughput keeps improving (up to 8), or pin it manually (1–16).
- **Adaptive LZ4 compression**: applied per chunk, only when it saves at least 10%; backs off automatically on incompressible data.
- **Resume anywhere**: cancel, crash or cable pull, then *Retry / Resume* continues from the saved state.
- **Small-file bundling**: thousands of tiny files are packed into bundles, with no per-file round trips.
- **Live bandwidth limit**: adjustable in the UI (MB/s) and applied immediately across all streams and sessions.
- **Integrity checks**: every chunk is verified with XXH3 before it is persisted.
- **Peer discovery**: devices find each other via UDP broadcast, or add one by IPv4 address.
- **Built-in firewall helper**: one click to open the required ports.
- **Responsive UI**: the UI thread never blocks on disk or network.

---

## 📁 Repository layout

```text
src/LanShare.Core     protocol, sender/receiver, discovery, firewall helper (library)
src/LanShare          Avalonia desktop app
bench/LanShare.Bench  repeatable loopback benchmark
LanShare.slnx         solution (dotnet build LanShare.slnx)
```

---

## 🚀 Getting started

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or .NET 9: change `net10.0` in `Directory.Build.props`)

### Build & run

```bash
dotnet build LanShare.slnx
dotnet run -c Release --project src/LanShare
```

Run LanShare on both machines, pick the peer, choose files, and send.

### Publish a single-file executable

```bash
# Windows
dotnet publish src/LanShare -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win

# Linux
dotnet publish src/LanShare -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux
```

---

## 🔥 Firewall & networking

| Purpose           | Protocol | Port    |
| ----------------- | -------- | ------- |
| File transfer     | TCP      | `45454` |
| Peer discovery    | UDP      | `45455` |

The app has a **Network access** card. If the ports are blocked, click **Allow through firewall**:

- **Windows**: prompts for UAC; rules are scoped to *Private* and *Domain* networks.
- **Linux**: uses polkit/`pkexec` with firewalld or ufw.

**Manual equivalent:** allow inbound TCP 45454 and UDP 45455 on every machine.

> **Discovery not working?** Wi-Fi client isolation or VLANs can block UDP broadcast. Use **Add by IPv4 address** instead.

---

## 🧠 How it works: the LFS2 protocol

<details>
<summary><b>Connections & framing</b></summary>

- **Control connection:** `LFS2|kind=1` → `Offer` (files, sizes, keys) → `Answer` (accept / decline / no-space, plus per-file resume bitmaps) → … → `Done`.
- **Data connections** (auto-tuned 2..8, or fixed 1..16): `LFS2|kind=2|sessionId`, then frames:

  ```text
  [fileIdx i32][chunkIdx i32][rawLen i32][wireLen i32][xxh3(raw) u64][payload]
  ```

  The receiver acks 1 byte per frame, in order, after decompress, verify and persist. `wireLen < rawLen` means the payload is LZ4-compressed.
- **Bundles:** `fileIdx = -1` marks a bundle, with many small files (≤ 256 KiB) packed into one frame.

</details>

<details>
<summary><b>Pipelining & reliability</b></summary>

- Per stream: read/hash/compress → socket write → ack reader, with up to **4 unacked frames** in flight. The receiver mirrors this (reader → channel → writer), so network and disk overlap on both ends.
- Files are split into **2 MiB chunks** served from a shared queue. If a stream fails, its unacked chunks are re-queued and the stream reconnects.

</details>

<details>
<summary><b>Receiver & resume</b></summary>

- Pre-allocated `.lfspart` file with random-access writes.
- `.lfsstate` bitmap (flush data → atomic bitmap replace), then rename on completion.
- Small files are written whole and tracked in a per-batch journal (`.lfs-<id>.journal`, removed on success).
- Cancel, crash or cable pull → **Retry / Resume** continues from the saved state.

</details>

<details>
<summary><b>Security of paths</b></summary>

Paths received from the peer are sanitized: no `..`, no rooted paths, and no escaping the target folder.

</details>

---

## ⚡ Performance

| Feature             | Behavior |
| ------------------- | -------- |
| **Auto-tune**       | Starts with 2 streams, adds one while throughput improves ≥ 8%, then settles (max 8). |
| **Compression**     | Adaptive LZ4 per chunk; needs ≥ 10% savings, backs off for 64 chunks after 4 misses. |
| **Bandwidth limit** | Shared token-style limiter across all streams/sessions, adjustable live (MB/s). |
| **Small files**     | Packed into bundles: no per-file round trips and no per-file open handles on the receiver. |

### Benchmark

```bash
# Create a baseline
dotnet run -c Release --project bench/LanShare.Bench -- --size-mb 1024 --small-count 20000 --out base.json

# Later, after changes: compare against it
dotnet run -c Release --project bench/LanShare.Bench -- --baseline base.json --tolerance 0.15
```

The benchmark runs over loopback, so it measures CPU, disk and protocol overhead rather than your NIC. It verifies every file by hash and **exits with code 2 on regression**.

---

## 🛡️ Hang & stuck-state protections

- The UI thread never blocks on disk or network: free-space probe, file scanning, session construction and shutdown all run off-thread.
- **Sender:** 120 s ack watchdog per stream (alive-but-stalled receiver → reconnect and resend).
- **Receiver:** fails a session if the sender opens no data streams for 90 s, or delivers nothing for 5 min.
- The bandwidth limiter re-evaluates every 250 ms, so lowering or removing the limit applies immediately.
- A transfer at 100% shows *"Finalizing - flushing to disk"* until the receiver has fsynced and renamed the file.

---

## ⚠️ Current limitations

LanShare is designed for **trusted LANs**. Not included yet:

- [ ] Encryption / authentication (currently relies on a trusted network plus an explicit accept prompt)
- [ ] Settings persistence
- [ ] Whole-file end-to-end hash

---

## 🤝 Contributing

Issues and pull requests are welcome. Before opening a PR, run the benchmark against a baseline to make sure performance hasn't regressed.

## 📄 License

Add your license here (e.g. MIT) and include a `LICENSE` file in the repo root.
