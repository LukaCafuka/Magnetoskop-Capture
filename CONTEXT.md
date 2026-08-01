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
- Sidecar JSON metadata next to each recording

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
| NAudio (WASAPI) | Audio capture + meters | Mature WASAPI capture and peak metering |
| FFmpeg (external process) | Encoding + muxing | FFV1, H.264, ProRes, PCM/AAC, Matroska/MP4/MOV in one tool |
| xUnit (+ SkippableFact) | Tests | Unit + integration (ffmpeg) + compatibility suites |
| Bounded `Channel<T>` | Frame/buffer fan-out | Back-pressure; preview drop-oldest; recording bounded |

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
- `TimecodeSource` — Ctl, Ctl2, Ltc, Vitc, CorrectedLtc, HoldVitc
- `UserBits` — 4 bytes / 8 binary groups
- `TimeInformation` — snapshot of CTL/LTC/VITC/user bits + primary source
- `TransportState` / `TransportCommand` — transport modes and commands (required five + reserved)
- `VtrStatus` — connected, transport, local, tape out, servo lock/ref missing, rec inhibit, EOT, alarms, reverse
- `VtrDeviceProfile` — id, display name, optional device-type code, supported commands
  (empty = permissive), poll intervals, response timeout, retries, frame rate, LTC/VITC flags
- `RecordingProfile` — codec/container options; three defaults above
- Capture models: `CaptureDeviceInfo`, `VideoFormat`, `VideoFrame`, `AudioFormat`, `AudioBuffer`

**Key abstractions:**

- `IVtrController` — connect/disconnect, transport commands, status/time events
- `ISerialTransport` — open/close, write/read, discard input; `SerialSettings` defaults
  38400 / 8 / odd / 1 (Sony 9-pin)
- `IVideoCaptureService` / `IAudioCaptureService` — enumerate, start/stop, subscribe channels
- `IRecordingService` — start/stop with profile + capture sources; `RecordingStatus`

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
| Controller | `Sony9PinController : IVtrController` | Open port, Device Type Request, background poll (status + best TC + CTL + user bits), profile gating, NAK→`UnsupportedCommandException`, capability learning |
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

- `DirectShowDeviceEnumerator` — COM interop (`ICreateDevEnum` / `IPropertyBag`) for
  device **names** in OpenCV DSHOW index order
- `OpenCvVideoCaptureService` — dedicated capture thread; format detection (PAL 25
  fallback); BGR24 normalization; bounded channel fan-out (drop-oldest for preview);
  consecutive-failure cutoff; targets `net8.0-windows`

### 5.5 Magnetoskop.Capture.Audio

- `NAudioCaptureService` — WASAPI capture; 16-bit PCM at endpoint rate (≤2 ch);
  timestamped buffers; per-channel peak metering
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
  - Subscribe capture streams **before** starting ffmpeg
  - Start video pump immediately (ffmpeg probes stdin before opening second input —
    waiting on audio pipe first deadlocks)
  - Graceful stop: cancel pumps → close stdin/pipe (EOF) → wait ≤15 s → kill fallback
  - Premature exit detection; stderr rolling tail in errors
  - **Watchdog:** stall if no frames for 15 s; stop+finalize if free disk < 500 MB

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
- `CaptureSessionCoordinator` — preflight warnings, auto-play, timecode-stamped paths,
  start/stop recording, JSON sidecar metadata, low-disk preflight (~10 GB warn)
- `SettingsService` — `%AppData%\MagnetoskopCapture\settings.json`
- `FileLoggerProvider` — daily rolling logs under `%AppData%\MagnetoskopCapture\logs\` (14-day retention)

**UI (`MainWindow` + `MainViewModel`):**

- Preview surface, time panel (CTL/LTC/VITC/UB), recorder connection, recorder status
  (incl. identified profile + learned capabilities), capture devices + meters,
  recording panel (profiles, output folder, auto-play checkbox), transport bar,
  dismissible error bar, log list
- All hardware work off UI thread; dispatcher marshaling for bindings

**Recording file naming:**
`capture_yyyyMMdd_HHmmss[_TChh-mm-ss-ff].{ext}` plus sidecar `.json`
(device, VTR, profile, start/end TC/CTL, user bits, frame counts).

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
| Video capture | One thread per device; bounded channels |
| Audio capture | NAudio callbacks → bounded channels |
| Recording | Dedicated pump tasks to ffmpeg stdin / named pipe |
| Preview under load | Drop-oldest; recording path has priority |
| Shutdown order | Stop recording → stop capture → stop polling → close port |
| Disk safety | Preflight warning; mid-record stop before disk full |
| Stall safety | Watchdog faults session after no frames for 15 s |
| Unhandled exceptions | Logged + surfaced; recording not killed by UI exceptions |

---

## 8. Testing strategy and current metrics

| Suite | Approx. tests | What it proves |
|---|---:|---|
| Core | 17 | Timecode math; SimulatedVtr transport/timecode rules |
| Protocol Sony9Pin | 80 | Framing, BCD, responses, status bits, transceiver retries, controller, profiles |
| Recording | 28 | Argument builder; real ffmpeg integration (skippable if ffmpeg absent) for FFV1/H.264/ProRes |
| App | 17 | Preflight matrix, naming, auto-play, sidecar, settings round-trip/corruption |
| Compatibility | 55 | Real controller vs five wire-level deck personalities |
| **Total** | **~197** | All green as of last full run |

**Key testing ideas for the thesis:**

- Protocol layer tested against in-memory `FakeSerialTransport` (no hardware).
- Compatibility suite tests against independently implemented `SimulatedDeckTransport`.
- Recording integration tests execute real ffmpeg when available.
- App coordinator tested with fakes (no WPF MessageBox dependency via override).

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
| 10 | Reliability, docs, release packaging | Done |

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
Capture device → OpenCvVideoCaptureService → Channel → PreviewLoop → WriteableBitmap
                                             ↘ Channel → FfmpegRecordingService (stdin)
Audio device → NAudioCaptureService → Channel → FfmpegRecordingService (named pipe)
                       ↘ PeakLevels → meter timer → UI
```

### 10.3 Recording session

```
Record click → optional Start preview → PreflightWarnings (+ dialog)
  → optional EnsurePlayingAsync (Play + wait servo lock)
  → CaptureSessionCoordinator.StartRecordingAsync (path + metadata)
  → FfmpegRecordingService.StartAsync
Stop → StopAsync (finalize container) → write sidecar JSON
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
2. **A/V sync** relies on ffmpeg pacing inputs against declared rates; long-term drift
   compensation (drop/dupe vs audio clock) is not fully hardened for multi-hour runs.
3. **WASAPI shared mode** may resample; exclusive/bit-exact mode not implemented.
4. **Interlaced archival quality** depends on the capture device delivering unprocessed
   fields; software preserves field order when `AllowProcessing=false` but cannot invent
   true interlaced source if the device deinterlaces.
5. **Response timeout** deliberately loose (100–150 ms) until adapter latency measured.
6. **FFmpeg not bundled** with the release zip.
7. Reserved transport commands (jog/shuttle/cue/preroll/record/edit) exist in the
   catalog/profiles but are not first-class UI features yet.
8. OpenCV DSHOW index ↔ DirectShow name order should be sanity-checked on machines
   with multiple capture devices.

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
6. **Capture subsystem** — video (OpenCV/DirectShow), audio (NAudio), preview, metering
7. **Recording subsystem** — why FFmpeg, stdin+named pipe design, profiles, interlacing,
   watchdog/disk safety, sidecar metadata
8. **Application layer** — WPF/MVVM, workflow coordinator, settings, logging, UX
9. **Simulation and testing** — SimulatedVtr vs SimulatedDeckTransport, test pyramid,
   compatibility suite results (~197 tests)
10. **Deployment** — packaging, runtime dependencies, user workflow
11. **Evaluation** — simulation results; hardware protocol status; limitations
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
| VTR switch | `src/Magnetoskop.App/Services/VtrConnectionService.cs` |
| Protocol controller | `src/Magnetoskop.Protocol.Sony9Pin/Sony9PinController.cs` |
| Transceiver | `src/Magnetoskop.Protocol.Sony9Pin/Sony9PinTransceiver.cs` |
| Profiles | `src/Magnetoskop.Protocol.Sony9Pin/KnownDeviceProfiles.cs` |
| Serial | `src/Magnetoskop.Serial/SerialPortTransport.cs` |
| Video capture | `src/Magnetoskop.Capture.Video/OpenCvVideoCaptureService.cs` |
| Audio capture | `src/Magnetoskop.Capture.Audio/NAudioCaptureService.cs` |
| FFmpeg recorder | `src/Magnetoskop.Recording/FfmpegRecordingService.cs` |
| Arg builder | `src/Magnetoskop.Recording/FfmpegArgumentsBuilder.cs` |
| Wire-level decks | `src/Magnetoskop.Simulation/SimulatedDeckTransport.cs` |
| Personalities | `src/Magnetoskop.Simulation/DeckPersonality.cs` |
| Compatibility tests | `tests/Magnetoskop.Compatibility.Tests/DeckCompatibilityTests.cs` |
| Publish script | `build/publish.ps1` |

---

## 15. Current project state (snapshot)

- Phases **0–10 implemented** in software.
- App runs against **simulator** without hardware; real COM ports and capture devices
  work when present.
- **~197 automated tests** passing (protocol, recording with real ffmpeg when present,
  app workflow, five-deck compatibility).
- Release packaging produces self-contained **win-x64** zip; smoke-tested startup with
  file logging.
- Remaining work for a complete empirical thesis evaluation: execute
  `docs/HARDWARE_TESTING.md` on the five physical decks and update profiles with
  measured device-type codes, status sizes, timings, and command coverage.

*End of CONTEXT.md — keep this file updated if major architecture or phase status changes.*
