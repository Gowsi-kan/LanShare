# LanShare

LAN large-file transfer. .NET 10 | Avalonia 11 | MVVM (CommunityToolkit.Mvvm) | raw async TCP | UDP discovery.

## Layout
    src/LanShare.Core   protocol, sender/receiver, discovery, firewall helper (library)
    src/LanShare        Avalonia desktop app
    bench/LanShare.Bench  repeatable loopback benchmark
    LanShare.slnx       solution (dotnet build LanShare.slnx)

## Run
    dotnet run -c Release --project src/LanShare

## Publish (single file)
    dotnet publish src/LanShare -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win
    dotnet publish src/LanShare -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux

(Change `net10.0` in Directory.Build.props if you only have .NET 9.)

## Firewall
The app has a **Network access** card: if the ports are blocked, click **Allow through firewall**
(UAC on Windows; polkit/pkexec on Linux with firewalld or ufw). Windows rules are scoped to Private + Domain networks.
Manual equivalent: allow inbound **TCP 45454** and **UDP 45455** on every machine. If discovery is blocked
(Wi-Fi client isolation, VLANs), use "Add by IPv4 address".

## Protocol (LFS2)
- Control conn: `LFS2|kind=1` -> Offer(files, sizes, keys) -> Answer(accept/decline/no-space + per-file resume bitmaps) -> ... -> `Done`.
- Data conns (auto-tuned 2..8, or fixed 1..16): `LFS2|kind=2|sessionId`, then frames
  `[fileIdx i32][chunkIdx i32][rawLen i32][wireLen i32][xxh3(raw) u64][payload]`; receiver acks 1 byte per frame, in order,
  after decompress + verify + persist. `wireLen < rawLen` means the payload is LZ4-compressed.
- `fileIdx = -1` is a **bundle**: many small files (<= 256 KiB) packed into one frame.
- Pipelining: per stream, read/hash/compress -> socket write -> ack reader, up to 4 unacked frames in flight; the receiver
  mirrors it (reader -> channel -> writer), so network and disk overlap on both ends.
- 2 MiB chunks from a shared queue; on any stream failure its unacked chunks are re-queued and the stream reconnects.
- Receiver: pre-allocated `.lfspart`, random-access writes, `.lfsstate` bitmap (flush data -> atomic bitmap replace),
  rename on completion. Small files: written whole, tracked in a per-batch journal (`.lfs-<id>.journal`, removed on success).
  Cancel/crash/cable pull -> "Retry / Resume" continues from the saved state.
- Paths from the peer are sanitized (no `..`, no rooted paths, no escape from the target folder).

## Performance features
- **Auto-tune**: starts with 2 streams, adds one while throughput improves >= 8%, then settles (max 8).
- **Compression**: adaptive LZ4 per chunk; needs >= 10% savings, backs off for 64 chunks after 4 misses (incompressible data).
- **Bandwidth limit**: shared token-style limiter across all streams/sessions, adjustable live in the UI (MB/s).
- **Small files**: packed into bundles; no per-file round trips, no per-file open handles on the receiver.

## Benchmark
    dotnet run -c Release --project bench/LanShare.Bench -- --size-mb 1024 --small-count 20000 --out base.json
    # later, after changes:
    dotnet run -c Release --project bench/LanShare.Bench -- --baseline base.json --tolerance 0.15
Runs over loopback (measures CPU/disk/protocol overhead, not your NIC). Verifies every file by hash; exit 2 on regression.

## Hang / stuck-state protections
- UI thread never blocks on disk/network: free-space probe, file scanning, session construction and shutdown all run off-thread.
- Sender: 120 s ack watchdog per stream (alive-but-stalled receiver -> reconnect + resend).
- Receiver: fails a session if the sender opens no data streams for 90 s or delivers nothing for 5 min.
- Bandwidth limiter re-evaluates every 250 ms, so lowering / removing the limit applies immediately.
- A transfer at 100% shows "Finalizing - flushing to disk" until the receiver has fsynced and renamed the file.

## Not included (yet)
Encryption/auth (trusted LAN + explicit accept prompt), settings persistence, whole-file end-to-end hash.
