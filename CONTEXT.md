# Magnetoskop Capture — Technical Context for Thesis Writing

**Purpose of this file:** Give a future LLM (or human writer) accurate, detailed technical
context for producing a thesis Word document about the whole project. Prefer facts from
this file over guessing. Cross-check with `docs/ARCHITECTURE.md`, `docs/PHASES.md`,
`docs/USER_GUIDE.md`, and `docs/HARDWARE_TESTING.md` when expanding sections.

**Document language note:** The codebase and most docs are in English. A thesis may be
written in another language; keep technical terms (Sony 9-pin, FFV1, LTC/VITC, etc.)
consistent with the literature.

---

## 1. Project identity

| Field | Value |
|---|---|
| **Thesis / project title** | Development of Software for Videotape Recorder Control and Audiovisual Signal Digitization |
| **Application name** | Magnetoskop Capture |
| **Product version** | 1.0.0 (self-contained win-x64 release via `build/publish.ps1`) |
| **Platform** | Windows 10/11, x64 only |
| **Target framework** | .NET 8 (`net8.0` / `net8.0-windows` for WPF and DirectShow) |
| **UI** | WPF + MVVM (CommunityToolkit.Mvvm) |
| **Solution file** | `MagnetoskopCapture.slnx` |
| **Development method** | Incremental phases 0–10; each phase kept the solution buildable |

**Problem domain:** Digitization of analog videotape archives requires (1) remote control
of professional VTRs over RS-422 using the industry Sony 9-pin protocol, and (2)
synchronized capture of the converted video/audio into preservation-oriented and access
formats. Commercial tools exist, but this thesis implements an end-to-end open,
testable pipeline with a custom protocol stack, device profiles, simulation, and
archival recording (FFV1+PCM).

**Target VTR models** (generic Sony 9-pin + optional per-device profiles — not
single-model design):

1. Sony PVW-2600P (Betacam SP)
2. Sony DVW-M2000P (Digital Betacam)
3. Sony BVU-950P (U-matic SP)
4. Sony UVW-1800P (Betacam SP)
5. JVC BR-S622E (S-VHS, Sony-compatible RS-422 subset)

---

## 2. Goals and required functionality

### 2.1 Functional goals

1. Control a VTR over RS-422 (Sony 9-pin).
2. Capture A/D-converted video and audio from PC capture devices.
3. Live preview + real-time time information: CTL, LTC, VITC, user bits.
4. Synchronized recording to archival and access formats.

### 2.2 Required UI/features (implemented)

- Video and audio capture-device selection
- Live video preview (`WriteableBitmap`)
- Audio level meters (WASAPI peak levels)
- Synchronized video + audio recording
- Output path and recording-profile selection
- Transport controls: Play, Stop, Fast Forward, Rewind, Eject
- Real-time recorder status and status flags (tape out, servo lock, etc.)
- CTL / LTC / VITC / user-bit display
- Logging and error reporting (in-app log + file logs + dismissible error bar)
- Settings persistence across sessions
- Preflight warnings before record; optional auto-play of deck on record
- Required PAL/NTSC/Custom and Progressive/TFF/BFF declaration before recording;
  requested-versus-driver-readback acknowledgement persisted for the exact device/format
- Stable DirectShow device-path persistence with best-effort resolution to the current
  transient OpenCV index
- Per-video/audio-device-pair static A/V offset, visibly uncalibrated until measured
- Preview and VTR/timecode fresh → stale → lost indicators with automatic recovery
- Strict recording queues, exact overload/loss counters, explicit
  `Completed`/`Incomplete`/`Faulted` outcomes and awaitable terminal completion
- Adaptive audio resampling against a video-master, QPC-correlated timeline
- Atomic summary JSON plus strict per-frame `.frames.jsonl` audit and retained partial
  summary evidence

### 2.3 Recording profiles (implemented)

| Profile ID | Display name | Video | Audio | Container | Role |
|---|---|---|---|---|---|
| `ffv1-archival` | FFV1 + PCM (Matroska, archival) | FFV1 level 3, GOP 1, 24 slices, slice CRC, yuv422p | PCM s24le | MKV | Archival master; interlacing preserved |
| `h264-access` | H.264 + AAC (MP4, access copy) | libx264 CRF 18, yuv420p | AAC 192k | MP4 | Access / viewing copy |
| `prores-hq` | ProRes HQ + PCM (MOV) | prores_ks HQ, yuv422p10le | PCM s16le | MOV | Post-production |

**Design rule:** OpenCvSharp `VideoWriter` is **not** used for recording. It cannot
reliably produce FFV1+PCM/MKV or ProRes with controlled interlacing and A/V muxing.
Recording is always an external **FFmpeg** process.

---

## 3. Technology stack and rationale

| Technology | Role | Why chosen |
|---|---|---|
| C# / .NET 8 | Language + runtime | Modern async APIs, strong Windows desktop story, DI/logging ecosystem |
| WPF | Desktop UI | Native Windows, good for live preview via `WriteableBitmap` |
| CommunityToolkit.Mvvm | MVVM | Observable properties / relay commands with low boilerplate |
| Microsoft.Extensions.Hosting | DI composition root, logging host | Clean service wiring; testable |
| System.IO.Ports | Serial I/O | USB→RS-422 adapters appear as COM ports; odd parity supported |
| Custom Sony 9-pin stack | Protocol | No suitable maintained .NET library for full archival control needs |
| OpenCvSharp | Video capture + preview frames | DirectShow/MSMF capture path; frame access for UI |
| NAudio (low-level WASAPI) | Audio packets, timing + meters | Retains device/QPC positions and discontinuity flags; WDL sinc resampler |
| FFmpeg (external process) | Encoding + muxing | FFV1, H.264, ProRes, PCM/AAC, Matroska/MP4/MOV in one tool |
| xUnit (+ SkippableFact) | Tests | Unit + integration (ffmpeg) + compatibility suites |
| Bounded `Channel<T>` | Frame/buffer fan-out | Non-blocking role policy: preview drop-oldest; recording reject-new with exact counters |

**Hardware dependencies (optional at development time):** USB→RS-422 adapter
(FTDI recommended), 9-pin remote cable, DirectShow video capture device, WASAPI
audio input. Simulation substitutes all of these.

---

## 4. Solution structure and dependency rules

```
MagnetoskopCapture.slnx
├── src/
│   ├── Magnetoskop.Core/                  # Contracts + domain models (no external deps)
│   ├── Magnetoskop.Serial/                # ISerialTransport + System.IO.Ports
│   ├── Magnetoskop.Protocol.Sony9Pin/     # Framing, commands, parsing, controller
│   ├── Magnetoskop.Capture.Video/         # DirectShow enum + OpenCvSharp capture
│   ├── Magnetoskop.Capture.Audio/         # NAudio WASAPI capture
│   ├── Magnetoskop.Recording/             # FFmpeg recorder + argument builder
│   ├── Magnetoskop.Simulation/            # Simulated VTR, A/V, wire-level decks
│   └── Magnetoskop.App/                   # WPF UI, MVVM, DI root, workflow
├── tests/
│   ├── Magnetoskop.Core.Tests/
│   ├── Magnetoskop.Protocol.Sony9Pin.Tests/
│   ├── Magnetoskop.Recording.Tests/
│   ├── Magnetoskop.App.Tests/
│   └── Magnetoskop.Compatibility.Tests/
├── docs/                                  # Architecture, phases, user guide, hardware protocol
├── build/publish.ps1                      # Release packaging
├── context_source/sony_9pin/              # Archived protocol references (inputs, not code)
├── README.md, CONTEXT.md
└── artifacts/                             # Publish output (gitignored)
```

### Dependency rules (architectural invariants)

1. `Magnetoskop.Core` has **no** dependencies on other project layers or hardware libraries.
2. Other `src` projects depend only on `Core` (+ their own third-party package).
3. `Magnetoskop.App` is the **only** project that references everything; it is the
   composition root via Generic Host DI.
4. All hardware access is behind Core interfaces:
   `IVtrController`, `ISerialTransport`, `IVideoCaptureService`,
   `IAudioCaptureService`, `IRecordingService`.
5. Therefore Simulation (and fakes in tests) can replace every device without UI changes.

---

## 5. Module-by-module technical detail

### 5.1 Magnetoskop.Core

**Role:** Pure domain + contracts. No I/O.

**Key models:**

- `Timecode` — HH:MM:SS:FF value type; frame-count conversion; drop-frame / color-frame flags
- `TimecodeSource` — Ctl, Ctl2, Ltc, Vitc, CorrectedLtc, HoldVitc, HoldLtc
- `UserBits` — 4 bytes / 8 binary groups
- `TimeInformation` — CTL/LTC/VITC and LTC/VITC user bits with independent wall-clock
  and normalized QPC receipt times, source kinds, and 1 s stale / 3 s lost classification
- `TransportState` / `TransportCommand` — transport modes and commands (required five + reserved)
- `VtrStatus` — connected, transport, local, tape out, servo lock/ref missing, rec inhibit, EOT, alarms, reverse
- `VtrDeviceProfile` — id, display name, optional device-type code, supported commands
  (empty = permissive), poll intervals, response timeout, retries, frame rate, LTC/VITC flags
- `RecordingProfile` — codec/container options; three defaults above
- Capture models: `CaptureDeviceInfo`, `VideoFormat`, `VideoFrame` (delivery sequence +
  100 ns monotonic delivery time), `AudioFormat`, `AudioBuffer` (first device sample,
  sample count, QPC/device positions and WASAPI flags)
- `CaptureMonotonicClock` — process-wide QPC/`Stopwatch` normalization to 100 ns units;
  values are monotonic positions, not UTC
- `CaptureSubscription<T>` — disposable role-aware bounded reader with exact accepted,
  consumed, eviction, rejection, depth and high-water counters
- `VideoInputConfiguration` / `VideoInputFormatStatus` — explicit standard/scan request,
  actual size/rate readback and scan-readback provenance
- `RecordingResult`, `RecordingOutcome`, `RecordingStopReason`, `RecordingMetrics` —
  terminal integrity contract and synchronization/queue/sample evidence

**Key abstractions:**

- `IVtrController` — connect/disconnect, transport commands, status/time events
- `ISerialTransport` — open/close, write/read, discard input; `SerialSettings` defaults
  38400 / 8 / odd / 1 (Sony 9-pin)
- `IVideoCaptureService` / `IAudioCaptureService` — enumerate, start/stop and create
  removable preview/monitor/recording subscriptions
- `IConfigurableVideoCaptureService` — requested input configuration and driver readback
- `IRecordingService` — calibrated start, reasoned stop, live status, committed-frame
  event, and awaitable terminal `Completion`

### 5.2 Magnetoskop.Serial

- `SerialPortTransport` implements `ISerialTransport` over `System.IO.Ports.SerialPort`
- Writes whole command blocks in one call (keeps inter-byte gaps ≪ 10 ms at 38400 baud)
- Maps I/O errors to `VtrCommunicationException`
- `SerialPortEnumerator` lists COM ports for the UI
- **No protocol knowledge** in this layer

### 5.3 Magnetoskop.Protocol.Sony9Pin

Layered protocol implementation (facts from archived references under
`context_source/sony_9pin` — Rick Davies / DVR-2000 summary and Drastic VVCR 422 page):

| Layer | Types | Responsibility |
|---|---|---|
| Framing | `CommandBlock` | CMD-1/count, CMD-2, ≤15 data bytes, checksum = low 8 bits of sum; `TryParse`, checksum errors |
| BCD | `Bcd` | Digits + 4-byte timecode (frames, seconds, minutes, hours); CF/DF on frames byte; user bits raw |
| Commands | `Sony9PinCommands` | Factories: Local Enable/Disable, Device Type, Stop/Play/Record/Standby/Eject/FF/Rew/Preroll, Jog/Var/Shuttle, Cue Up, Status Sense, Current Time Sense (+ `TimeSenseRequest` bitmask; BestTimecode = 0x03) |
| Responses | `Sony9PinResponseParser`, `StatusBitsParser` | ACK `10 01`, NAK `11 12`+bits, Device Type `12 11`, Status `7X 20`, time/user-bit `74 xx` |
| Wire exchange | `Sony9PinTransceiver` | Single-flight (semaphore); timeout (default 100 ms, looser than 9 ms spec for USB adapters); retries timeouts / corrupt / transmission NAKs with ≥10 ms wait; does **not** retry undefined-command NAK |
| Controller | `Sony9PinController : IVtrController` | Open port, Device Type Request, fast status/best-TC/CTL polling plus slower staggered explicit LTC/VITC and both user-bit polls, independent receipt timestamps, link freshness, profile gating, NAK→`UnsupportedCommandException`, capability learning |
| Profiles | `KnownDeviceProfiles` | Generic + five target decks; PVW-2600P documented type `21 40`; others `DeviceTypeCode = null` until hardware |
| Learning | `DeviceCapabilities` | Records supported/unsupported from ACK / NAK-undefined at runtime |

**Required transport commands on the wire:**

| Command | Code |
|---|---|
| Stop | `20 00` |
| Play | `20 01` |
| Fast Forward | `20 10` |
| Rewind | `20 20` |
| Eject | `20 0F` |

Also: Device Type `00 11`, Status Sense `61 20`, Current Time Sense `61 0C`.

### 5.4 Magnetoskop.Capture.Video

- `DirectShowDeviceEnumerator` — COM interop (`ICreateDevEnum` / `IPropertyBag`) reads
  friendly name and stable `DevicePath`/display moniker. At open it resolves that ID to
  the current numeric OpenCV DSHOW index. This resolution is **best-effort** because
  DirectShow and OpenCV do not guarantee identical enumeration order.
- `OpenCvVideoCaptureService` — dedicated capture thread; applies requested
  PAL/NTSC/Custom width/height/rate and reads back what the driver opened; scan mode and
  field order remain operator declarations because OpenCV cannot measure them.
- Frames are normalized to BGR24 and receive a delivery sequence and host-QPC timestamp
  after OpenCV delivers/copies them. This is not a hardware acquisition timestamp.
- Strict recording subscribers are published before best-effort preview subscribers.
  Publication never blocks the capture thread. Preview uses exact-counted `DropOldest`;
  recording uses exact-counted `RejectNew`.
- Consecutive read-failure cutoff, format-change health, completed subscriptions and
  idempotent cleanup are implemented. Preview UI is stale after 500 ms and lost after
  2 s without delivery.

### 5.5 Magnetoskop.Capture.Audio

- `WasapiPacketCapture` uses NAudio's low-level `AudioClient`/`AudioCaptureClient` and the
  extended `GetBuffer` call. Every packet retains first device-sample position, sample
  count, WASAPI QPC position (100 ns), discontinuity, silence and timestamp-error flags.
- `NAudioCaptureService` publishes PCM sample-aligned buffers and per-channel peak
  metering. If device QPC is invalid, it estimates a first-sample host time, marks the
  packet, and the final recording is `Incomplete` because timing telemetry was degraded.
- Audio subscribers use the same removable role/overflow contract as video. Hardware
  callbacks never wait for an encoder or UI consumer.
- Auto-select: token-overlap scoring between video device name and audio endpoints;
  fallback to default endpoint; UI manual override tracked

### 5.6 Magnetoskop.Recording

- `FfmpegArgumentsBuilder` — pure, unit-tested CLI composition
  - Video: rawvideo on **stdin** (`pipe:0`)
  - Audio: PCM on a **Windows named pipe**
  - FFV1 archival flags; H.264 with optional interlaced DCT flags; ProRes HQ;
    field-order metadata when interlaced and `AllowProcessing=false` (no deinterlace)
- `FfmpegLocator` — configured path → app folder / `ffmpeg/` → PATH; version probe
- `FfmpegRecordingService`
  - Starts FFmpeg/connects its audio pipe before arming approximately two-second strict
    media subscriptions; authoritative start is that arm time, not the button click.
  - Applies the persisted device-pair offset, selects the first video frame not earlier
    than calibrated audio, and explicitly counts early video/PCM as startup trims. It
    does not synthesize silence or use FFmpeg `-itsoffset`.
  - Video is the master timeline. Because FFmpeg can block while probing/interleaving its
    two pipe inputs, audio may be submitted against the strict video queue's accepted,
    contiguous prefix rather than only frames whose pipe write has already completed.
    This mux lookahead is bounded by the approximately two-second strict queue. The live
    raw-video input does not use `-fflags nobuffer`, and the live encode has no
    `-shortest` limiter because either can deadlock the blocking-input handshake.
  - After a successful live FFmpeg exit, any lookahead triggers a finite-file stream-copy
    pass with `-t <committed-video-duration> -map 0 -c copy`. It writes a same-directory,
    same-extension temporary file and replaces the first-pass media only after exit code
    zero and a nonempty output. Failure preserves the playable original and returns
    `Incomplete/FinalizationTrimFailed`. Metrics distinguish submitted samples, the
    application-side final sample budget, and interleave-lookahead trims.
  - `AvDriftEstimator`: 10 s warm-up, rolling 30 s least-squares fits and ≤1 Hz update,
    combining audio/video rate difference with a residual-phase controller.
  - `AdaptivePcmResampler`: NAudio WDL sinc; correction clamped to ±1000 ppm and slewed
    at ≤50 ppm/s. Residual over 10 ms sets a warning; residual over one field for 5 s or
    drift outside the correctable bound stops the session incomplete.
  - Detects strict queue overflow, video sequence/audio device-position gaps, WASAPI
    discontinuity/timing fallback, timestamp regression, format changes, stalls, low
    disk space and premature encoder exit.
  - The first stop reason freezes one QPC cutoff. Stop removes subscribers, drains
    accepted data only to that cutoff (including partial audio-buffer/end trims), flushes
    the WDL resampler without inserting silence, closes stdin/pipe (EOF), waits ≤15 s for
    trailer write, then uses a kill fallback. `MediaFinalized` is recorded.
  - Normal stop/orderly shutdown → `Completed`; detectable integrity failure with an
    FFmpeg trailer → playable `Incomplete`; startup/encoder/internal failure → `Faulted`.

### 5.7 Magnetoskop.Simulation

| Component | Role |
|---|---|
| `SimulatedVtr` | High-level `IVtrController`: transport state machine, synthesized CTL/LTC/VITC/user bits, ~8 ms latency, 25 Hz publication |
| `SimulatedVideoCaptureService` | 720×576@25 PAL color bars with motion |
| `SimulatedAudioCaptureService` | 48 kHz stereo 1 kHz tone + peaks |
| `SimulatedRecordingService` | Stub consumer (historical; app uses FFmpeg in production DI) |
| `SimulatedDeckTransport` | Wire-level fake deck behind `ISerialTransport` (independent framing/BCD) |
| `DeckPersonality` / `DeckPersonalities` | Per-model quirks for five target decks |

**Why wire-level decks matter for the thesis:** Compatibility tests run the **real**
protocol stack against a foreign deck implementation, not the protocol talking to itself.

Deck personality examples:

- PVW-2600P: device type `21 40`, 10 status bytes
- BVU-950P: 9 status bytes, no VITC (option assumption)
- JVC BR-S622E: no Device Type response; NAKs unsupported transport; slower latency;
  only Play/Stop/FF/Rew/Eject accepted
- Unknown type codes use placeholder `7F xx` so Generic fallback is exercised

### 5.8 Magnetoskop.App

**Composition root:** `App.xaml.cs` — Generic Host, DI, file logger, global exception
handlers (`DispatcherUnhandledException` marked handled; unobserved task exceptions
observed) so a UI fault does not kill an active recording.

**Services:**

- `VtrConnectionService` — runtime switch Simulator ↔ COM port + profile; facade
  `IVtrController`; exposes `DetectedProfile`, `LearnedCapabilities`, `CapabilityLearned`
- `CompositeVideoCaptureService` / `CompositeAudioCaptureService` — merge real + simulated devices
- `CaptureSessionCoordinator` — preflight/auto-play; one fresh VTR snapshot for naming
  and start metadata; format-acknowledgement gate; calibrated recorder start; terminal
  completion monitoring; atomic partial/final summary; strict per-frame audit
- `FrameAuditWriter` — bounded reject-new JSONL writer, flush at least once per second;
  overload/failure stops the session incomplete
- `VtrObservationHistory` — nearest preceding independently timestamped observation for
  each committed frame; interpolation only for fresh raw LTC/VITC during forward Playing
  with fresh servo lock
- `SettingsService` — `%AppData%\MagnetoskopCapture\settings.json`; stable device IDs,
  per-device input format, exact format acknowledgements and per-device-pair calibration
- `FileLoggerProvider` — daily rolling logs under `%AppData%\MagnetoskopCapture\logs\` (14-day retention)

**UI (`MainWindow` + `MainViewModel`):**

- Preview surface with 500 ms stale / 2 s lost overlay; independently aged time panel
  (CTL/LTC/VITC/UB, `STALE`, `CORR`, `HOLD`), recorder connection/link status
  (incl. identified profile + learned capabilities), capture devices + meters,
  required signal declaration/readback acknowledgement, device-pair calibration,
  recording panel with residual/drift/required/applied correction and distinct terminal
  outcomes, transport bar, dismissible error bar, log list
- All hardware work off UI thread; dispatcher marshaling for bindings

**Recording file naming:**
`capture_yyyyMMdd_HHmmss[_TChh-mm-ss-ff].{ext}` plus:

- `.frames.jsonl` — committed frame ordinal/source number/QPC offset and causal VTR association;
- `.json.partial` — initial/update evidence retained if orderly finalization fails;
- `.json` — atomically produced terminal metadata including outcome/reason,
  `MediaFinalized`, requested/actual signal, calibration, queues/losses, sample trims,
  synchronization metrics, audit reconciliation and first/last committed associations.

---

## 6. Sony 9-pin protocol facts (must stay accurate in the thesis)

| Item | Value |
|---|---|
| Physical layer | EIA RS-422-A |
| Baud rate | 38,400 bit/s |
| Byte format | 1 start + 8 data + **odd parity** + 1 stop |
| Block | CMD-1/count, CMD-2, DATA (≤15), CHECKSUM |
| Checksum | Lower 8 bits of sum of preceding block bytes |
| Slave response deadline | 9 ms (PC master uses longer timeout for USB adapters) |
| Inter-byte gap | ≤ 10 ms within a block |
| Flow | Master: one command at a time; wait for response |
| ACK | `10 01` |
| NAK | `11 12` + error bits (undefined / checksum / parity / overrun / framing / timeout) |
| NAK policy | Undefined-command: next command allowed immediately; other errors: wait ≥ 10 ms |

**Explicitly unverified until real hardware** (ARCHITECTURE §4.1 / HARDWARE_TESTING.md):

1. Per-model command subsets (esp. JVC)
2. Status byte count per model
3. Status bit timing / debounce per machine
4. VITC availability (BVU-950P option)
5. Device Type codes for DVW-M2000P, BVU-950P, UVW-1800P, JVC
6. USB–RS422 adapter latency at odd parity / 38.4 kbaud
7. Cable pinout per adapter/deck combination

**Thesis implication:** Software validates the stack against simulated decks; hardware
protocol document defines how to fill remaining assumptions into profiles.

---

## 7. Concurrency and reliability model

| Concern | Design |
|---|---|
| UI thread | Rendering + VM bindings only |
| Protocol | Single-flight exchange; user commands vs polling via shared transceiver lock |
| Shared clock | Process QPC normalized to 100 ns for video delivery, audio device/QPC correlation and VTR receipt; not UTC or shared hardware clock |
| Video capture | Dedicated OpenCV thread; strict subscribers first; non-blocking bounded fan-out |
| Audio capture | Low-level WASAPI packet thread; device/sample/QPC provenance; non-blocking fan-out |
| Preview/monitor under load | `DropOldest`, exact eviction count; last state remains responsive |
| Recording under load | `RejectNew`; first strict rejection requests incomplete stop; exact counters; about 2 s capacity |
| Recording | Dedicated video/audio pumps, drift estimator/WDL resampler, process monitor, watchdog and finalizer |
| Per-frame audit | Independent bounded JSONL pump; flush ≤1 s; rejection makes integrity incomplete |
| Shutdown order | Stop/drain/finalize recording and sidecars → stop capture → stop polling → close port |
| Disk safety | Preflight warning; mid-record stop before disk full |
| Stall safety | Watchdog stops incomplete after no video/audio capture or write progress for 15 s |
| Unhandled exceptions | Logged + surfaced; recording not killed by UI exceptions |

Capture queue continuity is provable only from successful publication into the strict
application subscription onward. An OpenCV delivery sequence cannot reveal loss inside
the device/driver before the frame reached the capture loop.

---

## 8. Testing strategy and current metrics

Test counts change as the reliability suite grows; use the latest `dotnet test` result
rather than copying the historical ~197 figure into the thesis.

| Suite | What it establishes |
|---|---|
| Core | Timecode/drop-frame math; exact preview eviction and strict rejection counters; disposal/restart; monotonic timing/freshness; signal configuration/readback rules |
| Protocol Sony9Pin | Framing, BCD, responses, status bits, retries, optional staggered polls, independent observation ages and lost-link recovery |
| Recording | FFmpeg argument construction and real FFmpeg integration when available; reliability acceptance also requires deterministic initial-alignment, terminal-condition and drift/resampler tests |
| App | Preflight/naming/settings; exact format acknowledgement and calibration persistence; frame-audit causality/overflow; atomic sidecars for terminal paths |
| Compatibility | Real controller versus five independently implemented wire-level deck personalities |

**Key testing ideas for the thesis:**

- Protocol layer tested against in-memory `FakeSerialTransport` (no hardware).
- Compatibility suite tests against independently implemented `SimulatedDeckTransport`.
- Recording integration tests execute real ffmpeg when available.
- App coordinator tested with fakes (no WPF MessageBox dependency via override).
- Freshness tests use fake clocks so fresh → stale → lost → recovered is deterministic.
  Accelerated multi-hour ±50/±100/±500 ppm drift cases are an acceptance requirement;
  record them in the release-candidate test evidence rather than assuming them from code.

Automated success does **not** replace the physical four-hour flash/tone test. The current
hardware result tables in `docs/HARDWARE_TESTING.md` remain blank/pending.

---

## 9. Development phases (0–10)

| Phase | Content | Status |
|---|---|---|
| 0 | Architecture and feasibility (`docs/ARCHITECTURE.md`) | Done |
| 1 | Solution skeleton + simulated hardware + WPF shell | Done |
| 2 | Sony 9-pin protocol + unit tests | Done |
| 3 | Real RS-422 (`SerialPortTransport`) + connection UI | Done |
| 4 | Video device selection + live preview | Done |
| 5 | Audio capture + monitoring + auto-select | Done |
| 6 | FFV1+PCM MKV recording PoC via FFmpeg | Done |
| 7 | H.264 + ProRes profiles | Done |
| 8 | Full workflow: DI, settings, preflight, auto-play, errors, file logs | Done |
| 9 | Simulated-deck compatibility suite + hardware validation protocol | Done (physical decks pending) |
| 10 | Reliability, evidence sidecars, active A/V correction, docs, release packaging | Implemented in software; four-hour/device hardware acceptance pending |

Detailed narratives: `docs/PHASES.md`.

---

## 10. End-to-end data / control flows

### 10.1 Transport control

```
UI → MainViewModel → VtrConnectionService → Sony9PinController
  → Sony9PinTransceiver → ISerialTransport (SerialPortTransport or SimulatedDeckTransport)
  → RS-422 adapter → VTR REMOTE port
```

Status/timecode reverse path: polling → events → VM → UI bindings.

### 10.2 Capture + preview

```
Capture device → OpenCvVideoCaptureService → strict Recording subscription → FFmpeg stdin
                           ↘ best-effort Preview subscription → WriteableBitmap + freshness
Audio device → low-level WASAPI packet reader → strict Recording subscription
                                             → WDL resampler → FFmpeg named pipe
                                             ↘ peak levels → UI meters
```

### 10.3 Recording session

```
Record click → optional Start preview → PreflightWarnings (+ dialog)
  → require signal declaration + exact readback acknowledgement
  → optional EnsurePlayingAsync (Play + wait servo lock)
  → CaptureSessionCoordinator writes initial .json.partial + opens .frames.jsonl
  → FFmpeg starts/connects audio pipe → strict subscriptions arm (authoritative start)
  → calibrated first-point trim → video-master encode + active audio drift correction
  → committed frame → causal VTR association → bounded JSONL writer
Stop/fault → remove admission → drain accepted media → FFmpeg trailer
  → atomically write final .json; delete partial only after success
```

---

## 11. Packaging and runtime artifacts

- **Publish:** `powershell -ExecutionPolicy Bypass -File build/publish.ps1`
  - Runs Release tests
  - Self-contained win-x64 publish of `Magnetoskop.App`
  - Copies README + docs
  - Writes `PUT_FFMPEG_HERE.txt` (FFmpeg not redistributed — licensing)
  - Zips to `artifacts/MagnetoskopCapture-<version>-win-x64.zip`
- **Settings:** `%AppData%\MagnetoskopCapture\settings.json`
- **Logs:** `%AppData%\MagnetoskopCapture\logs\magnetoskop_YYYYMMDD.log`

---

## 12. Known limitations and future work (honest thesis material)

1. **Physical deck validation pending** — device-type codes, status sizes, timing,
   VITC options still to be filled from `docs/HARDWARE_TESTING.md` results into
   `KnownDeviceProfiles` / `DeckPersonalities`.
2. **No shared hardware timebase.** Audio device/sample positions are correlated to host
   QPC by WASAPI and audio is actively resampled against a video-master host-delivery
   timeline. The saved calibration corrects a static pair offset. This is software
   correlation, not genlock or proof of a common device clock.
3. **OpenCV delivery boundary.** A video timestamp is assigned after OpenCV delivers and
   copies a frame. Sequence/gap and exact queue counters prove only application-level
   continuity; the driver may have lost or repeated data before delivery.
4. **Physical four-hour A/V validation pending.** ±ppm simulations and software tests do
   not establish end-to-end flash/tone alignment on the target combined capture device.
5. **Stable video identity is best-effort.** The app persists DirectShow DevicePath/
   moniker and resolves it to a current OpenCV index, but the two enumeration orders are
   not guaranteed equal. The operator must verify picture after USB changes.
6. **Scan/order readback is unavailable in OpenCV.** The operator explicitly declares
   PAL/NTSC/Custom and Progressive/TFF/BFF and acknowledges the exact size/rate readback;
   field order still needs signal/device verification.
7. **WASAPI shared mode** may resample; exclusive/bit-exact mode is not implemented.
   A packet without valid device QPC is marked and makes the session incomplete.
8. **Interlaced archival quality** depends on the capture device delivering unprocessed
   fields; software preserves field order when `AllowProcessing=false` but cannot invent
   true interlaced source if the device deinterlaces.
9. **Response timeout** deliberately loose (100–150 ms) until adapter latency is measured.
10. **FFmpeg not bundled** with the release zip.
11. Reserved transport commands (preroll/record/edit) exist in the
   catalog/profiles but are not first-class UI features yet.
12. A hard process/OS/power failure cannot atomically finalize media. The early partial
    JSON and flushed JSONL are evidence of interruption, not a claim that the file is
    complete or playable.

---

## 13. Suggested thesis document structure (for the writing LLM)

Use this as a chapter outline; fill with prose grounded in sections above:

1. **Introduction** — digitization needs, motivation, goals, thesis structure
2. **Background** — videotape formats/decks, RS-422, Sony 9-pin, timecode (CTL/LTC/VITC),
   archival codecs (FFV1), related software
3. **Requirements analysis** — functional/non-functional, target machines, constraints
4. **System architecture** — layered design, dependency rules, interfaces, concurrency
5. **Protocol implementation** — framing, checksum, transceiver, controller, profiles,
   capability learning; citation of protocol references
6. **Capture subsystem** — stable-ID caveat; explicit signal declaration/readback;
   OpenCV delivery/QPC boundary; low-level WASAPI device/sample/QPC provenance;
   role-aware strict versus best-effort queues; preview freshness
7. **Recording subsystem** — why FFmpeg, stdin+named pipe design, profiles, interlacing,
   video-master initial alignment, calibration, adaptive WDL resampling, stop/outcome
   semantics, watchdog/disk safety, frame audit and atomic sidecars
8. **Application layer** — WPF/MVVM, workflow coordinator, timecode/link freshness,
   format/calibration settings, logging, UX
9. **Simulation and testing** — SimulatedVtr vs SimulatedDeckTransport, test pyramid,
   deterministic queue/freshness tests, required accelerated drift cases, FFmpeg
   inspection and compatibility suite
10. **Deployment** — packaging, runtime dependencies, user workflow
11. **Evaluation** — simulation results; four-hour flash/tone and overload protocol;
    hardware results or explicit pending status; integrity scope and limitations
12. **Conclusion and future work**

**Figures worth generating:** solution dependency diagram; protocol exchange sequence;
capture→record pipeline; UI layout sketch; deck personality comparison table;
phase timeline.

**Sources to cite (present in repo):**

- `context_source/sony_9pin/` archived protocol pages
- `docs/ARCHITECTURE.md`, `docs/PHASES.md`, `docs/HARDWARE_TESTING.md`, `docs/USER_GUIDE.md`
- FFV1 archival practice literature (external; not in repo)
- OpenCvSharp, NAudio, FFmpeg documentation (external)

---

## 14. Quick reference — important source files

| Topic | Path |
|---|---|
| DI / host | `src/Magnetoskop.App/App.xaml.cs` |
| Main VM | `src/Magnetoskop.App/ViewModels/MainViewModel.cs` |
| Main UI | `src/Magnetoskop.App/Views/MainWindow.xaml` |
| Workflow | `src/Magnetoskop.App/Services/CaptureSessionCoordinator.cs` |
| Frame audit | `src/Magnetoskop.App/Services/FrameAuditWriter.cs`, `VtrObservationHistory.cs` |
| VTR switch | `src/Magnetoskop.App/Services/VtrConnectionService.cs` |
| Protocol controller | `src/Magnetoskop.Protocol.Sony9Pin/Sony9PinController.cs` |
| Transceiver | `src/Magnetoskop.Protocol.Sony9Pin/Sony9PinTransceiver.cs` |
| Profiles | `src/Magnetoskop.Protocol.Sony9Pin/KnownDeviceProfiles.cs` |
| Serial | `src/Magnetoskop.Serial/SerialPortTransport.cs` |
| Video capture | `src/Magnetoskop.Capture.Video/OpenCvVideoCaptureService.cs` |
| Video identity | `src/Magnetoskop.Capture.Video/DirectShowDeviceEnumerator.cs` |
| Audio capture/timing | `src/Magnetoskop.Capture.Audio/NAudioCaptureService.cs`, `WasapiPacketCapture.cs` |
| FFmpeg recorder | `src/Magnetoskop.Recording/FfmpegRecordingService.cs` |
| Drift/resampling | `src/Magnetoskop.Recording/AvDriftEstimator.cs`, `AdaptivePcmResampler.cs` |
| Capture subscription contract | `src/Magnetoskop.Core/Abstractions/CaptureSubscriptions.cs` |
| Arg builder | `src/Magnetoskop.Recording/FfmpegArgumentsBuilder.cs` |
| Wire-level decks | `src/Magnetoskop.Simulation/SimulatedDeckTransport.cs` |
| Personalities | `src/Magnetoskop.Simulation/DeckPersonality.cs` |
| Compatibility tests | `tests/Magnetoskop.Compatibility.Tests/DeckCompatibilityTests.cs` |
| Publish script | `build/publish.ps1` |

---

## 15. Current project state (snapshot)

- Phases **0–10 plus the reliability/A/V-correlation upgrade are implemented in
  software**: normalized monotonic timing, strict recording admission, active audio drift
  correction, explicit signal declaration, freshness indicators and evidence sidecars.
- App runs against **simulator** without hardware; real COM ports and capture devices
  work when present.
- Automated suites cover Core, protocol, recording/FFmpeg, app workflow/evidence and
  five simulated-deck personalities. Record the exact count and pass/skip output from the
  release-candidate test run; the historical ~197 count is obsolete.
- Release packaging produces self-contained **win-x64** zip; smoke-tested startup with
  file logging.
- Remaining work for a complete empirical thesis evaluation: execute
  `docs/HARDWARE_TESTING.md` on the physical decks and target capture device, including
  stable-ID/format tests, device-pair calibration, the four-hour flash/tone soak and
  controlled encoder overload; then update profiles and insert measured results.
- Until that is done, the defensible conclusion is **software-correlated and
  instrumented, hardware validation pending**. Do not claim a shared hardware clock,
  proven driver-level frame continuity, or proven multi-hour end-to-end synchronization.

*End of CONTEXT.md — keep this file updated if major architecture or phase status changes.*
