# Phase Log

## Phase 0 — Architecture and feasibility ✅

**Deliverable:** `docs/ARCHITECTURE.md`

- Solution structure and module responsibilities defined.
- Sony 9-pin protocol facts extracted from `context_source/sony_9pin`
  (serial settings, block format, checksum, timing, command/response codes).
- Items that must be verified on hardware are explicitly listed (§4.1).
- FFmpeg-external-process decision documented (OpenCV VideoWriter rejected for recording).

**Acceptance criteria:** architecture reviewed and approved. ✅ (approved with Phase 1)

---

## Phase 1 — Solution skeleton with simulated hardware ✅

### Completed work

- `MagnetoskopCapture.slnx` with 8 source projects + 3 xUnit test projects (all net8.0).
- **Magnetoskop.Core** — contracts and models only, no external dependencies:
  - Models: `Timecode`, `TimeInformation`/`UserBits`/`TimecodeSource`,
    `VtrStatus`/`TransportState`/`TransportCommand`, `VtrDeviceProfile`,
    `RecordingProfile` (H.264 / FFV1+PCM MKV / ProRes defaults),
    capture models (`CaptureDeviceInfo`, `VideoFormat`, `VideoFrame`, `AudioFormat`, `AudioBuffer`).
  - Abstractions: `IVtrController`, `ISerialTransport`+`SerialSettings` (Sony defaults:
    38400/8/odd/1), `IVideoCaptureService`, `IAudioCaptureService`, `IRecordingService`,
    exceptions `UnsupportedCommandException`, `VtrCommunicationException`.
- **Magnetoskop.Simulation** — full stand-ins for all hardware:
  - `SimulatedVtr`: transport state machine (Play/Stop/FF/Rew/Eject), synthesized
    CTL/LTC/VITC/user-bit values with realistic behavior (LTC unreadable at wind speed,
    VITC only near play speed), tape-out handling, ~8 ms simulated command latency,
    25 Hz status/time publication.
  - `SimulatedVideoCaptureService`: 720×576@25 PAL color bars with motion, bounded
    `Channel<VideoFrame>` fan-out with drop-oldest for slow consumers.
  - `SimulatedAudioCaptureService`: 48 kHz stereo 1 kHz tone, peak metering, auto-select hook.
  - `SimulatedRecordingService`: consumes both streams (exercises back-pressure),
    counts frames, publishes `RecordingStatus`; replaced by FFmpeg in Phase 6.
- **Magnetoskop.App** — WPF, MVVM (CommunityToolkit.Mvvm), Generic Host DI + logging:
  - `MainViewModel`: transport commands, device selection with audio auto-select
    (manual override tracked), preview via `WriteableBitmap`, output path + profile
    selection, recording start/stop, error reporting, in-app log panel.
  - `MainWindow`: preview surface, CTL/LTC/VITC/user-bits display, recorder status +
    flags, transport bar, device pickers, recording panel, log list.
  - Everything hardware-facing runs off the UI thread; dispatcher marshaling in the VM.
- **Empty scaffolds** ready for later phases: `Magnetoskop.Serial` (System.IO.Ports
  referenced), `Magnetoskop.Protocol.Sony9Pin`, `Magnetoskop.Capture.Video`,
  `Magnetoskop.Capture.Audio`, `Magnetoskop.Recording`.
- **Tests** (20 passing): Timecode conversion/formatting round-trips; SimulatedVtr
  transport transitions, timecode advance/visibility rules, eject/tape-out errors,
  unsupported-command and disconnected-error paths; recording profile defaults.

### Modified/created files

- `docs/ARCHITECTURE.md`, `docs/PHASES.md`, `.gitignore`, `MagnetoskopCapture.slnx`
- `src/Magnetoskop.Core/**` (Models, Abstractions)
- `src/Magnetoskop.Simulation/**` (4 services)
- `src/Magnetoskop.App/**` (App bootstrap, MainViewModel, MainWindow)
- `tests/Magnetoskop.Core.Tests/**`, `tests/Magnetoskop.Protocol.Sony9Pin.Tests/**`,
  `tests/Magnetoskop.Recording.Tests/**`

### Build and test instructions

```
dotnet build MagnetoskopCapture.slnx     # requires .NET 8 SDK or later
dotnet test  MagnetoskopCapture.slnx     # 20 tests, all green
dotnet run --project src/Magnetoskop.App # launches the WPF shell (simulated hardware)
```

Manual smoke test of the app:
1. Window opens; "Simulated VTR (PAL, 25 fps)" appears under RECORDER STATUS.
2. Press ▶ Play — transport shows *Playing*, CTL counts up from 00:00:00:00,
   LTC/VITC count from 01:00:00:00, SERVO LOCK flag lights.
3. Press ⏩ FF — LTC/VITC go blank (unreadable at wind speed), CTL races.
4. Press ⏏ Eject — TAPE OUT flag appears; Play now logs an error (no tape).
5. Select the simulated video device, Start preview — moving color bars appear;
   audio device is auto-selected.
6. ● Record — status shows frame count increasing; ■ Stop rec stops cleanly.
7. Close the window — clean shutdown (no hanging process).

### Limitations and risks

- Recording writes no file yet (stub until Phase 6).
- No real protocol/serial/capture code yet — the point of this phase is that all of it
  is behind `Core` interfaces so swapping in real implementations does not touch the UI.
- The SDK on this machine is .NET 10 and created a `.slnx` solution file; all projects
  target `net8.0` as required. If a classic `.sln` is needed for older tooling it can
  be regenerated.
- Preview pushes full frames through the WPF dispatcher; fine for 720×576@25 but will be
  revisited (front/back buffer) if profiling shows UI pressure in Phase 4.

### Acceptance criteria

- [x] `dotnet build` succeeds with 0 warnings / 0 errors.
- [x] `dotnet test` passes (20/20).
- [x] App runs against simulated hardware: transport control, live CTL/LTC/VITC/UB
      display, live preview, device selection with audio auto-select, recording
      start/stop, logging and error reporting all functional.
- [x] No UI/capture/protocol logic mixed in a single class; all hardware behind
      `Magnetoskop.Core` abstractions.

**Status: approved. ✅**

---

## Phase 2 — Sony 9-pin protocol and tests ✅

### Completed work

All code lives in `src/Magnetoskop.Protocol.Sony9Pin` and depends only on
`Magnetoskop.Core` abstractions (no serial hardware yet — Phase 3 supplies the real
`ISerialTransport`).

- **`CommandBlock`** — framing per the reference: CMD-1/data-count byte, CMD-2, up to
  15 data bytes, checksum = low 8 bits of the byte sum. Serialization, incremental
  `TryParse` (returns null on partial frames), `ChecksumException` on corruption,
  validation of data-count consistency.
- **`Bcd`** — BCD digit encode/decode, 4-byte timecode encode/decode (frames, seconds,
  minutes, hours; tens in high nibble) including Color-Frame (bit 7) and Drop-Frame
  (bit 6) flags on the frames byte; raw 4-byte user-bit pass-through.
- **`Sony9PinCommands`** — typed factories: Local Enable/Disable, Device Type Request,
  Stop/Play/Record/Standby/Eject/FF/Rew/Preroll, Jog/Var/Shuttle fwd+rev (with speed
  byte, reserved for later phases), Cue Up With Data, Status Sense (start/count nibble
  encoding), Current Time Sense with `TimeSenseRequest` bitmask (incl. `BestTimecode`
  = 0x03 for deck-selected LTC/VITC/corrected-LTC).
- **`Sony9PinResponseParser` + `Sony9PinResponse`** — classifies ACK (`10 01`),
  NAK (`11 12` + error bits: undefined command / checksum / parity / overrun /
  framing / timeout), Device Type (`12 11`), Status Data (`7X 20`), all `74 xx`
  time and user-bit registers (Timer-1/2, LTC, VITC, corrected/hold variants),
  Unknown (raw retained for diagnostics).
- **`StatusBitsParser`** — maps the 10 documented status bytes onto `VtrStatus`
  (transport state with record-over-play priority, Local, Tape Out, Servo Ref Missing,
  Servo Lock, direction, jog/shuttle/var/still, Rec Inhibit, alarms, EOT/near-EOT);
  tolerates short status responses from smaller decks.
- **`Sony9PinTransceiver`** — single-flight exchange engine over `ISerialTransport`:
  response timeout (default 100 ms — deliberately looser than the 9 ms spec to absorb
  USB-adapter latency), cancellation, retry policy (retries timeouts, corrupt frames,
  and transmission-error NAKs after the spec-mandated ≥ 10 ms wait; does **not** retry
  undefined-command NAKs), input discarding on recovery, hex TX/RX logging at Trace.
- **`Sony9PinController : IVtrController`** — connect (opens port with 38400/8/odd/1,
  issues Device Type Request, tolerates identification failure), background polling
  loop (status @ profile rate, best-timecode + CTL @ timecode rate, user bits at a
  slower cadence), transient-failure resilience with backoff, transport command
  dispatch with profile gating and NAK-undefined → `UnsupportedCommandException`
  mapping, clean disconnect stopping polling and closing the port.
- **`KnownDeviceProfiles`** — Generic + PVW-2600P (`2X 40` documented), DVW-M2000P,
  BVU-950P, UVW-1800P, JVC BR-S622E (unknown codes explicitly marked *verify on
  hardware*); device-type → profile matcher ignoring the NTSC/PAL variant nibble.

### Tests (80 protocol tests; suite total 99, all green)

- Framing: byte-exact serialization of every required transport command
  (Play `20 01 21`, Stop `20 00 20`, FF `20 10 30`, Rew `20 20 40`, Eject `20 0F 2F`),
  checksum math, round-trip parse, partial-frame handling, corrupt-checksum rejection,
  data-count validation.
- BCD: digit and timecode round-trips, CF/DF flag handling, invalid-BCD rejection,
  user-bit pass-through.
- Response classification: ACK/NAK (+ every error bit), device type, status data,
  all time/user-bit registers, unknown responses.
- Status bits: play/stop/record/wind/eject/jog/shuttle/var/still decoding, local &
  cassette-out, alarm byte, short-response tolerance.
- Transceiver (against `FakeSerialTransport`): success path; timeout → retry →
  eventual failure with attempt counting; recovery on retry; NAK retry semantics
  (transmission errors retried, undefined-command not retried); corrupt-response
  retry; byte-at-a-time reception; cancellation; proof that concurrent callers are
  serialized to one in-flight command.
- Controller: serial settings verification (38400/odd parity), device identification
  incl. graceful failure, transport command bytes on the wire, profile gating without
  wire traffic, NAK-undefined mapping, status/time/user-bit polling publication,
  disconnect stops polling, polling survives transient link failures.
- Device profiles: catalog completeness, device-type matching, permissive defaults.

### Modified/created files

- `src/Magnetoskop.Protocol.Sony9Pin/CommandBlock.cs`, `Bcd.cs`, `Sony9PinCommands.cs`,
  `Responses.cs`, `Sony9PinTransceiver.cs`, `Sony9PinController.cs`,
  `KnownDeviceProfiles.cs`
- `tests/Magnetoskop.Protocol.Sony9Pin.Tests/FakeSerialTransport.cs`,
  `CommandBlockTests.cs`, `BcdTests.cs`, `ResponseParserTests.cs`,
  `TransceiverTests.cs`, `ControllerTests.cs`, `KnownDeviceProfilesTests.cs`
  (placeholder test removed)

### Build and test instructions

```
dotnet build MagnetoskopCapture.slnx    # 0 warnings / 0 errors
dotnet test  MagnetoskopCapture.slnx    # 99 tests, all green
```

### Limitations and risks

- The transceiver's inter-byte-gap rule (≤ 10 ms between bytes of the master's block)
  is delegated to the OS/driver write path; on real hardware Phase 3 must confirm no
  buffering splits our 3–7 byte blocks (they normally go out in one write).
- Response timeout default (100 ms) is far above the 9 ms spec on purpose; it will be
  tightened per profile in Phase 9 after hardware measurements.
- Device-type codes for four of five decks are unknown (documented as such) and the
  status-byte count/timing per deck remain to be verified (ARCHITECTURE.md §4.1).
- The app still uses the SimulatedVtr; the Sony9PinController gets wired to a real
  COM port in Phase 3 (a serial transport implementation + connection UI).

### Acceptance criteria

- [x] Command construction with correct checksum for every required transport command.
- [x] Response parsing: ACK, NAK + error bits, device type, status bits, CTL/LTC/VITC
      timecode and user bits (incl. corrected/hold variants).
- [x] Timeouts, cancellation, and retries implemented and unit-tested, including the
      spec rules for NAK handling (immediate resend allowed only for undefined command).
- [x] Status polling loop with per-profile intervals, resilient to transient failures.
- [x] Communication logging (hex frames at Trace, structured warnings on retries).
- [x] Unsupported-command handling at both profile level and wire level.
- [x] Protocol logic fully separated from serial I/O (`ISerialTransport` boundary),
      proven by the whole suite running against an in-memory fake.
- [x] Solution builds with 0 warnings; 99/99 tests pass.

**Status: approved. ✅**

---

## Phases 3–5 — RS-422 serial, video capture + preview, audio capture + monitoring ✅

Delivered together on request.

### Phase 3 — Real RS-422 recorder communication

- **`Magnetoskop.Serial/SerialPortTransport`** — `ISerialTransport` over
  `System.IO.Ports.SerialPort`: opens with caller-supplied settings (Sony 9-pin
  defaults 38400/8/odd/1, no handshake), async read/write over `BaseStream`
  (whole command block written in one call so UART pacing at 38400 baud keeps
  inter-byte gaps ≪ 10 ms), input discard, hardened error mapping to
  `VtrCommunicationException`, clean close/dispose.
  `SerialPortEnumerator` lists COM ports.
- **`Magnetoskop.App/Services/VtrConnectionService`** — runtime-switchable VTR
  source implementing `IVtrController`: the simulator (default) or a
  `Sony9PinController` on a selected COM port with a selected device profile
  (`KnownDeviceProfiles`). Handles teardown/reattach of status/time events on switch.
- **UI**: "RECORDER CONNECTION" panel — connection combo (Simulator + every COM
  port, refreshable), device-profile combo (Generic + 5 target decks), Connect
  button with error surfaced to the log panel.

### Phase 4 — Video device selection and live preview

- **`Magnetoskop.Capture.Video/DirectShowDeviceEnumerator`** — COM interop
  (`ICreateDevEnum`/`IPropertyBag`) to obtain the DirectShow device *names* in the
  same order OpenCV's DSHOW backend uses, so index i ↔ `VideoCapture(i, CAP_DSHOW)`.
- **`OpenCvVideoCaptureService`** — `IVideoCaptureService` over OpenCvSharp:
  dedicated capture thread, format detection (size/fps; PAL 25 fallback when the
  driver reports none; interlaced flag heuristics for SD sources), BGR24
  normalization, failure tolerance (consecutive-failure cutoff), bounded channel
  fan-out (drop-oldest), index-probing fallback when DirectShow enumeration fails.
  Project targets `net8.0-windows` (DirectShow interop).
- **`CompositeVideoCaptureService`** — merges real + simulated sources in one
  picker; `sim:` id prefix routes to the simulator; real devices take default
  priority when present.

### Phase 5 — Audio capture and monitoring

- **`Magnetoskop.Capture.Audio/NAudioCaptureService`** — `IAudioCaptureService`
  over NAudio `WasapiCapture`: endpoint enumeration with default detection,
  16-bit PCM at the endpoint's native rate (max 2 ch), timestamped buffers
  (sample-count based clock), per-channel peak metering, error propagation from
  `RecordingStopped`.
- **Auto-select** (`FindMatchingDeviceAsync`): token-overlap scoring between the
  chosen video device name and audio endpoint names (capture cards usually share a
  brand/model prefix); falls back to the default endpoint. Manual selection in the
  UI overrides auto-select (tracked by `AudioManuallySelected`).
- **`CompositeAudioCaptureService`** mirrors the video composite.
- **UI**: stereo peak-level meters under the device panel, refreshed at 10 Hz by a
  background-priority `DispatcherTimer`.

### Modified/created files

- `src/Magnetoskop.Serial/SerialPortTransport.cs`
- `src/Magnetoskop.Capture.Video/DirectShowDeviceEnumerator.cs`,
  `OpenCvVideoCaptureService.cs`, csproj → `net8.0-windows` + OpenCvSharp4 packages
- `src/Magnetoskop.Capture.Audio/NAudioCaptureService.cs`, csproj + NAudio package
- `src/Magnetoskop.App/Services/CompositeCaptureServices.cs`,
  `Services/VtrConnectionService.cs`
- `src/Magnetoskop.App/ViewModels/MainViewModel.cs` (VTR connection commands,
  audio meters), `Views/MainWindow.xaml` (connection panel, meters),
  `App.xaml.cs` (DI registrations)

### Build and test instructions

```
dotnet build MagnetoskopCapture.slnx     # 0 warnings / 0 errors
dotnet test  MagnetoskopCapture.slnx     # 99 tests, all green
dotnet run --project src/Magnetoskop.App
```

Manual verification (no hardware):
1. Recorder connection combo lists "Simulator (no hardware)" plus every COM port
   present on the machine; ⟳ refreshes the port list.
2. With Simulator selected, Connect behaves exactly as Phase 1.
3. Video device combo lists real webcams/capture devices (by DirectShow name)
   plus the simulated source; audio combo lists WASAPI endpoints plus the
   simulated tone.
4. Selecting a real webcam + Start preview shows live video; audio auto-selects
   a matching/default endpoint and the level meters move.
5. Selecting a COM port with no deck attached and pressing Connect produces a
   clean, logged error (timeout after retries), and the UI stays responsive.

Hardware verification (deferrable to when a deck is available; Phase 9 will
formalize): connect USB-RS422 adapter → deck REMOTE port, select the COM port and
the deck's profile, Connect. Expect: device-type code logged, transport buttons
control the deck, CTL/LTC/VITC and user bits update live, status flags reflect
tape state.

### Limitations and risks

- OpenCV DSHOW index ↔ DirectShow enumeration order correspondence is the
  documented behavior but should be sanity-checked on the capture machine with
  multiple devices attached.
- Whether the capture device delivers unprocessed interlaced fields (critical for
  the FFV1 archival path) is a device property that must be verified in Phase 6.
- WASAPI shared mode resamples to the engine rate; exclusive mode (bit-exact) can
  be added later if the archival requirements demand it.
- The audio/video clocks are independent free-running counters at this point;
  Phase 6 aligns them for synchronized recording.
- Serial hardware behavior (adapter latency, parity handling) remains unverified
  until a deck is available — the code paths are exercised only by the fake
  transport tests so far.

### Acceptance criteria

- [x] `SerialPortTransport` implements `ISerialTransport` with the Sony settings,
      error mapping, and no protocol knowledge; port enumeration exposed to the UI.
- [x] VTR source switchable at runtime between simulator and any COM port +
      device profile, with clean teardown of the previous connection.
- [x] Video devices enumerated by name; live preview renders real device frames
      through the same bounded-channel path as the simulator; UI stays responsive.
- [x] Audio devices enumerated; capture publishes PCM with peak metering; audio
      auto-selects to match the video device unless manually overridden.
- [x] Solution builds with 0 warnings; 99/99 tests pass.

**Status: approved. ✅**

---

## Phases 6–7 — FFmpeg recording: FFV1 archival + H.264 + ProRes ✅

Delivered together on request. The `SimulatedRecordingService` stub is replaced by a
real FFmpeg-based recorder; all three required profiles work end-to-end.

### Completed work (`src/Magnetoskop.Recording`)

- **`FfmpegArgumentsBuilder`** — pure, unit-testable command-line composition:
  - Video input: rawvideo on **stdin** (`pipe:0`) with pixel format, size, and
    exact rational frame rates (25, 30000/1001, …).
  - Audio input: PCM (s16le/s24le/s32le by capture bit depth) on a **named pipe**.
  - **FFV1 archival**: `-c:v ffv1 -level 3 -g 1 -slices 24 -slicecrc 1
    -pix_fmt yuv422p` + `-c:a pcm_s24le` in Matroska — the recommended
    preservation configuration (intra-only, per-slice CRCs).
  - **H.264 access copy**: `libx264` with profile CRF/preset, `yuv420p`, AAC 192k,
    MP4 `+faststart`; interlaced sources get `-flags +ildct+ilme`.
  - **ProRes**: `prores_ks` with selectable profile (HQ default), `yuv422p10le`,
    PCM s16le in MOV.
  - **Interlace preservation**: when the source is interlaced and the profile has
    `AllowProcessing=false`, only `-field_order tt|bb` metadata is set — never a
    deinterlace filter.
- **`FfmpegLocator`** — finds ffmpeg.exe (configured path → app folder → PATH) and
  can probe `-version`.
- **`FfmpegRecordingService : IRecordingService`** — process lifecycle management:
  - Subscribes to capture streams (bounded channels) *before* launching ffmpeg.
  - Video pump starts immediately (ffmpeg probes stdin before opening the second
    input — waiting on the audio pipe first deadlocks; found and fixed via the
    integration tests), then awaits the audio-pipe connection with a 10 s timeout.
  - A/V synchronization: both streams carry monotonic timestamps from their
    sources and are muxed by ffmpeg against the declared frame rate/sample rate.
  - Frame counting + drop counting (frames with mismatched size are dropped rather
    than corrupting the rawvideo stream), 1 Hz status publication.
  - Graceful stop: cancel pumps → close stdin/pipe (EOF) → ffmpeg flushes and
    finalizes the container → wait ≤ 15 s → kill as last resort; exit code checked.
  - Fault handling: premature ffmpeg exit detection, stderr captured to the log and
    kept as a rolling tail included in error messages.
- DI switched: `IRecordingService` → `FfmpegRecordingService` (App.xaml.cs).

### Tests (28 recording tests; suite total 125, all green **including real ffmpeg runs**)

- Arguments builder (19): archival FFV1 flags, interlace tagging (tt/bb/none),
  H.264 CRF/preset/faststart/interlaced flags, ProRes profile + 10-bit, raw
  stdin video description, PCM pipe description, video-only mode, validation
  errors, frame-rate rationals, PCM format mapping, argument quoting.
- Integration (5, `[SkippableFact]`, auto-skip when ffmpeg is absent — it was
  **present and executed** on this machine): 2-second recordings through real
  ffmpeg for **FFV1/MKV**, **H.264/MP4**, and **ProRes/MOV** from the simulated
  color-bars + tone sources; file existence/size assertions; double-start
  rejection; start-without-capture rejection.
- Profile defaults (2, retained from Phase 1).

### Modified/created files

- `src/Magnetoskop.Recording/FfmpegArgumentsBuilder.cs`, `FfmpegLocator.cs`,
  `FfmpegRecordingService.cs`
- `src/Magnetoskop.App/App.xaml.cs` (DI switch)
- `tests/Magnetoskop.Recording.Tests/FfmpegArgumentsBuilderTests.cs`,
  `FfmpegRecordingIntegrationTests.cs`, csproj (+ Xunit.SkippableFact,
  + Simulation reference)

### Build and test instructions

```
dotnet build MagnetoskopCapture.slnx     # 0 warnings / 0 errors
dotnet test  MagnetoskopCapture.slnx     # 125 tests, all green
dotnet run --project src/Magnetoskop.App
```

ffmpeg is required at runtime for recording (found automatically on PATH; already
present on this machine via chocolatey). Manual app check: Start preview →
● Record with each of the three profiles → ■ Stop rec → open the produced
`capture_*.mkv/.mp4/.mov` from the output folder in a media player; verify with
`ffprobe <file>` that streams read `ffv1 + pcm_s24le`, `h264 + aac`, or
`prores + pcm_s16le` respectively.

### Limitations and risks

- BGR24→yuv422p conversion happens inside ffmpeg; if a capture device can deliver
  UYVY (native SD chroma), `VideoPixelFormat.Yuv422` is already plumbed through and
  avoids the extra conversion — enable per device in Phase 9 if available.
- A/V sync currently relies on ffmpeg pacing both inputs against declared rates;
  drift compensation (dropping/duplicating frames against the audio clock) is a
  Phase 10 hardening item for long recordings on real hardware.
- `prores_ks` and `libx264` must be present in the ffmpeg build (standard in
  full/gpl builds; both worked here). The audio-pipe timeout produces a clear
  error message when a codec/muxer is missing.
- Recording continues if audio capture is not running (video-only file) — by
  design, but the UI does not currently warn about it.

### Acceptance criteria

- [x] FFV1 + PCM in Matroska records and finalizes with archival settings
      (level 3, intra-only, slice CRCs) — verified by integration test.
- [x] H.264/MP4 and ProRes/MOV profiles record and finalize — verified by
      integration tests.
- [x] Interlacing preserved (metadata tagging, no deinterlacing) when
      `AllowProcessing=false`.
- [x] Synchronized A/V muxing through one ffmpeg process; bounded queues protect
      capture from a slow encoder.
- [x] Robust lifecycle: clean EOF finalization, timeout+kill fallback, premature
      exit detection, stderr surfaced in errors, cancellation support.
- [x] OpenCV VideoWriter not used anywhere in the recording path.
- [x] Solution builds with 0 warnings; 125/125 tests pass.

**Status: approved. ✅**

---

## Phase 8 — Full application workflow integration ✅

### Completed work

Deck control + capture + recording now operate as one coherent workflow with
persisted settings and visible errors.

- **DI wiring fixed**: `CaptureSessionCoordinator` and `SettingsService` were
  consumed by `MainViewModel` but never registered — the app could not resolve its
  view model. Both are now registered in `App.xaml.cs`.
- **Settings persistence completed** (`%AppData%\MagnetoskopCapture\settings.json`):
  - Saved video/audio device IDs are re-applied after device enumeration
    (manual audio choice falls back to auto-select when the device is gone).
  - The saved VTR connection (COM port + profile) is reconnected on startup;
    failure falls back to the simulator with a logged error.
  - `FfmpegPath` and the new `AutoPlayOnRecord` flag are saved.
- **Preflight confirmation**: warnings (missing ffmpeg, capture not running, tape
  out, transport not playing, low disk) are shown in a Yes/No dialog before
  recording starts, in addition to the log. Testable via
  `PreflightConfirmationOverride`.
- **Auto-play on record** (persisted checkbox): `CaptureSessionCoordinator.
  EnsurePlayingAsync` issues Play and waits (≤ 5 s) for Playing + servo lock before
  ffmpeg starts; the "not Playing" preflight warning is suppressed in this mode.
- **Error bar**: `LastError` is now bound in the UI — a dismissible red bar above
  the log panel.
- **File logging**: `FileLoggerProvider` writes daily rolling logs to
  `%AppData%\MagnetoskopCapture\logs\` (14-day retention, never throws into the app).

### Modified/created files

- `src/Magnetoskop.App/App.xaml.cs` (DI registrations, file logging)
- `src/Magnetoskop.App/Services/CaptureSessionCoordinator.cs` (auto-play, preflight)
- `src/Magnetoskop.App/Services/SettingsService.cs` (`AutoPlayOnRecord`)
- `src/Magnetoskop.App/Services/FileLoggerProvider.cs` (new)
- `src/Magnetoskop.App/ViewModels/MainViewModel.cs` (settings restore, preflight
  dialog, auto-play, dismiss-error command)
- `src/Magnetoskop.App/Views/MainWindow.xaml` (error bar, auto-play checkbox)
- `tests/Magnetoskop.App.Tests/**` (new project: coordinator preflight matrix,
  output naming, auto-play, sidecar metadata; SettingsService round-trip/corruption)

### Build and test instructions

```
dotnet build MagnetoskopCapture.slnx     # 0 warnings / 0 errors
dotnet test  MagnetoskopCapture.slnx     # all green (17 new app tests)
dotnet run --project src/Magnetoskop.App
```

Manual check: settings (devices, output folder, profile, auto-play) survive an app
restart; recording with the deck stopped raises the preflight dialog; errors appear
in the red bar and can be dismissed.

### Limitations and risks

- The preflight dialog blocks the UI thread by design (modal); auto-record
  scenarios would need a headless path (not required).
- Startup reconnect to a COM port adds the connect timeout (~1 s with retries) to
  startup when the deck is off; the failure is logged and the simulator takes over.

### Acceptance criteria

- [x] App starts and resolves all services (DI regression fixed).
- [x] Settings round-trip: devices, output, profile, VTR target, auto-play, ffmpeg path.
- [x] Preflight warnings surfaced and confirmable before recording.
- [x] Optional auto-play with servo-lock wait before recording.
- [x] Errors visible in the UI without opening the log.
- [x] File logs written with retention.
- [x] Solution builds with 0 warnings; full suite green.

**Status: complete.**

---

## Phase 9 — Compatibility testing with all listed recorders ✅

No physical decks were available, so compatibility was implemented as:
wire-level simulated decks for all five models + an automated compatibility suite
+ a formal hardware validation protocol for when the machines are at hand.

### Completed work

- **`Magnetoskop.Simulation/SimulatedDeckTransport`** — a simulated deck *behind*
  `ISerialTransport`: parses incoming Sony 9-pin blocks and answers with raw bytes
  (ACK/NAK + error bits, device type, status data with configurable byte count,
  BCD timecode/user bits, transport state machine with tape position). Framing and
  BCD are implemented **independently** of `Magnetoskop.Protocol.Sony9Pin`, so the
  tests exercise byte-exact interop rather than the protocol talking to itself.
- **`DeckPersonality` + `DeckPersonalities`** — per-model behavior for
  PVW-2600P (documented type `21 40`), DVW-M2000P, BVU-950P (9 status bytes, no
  VITC), UVW-1800P, and JVC BR-S622E (no device-type response, 20 ms latency,
  NAK-undefined outside the required five commands). Placeholder device-type codes
  (`7F xx`) deliberately match no known profile so the unknown-device fallback is
  exercised; every assumption is annotated *verify on hardware*.
- **`tests/Magnetoskop.Compatibility.Tests`** (55 tests) — the real
  `Sony9PinController`/`Sony9PinTransceiver` against every personality:
  connect + serial settings, device identification (PVW recognized, placeholders →
  Generic, JVC tolerated), all five required transport commands, status polling
  (incl. short status responses), tape-out via eject, LTC/CTL/user-bit polling,
  VITC vs corrected-LTC behavior, capability learning on NAK-undefined,
  timeout-retry recovery of dropped responses, clean disconnect.
- **Compatibility surfaced in the UI**: `VtrConnectionService` exposes
  `DetectedProfile`, `LearnedCapabilities`, and a `CapabilityLearned` event; the
  recorder status panel shows "Identified: …" after connect and a "Learned: …"
  line as command support is discovered; a note is logged when the identified deck
  differs from the selected profile.
- **`docs/HARDWARE_TESTING.md`** — per-deck validation protocol covering every
  open item from ARCHITECTURE.md §4.1 (device-type codes, status byte counts,
  status-bit timing/debounce, VITC availability, adapter latency/soak test,
  pinout), with a fill-in results matrix and profile-tuning instructions.

### Modified/created files

- `src/Magnetoskop.Simulation/DeckPersonality.cs`, `SimulatedDeckTransport.cs` (new)
- `src/Magnetoskop.App/Services/VtrConnectionService.cs` (compatibility surface)
- `src/Magnetoskop.App/ViewModels/MainViewModel.cs`, `Views/MainWindow.xaml`
  (detected profile + learned capabilities display)
- `tests/Magnetoskop.Compatibility.Tests/**` (new project)
- `docs/HARDWARE_TESTING.md` (new)

### Build and test instructions

```
dotnet test tests/Magnetoskop.Compatibility.Tests   # 55 tests
dotnet test MagnetoskopCapture.slnx                  # full suite
```

### Limitations and risks

- The simulated decks encode **documented facts plus explicit assumptions**; they
  cannot replace hardware validation. Real device-type codes, status byte counts,
  and status-bit timing must be captured per `docs/HARDWARE_TESTING.md` and fed
  back into `KnownDeviceProfiles` and `DeckPersonalities`.
- Response-timeout values remain deliberately loose (100–150 ms vs the 9 ms spec)
  until adapter latency is measured on hardware.

### Acceptance criteria

- [x] All five target decks simulated at the wire level with per-model quirks.
- [x] The full protocol stack passes the compatibility suite against every deck.
- [x] Capability learning and device identification visible in the UI.
- [x] Hardware validation protocol documented with result tables.
- [x] Solution builds with 0 warnings; full suite green.

**Status: complete (hardware pass pending deck availability).**

---

## Phase 10 — Reliability, documentation, and release packaging ✅

### Completed work

**Reliability**

- Global exception safety nets in `App.xaml.cs`:
  `DispatcherUnhandledException` (marked handled — an active recording survives a
  UI exception) and `TaskScheduler.UnobservedTaskException` (observed); both are
  logged to file and surfaced in the error bar.
- **Recording watchdog** in `FfmpegRecordingService`: a 1 Hz monitor faults the
  session when no frame reaches ffmpeg for 15 s (stall — includes the stderr tail
  in the error) and **stops + finalizes** the recording when free disk space drops
  below 500 MB, so the container trailer is written before the disk is full.
- **Low-disk preflight warning** (`CaptureSessionCoordinator`): recording into a
  volume with < 10 GB free adds a warning to the preflight dialog.

**Documentation**

- `README.md` — overview, requirements, build/test/run, packaging, solution
  layout, profile table.
- `docs/USER_GUIDE.md` — hardware setup, connection, capture workflow, recording
  profiles, verification, troubleshooting table.
- This phase log.

**Packaging**

- `build/publish.ps1` — runs the test suite (Release), publishes a
  **self-contained win-x64** build of `Magnetoskop.App`, bundles the docs and an
  ffmpeg placement note, and produces `artifacts/MagnetoskopCapture-<version>-win-x64.zip`.
  Options: `-Version`, `-SkipTests`, `-NoZip`.
- Product/version metadata (`Version` 1.0.0, product name, description) in
  `Magnetoskop.App.csproj`.

### Modified/created files

- `src/Magnetoskop.App/App.xaml.cs` (exception handlers)
- `src/Magnetoskop.App/ViewModels/MainViewModel.cs` (`ReportUnhandledException`)
- `src/Magnetoskop.Recording/FfmpegRecordingService.cs` (watchdog, disk monitor)
- `src/Magnetoskop.App/Services/CaptureSessionCoordinator.cs` (disk preflight)
- `src/Magnetoskop.App/Magnetoskop.App.csproj` (version/product metadata)
- `build/publish.ps1`, `README.md`, `docs/USER_GUIDE.md` (new)

### Build, test, and packaging instructions

```
dotnet build MagnetoskopCapture.slnx     # 0 warnings / 0 errors
dotnet test  MagnetoskopCapture.slnx     # full suite green
powershell -ExecutionPolicy Bypass -File build/publish.ps1
```

The publish script output (`artifacts/MagnetoskopCapture-1.0.0-win-x64/`) runs on a
clean Windows x64 machine without a .NET installation; drop `ffmpeg.exe` next to
the exe (see `PUT_FFMPEG_HERE.txt`) to enable recording.

### Limitations and risks

- FFmpeg is not redistributed (licensing); the package contains a placement note.
- A/V drift compensation over multi-hour recordings relies on ffmpeg pacing both
  inputs against declared rates; long-recording behavior should be observed during
  the hardware validation pass (`docs/HARDWARE_TESTING.md` §2.6).
- The stall watchdog cannot distinguish a wedged encoder from a stopped capture
  source; both correctly fault the recording, but the message says "stalled".

### Acceptance criteria

- [x] Unhandled UI/background exceptions logged and surfaced without process death.
- [x] Recording self-protects against disk-full and encoder stalls.
- [x] README + user guide + hardware protocol complete.
- [x] One-command release packaging producing a self-contained zip.
- [x] Solution builds with 0 warnings; full suite green.

**Status: complete.**
