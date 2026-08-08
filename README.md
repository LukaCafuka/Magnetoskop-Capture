# Magnetoskop Capture

Windows desktop application for videotape recorder control and audiovisual signal
digitization. Controls a VTR over RS-422 (Sony 9-pin protocol), captures the
analog-to-digital converted video/audio signal, shows live preview and time
information (CTL, LTC, VITC, user bits), and records synchronized A/V to archival
and access formats.

Target recorders: Sony PVW-2600P, DVW-M2000P, BVU-950P, UVW-1800P, JVC BR-S622E
(generic Sony 9-pin implementation + per-device profiles).

## Requirements

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
  (the published release build is self-contained and needs no runtime)
- **FFmpeg** (`ffmpeg.exe`) for recording — on PATH, next to the application, or in
  an `ffmpeg/` subfolder. A full/GPL build is required for `libx264` and `prores_ks`.
- USB→RS-422 adapter (FTDI-based recommended) for deck control
- A DirectShow-compatible video capture device and a WASAPI audio input

Everything hardware-related is optional at development time: the app ships with a
simulated VTR, video source, and audio source.

## Build, test, run

```
dotnet build MagnetoskopCapture.slnx      # 0 warnings expected
dotnet test  MagnetoskopCapture.slnx      # full suite incl. deck compatibility tests
dotnet run --project src/Magnetoskop.App  # launches the WPF application
```

## Release packaging

```
powershell -ExecutionPolicy Bypass -File build/publish.ps1
```

Produces a self-contained win-x64 build and a versioned zip under `artifacts/`.
See `build/publish.ps1 -?` for options (version override, skipping the zip).

## Solution layout

| Project | Responsibility |
|---|---|
| `src/Magnetoskop.Core` | Contracts and domain models; no external dependencies |
| `src/Magnetoskop.Serial` | `ISerialTransport` over `System.IO.Ports` |
| `src/Magnetoskop.Protocol.Sony9Pin` | Sony 9-pin framing, commands, parsing, controller |
| `src/Magnetoskop.Capture.Video` | DirectShow enumeration + OpenCvSharp capture |
| `src/Magnetoskop.Capture.Audio` | NAudio WASAPI capture + level metering |
| `src/Magnetoskop.Recording` | FFmpeg external-process recorder (FFV1/H.264/ProRes/DNxHD) |
| `src/Magnetoskop.Simulation` | Simulated VTR, capture sources, and wire-level decks |
| `src/Magnetoskop.App` | WPF UI, MVVM view models, DI composition root |
| `tests/*` | xUnit suites: core, protocol, recording, app workflow, deck compatibility |

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — architecture, protocol summary, feasibility
- [docs/USER_GUIDE.md](docs/USER_GUIDE.md) — setup and capture workflow
- [docs/HARDWARE_TESTING.md](docs/HARDWARE_TESTING.md) — per-deck hardware validation protocol
- [docs/PHASES.md](docs/PHASES.md) — development phase log

## Recording profiles

| Profile | Video | Audio | Container | Purpose |
|---|---|---|---|---|
| FFV1 + PCM | FFV1 level 3, intra-only, slice CRCs, yuv422p | PCM s24le | MKV | Archival master (interlacing preserved) |
| H.264 + AAC | libx264 CRF 18, yuv420p | AAC 192k | MP4 | Access copy |
| ProRes HQ + PCM | prores_ks HQ, yuv422p10le | PCM s16le | MOV | Post-production |
| DNxHR HQ + PCM | dnxhd dnxhr_hq, yuv422p | PCM s16le | MOV | Post-production (Avid) |

Logs are written to `%AppData%\MagnetoskopCapture\logs\`; user settings to
`%AppData%\MagnetoskopCapture\settings.json`. Every recording gets a `.json`
sidecar with device, profile, and start/end timecode metadata.
