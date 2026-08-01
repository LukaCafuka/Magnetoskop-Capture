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
2. In **RECORDER CONNECTION**, pick the adapter's COM port (press ⟳ if it was
   plugged in after the app started).
3. Pick the **device profile** matching your deck (or *Generic Sony 9-pin* for
   anything else).
4. Press **Connect**.

What you should see:

- **RECORDER STATUS** shows the device description. If the deck answers the
  device-type request with a recognized code, an "Identified: …" line appears.
- The transport state (Stopped/Playing/…) updates live, along with status flags
  (TAPE OUT, SERVO LOCK, REC INHIBIT, …).
- **TIME INFORMATION** shows CTL, LTC, VITC, and user bits. Values the deck cannot
  read in the current transport mode (e.g. LTC during fast wind) display as
  `--:--:--:--`.
- If the deck rejects a command as unsupported, the app remembers it and shows it
  in the "Learned:" line of the status panel.

If the connection fails (timeout), check: REMOTE switch, cable pinout, COM port
number, and that no other program holds the port.

## 4. Capture devices and preview

1. In **CAPTURE DEVICES**, pick the video device. The audio device is
   auto-selected by name match (capture cards usually expose both); picking an
   audio device manually overrides auto-select.
2. Press **Start preview**. Live video appears on the left; the stereo level
   meters below the device pickers should move with the audio.

## 5. Transport control

The bottom bar sends commands to the connected deck:
**⏪ Rew · ▶ Play · ■ Stop · ⏩ FF · ⏏ Eject**

Errors (no tape, local mode, unsupported command) are reported in the red error
bar and the log panel.

## 6. Recording

1. In **RECORDING**, choose a profile:
   - **FFV1 + PCM (Matroska, archival)** — lossless preservation master.
     Interlacing and field order are preserved untouched.
   - **H.264 + AAC (MP4, access copy)** — small viewing copy.
   - **ProRes HQ + PCM (MOV)** — for post-production workflows.
2. Choose the output folder (check free space: FFV1 SD material is roughly
   60–90 GB/hour).
3. Optional: enable **Auto-play deck on record** — the app then issues Play and
   waits for servo lock before recording starts.
4. Press **● Record**. If preview is not running it starts automatically. Any
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
| COM port missing from the list | Adapter driver installed? Press ⟳ to re-enumerate. |
| Connect times out | REMOTE switch, cable/pinout, correct COM port. See `docs/HARDWARE_TESTING.md`. |
| Transport buttons error with "not supported" | The deck NAKed the command; expected for some decks (see the "Learned:" line). |
| Timecode frozen at `--:--:--:--` | No tape, or the deck cannot read LTC/VITC in this transport mode. |
| No preview | Another program may hold the capture device; try Refresh + Start preview again. |
| Recording faults immediately | Read the error bar / log; usually a codec missing from the ffmpeg build. |
| Choppy preview while recording | Preview drops frames by design under load; the recording path has priority. |

Logs: `%AppData%\MagnetoskopCapture\logs\magnetoskop_YYYYMMDD.log` (kept 14 days).
Attach the current day's log when reporting problems.
