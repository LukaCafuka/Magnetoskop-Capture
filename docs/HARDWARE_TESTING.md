# Hardware Validation Protocol

**Scope:** Phase 9 compatibility verification of Magnetoskop Capture against the five
physical target recorders. The automated compatibility suite
(`tests/Magnetoskop.Compatibility.Tests`) validates the protocol stack against
wire-level simulated decks; this document is the companion protocol for validating the
**assumptions the simulation cannot verify** — everything from `docs/ARCHITECTURE.md`
§4.1 — on the real machines.

Target machines:

| # | Model | Format | Notes |
|---|---|---|---|
| 1 | Sony PVW-2600P | Betacam SP | Device type documented: `2X 40` |
| 2 | Sony DVW-M2000P | Digital Betacam | Device type unknown |
| 3 | Sony BVU-950P | U-matic SP | Device type unknown; VITC reader is an installed option |
| 4 | Sony UVW-1800P | Betacam SP | Device type unknown |
| 5 | JVC BR-S622E | S-VHS | Sony-compatible RS-422 subset; coverage unknown |

## 1. Test setup

Required equipment:

- Windows PC with Magnetoskop Capture installed and a USB→RS-422 adapter
  (FTDI-based recommended; must support odd parity at 38,400 baud).
- 9-pin control cable. **Pinouts vary** — verify against the standard Sony controller
  pinout (pin 1 GND, 2 TxA−, 3 RxB+, 7 TxB+, 8 RxA−, frame GND on shell) and the
  deck's service manual before connecting.
- A test cassette per deck **with known LTC and VITC** (record bars + timecode first
  on a working deck if none exists). Note the start timecode on the label.
- Capture device connected to the deck's composite or component output; audio
  connected to the capture device or sound-card line-in.

Deck preparation:

1. Set the deck's remote switch to **REMOTE** (9-pin).
2. Insert the test cassette, rewind to head.
3. In Magnetoskop Capture open **View → Connections…**, select the COM port and
   the deck's profile (connects automatically).

Log collection: application logs are written to
`%AppData%\MagnetoskopCapture\logs\magnetoskop_YYYYMMDD.log`. Protocol frames are
logged in hex at Trace level (raise the log level in `App.xaml.cs` during testing).

## 2. Per-deck test procedure

Repeat sections 2.1–2.7 for every deck. Record results in the table in section 3.

### 2.1 Connection and device identification (§4.1 item 5)

1. Connect. Expected: no timeout errors; the status panel shows the transport state.
2. Read the log line `Device identified: type XX XX → profile ...`.
   - Record the two device-type bytes. If the deck did not answer the Device Type
     Request (JVC is expected not to), record that instead.
3. **Action item:** enter the captured code in
   `src/Magnetoskop.Protocol.Sony9Pin/KnownDeviceProfiles.cs` (`DeviceTypeCode`) and
   in the corresponding `DeckPersonality` so identification becomes automatic.

### 2.2 Transport command coverage (§4.1 item 1)

Issue each command from the transport bar and record ACK/NAK (a NAK "undefined
command" appears in the UI as "Command X not supported by this device" and in the
"Learned:" line of the recorder status panel):

| Command | Expected | Verify |
|---|---|---|
| Play | ACK, deck plays | tape moves, status shows Playing |
| Stop | ACK | deck stops |
| Fast Forward | ACK | wind mode |
| Rewind | ACK | wind mode |
| Eject | ACK | cassette ejects, TAPE OUT flag appears |

### 2.3 Status polling (§4.1 items 2, 3)

1. With the deck stopped, confirm the status panel shows *Stopped* and no spurious
   alarm flags.
2. Press Play on the **front panel** — the app must follow within one poll interval.
3. Record how many status bytes the deck returns (visible in the Trace log:
   `RX 7X20...` — the low nibble of the first byte is the count).
4. Watch for status-bit flicker during Play → Record and Stop → Play transitions
   (the reference warns the timing "varies almost from machine to machine"). If the
   transport display bounces, note the required debounce and adjust
   `StatusPollInterval` in the deck's profile.

### 2.4 Timecode and user bits (§4.1 item 4)

1. Play the test cassette. Verify LTC matches the value recorded on the label and
   advances monotonically at 25 fps.
2. Verify CTL counts and (after zeroing the counter on the deck, where supported)
   agrees with tape movement.
3. Still/pause the tape: check whether VITC is reported (`74 06`/`74 16` in the
   Trace log). For the BVU-950P record whether the VITC option is installed.
4. Fast-forward: LTC should drop out or switch to corrected LTC (`74 14`); the app
   must keep displaying CTL without errors.
5. Verify user bits match the values recorded on the test tape.

### 2.5 Serial link robustness (§4.1 items 6, 7)

1. Record the adapter make/model and driver version.
2. Run 15 minutes of continuous polling (just leave the app connected). Expected:
   zero `Response timeout` warnings in the log. Record the count if not zero, and
   raise `ResponseTimeout` in the profile if the adapter needs it.
3. Unplug the RS-422 cable mid-session: the app must log polling failures and keep
   running; replugging must resume status updates without restarting the app.

### 2.6 End-to-end capture workflow

1. Start preview, confirm picture and audio meters.
2. Enable "Auto-play deck on record", press Record from the deck stopped:
   the deck must go to Play, and recording must start after servo lock.
3. Record ≥ 5 minutes with the FFV1 archival profile. Verify with `ffprobe`:
   `ffv1` + `pcm_s24le` streams, interlaced field order tagged, no dropped frames
   reported in the recording status.
4. Confirm the sidecar `.json` contains the correct start/end timecodes.
5. Repeat a short recording with H.264 and ProRes profiles.

### 2.7 Profile tuning

After the above, tighten the deck's profile values in `KnownDeviceProfiles.cs`:

- `ResponseTimeout`: measured worst-case response time + margin (spec is 9 ms; USB
  adapters add latency).
- `StatusPollInterval` / `TimecodePollInterval`: as fast as the deck tolerates
  without NAKs or timeouts.
- `SupportedCommands`: populate explicitly for decks that NAK part of the set
  (expected for the JVC), so the UI can disable those buttons up front.

## 3. Results

Fill one column per deck. Every "assumed" value in the simulation
(`src/Magnetoskop.Simulation/DeckPersonality.cs`) must end up confirmed or corrected.

| Item | PVW-2600P | DVW-M2000P | BVU-950P | UVW-1800P | BR-S622E |
|---|---|---|---|---|---|
| Device type bytes | | | | | |
| Status byte count | | | | | |
| Play/Stop/FF/Rew/Eject ACKed | | | | | |
| Commands NAKed | | | | | |
| VITC readable | | | | | |
| Worst response time (ms) | | | | | |
| Timeout count in 15 min soak | | | | | |
| Status-bit debounce needed | | | | | |
| FFV1 5-min capture OK | | | | | |
| Adapter + driver used | | | | | |
| Cable pinout verified | | | | | |

Sign-off: date, tester, firmware/serial numbers of the decks tested.
