# Hardware Validation Protocol

**Status:** protocol ready; results tables intentionally blank until the tests are run

**Scope:** target VTR compatibility, capture-device identity/format, application-level
continuity, and four-hour software-correlated A/V synchronization

Automated tests and accelerated simulations establish deterministic software behavior.
They do not validate cable pinouts, deck-specific Sony 9-pin behavior, DirectShow driver
delivery, analog signal paths, or long-duration timing of a physical device. This document
is the required companion evidence.

Target VTRs:

| # | Model | Format | Known uncertainty |
|---|---|---|---|
| 1 | Sony PVW-2600P | Betacam SP | verify command/status behavior on installed firmware |
| 2 | Sony DVW-M2000P | Digital Betacam | capture actual device type and timing |
| 3 | Sony BVU-950P | U-matic SP | VITC depends on installed option |
| 4 | Sony UVW-1800P | Betacam SP | capture actual device type and timing |
| 5 | JVC BR-S622E | S-VHS | Sony-compatible subset; map NAKed commands |

## 1. Evidence rules

Use disposable test tapes until all failure tests are complete. For every run retain:

- the media file;
- final `.json` or retained `.json.partial`;
- `.frames.jsonl`;
- application log;
- `ffprobe` JSON/text output and full-decode result;
- workstation, OS, driver, FFmpeg, adapter, capture-device and deck identifiers;
- the exact requested/actual signal format and calibration used;
- photos or analyzer traces for marker/cable measurements where available.

An accepted transfer requires `Outcome = Completed` and `MediaFinalized = true`. An
`Incomplete` file may be a useful, playable partial artifact but is not a passed transfer.
A `.json.partial` without a final `.json` is crash/startup evidence, not completion.

The integrity scope begins when the application arms its strict capture subscriptions.
OpenCV assigns a delivery sequence only after the driver supplies a frame. Consequently,
zero application losses does not prove that the capture driver delivered every hardware
field/frame. The final report must state that limitation.

## 2. Test setup and baseline record

Required equipment:

- Windows 10/11 x64 workstation with Magnetoskop Capture and a full FFmpeg build;
- USB-to-RS-422 adapter supporting 38,400 baud, 8 data bits, odd parity, one stop bit;
- verified 9-pin remote cable;
- DirectShow video capture device and WASAPI audio endpoint;
- test cassette with known LTC/VITC/user bits;
- a repeated simultaneous visual/audio marker: one-frame white flash or LED plus a
  sharp tone/click recorded from the same trigger at the start, each hour, and end;
- sufficient output storage for the four-hour media plus a possible second, same-volume
  temporary copy of roughly comparable size during finalization; do not use a fixed
  percentage margin without first estimating the selected codec's measured output;
- optional oscilloscope/audio interface for independently verifying marker simultaneity.

Before connecting, verify the deck and adapter manuals. Pinouts vary; do not assume a
generic cable. Record the actual pinout and shield/ground arrangement used.

Create a baseline sheet for each workstation/device pair:

| Field | Value |
|---|---|
| Date/tester | |
| PC model, CPU, RAM | |
| Windows build | |
| Magnetoskop commit/build | |
| FFmpeg version/build | |
| Video device model/serial/driver | |
| Persisted DirectShow ID | |
| Audio endpoint ID/driver | |
| RS-422 adapter/driver | |
| VTR model/serial/firmware | |
| Cable pinout verified by | |
| Tape format/ID | |
| Requested standard/scan | |
| Actual width/height/rate | |
| Scan order independently verified | |
| Calibration offset/date/method | |
| Output-volume free bytes before capture/final trim | |
| Stop-to-terminal-finalization elapsed time | |

Run the software baseline before hardware work:

```powershell
dotnet build MagnetoskopCapture.slnx -c Release
dotnet test MagnetoskopCapture.slnx -c Release --no-build
ffmpeg -version
ffprobe -version
```

Save the command output. Skipped FFmpeg integration tests must be resolved before a
capture result is accepted.

## 3. VTR control and time-information validation

Repeat this section for every target deck.

### 3.1 Connection and device identification

1. Set the deck to **REMOTE**, insert the test tape, and connect the selected profile.
2. Confirm status polling without repeated timeout errors.
3. Record the two Device Type bytes from the Trace log, or explicitly record that the
   deck does not answer Device Type Request.
4. Compare detected and selected profiles. Do not add a code to
   `KnownDeviceProfiles.cs` until it is observed on the physical deck.

### 3.2 Command and status coverage

For each command, record ACK, undefined-command NAK, other NAK, timeout, physical result,
and status-panel result:

| Command | Wire/physical expectation | Result |
|---|---|---|
| Play | tape plays; status becomes Playing | |
| Stop | tape stops | |
| Fast Forward | wind mode | |
| Rewind | wind mode | |
| Eject | cassette ejects; Tape Out appears | |
| Pause/still | stopped picture without Stop, if supported | |
| Frame step forward/reverse | one-frame movement or documented fallback | |
| Standby on/off | status follows deck, if supported | |
| Jog/Shuttle | sign and speed are correct, if supported | |
| Cue LTC/VITC/CTL | reaches requested location within deck tolerance | |

Operate the front panel as well as the application and confirm the UI follows. Record
the returned Status Sense byte count and any transition flicker/debounce requirement.

### 3.3 Timecode, user bits and freshness

1. During 1x forward play, compare LTC/VITC and user bits against the labelled reference.
2. Check CTL movement, signed/wrapped display, and Timer-1 reset where supported.
3. Pause/still: record raw VITC versus held VITC behavior and the `HOLD` label.
4. Fast wind: record LTC loss or corrected-LTC behavior and the `CORR` label.
5. Confirm independent polling: an updated CTL value must not refresh old LTC/VITC.
6. Interrupt the RS-422 response path. Expected: observations become `STALE` after
   approximately 1 second, clear after 3 seconds, and the link warning becomes red/lost.
7. Restore the path. Expected: fresh values and online state recover without application
   restart. Unsupported optional LTC/VITC/user-bit requests must not make an otherwise
   responsive link appear lost.

### 3.4 Serial robustness

Leave continuous polling active for at least 15 minutes. Record timeout, checksum,
parity/framing and NAK counts, measured worst response time, and recovery after a cable
disconnect/reconnect. Tune a device profile only from recorded measurements and keep the
raw log as evidence.

## 4. Capture-device identity, signal declaration and preview health

Run these tests on the target workstation before the long capture:

1. Record DirectShow friendly name, persisted `DevicePath`/display moniker and transient
   OpenCV index. Verify the displayed picture identifies the expected physical input.
2. Disconnect/reconnect USB devices in a different order. Re-enumerate and verify the
   persisted stable ID resolves to the intended picture.
3. Repeat with duplicate friendly names. Selection must be based on stable ID, while the
   operator still verifies the image.
4. Remove the selected device. Opening must fail visibly rather than silently substitute
   a different source.
5. Record any stable-ID-to-index mismatch. Such a result confirms the documented OpenCV
   limitation and blocks unattended use on that configuration.

For PAL, NTSC and every required custom format:

1. select the standard and Progressive/TFF/BFF declaration;
2. start preview, save requested and actual readback size/rate;
3. inspect motion or a known field-order pattern to verify scan/order independently;
4. acknowledge only the exact verified pair;
5. change one requested or actual property and confirm the old acknowledgement no longer
   authorizes recording;
6. record FFmpeg/ffprobe frame rate and field-order metadata.

Stop video delivery without stopping the app. Expected: amber stale overlay after about
500 ms, red signal-lost overlay after about 2 seconds, terminal error text when present,
and automatic recovery on the next frame.

## 5. Static A/V calibration

Calibration is stored per stable video/audio device pair and defaults to zero,
uncalibrated. Calibrate each pair separately:

1. Capture at least ten simultaneous flash/tone markers with zero static offset and no
   application-reported loss.
2. Locate the flash transition by video frame/field and the audio click onset by sample.
3. Compute audio relative to picture for every marker and report mean, standard deviation,
   minimum and maximum. State the sign convention used.
4. Enter the correction needed by the application: **positive when audio must be delayed,
   negative when audio must be advanced**. Mark it **Measured** and record date/method.
5. Repeat the marker capture. The corrected absolute offset must be within one field of
   the selected interlaced standard (PAL: 20.000 ms; NTSC 29.97: about 16.683 ms). For a
   progressive source, use one frame period as the declared tolerance.
6. Change either device and confirm the calibration becomes the other pair's stored value
   or zero/uncalibrated; it must not leak between pairs.

The calibration corrects a static path delay. The rolling resampler corrects relative
clock rate and phase during a session. Neither creates a shared hardware clock.

## 6. Short recording and artifact reconciliation

Before the soak, record at least five minutes in FFV1 + PCM and one short file in every
other required profile.

For each file:

1. require final JSON `Outcome = Completed`, `MediaFinalized = true`, and no terminal
   `Error`;
2. require zero `VideoFramesDropped`, `VideoSequenceGaps`, `VideoFormatRejects`,
   `VideoQueueOverflows`, `AudioBuffersDropped`, `AudioQueueOverflows`,
   `AudioDiscontinuities`, `AudioSamplesMissing`, and
   `AudioTimestampEstimatedBuffers`;
3. require `SyncTelemetryComplete = true`;
4. require `FrameAuditRecordsRejected = 0` and
   `FrameAuditRecordsAccepted = FrameAuditRecordsWritten = VideoFramesWritten`;
5. verify intentional startup/tail trims are present as counts rather than unexplained
   loss and `AudioSamplesInserted = 0`;
6. reconcile strict counters for both streams:
   `VideoQueuePublishAttempts = VideoQueueAccepted + VideoQueueRejectedNew` and the
   equivalent audio fields. After an orderly drain, require
   `VideoQueueAccepted = VideoQueueConsumed`,
   `AudioQueueAccepted = AudioQueueConsumed`, both depths zero, and both
   `QueueRejectedNew` values zero for a completed transfer;
7. reconcile FFmpeg interleave lookahead: `AudioSamplesSubmittedToEncoder` must equal
   `AudioSamplesWritten + AudioSamplesInterleaveLookaheadTrimmed`. A nonzero lookahead
   trim is valid because audio may be submitted against the strict video queue's accepted
   contiguous prefix to avoid a two-pipe deadlock; it is intentional bookkeeping, not a
   recording-path loss.
   Confirm that it remains within the approximately two-second strict-queue horizon. When
   it is nonzero, require a successful finite EOF stream-copy trim (`-t` to committed
   video duration, `-map 0 -c copy`) and verify the probed result within stream-copy packet
   granularity. For a passed transfer,
   `AudioSamplesInterleaveLookaheadSubmitted` must equal
   `AudioSamplesInterleaveLookaheadTrimmed`. The live process must not depend on an
   output-duration limiter;
8. inspect `VideoFramesTrimmedAtStart/End` and
   `AudioSamplesTrimmedAtStart/End` against the common start alignment and stop cutoff;
9. inspect codec, sample rate, frame rate, dimensions and field order with `ffprobe`;
10. decode every stream through the end with zero errors:

```powershell
ffprobe -v error -show_streams -show_format -of json .\capture.mkv > .\capture.ffprobe.json
ffmpeg -v error -i .\capture.mkv -map 0 -f null -
```

Also compare the first and last JSONL lines with the summary associations. Interpolated
timecode is acceptable only for fresh raw LTC/VITC while transport is 1x forward and
fresh servo lock is true; all other records must expose observation uncertainty rather
than invent a continuous value.

## 7. Four-hour A/V synchronization soak

Use the same combined capture device intended for thesis evaluation and the calibrated
pair. Disable unrelated background updates, but do not artificially increase queue
capacity. Record resource use at 10-minute intervals.

### 7.1 Marker schedule and measurements

Generate/record simultaneous flash/tone markers at:

- start (within the first minute);
- approximately 1:00, 2:00 and 3:00 hours;
- end (within the final minute).

For every marker record:

| Marker | Media video frame/field | Audio sample/time | Signed offset (ms) | Absolute offset | Pass? |
|---|---:|---:|---:|---:|---|
| Start | | | | | |
| 1 h | | | | | |
| 2 h | | | | | |
| 3 h | | | | | |
| End | | | | | |

Use a repeatable threshold detector and document its uncertainty. Report both residual
offset and change relative to the first marker; do not report only the best marker.

### 7.2 Acceptance criteria

The soak passes only if all are true:

- absolute flash/tone offset remains within one selected-standard field at every marker
  (or one declared progressive frame);
- application recording-path loss/discontinuity/timestamp-estimation counters are zero;
- queue depth/high-water marks remain bounded and no strict rejection occurs;
- memory use remains bounded with no monotonic multi-hour growth attributable to
  abandoned subscriptions or audit records;
- `Outcome = Completed`, `MediaFinalized = true`, and complete timing telemetry;
- JSONL line count, summary audit counts, committed frames, audio sample budget and logs
  reconcile;
- current/peak measured drift, required correction and applied correction stay within
  the configured +/-1000 ppm bound; any >10 ms residual warning is investigated;
- ffprobe reports the requested streams/rates/field order and a full decode emits zero
  errors;
- when lookahead trimming runs, its free-space guard passes, the same-volume temporary
  copy remains bounded, and stop-to-terminal-finalization time is recorded rather than
  assuming that stream copy is instantaneous.

Save the raw measurements, not only the pass/fail statement. A failed run is still useful
thesis evidence when its terminal reason and counters reconcile.

## 8. Controlled failure and playable-partial tests

Run these only with disposable/generated input.

### 8.1 Encoder overload

Constrain encoder throughput without killing FFmpeg (for example, a deliberately slow
test profile plus recorded CPU-affinity/load conditions) until a strict recording queue
rejects an item. Record the exact induction method so it is reproducible.

Expected:

- the hardware callback/capture thread does not block indefinitely;
- stop reason is `QueueOverflow` (or the first more severe independently observed cause);
- outcome is `Incomplete`, not `Completed`;
- FFmpeg writes a trailer and `MediaFinalized = true`;
- the partial media opens, ffprobe completes, and a full decode reaches its end;
- exact rejection/loss count and high-water marks are non-zero and agree with logs/JSON;
- preview losses, if any, remain separate from recording-path losses.

If FFmpeg is killed or exits first, the correct result is `Faulted/EncoderExited`; that
does not exercise the playable-incomplete overload path and must be reported separately.

### 8.2 Other terminal paths

Exercise and retain artifacts for:

| Induction | Expected terminal evidence |
|---|---|
| manual stop | `Completed/UserRequested`, finalized media and final JSON |
| close app while recording | orderly `ApplicationShutdown`, finalized artifacts |
| cancel startup/operation through a deterministic harness | `Cancelled/Cancellation` |
| strict video/audio queue saturation | `Incomplete/QueueOverflow`, exact rejection count |
| video or audio stall | `Incomplete/VideoStall` or `AudioStall`, playable if finalized |
| injected video delivery-sequence gap | `Incomplete/VideoDiscontinuity`, exact gap count |
| capture source completes unexpectedly | `Incomplete/CaptureEnded` |
| format change | `Incomplete/FormatChanged` |
| reported WASAPI discontinuity/sample gap | `Incomplete/AudioDiscontinuity` |
| regressing video/audio QPC timestamp | `Incomplete/TimestampRegression` |
| audio packet with estimated rather than device QPC | `Incomplete/TimingTelemetryIncomplete`; finalized media may still decode |
| correction beyond clamp or residual over one field for 5 s | `Incomplete/ExcessiveDrift` |
| low output space on disposable volume | `Incomplete/LowDiskSpace` before volume exhaustion |
| audit saturation/failure | `Incomplete/FrameAuditOverflow` |
| insufficient same-volume space for the finite trim copy | free-space guard preserves the nonempty/playable first-pass media; otherwise successful run becomes `Incomplete/FinalizationTrimFailed` |
| force another finite lookahead-trim failure | first-pass media remains playable and is not replaced; otherwise use `Incomplete/FinalizationTrimFailed`, or retain an earlier causal reason and append the trim diagnostic to `Error` |
| invalid/missing FFmpeg at startup | `Faulted/StartupFailure`, retained partial summary |
| unexpected FFmpeg exit | `Faulted/EncoderExited`; `MediaFinalized` normally false |
| deterministic internal-failure injection | `Faulted/InternalFailure` |
| forced hard process termination | `.json.partial` remains; no completion claim |

## 9. Results and sign-off

### 9.1 Per-deck compatibility

| Item | PVW-2600P | DVW-M2000P | BVU-950P | UVW-1800P | BR-S622E |
|---|---|---|---|---|---|
| Device type bytes | | | | | |
| Status byte count | | | | | |
| Commands ACKed/NAKed | | | | | |
| LTC/VITC/user bits verified | | | | | |
| Worst response time | | | | | |
| Errors in 15-minute poll | | | | | |
| Disconnect/recovery passed | | | | | |
| Profile changes required | | | | | |

### 9.2 Capture and synchronization

| Item | Result/evidence path |
|---|---|
| Stable identity reorder/duplicate/removal tests | |
| PAL/NTSC/custom readbacks | |
| Progressive/TFF/BFF verification | |
| Preview stale/lost/recovery | |
| Calibrated offset and uncertainty | |
| Five-minute artifact reconciliation | |
| Four-hour marker offsets | |
| Peak drift/required/applied correction | |
| Queue/memory maximums | |
| Full decode result | |
| Controlled overload result | |

Sign-off must include date, tester, commit/build, all hardware serial/firmware/driver
versions, deviations from this protocol, failed criteria, and links to retained evidence.

Until section 9 contains measured results, the correct conclusion is: **the reliability
mechanisms are implemented and software-tested, but physical long-duration A/V
synchronization and end-to-end driver continuity have not been validated.**
