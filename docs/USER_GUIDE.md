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
- **TIME INFORMATION** shows CTL, LTC, VITC, and user bits. Values the deck cannot
  read in the current transport mode (e.g. LTC during fast wind) display as
  `--:--:--:--`. CTL below zero shows with a leading minus by default
  (e.g. `-00:00:00:01`); enable **CTL 24-hour wrap** in **File → Settings…** to
  show the deck-style wrap (`23:59:59:24`) instead.
- Click **LTC** to edit a target timecode, then press **Enter** to cue the deck
  there (Sony Cue Up With Data, forced to TIME CODE / LTC mode). Use
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
2. Live video appears on the main window; stereo level meters and **Monitor audio**
   are on the main window (and also in Connections). Press **Refresh** in Connections
   to re-enumerate devices if hardware was plugged in after launch.
3. For easier watching of interlaced sources, enable **Yadif 2× deinterlace
   preview** under **File → Settings…**. This bob-deinterlaces the live preview
   only (recording encode is unchanged).
4. Use **View → Watch window…** for a maximized preview with transport and
   Jog/Shuttle only (Esc closes). The main window stays open underneath.

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
   60–90 GB/hour.
3. Optional: enable **Auto-play deck on record** — the app then issues Play and
   waits for servo lock before recording starts.
4. Press **● Capture**. If preview is not running it starts automatically. Any
   preflight issues (missing ffmpeg, no tape, deck not playing, low disk space)
   are listed in a confirmation dialog first.
5. Press **■ Stop rec** to finish. The file is finalized and a `.json` sidecar
   with device, profile, and start/end timecode metadata is written next to it.

Files are named `capture_YYYYMMDD_HHMMSS[_TChh-mm-ss-ff].ext`, where the `TC`
part is the tape timecode at the moment recording started (when a deck is
connected).

Built-in protections while recording:

- The recording **stops automatically and finalizes the file** before the output
  disk runs full (below ~500 MB free).
- A stalled encoder (no frames written for 15 s) faults the recording with the
  FFmpeg error output attached.
- If FFmpeg exits unexpectedly, the error appears in the error bar and the log.

## 7. Verifying a recording

Open the file in VLC or check the streams with ffprobe:

```
ffprobe capture_20260712_193000_TC01-00-00-00.mkv
```

Expected: `ffv1` + `pcm_s24le` (archival), `h264` + `aac` (MP4), or
`prores` + `pcm_s16le` (MOV).

## 8. Troubleshooting

| Symptom | Check |
|---|---|
| "ffmpeg.exe not found" | Install FFmpeg and add to PATH, or copy `ffmpeg.exe` next to the app. |
| COM port missing from the list | Open **View → Connections…**. Adapter driver installed? Press ⟳ to re-enumerate. |
| Connect times out | REMOTE switch, cable/pinout, correct COM port. See `docs/HARDWARE_TESTING.md`. |
| Transport buttons error with "not supported" | The deck NAKed the command; expected for some decks (see the "Learned:" line). |
| Timecode frozen at `--:--:--:--` | No tape, or the deck cannot read LTC/VITC in this transport mode. |
| No preview | Another program may hold the capture device; open Connections and press **Refresh**. |
| Recording faults immediately | Read the error bar / log; usually a codec missing from the ffmpeg build. |
| Choppy preview while recording | Preview drops frames by design under load; the recording path has priority. |

Logs: `%AppData%\MagnetoskopCapture\logs\magnetoskop_YYYYMMDD.log` (kept 14 days).
Attach the current day's log when reporting problems.
