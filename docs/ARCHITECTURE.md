# Architecture and Feasibility

**Project:** Development of Software for Videotape Recorder Control and Audiovisual Signal Digitization
**Target framework:** .NET 8 (WPF, Windows only)
**Status:** Phase 0 deliverable

---

## 1. Goals

A Windows desktop application that:

1. Controls a videotape recorder (VTR) over RS-422 using the Sony 9-pin protocol.
2. Captures the analog-to-digital converted video and audio signal from capture devices.
3. Shows a live preview and time information (CTL, LTC, VITC, user bits).
4. Records synchronized video + audio to archival and access formats (H.264, FFV1+PCM/MKV, ProRes).

Target VTRs (via generic implementation + optional device profiles):
Sony PVW-2600P, DVW-M2000P, BVU-950P, UVW-1800P, JVC BR-S622E.

## 2. Solution structure

```
MagnetoskopCapture.sln
├── src/
│   ├── Magnetoskop.Core/                  # Contracts and domain models (no external deps)
│   ├── Magnetoskop.Serial/                # ISerialTransport + System.IO.Ports implementation
│   ├── Magnetoskop.Protocol.Sony9Pin/     # Sony 9-pin protocol (framing, checksum, parsing, controller)
│   ├── Magnetoskop.Capture.Video/         # OpenCvSharp device enumeration + capture loop
│   ├── Magnetoskop.Capture.Audio/         # NAudio device enumeration + capture
│   ├── Magnetoskop.Recording/             # FFmpeg process-based recorder + recording profiles
│   ├── Magnetoskop.Simulation/            # Simulated VTR and simulated capture sources
│   └── Magnetoskop.App/                   # WPF UI, MVVM view models, DI composition root
└── tests/
    ├── Magnetoskop.Core.Tests/
    ├── Magnetoskop.Protocol.Sony9Pin.Tests/
    └── Magnetoskop.Recording.Tests/
```

### Dependency rules

- `Magnetoskop.Core` has **no** dependencies on other project layers or hardware libraries.
- All other `src` projects depend only on `Core` (and their own third-party library).
- `Magnetoskop.App` is the only project referencing everything; it wires services via
  `Microsoft.Extensions.Hosting` dependency injection.
- Hardware access is always behind an interface defined in `Core`
  (`IVtrController`, `IVideoCaptureService`, `IAudioCaptureService`, `ISerialTransport`,
  `IRecordingService`), so `Magnetoskop.Simulation` can substitute every device.

## 3. Module responsibilities

### 3.1 Magnetoskop.Core

- Domain models: `Timecode` (BCD-safe HH:MM:SS:FF value type), `TimecodeSource`
  (CTL/LTC/VITC/…), `UserBits`, `TransportState`, `VtrStatus`, `RecordingProfile`,
  `VtrDeviceProfile`, `CaptureDeviceInfo`, video/audio format descriptors.
- Service abstractions listed above.
- No I/O.

### 3.2 Magnetoskop.Serial

- `ISerialTransport`: async open/close, `WriteAsync(ReadOnlyMemory<byte>, CancellationToken)`,
  `ReadAsync(Memory<byte>, CancellationToken)`, port enumeration.
- `SerialPortTransport` implementation over `System.IO.Ports.SerialPort`.
- Configuration is supplied by the protocol layer / device profile; the transport itself does
  not know anything about the Sony 9-pin protocol.

### 3.3 Magnetoskop.Protocol.Sony9Pin

Layered inside the project:

1. **Framing** – `CommandBlock` (CMD-1 nibble + data count, CMD-2, data, checksum),
   serializer/deserializer, checksum calculation.
2. **Command catalog** – strongly-typed factory methods for the supported commands.
3. **Response parsing** – ACK, NAK (+ error bitmask), Device Type, Status Data,
   time data (LTC/VITC/CTL timers, user bits, corrected/hold variants).
4. **Controller** – `Sony9PinController : IVtrController`:
   - single-flight command queue (protocol forbids overlapping commands),
   - per-command timeout, cancellation, and configurable retry policy,
   - background status/timecode polling with priority given to user commands,
   - communication logging (hex dumps at `Trace`/`Debug` level via `ILogger`),
   - `UnsupportedCommandException` mapping for NAK "Undefined Command" and for commands
     excluded by the active device profile.

### 3.4 Magnetoskop.Capture.Video

- Device enumeration (DirectShow names where obtainable, index-based fallback).
- `OpenCvVideoCaptureService`: background capture thread reading `Mat` frames, publishing
  `VideoFrame` (pixel data + timestamp) into bounded `Channel<T>` consumers:
  - preview consumer (drop-oldest policy; UI never blocks capture),
  - recording consumer (blocking with bounded capacity so encode back-pressure is visible).

### 3.5 Magnetoskop.Capture.Audio

- NAudio (WASAPI capture; WaveInEvent fallback) device enumeration and capture.
- Auto-selection: when the user has not chosen an audio device explicitly, pick the audio
  endpoint whose name best matches the selected video capture device, else system default.
- Publishes PCM buffers with timestamps; exposes peak levels for UI meters.

### 3.6 Magnetoskop.Recording

- **Decision: FFmpeg as an external process**, fed via stdin/named pipes with raw video
  (`rawvideo`, known pixel format/size/rate) and raw PCM audio. OpenCvSharp `VideoWriter`
  is *not* used for recording: it cannot produce FFV1+PCM in Matroska or ProRes reliably,
  and gives no control over interlaced flags or audio muxing.
- `FfmpegRecordingService`: builds the argument list from a `RecordingProfile`, manages the
  process lifetime, monitors stderr for progress/errors, supports cancellation and clean
  finalization (close pipes, wait for trailer write).
- Profiles (initial set):
  - **FFV1 (archival):** `-c:v ffv1 -level 3 -g 1 -slicecrc 1`, PCM s16/s24 audio, MKV.
    Interlacing preserved: no filters by default; field order flagged via
    `-top`/`setfield` metadata according to source configuration.
  - **H.264 (access):** `libx264`, configurable CRF/preset, AAC or PCM audio, MP4/MKV.
    Optional deinterlace only when the user explicitly enables processing.
  - **ProRes:** `prores_ks` with selectable profile (Proxy…HQ), PCM audio, MOV.
- A/V sync: both elementary streams carry wallclock-based timestamps from the capture
  services; the recorder computes the initial offset and instructs FFmpeg accordingly
  (`-itsoffset` / audio resample fill). Details validated in Phase 6.

### 3.7 Magnetoskop.Simulation

- `SimulatedVtr : IVtrController`: full transport state machine (Stop/Play/FF/Rew/Eject +
  future Pause/Record/Jog/Shuttle), synthesized CTL/LTC/VITC counters, artificial latency.
- `SimulatedVideoSource` / `SimulatedAudioSource`: SMPTE-bar-like frames with burned-in
  counter and a sine tone, so the whole pipeline runs without hardware.

### 3.8 Magnetoskop.App (WPF, MVVM)

- CommunityToolkit.Mvvm for observable view models and relay commands.
- Generic Host (`Microsoft.Extensions.Hosting`) composition root; `ILogger` +
  file/debug sinks; user settings persisted to JSON in `%APPDATA%`.
- Views: main window with preview surface (`WriteableBitmap`), transport control bar,
  timecode/status panel, device & profile selection, recording controls, log/error panel.
- All device work happens off the UI thread; view models marshal updates via dispatcher.

## 4. Sony 9-pin protocol summary (from context_source/sony_9pin)

Facts taken from the archived protocol reference (Rick Davies summary of the Sony
DVR-2000/2100 document) and the Drastic VVCR 422 page:

| Item | Value |
|---|---|
| Physical layer | EIA RS-422-A |
| Baud rate | 38,400 bit/s |
| Byte format | 1 start + 8 data + **odd parity** + 1 stop |
| Block format | CMD-1/count, CMD-2, DATA-1..N (N ≤ 15), CHECKSUM |
| Checksum | Lower 8 bits of the sum of all preceding block bytes |
| Response deadline | Slave responds within 9 ms |
| Inter-byte gap | ≤ 10 ms within a block |
| Flow rule | Master sends one command at a time; waits for response |
| ACK | `10 01` |
| NAK | `11 12` + error byte (bit0 undefined cmd, bit2 checksum, bit4 parity, bit5 overrun, bit6 framing, bit7 timeout) |
| NAK handling | Stop transmitting immediately; "undefined command" may be followed immediately, other errors require ≥ 10 ms wait |

Commands used initially:

| Command | Code | Response |
|---|---|---|
| Device Type Request | `00 11` | `12 11` + 2 data bytes |
| Stop | `20 00` | ACK |
| Play | `20 01` | ACK |
| Fast Forward | `20 10` | ACK |
| Rewind | `20 20` | ACK |
| Eject | `20 0F` | ACK |
| Status Sense | `61 20` + start/count byte | `7X 20` status data (10 bytes documented) |
| Current Time Sense | `61 0C` + source bitmask | `74 0X` time / user-bit data |

Time data format (all time responses): DATA-1..4 = frames, seconds, minutes, hours in BCD
(tens digit in the high nibble). For LTC preset/response, bit 7 of DATA-1 = color frame,
bit 6 = drop frame. User bits: 8 binary groups, 2 per data byte.
CTL is exposed as Timer-1/Timer-2 data (`74 00` / `74 01`), selected via the Current Time
Sense mask (Timer-1 = `04`). "Best available timecode" mask `03` returns LTC, VITC, or
corrected LTC (`74 14`) depending on tape speed and signal validity.

Commands planned for later phases (already in the catalog, gated by device profile):
Record `20 02`, Standby On/Off `20 05`/`20 04`, Jog/Var/Shuttle Fwd `2X 11/12/13`,
Jog/Var/Shuttle Rev `2X 21/22/23`, Cue Up With Data `24 31`, Preroll `20 30`,
Edit On/Off `20 65`/`20 64`, Local Enable/Disable `00 1D`/`00 0C`.

### 4.1 Must be verified on documentation or real hardware

These are **not** assumed correct until tested against the actual decks:

1. **Per-model command support.** The reference documents the DVR-2000/2100 command set.
   PVW/UVW/BVU decks implement subsets; the JVC BR-S622E implements a Sony-compatible
   subset whose exact coverage must be probed (NAK "undefined command" responses will be
   used to detect and record unsupported commands per profile).
2. **Status byte count per model.** 10 bytes documented for DVR-2000; smaller decks may
   return fewer. Status Sense will request a conservative count and adapt.
3. **Status bit timing.** The reference explicitly warns that Play/Record status bit
   timing "varies almost from machine to machine". Poll-rate and debounce values in the
   device profiles must be tuned on hardware.
4. **VITC availability** at low tape speeds and per deck (BVU-950P VITC option status).
5. **Device Type codes** for DVW-M2000P, UVW-1800P, BVU-950P, JVC BR-S622E — not present
   in the reference table; will be captured from hardware and stored in profiles.
6. **USB–RS422 adapter behavior** with odd parity at 38.4 kBaud and its added latency
   relative to the 9 ms response window (the PC master is tolerant here, but timing of
   our inter-byte gaps must be checked with a scope/analyzer if problems appear).
7. **Cable pinout.** The reference notes pinouts "vary a lot"; the standard Sony
   controller pinout (1 GND, 2 RxA-, 3 TxB+, 7 TxA-... as listed) must be verified per
   adapter/deck combination.

## 5. Concurrency and reliability model

- **UI thread:** rendering + view models only.
- **Protocol:** one dedicated command loop (`Channel<PendingCommand>`); polling requests
  are enqueued at low priority, user commands at high priority; every command carries a
  `CancellationToken` and timeout.
- **Video capture:** one thread per device; frames flow through bounded channels.
- **Audio capture:** NAudio callback threads; buffers forwarded to bounded channels.
- **Recording:** FFmpeg stdin writers on dedicated tasks; back-pressure is surfaced as a
  "frames dropped / queue full" health metric instead of blocking capture.
- Everything disposable implements `IAsyncDisposable`; the host coordinates ordered
  shutdown (stop recording → stop capture → stop polling → close port).
- Structured logging via `Microsoft.Extensions.Logging` throughout; protocol layer logs
  raw frames in hex at Trace level for offline analysis.

## 6. Feasibility notes

| Concern | Assessment |
|---|---|
| .NET 8 + WPF | Installed runtime confirmed (8.0.x present). |
| OpenCvSharp capture of USB capture sticks/cards | Standard DirectShow/MSMF path; device *names* require a DirectShow enumeration helper (OpenCV exposes only indices). |
| NAudio WASAPI capture | Mature; supports device notifications and level metering. |
| FFmpeg availability | Shipped alongside the app (`ffmpeg.exe` in a `tools/` folder or on PATH); recorder validates presence and encoder support (`ffmpeg -encoders`) at startup. |
| RS-422 from PC | Requires USB→RS422 adapter (e.g., FTDI-based) presenting a COM port; `System.IO.Ports` with odd parity is supported. |
| Interlaced archival capture | Depends on the capture device delivering fields untouched; FFV1 path applies no filtering by default. |

## 7. Development phases

| Phase | Content | Exit criterion |
|---|---|---|
| 0 | This document | Reviewed and approved |
| 1 | Solution skeleton, simulated VTR + simulated capture, WPF shell, DI, logging, tests build | `dotnet build` + `dotnet test` green; app runs against simulation |
| 2 | Full Sony 9-pin protocol + unit tests | Protocol test suite green against fake transport |
| 3 | Real RS-422 communication | Deck responds to transport commands; status/timecode polled |
| 4 | Video device selection + live preview | Live preview at native rate without UI stalls |
| 5 | Audio capture + monitoring | Device selection, auto-select, level meters |
| 6 | FFV1+PCM MKV recording PoC | File plays in VLC/ffprobe verifies streams & sync |
| 7 | H.264 + ProRes profiles | All three profiles produce valid files |
| 8 | Workflow integration | Control deck + capture + record concurrently, errors surfaced |
| 9 | Deck compatibility | Profiles validated on all five machines |
| 10 | Reliability, docs, packaging | Release build, user guide |