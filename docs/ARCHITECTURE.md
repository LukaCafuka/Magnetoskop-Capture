# Architecture and Reliability Model

**Project:** Development of Software for Videotape Recorder Control and Audiovisual Signal Digitization

**Target:** .NET 8, WPF, Windows 10/11 x64

**Implementation status:** software reliability and A/V-correlation path implemented; physical deck and four-hour capture validation pending

---

## 1. Goals and claim boundary

Magnetoskop Capture combines two tasks:

1. control a videotape recorder (VTR) over RS-422 using the Sony 9-pin protocol;
2. capture video and audio from Windows devices, preview them, and record them through FFmpeg;
3. expose CTL, LTC, VITC, user bits, signal health, and recording-integrity evidence;
4. preserve archival interlacing and field order when the selected profile does not process the picture.

The synchronization design supports a claim of **software-correlated A/V timing**. Video
is the master output timeline; WASAPI audio device positions are correlated to the host's
monotonic QPC domain and audio is adaptively resampled. A static measured offset can be
stored for each video/audio device pair.

This is not a shared hardware clock. In particular, an OpenCV video timestamp represents
delivery to the application, not the instant at which the capture device sampled the
field. The application therefore cannot prove that a driver did not lose a frame before
OpenCV delivered it. Those limits must remain explicit in the thesis and in any practical
fitness claim.

## 2. Solution structure

```text
MagnetoskopCapture.slnx
├── src/
│   ├── Magnetoskop.Core/                  contracts, domain and timing models
│   ├── Magnetoskop.Serial/                System.IO.Ports transport
│   ├── Magnetoskop.Protocol.Sony9Pin/     framing, commands, parsing and controller
│   ├── Magnetoskop.Capture.Video/         DirectShow enumeration + OpenCV capture
│   ├── Magnetoskop.Capture.Audio/         low-level WASAPI packet capture via NAudio
│   ├── Magnetoskop.Recording/             FFmpeg recorder, drift estimator, resampler
│   ├── Magnetoskop.Simulation/            simulated VTR and timed A/V sources
│   └── Magnetoskop.App/                   WPF/MVVM, workflow and evidence sidecars
└── tests/
    ├── Magnetoskop.Core.Tests/
    ├── Magnetoskop.Protocol.Sony9Pin.Tests/
    ├── Magnetoskop.Recording.Tests/
    ├── Magnetoskop.App.Tests/
    └── Magnetoskop.Compatibility.Tests/
```

### Dependency rules

- `Magnetoskop.Core` has no hardware-library or project-layer dependency.
- Hardware access is behind Core contracts: `IVtrController`, `ISerialTransport`,
  `IVideoCaptureService`, `IAudioCaptureService`, and `IRecordingService`.
- Capture, protocol, serial, recording, and simulation depend on Core plus their own
  library. `Magnetoskop.App` is the composition root.
- Simulation and test fakes can replace all hardware without changing the workflow or UI.

## 3. Shared monotonic time and packet provenance

`CaptureMonotonicClock` converts the process-wide high-resolution performance counter
(`Stopwatch`, backed by QPC on Windows) to signed 100-nanosecond units. These values are
monotonic process positions, not UTC wall-clock timestamps.

The following observations use that domain:

| Observation | Timing/provenance retained |
|---|---|
| Video frame | OpenCV delivery timestamp, delivery sequence, source frame number |
| Audio packet | first device sample index, sample-frame count, WASAPI device position, QPC position, discontinuity/silence/timestamp-error flags |
| VTR data | independent receipt time and QPC timestamp for CTL, LTC, VITC, LTC user bits, and VITC user bits |
| Capture health | last successful delivery and health-change QPC timestamps |

The low-level audio reader calls `IAudioCaptureClient::GetBuffer` through NAudio so the
device position and QPC position for the packet's first audio frame are not discarded.
WASAPI supplies the QPC position already expressed in 100-nanosecond units. If the
driver marks it invalid, the service estimates a first-sample time from host receipt and
packet duration and marks `TimestampEstimated=true`. Such a session is finalized as
`Incomplete`, even if no media queue overflowed, because its timing evidence is weaker.
See [IAudioCaptureClient::GetBuffer](https://learn.microsoft.com/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer).

## 4. Capture fan-out and overload policy

Capture consumers use removable `CaptureSubscription<T>` instances with a role and a
bounded capacity. Each subscription exposes exact counters for publish attempts,
enqueued/dequeued items, current depth, high-water mark, oldest-item drops, and rejected
new items.

| Role | Full-queue policy | Intended use |
|---|---|---|
| `Preview`, `Monitor` | `DropOldest` | keep the most recent live state; an exact eviction counter is incremented |
| `Recording`, `FrameAudit` | `RejectNew` | preserve every accepted item; reject rather than silently replace data |

Strict subscribers are published before best-effort subscribers. Publication never
blocks the OpenCV capture thread or the WASAPI packet callback. The recording service
uses approximately two seconds of strict video and audio capacity. Its first observed
rejection requests an integrity stop; admission is removed, accepted data is drained,
and FFmpeg is asked to write a trailer. The terminal outcome is `Incomplete` when that
trailer is successfully written, not a false success.

Subscriptions remove themselves on disposal and are completed when capture stops or
faults. Capture cleanup is idempotent, including the path where the OpenCV read loop
fails before normal shutdown. Repeated preview/record cycles therefore do not retain
abandoned queues.

The capture services also report source health. The recording path treats all of the
following as integrity failures: video delivery-sequence gaps, audio device-position
gaps, WASAPI discontinuities, non-monotonic timestamps, format changes, media stalls,
strict queue rejection, frame-audit rejection, low disk space, and unexpected encoder
termination.

## 5. Video capture, stable identity and declared signal format

`DirectShowDeviceEnumerator` reads each friendly name and, where available, the
DirectShow `DevicePath` or display moniker. The stable string is persisted; whenever the
device is opened, it is resolved to the current numeric OpenCV DirectShow index. Numeric
IDs remain supported for migrated settings and for the limited fallback probe.

This improves persistence across USB reorderings, but does not make OpenCV moniker-bound:
DirectShow enumeration order and OpenCV's numeric index order are not guaranteed to be
identical. The mapping remains best-effort, and the operator must verify the picture.

The application does not infer progressive/interlaced structure or field order from
image height. Before recording, the operator must select:

- PAL, NTSC, or Custom (width, height and frame rate); and
- Progressive, TFF, or BFF.

Requested width, height and frame rate are applied as OpenCV properties and read back.
The production OpenCV backend cannot read back scan structure or field order, so even a
matching size/rate requires explicit confirmation of the exact requested/actual pair.
The confirmation is fingerprinted by stable device ID and both formats; a changed
device or format requires a new acknowledgement. Requested and actual values, the
acknowledgement key, and the scan-readback limitation are recorded in the sidecar.

## 6. Audio capture

`NAudioCaptureService` enumerates WASAPI endpoints and captures shared-mode PCM through
the low-level packet reader described in section 3. It publishes complete interleaved
sample frames and exposes peak levels for the UI. A name-overlap heuristic can choose an
audio endpoint for the selected video device; the operator may override it.

Shared-mode Windows audio processing can still resample before the application. An
exclusive/bit-exact capture mode is not implemented.

## 7. Recording and active A/V correction

OpenCvSharp `VideoWriter` is not used. `FfmpegRecordingService` sends raw BGR video to
FFmpeg stdin and PCM audio to a Windows named pipe. FFmpeg provides the FFV1, H.264,
ProRes/DNxHR, PCM/AAC and container support and writes field-order metadata from the
operator-declared input configuration.

### 7.1 Arming and initial alignment

Recording startup is ordered as follows:

1. create/start FFmpeg and connect its audio pipe;
2. record the arm wall-clock/QPC time and install strict media subscriptions;
3. read the first live video and audio observations;
4. add the persisted device-pair calibration offset to the audio timestamp;
5. retain the first video frame not earlier than the calibrated first audio sample;
6. trim earlier video and the corresponding PCM prefix as intentional startup trims.

The recorder does not synthesize startup silence and does not use FFmpeg `-itsoffset`.
The click/request time is retained separately from the authoritative arm time.

### 7.2 Video-master timeline and drift controller

Committed video frames define the final media duration. FFmpeg's two blocking raw-pipe
inputs must nevertheless be probed and interleaved concurrently: gating audio only on a
completed video-pipe write can deadlock both pumps. While the video pump is active, the
recorder may therefore submit audio against the strict video subscription's accepted,
contiguous prefix. The amount by which that prefix can lead committed video is bounded by
the approximately two-second strict queue; it is mux lookahead, not synthetic audio or
an unreported recording-path loss. The live raw-video input deliberately has no
`-fflags nobuffer`, and the live encode has no `-shortest` duration limiter; both variants
can deadlock admission between the blocking inputs.

After both inputs reach EOF, the exact budget is
`floor(committed video frames × sample rate / frame rate)` sample frames. If the first
FFmpeg process exits successfully and lookahead exists, the recorder starts a second,
finite-file FFmpeg pass using `-t <committed-video-duration> -map 0 -c copy`. It writes to
a same-directory temporary path with the same extension, so no media is re-encoded and
the original is replaced atomically only when the trim exits with code zero and produces
a nonempty file. A trim failure leaves the playable first-pass file in place and changes
the result to `Incomplete/FinalizationTrimFailed`.

This pass writes a second copy on the same volume. For a long recording, the temporary
file can require additional space of the same order as the first-pass media—potentially
close to its size—and copying it can add appreciable time after capture has stopped. A
free-space guard runs before the pass. Insufficient space is treated as a trim failure:
the first-pass file is preserved, an otherwise successful result becomes
`Incomplete/FinalizationTrimFailed`, and an earlier causal stop reason remains primary
with the trim diagnostic appended to `Error`.

Telemetry reports `AudioSamplesSubmittedToEncoder`, the application-side final budget
`AudioSamplesWritten`, `AudioSamplesInterleaveLookaheadSubmitted`, and
`AudioSamplesInterleaveLookaheadTrimmed` separately. A submitted lookahead is counted as
trimmed, and contributes to the intentional end-trim total, only after replacement
succeeds. `AudioSamplesWritten` is not a decoded-container sample count: the finite
stream-copy boundary has packet granularity, so the resulting streams and durations must
still be checked with `ffprobe` and a full decode.

After a 10-second warm-up, rolling 30-second least-squares fits compare video sequence
progress and audio device-sample progress against the shared QPC domain. The estimate is
updated at most once per second. A phase term combines filtered residual skew with the
measured clock-rate difference. PCM passes through NAudio's WDL sinc resampler.

Default controller bounds are:

| Parameter | Value |
|---|---:|
| Correction clamp | +/-1000 ppm |
| Correction slew | at most 50 ppm/s |
| Residual-skew warning | over 10 ms |
| Residual integrity stop | over one field for 5 continuous seconds |
| Capture/write stall | 15 seconds |
| Low-disk stop | below 500 MiB free |

Drift beyond the correctable bound, a timestamp regression, or the sustained residual
condition stops the session as `Incomplete`. Current and peak estimated drift, required
and applied correction, initial/current offset, warning state, sample counts, intentional
trims, missing samples, discontinuities, and queue metrics are frozen in
`RecordingResult` and the final JSON.

### 7.3 Stop and outcome semantics

The first stop reason establishes one monotonic QPC cutoff. Strict subscriptions are
then removed and their accepted queues are drained: a video frame crossing the cutoff is
counted as an intentional end trim, and an audio buffer may be retained only through the
sample immediately before that cutoff. The WDL resampler is flushed without inserting
silence. Video remains the duration authority and the resulting PCM tail is capped to
its final sample budget. The pipes are closed to send EOF; FFmpeg is given 15 seconds to
exit and write its container trailer before a kill fallback.

| Outcome | Meaning |
|---|---|
| `Completed` | normal user stop or orderly application shutdown, complete timing telemetry, FFmpeg trailer written |
| `Incomplete` | a detectable continuity/timing/audit/disk/capture failure occurred, but FFmpeg still finalized the playable partial media |
| `Faulted` | startup/internal/encoder failure or inability to finalize the media reliably |
| `Cancelled` | start/operation cancellation |

`MediaFinalized` is the independent evidence that FFmpeg exited normally after EOF. A
file's mere existence is never treated as proof of integrity.

## 8. Time information, preview freshness and frame audit

The Sony controller keeps the existing fast status, best-timecode and CTL polls. It also
polls explicit LTC and VITC at a slower staggered cadence and their user bits at a still
slower cadence. An unsupported optional LTC/VITC/user-bit request is logged at debug
level and does not mark the whole link unhealthy.

CTL, LTC, VITC and each user-bit source retain independent receipt times. A poll of one
register cannot make another old value appear fresh. The default classification is:

- fresh through 1 second;
- stale after 1 second and through 3 seconds;
- lost after 3 seconds.

The UI labels stale values with age in amber, retains `CORR` and `HOLD` provenance, and
clears lost values. A stale VTR link is amber; a lost/disconnected link is red. Preview
uses separate QPC thresholds: an overlay appears after 500 ms without a frame and turns
to a red signal-loss warning after 2 seconds. A newly delivered frame clears the warning.

For every frame successfully committed to FFmpeg stdin, the application queues one
`.frames.jsonl` record containing recording ordinal, source frame number, capture QPC,
capture offset, nearest preceding VTR observation, its source/age/freshness, servo and
transport state, and an optional interpolated timecode. Interpolation is allowed only
from fresh raw LTC/VITC while transport is forward `Playing`, servo lock is true and its
status observation is fresh. Corrected/held values and uncertain transport states are
recorded as observations, not extrapolated facts.

The audit writer is bounded and non-blocking so it cannot stall FFmpeg. It flushes at
least once per second. A rejected audit record or writer failure makes the recording
incomplete.

## 9. Sidecars and crash evidence

Each media file has:

- `capture_....frames.jsonl`: one association per committed application frame;
- `capture_....json.partial`: initial and in-progress summary evidence;
- `capture_....json`: atomically replaced final summary after orderly termination.

The initial partial JSON is written before recorder startup. Final JSON includes session
and device identifiers, requested/actual video format, format acknowledgement,
calibration provenance, one freshness-checked start/end VTR snapshot, first/last
committed-frame associations, terminal outcome/reason, FFmpeg finalization state, all
queue and synchronization metrics, exact detectable losses, and the integrity scope.
JSON replacement uses a same-directory temporary file. The partial summary is removed
only after final JSON succeeds; a hard process crash may therefore leave it as evidence
of an unfinalized session.

The scope statement is deliberately limited to application capture subscriptions through
the finalized media file. Upstream OpenCV/DirectShow continuity remains unknown.

## 10. Sony 9-pin protocol architecture

The protocol project retains four layers:

1. block framing/checksum (`CommandBlock`);
2. strongly typed command catalog and BCD/time-data parsing;
3. a single-flight transceiver with timeout/retry/NAK rules;
4. `Sony9PinController`, which polls status/time information and exposes capability
   learning behind `IVtrController`.

Wire settings are EIA RS-422-A, 38,400 bit/s, 8 data bits, odd parity and one stop bit.
The master sends one command at a time. Device-specific command subsets, response timing,
status sizes, VITC options and USB-adapter behavior remain hardware-validation items; see
`docs/HARDWARE_TESTING.md`.

## 11. Concurrency and shutdown

| Component | Execution model |
|---|---|
| WPF | rendering and bindings on the UI thread |
| Sony protocol | asynchronous single-flight exchange and background polling |
| Video | dedicated OpenCV capture thread; non-blocking subscriber publication |
| Audio | WASAPI packet thread; non-blocking subscriber publication |
| Recording | independent video/audio pumps, process monitor, watchdog and finalizer |
| Frame audit | independent single-reader JSONL writer |

Orderly host shutdown asks the session coordinator to stop/finalize the active recording
before preview/capture and device connections are disposed. Cleanup operations are
idempotent.

## 12. Evidence and remaining validation

Automated tests cover timing math, exact queue overflow counts, subscription disposal,
fresh/stale/lost transitions, format/readback acknowledgement, timecode association,
sidecar terminal paths, protocol behavior, simulations, and FFmpeg integration where
the executable is available. These tests establish deterministic software behavior;
they are not a substitute for a capture-device/deck soak.

The current implementation still requires the documented hardware campaign:

- all target VTR profiles and cable/adapter assumptions;
- reordered/duplicate/removed DirectShow devices on the target workstation;
- PAL/NTSC/custom plus Progressive/TFF/BFF verification;
- four-hour flash/tone marker capture on the available combined A/V device;
- induced encoder overload and inspection of the resulting incomplete but finalized file.

Until those results are recorded, the thesis must say **implemented and
software-tested; physical long-duration validation pending**, not that long-term
synchronization or driver-level continuity has been proved.
