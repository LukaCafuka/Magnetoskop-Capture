# Magnetoskop Capture — User Guide

## 1. What you need

| Component | Notes |
|---|---|
| Windows PC | Windows 10/11, x64. The release build is self-contained (no .NET install needed). |
| FFmpeg | `ffmpeg.exe` on PATH, next to `Magnetoskop.App.exe`, or in an `ffmpeg\` subfolder beside it. Use a **full/GPL build** (needs `libx264` and `prores_ks`). |
| USB→RS-422 adapter | FTDI-based adapters are known to handle odd parity at 38,400 baud. Appears as a COM port. |
| 9-pin control cable | Connects the adapter to the deck's REMOTE (9-pin) connector. **Pinouts vary between adapters** — see `docs/HARDWARE_TESTING.md` §1. |
| Video capture device | Any DirectShow-compatible device (USB capture stick or card) fed from the deck's composite/component output. |
| Audio input | Line-in on the capture device or the sound card (WASAPI endpoint). |

Everything works without hardware too: the app includes a simulated recorder,
video source (moving color bars), and audio source (1 kHz tone) for training and
testing.

## 2. First start

1. Launch `Magnetoskop.App.exe`.
2. The app connects to the built-in **Simulator** and enumerates capture devices.
3. All settings (devices, output folder, profile, deck connection) are remembered
   between sessions in `%AppData%\MagnetoskopCapture\settings.json`.

## 3. Connecting a recorder

1. Set the deck's control switch to **REMOTE**.
2. Open **View → Connections…**.
3. Under **RECORDER**, pick the adapter's COM port — the app connects
   automatically (press ⟳ if the port was plugged in after the app started).
4. Serial control defaults to the **Generic Sony 9-pin** protocol. To use a
   deck-specific profile (poll rates / timeouts / command set), choose the model
   under **Device model** in the same window.
5. If you change the VTR model while already connected to a COM port, the app
   reconnects with the new profile.

What you should see:

- **RECORDER STATUS** on the main window shows the device description. If the
  deck answers the device-type request with a recognized code, an "Identified: …"
  line appears. Use **Connections…** there (or **View → Connections…**) to change
  the port or model.
- The transport state (Stopped/Playing/…) updates live, along with status flags
  (TAPE OUT, SERVO LOCK, REC INHIBIT, …).
- Transport buttons: Rew / Play / Stop / FF / Eject. **Standby** toggles threaded
  stop (yellow = standby on, gray = off). The color follows live deck status, so
  if the VTR auto-turns standby off after idle, the button updates on its own.
  By default (**File → Settings…**), transport buttons are disabled while a
  recording is active; uncheck **Disable transport buttons during recording** to
  allow deck control during capture.
- **TIME INFORMATION** shows independently polled CTL, LTC, VITC, and user bits.
  A value older than one second is labelled `STALE` with its age in amber; after
  three seconds it is cleared. A lost/disconnected VTR link is shown in red. `CORR`
  means corrected LTC and `HOLD` means the deck returned a held value; these labels
  describe provenance, not a new measurement. Values the deck cannot read in the
  current transport mode display as `--:--:--:--`. CTL below zero shows with a
  leading minus by default (e.g. `-00:00:00:01`); enable **CTL 24-hour wrap** in
  **File → Settings…** to show the deck-style wrap (`23:59:59:24`) instead.
- Click **LTC** or **VITC** to edit a target timecode, then press **Enter** to cue the deck
  there (Sony Cue Up With Data, forced to the shared TIME CODE mode). Use
  `HH:MM:SS:FF` (e.g. `00:01:00:00` for one minute) or short `MM:SS:FF`.
  **Esc** or clicking away cancels. Note: `1:00:00:00` is **one hour**, not one minute.
- Click **CTL** the same way to cue on TIMER-1 / CTL (Timer Mode Select `01`).
  Signed CTL (`−00:00:00:01`) is allowed when 24h wrap display is off; the
  cue is sent as a 24-hour wrap on the wire (decks ignore a hours “sign bit”).
- **Reset** next to CTL sends Timer-1 Reset (`40 08`), zeroing the CTL counter at the
  current tape position without seeking.
- If the deck rejects a command as unsupported, the app remembers it and shows it
  in the "Learned:" line of the status panel.

If the connection fails (timeout), check: REMOTE switch, cable pinout, COM port
number, and that no other program holds the port.

## 4. Capture devices and preview

Preview starts automatically after the app enumerates capture devices. Changing
the video or audio device restarts preview on the new source (not while a
recording is in progress).

1. Open **View → Connections…**. Under **CAPTURE DEVICES**, pick the video device
   if the default is wrong. Audio is auto-selected by name match (capture cards
   usually expose both); picking an audio device manually overrides auto-select.
2. Select the **Input standard** (`PAL`, `NTSC`, or `Custom`) and **Scan mode /
   field order** (`Progressive`, `TFF`, or `BFF`). These are operator declarations,
   not automatic image analysis. For `Custom`, also enter width, height, and frame rate.
3. Start/restart preview and inspect the driver readback. OpenCV can read back frame
   size and rate, but not scan structure/field order. If the panel asks for confirmation,
   verify the source, capture-device controls, motion/field order, and the displayed
   picture, then check **I verified this exact requested/driver format**. The app stores
   that confirmation only for this device and exact requested/actual pair; a change
   requires confirmation again. Recording is blocked until this step is complete.
4. Live video appears on the main window; stereo level meters and **Monitor audio**
   are on the main window (and also in Connections). Press **Refresh** in Connections
   to re-enumerate devices if hardware was plugged in after launch.
5. For easier watching of interlaced sources, enable **Yadif 2× deinterlace
   preview** under **File → Settings…**. This bob-deinterlaces the live preview
   only (recording encode is unchanged).
6. Use **View → Watch window…** for a maximized preview with transport and
   Jog/Shuttle only (Esc closes). The main window stays open underneath.

The app persists the DirectShow device path/moniker and resolves it to the current
OpenCV index when opening. This is more stable than saving an index, but OpenCV still
opens by number and its order is not guaranteed to equal DirectShow enumeration order.
Always confirm the picture after USB devices are added, removed, or reordered, especially
when two devices have the same friendly name.

Preview health is based on the last delivered frame. After about 500 ms without a new
frame, an amber overlay reports the age of the retained picture; after two seconds it
changes to a red **VIDEO SIGNAL LOST** warning. A terminal capture error is included in
the overlay. The warning clears automatically when frames resume. Preview may drop old
frames under load; this does not by itself mean that the strict recording queue lost data.

### 4.1 Static A/V calibration

Connections also contains **Static A/V calibration — audio offset (ms)** for the selected
video/audio device pair. Measure the pair with a simultaneous flash/tone source, enter
the signed offset, and check **Measured** only after documenting the measurement.
Positive values place audio later on the correlated timeline. An unmeasured value may be
stored for reference, but the application applies **0.000 ms** until **Measured** is
checked. Calibration is a fixed device-pair correction; it does not replace the active
drift controller or the hardware soak in `docs/HARDWARE_TESTING.md`.

## 5. Transport control

The bottom bar sends commands to the connected deck:
**⏪ Rew · ▶ Play · ■ Stop · ⏸ Pause · ◀ Frame · Frame ▶ · ⏩ FF · ⏏ Eject · Standby**

**Pause** is still (Shuttle 0): it freezes playback without issuing **Stop**.

**◀ Frame / Frame ▶** are Sony **FRAME STEP** (`20 24` / `20 14`): move one frame
backward or forward, then still. If the deck NAKs FRAME STEP as undefined, the app
falls back to **Cue Up** on TIMER-1 / CTL ± 1 frame (same Still result). (There is no
separate field-step command in 9-pin; finer field jogging uses the Jog wheel.)
Keyboard **`,`** = reverse, **`.`** = forward when the app has focus and you are not
typing in a text field.

Keyboard **media keys** (Play/Pause, Stop, Next track = FF, Previous track = Rew)
also drive transport while the app has focus; turn this off under **File → Settings…**
if needed. Play/Pause toggles play vs pause.

**J / K / L** (same setting): **K** toggles play/stop; **L** steps up forward speed
(1× → 2× → 4× …); **J** starts slow reverse and steps up reverse speed. Ignored while
typing in text fields. **Ctrl+S** toggles Standby.

Next to those controls, a **Jog / Shuttle** wheel searches at variable speed:

- Choose **Jog** (fine positioning, up to ~1×) or **Shuttle** (visual search; max rate
  depends on the selected VTR — **42×** on DVW-M2000P, ~**50×** on other profiles).
- Drag left for reverse, right for forward; the label shows the current rate (e.g. `+2.50×`).
- Release the wheel to return to still and send **Stop**.

Errors (no tape, local mode, unsupported command) are reported in the red error
bar and the log panel.

## 6. Recording

1. In **RECORDING**, choose a profile:
   - **FFV1 + PCM (Matroska, archival)** — lossless preservation master.
     Interlacing and field order are preserved untouched.
   - **H.264 + AAC (MP4, access copy)** — small viewing copy.
   - **ProRes HQ + PCM (MOV)** — for post-production workflows.
   - **DNxHR HQ + PCM (MOV)** — Avid-friendly DNxHD/HR encode (LB/SQ/HQ/HQX/444
     selectable in Video settings).
2. Choose the output folder with **Browse…** in the RECORDING panel (or
   **File → Choose save location…**). Check free space: FFV1 SD material is roughly
   60–90 GB/hour. If final lookahead trimming is needed, the app writes a second
   temporary copy on that volume; for a long capture, reserve additional space of the
   same order as the recording itself rather than relying only on the normal 500 MiB
   recording-stop reserve.
3. Complete the video standard/scan confirmation and, if available, the static A/V
   calibration described in section 4. Uncalibrated device pairs are allowed, but are
   visibly recorded as uncalibrated and use zero static offset.
4. Optional: enable **Auto-play deck on record** — the app then issues Play and
   waits for servo lock before recording starts.
5. Press **● Capture**. If preview is not running it starts automatically. Any
   preflight issues (missing ffmpeg, no tape, deck not playing, low disk space)
   are listed in a confirmation dialog first.
6. Press **■ Stop capture** to finish. Wait for the status to leave **Stopping**;
   FFmpeg must write its container trailer and the application must finalize the audit
   and summary before the result is authoritative.

Files are named `capture_YYYYMMDD_HHMMSS[_TChh-mm-ss-ff].ext`, where the `TC`
part is included only when a fresh VTR time observation is available. One
freshness-checked snapshot is reused for the name and start metadata.

The recorder starts FFmpeg and connects its audio pipe before arming the strict video and
audio subscriptions. That arm instant—not the button-click time—is the recording start.
It applies the measured device-pair offset, chooses the first video frame at or after the
calibrated first audio sample, and counts earlier picture/PCM as intentional startup
trims. It does not create artificial silence. Video is then the master duration and audio
is slowly resampled to follow it. To prevent FFmpeg's two blocking inputs from waiting on
each other, the recorder may submit audio against the strict video queue's accepted
contiguous prefix, at most about two seconds ahead of committed video. The live encode is
not duration-limited because doing so can deadlock its two live inputs. At stop, one
monotonic cutoff is shared by both streams; a crossing video frame or PCM tail is recorded
as an intentional end trim, and the resampler is flushed without adding silence before
FFmpeg receives EOF.

After that FFmpeg process exits successfully, any interleave lookahead is handled by a
finite stream-copy pass targeted to committed video duration. The app writes a temporary
file beside the recording and replaces the original only if FFmpeg exits successfully and
the replacement is nonempty. If this final trim fails, the playable first-pass file is
kept. The app checks free space before creating the copy, but insufficient space or any
other trim failure makes an otherwise successful result
`Incomplete/FinalizationTrimFailed`; if another integrity failure already supplied the
stop reason, that reason is retained and the trim failure is appended to `Error`.

Copying a long recording can take appreciable time. Keep the app open and the output
volume connected until **Stopping** clears and the terminal JSON is written. The summary
distinguishes `AudioSamplesSubmittedToEncoder`, application-side final
`AudioSamplesWritten`, `AudioSamplesInterleaveLookaheadSubmitted`, and successful
`AudioSamplesInterleaveLookaheadTrimmed`; a nonzero lookahead is not a queue loss. Use
`ffprobe` to verify actual container duration and packet-level boundaries.

Files written beside the media:

- `.frames.jsonl` — one record for every application frame committed to FFmpeg, with
  source frame number, monotonic capture offset, nearest preceding VTR observation,
  age/source/freshness, servo/transport state, and optional guarded interpolation;
- `.json.partial` — written before recorder startup and retained if final summary writing
  or a hard process exit prevents orderly completion;
- `.json` — atomically produced terminal summary with outcome/reason, requested and
  actual signal format, calibration, detected losses, queue high-water marks, A/V
  offset/drift/correction, sample counts and trims, audit counts, and integrity scope.

Do not delete a `.json.partial` file before investigating why its matching `.json` is
missing. A `.frames.jsonl` file is evidence about frames delivered to and committed by
the application; it cannot prove what happened inside the capture driver before OpenCV.

Built-in protections while recording:

- Preview/monitor queues may discard their oldest item, but their exact evictions are
  counted. Recording queues hold about two seconds and reject a new item when full;
  they never silently replace an accepted item and never block a hardware callback.
- The first strict queue rejection, video sequence gap, audio sample-position gap,
  WASAPI discontinuity/timestamp error, timestamp regression, format change, audit
  overload, capture stall, or excessive A/V drift ends the session as **Incomplete**.
- The drift estimator has a 10-second warm-up and rolling 30-second window. Audio
  correction is limited to +/-1000 ppm and changes by at most 50 ppm/s. Residual skew
  over 10 ms is recorded as a warning; residual over one field for five seconds stops
  the session.
- The recorder stops before the output disk fills (below about 500 MiB free) and after
  about 15 seconds without video/audio capture or write progress.
- After an integrity stop, the app seals capture admission, drains data already accepted,
  and asks FFmpeg to finalize a playable partial file. The result is successful only if
  the JSON says `"Outcome": "Completed"`. `Incomplete` means the file may be playable but
  must not be treated as a complete transfer. `Faulted` means startup/encoder/internal
  finalization failed. Check `MediaFinalized` independently in every case.

While recording, the A/V status line shows filtered residual offset, estimated drift,
applied correction, and required correction. It turns amber for a sync warning or
incomplete timestamp telemetry. The terminal recording status distinguishes
**Incomplete**, **Cancelled**, and **Faulted** rather than presenting all three as a
successful stop.

## 7. Verifying a recording

Do not accept a transfer based only on VLC playback. Preserve the media, `.json`, and
`.frames.jsonl` together, then perform all of these checks:

1. In the final JSON require `Outcome = Completed` and `MediaFinalized = true`.
2. Confirm recording-path queue overflows/drops, video sequence gaps, audio missing
   samples/discontinuities, timestamp-estimated buffers, format rejects, and frame-audit
   rejects are all zero. Check that accepted/written audit counts reconcile with committed
   video frames, allowing only documented startup trims.
3. Inspect requested versus actual format, scan order, calibration state, initial/current
   A/V offset, peak drift/correction, warning flag, and intentional start/end trims.
4. Inspect stream metadata and durations with `ffprobe`, then decode the entire file to a
   null sink so corruption near the end is not missed.

```powershell
ffprobe -hide_banner -show_streams -show_format capture_20260712_193000_TC01-00-00-00.mkv
ffmpeg -v error -i capture_20260712_193000_TC01-00-00-00.mkv -map 0 -f null -
```

Expected: `ffv1` + `pcm_s24le` (archival), `h264` + `aac` (MP4), or
`prores` + `pcm_s16le` (MOV), with the selected frame rate and field order. A completed
software run still does not establish end-to-end device timing; use the marker/soak
procedure in `docs/HARDWARE_TESTING.md` for that claim.

## 8. Troubleshooting

| Symptom | Check |
|---|---|
| "ffmpeg.exe not found" | Install FFmpeg and add to PATH, or copy `ffmpeg.exe` next to the app. |
| COM port missing from the list | Open **View → Connections…**. Adapter driver installed? Press ⟳ to re-enumerate. |
| Connect times out | REMOTE switch, cable/pinout, correct COM port. See `docs/HARDWARE_TESTING.md`. |
| Transport buttons error with "not supported" | The deck NAKed the command; expected for some decks (see the "Learned:" line). |
| Timecode frozen at `--:--:--:--` | No tape, or the deck cannot read LTC/VITC in this transport mode. |
| `STALE`/`LOST` timecode or red VTR warning | Check cable/REMOTE mode and polling log. One successful CTL poll does not refresh old LTC/VITC. |
| No preview / signal-loss overlay | Another program may hold the capture device; open Connections and press **Refresh**. Check the age/error shown over the retained frame. |
| Recording is blocked by input format | Select PAL/NTSC/Custom and Progressive/TFF/BFF, restart preview, compare readback, then acknowledge that exact pair. |
| Wrong video device after USB changes | Verify the picture, refresh enumeration, and reselect it. Stable ID-to-OpenCV-index mapping remains best-effort. |
| Recording faults immediately | Read the error bar / log; usually a codec missing from the ffmpeg build. |
| Choppy preview while recording | Preview drops frames by design under load; the recording path has priority. |
| Final JSON says `Incomplete` | Preserve all artifacts. Use `StopReason`, `Error`, queue/sync/audit metrics, and the log to find the detected integrity failure; do not label the file a complete transfer. |
| Only `.json.partial` exists | The session or final sidecar write did not complete. Preserve it as crash evidence and inspect the media with ffprobe/full decode. |

Logs: `%AppData%\MagnetoskopCapture\logs\magnetoskop_YYYYMMDD.log` (kept 14 days).
Attach the current day's log when reporting problems.
