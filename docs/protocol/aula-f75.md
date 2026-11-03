# AULA F75 lighting protocol (wired 258A:010C, 2.4 GHz dongle 3554:FA09)

Status: research document, written 2026-11-03 for the `AulaF75Device` implementation (C#/.NET 8, HidSharp).
Nothing in this document was sent to a real keyboard while writing it. Every fact has a source tag
(see [Sources](#13-sources)) and a confidence:

- **CONFIRMED**: seen in at least one byte-exact capture of the OEM app *and* in the decompiled driver or the local `KB.ini`, or in two independent byte-level sources that agree.
- **PROBABLE**: one strong source (decompilation or hardware test), not contradicted.
- **UNVERIFIED**: one weak source, or sources disagree. Do not depend on it without a hardware test.

All offsets are decimal unless written `0x..`. "Frame offset" = offset in the full 520-byte report
buffer including the report-ID byte. "Page offset" = offset inside the payload (frame offset − 8).

---

## 0. TL;DR for the implementer

| Thing | Value |
| --- | --- |
| Wired device | VID `0x258A`, PID `0x010C`, USB interface 1, top-level collection usage page `0xFF00`, usage `0x0001`, **feature report ID `0x06`, 520 bytes incl. ID** (519 data bytes) |
| Frame | `06 cmd param 00 packages index lenLo lenHi` + data, zero padded to 520 bytes |
| Read | SET_FEATURE(header only, len = bytes wanted) → wait 20 ms → GET_FEATURE(0x06) → data at frame offset 8 |
| Write | wait 20 ms → SET_FEATURE(header + data) |
| Checksum (wired) | **none**. `CRC=1` in KB.ini means "regions end in the marker `5A A5`" (settings page bytes 126–127, colour table bytes 506–507) |
| Handshake | identity read `06 82 01 00 01 00 06 00`; reply bytes 8..13 must be `03 00 00 00 00 CD` / `03 00 00 00 00 E1` / `03 00 00 00 01 91` (local KB.ini `Psd=`). No unlock, no apply/save, no keepalive for stored settings |
| Effect | read settings page (`0x84`, 128 B) → patch byte `0x0a` (effect), `0x09` (per-key flag), slot pair at `0x3a + 2*slot` → write (`0x04`, 128 B) |
| Custom colour | colour table (`0x8a`/`0x0a`, 512 B): row = effect id, 7 RGB slots × 3 bytes, offset `row*21 + slot*3`; settings low nibble selects slot (0–6), 7 = colourful |
| Per-key | `0x06` param 0, **378 bytes planar**: R[126], G[126], B[126], index = LED index; then settings page with effect `21` (0x15), `0x09 = 1`, `0x0b = 0x20` |
| Live/volatile per-key | `0x08` param 0, 378 bytes **interleaved** RGB by LED index; reverts to stored effect after ~3–5 s idle → resend ≥ 1–2 Hz |
| Dongle | 20-byte **output/input** report `0x13` (not feature): `13 cmd total index (sub<<4|len) data[14] sum`, `sum = (bytes 0..18) & 0xFF`; cmds: settings `0x04`/`0x44`, colour table `0x09`/`0x49`, per-key `0x02`/`0x42`, live `0x88` |
| Bluetooth | no lighting configuration. Send nothing |
| Never send | report ID `0x05` (bootloader), wired cmds `0x03 0x05 0x0b 0x0c 0x0d 0x0e 0x0f 0x11 0x88 0x89 0x9d 0x9e 0x9f`, dongle cmds other than the ones above, anything unknown |

---

## 1. Devices, interfaces, report IDs

### 1.1 Wired (USB cable, mode switch on "wired"/cable) — CONFIRMED

- VID/PID `258A:010C` [KBINI wired/1/F75KR `[OPT] VID/PID`] [ORGB] [OF75 §4].
- The keyboard is a composite device. Interface 0 = boot keyboard. The command channel is on **interface 1**, the top-level collection with **usage page `0xFF00`, usage `0x01`**, which declares **feature report `0x06` with 519 data bytes** (descriptor `85 06 … 96 07 02 … B1 02`) [OF75 §4 `CDevG5KB::FindHIDDevice`] [ORGB `REGISTER_HID_DETECTOR_IPU(…, 0x010C, 1, 0xFF00, 1)`] [XEV] [QY `interface_number == 1 && usage_page == 0xFF00`].
- On Windows every top-level collection is its own HID device path (`…&col0N`). With HidSharp: enumerate `DeviceList.Local.GetHidDevices(0x258A, 0x010C)` and pick the device whose `GetMaxFeatureReportLength() == 520` **and** whose report descriptor's top-level usage is `0xFF000001`. Do not open the keyboard/consumer collections for writing.
- Report buffer for HidSharp: `byte[520]`, `buf[0] = 0x06`. `HidStream.SetFeature(buf)` / `HidStream.GetFeature(buf)` (set `buf[0]=0x06` before `GetFeature`).
- Only report ID `0x06` is used by the official driver for this keyboard (profile `Fw=24` → report ID 6; other firmware families use ID 9) [OF75 §5].
- The USB `bcdDevice` (HidSharp `ReleaseNumberBcd`) is the firmware version; there is no firmware-version command [OF75 §14].
- `258A:010C` is shared by other AULA models (F87, F99, H108) and other brands (LEOBOG Hi75C Pro, Redragon K686). OpenRGB disabled other Sinowealth detectors after reused IDs bricked Redragon boards. **Always do the identity read first and refuse to write unless it matches** [OF75 `ids.ts`] [ORGB `SinowealthControllerDetect.cpp` comment].

### 1.2 2.4 GHz dongle (3554:FA09) — PROBABLE

- VID/PID `3554:FA09` [KBINI 1, F75KR `VID_Wireless/PID_Wireless`]. Note the **wired-only profile** (`kb\wired\KB.ini`) has `VID_Wireless=0`: some F75 SKUs have no dongle at all.
- Transport: **20-byte interrupt output reports, report ID `0x13`**, answered by 20-byte input reports `0x13`; *not* feature reports [OF75 §15 `CDev3632`] [BR0 report "Wireless findings"] [VINI `device.ts`] [GOD captures].
- Collection: the one with **usage page `0xFF02`** that declares output report `0x13` [VINI `FILTERS`/`isConfigInterface`] [QY `usage_page == 0xFF02`]. BR0 reports a second collection on page `0xFF04` with an 8-byte feature report 6 — not used for lighting. With HidSharp: `GetMaxOutputReportLength() == 20 && GetMaxInputReportLength() == 20`, usage page `0xFF02`. Write with `HidStream.Write(byte[20])` (`[0] = 0x13`); read replies with `HidStream.Read(byte[20])` and ignore input reports whose byte 0 is not `0x13`.
- The dongle has no interrupt-OUT endpoint on some hosts; Linux tools used SET_REPORT(Output) control transfers [GOD README]. On Windows `WriteFile` (HidSharp `Write`) is what the OEM driver uses (`FUN_00494c20`) [OF75 §15]; the Windows HID class driver falls back to SET_REPORT when there is no OUT endpoint.
- Full framing in [§9](#9-24-ghz-dongle-protocol).

### 1.3 Bluetooth — not supported for lighting (PROBABLE)

- No source configures lighting over Bluetooth. VINI/F75 Studio deliberately sends nothing over BT: "the keyboard answers only partially and config traffic disrupts typing" [VINI README, `device.ts BT_UNSUPPORTED`]. RustamSheoran/shou README: "Bluetooth: Unsupported". The driver has no BT path [OF75 §15].
- PID disagreement: the task statement says BT = `3554:FA07`; VINI filters `3554:FA08` for BT. BR0 mentions an unrelated `25A7:FA07` receiver. Unresolved; irrelevant because nothing is sent over BT.
- Settings written over cable/dongle are stored on the keyboard and apply in BT mode too [VINI README] [SHOU README "Onboard Persistence"].

---

## 2. Wired frame format — CONFIRMED

Built by `CDevG5KB::AccessData` (`FUN_004993c0`) [OF75 §5]; identical in BR0 (`aula_g5_build_packet`), VINI (`wiredPackets`), OpenRGB, and all captures [GOD fragments] [XEV].

| Frame offset | Field |
| --- | --- |
| 0 | report ID `0x06` |
| 1 | command (bit 7 set = read) |
| 2 | parameter (e.g. `0x01` for identity, per-key colour group, key layer) |
| 3 | `0x00` |
| 4 | number of packages = `ceil(totalLength / 512)` (`0x01` for everything in this document) |
| 5 | package index, 0-based |
| 6–7 | this package's payload length, little endian, ≤ 512 |
| 8–519 | payload, zero padded. **Always send all 520 bytes** (the firmware rejects short reports [OF75 `wired.ts`]) |

Note: RustamSheoran/shou (`f75.ts`) describe bytes 2–5 as a 4-byte "address `00 00 01 00`". For param 0 / one package that is the same bytes; use the driver's interpretation above.

### 2.1 Write
1. wait 20 ms (driver: `Sleep(0x14)` before every config transfer; 7 ms for streaming cmd `0x08`) [OF75 §5]
2. SET_FEATURE (520 bytes)
3. if SET_FEATURE fails, retry up to 3 times, 70 ms apart [OF75 §5] [VINI `wiredSend`]

### 2.2 Read
1. wait 20 ms
2. SET_FEATURE with the header only: `06 (cmd|0x80) param 00 01 00 lenLo lenHi`, rest zero
3. wait 20 ms
4. GET_FEATURE report `0x06` (`byte[520]`)
5. reply: bytes 0–7 echo the request header exactly (BR0 checks `memcmp(reply, request, 8)` and it passed on a live F75 [BR0 `protocol.c`, `analysis/report.md`]); data starts at frame offset 8.
   - Linux returns only 8 + length bytes (e.g. 136 for the settings page); Chrome/macOS returned 14 bytes for the identity [BR0] [OF75 `parseReply`]. On Windows `HidD_GetFeature` fills the 520-byte buffer; just take bytes 8..8+len.
   - A bare GET_FEATURE without the preceding SET returns nothing useful [XEV].
   - Validate: byte 1 == requested cmd, and the region marker (§3) is present. The driver itself does not validate (it silently copies stale buffers) — be stricter [OF75 §5].

### 2.3 Checksum — CONFIRMED: none on the wired link
`CRC=1` in `KB.ini` does **not** mean a computed CRC. It means each region carries the end marker `5A A5`
(`0x5A` then `0xA5`) at a fixed place [OF75 §7, §8, §12] [BR0 "the config marker is not a computed checksum"] [XEV "No checksum changes"]. Without `CRC=1` the driver would send the colour table as 420 bytes without marker.

| Region | Length | Marker position (payload offsets) |
| --- | --- | --- |
| Settings page (`0x84`/`0x04`) | 128 | 126–127 = `5A A5` |
| Colour table (`0x8a`/`0x0a`) | 512 | 506–507 = `5A A5` (rest of 420..511 zero) |
| Key matrix (`0x83`/`0x03`, not for us) | 512 | 508–511 = `00 00 5A A5` |
| Per-key colours (`0x86`/`0x06`) | 378 | **no marker** [BR0 `lighting_ext.h`] [GOD self_define capture] |
| Direct frame (`0x08`) | 378 | no marker |

---

## 3. Handshake, apply/save, keepalive

### 3.1 Identity ("password") read — CONFIRMED
Request (frame bytes 0–7): `06 82 01 00 01 00 06 00` [OF75 §4, `wired.ts buildIdentityRequest`] [ORGB `DetectSinowealthKeyboard10c` `{0x06,0x82,0x01,0x00,0x01,0x00,0x06}`].
Reply: 6 bytes at frame offset 8..13. OpenRGB uses `response[13]` (the 6th byte) as model id.

The driver compares all 6 bytes with the profile's `Psd=` and ignores the device on mismatch ("Psd unmatch") [OF75 §4]. Accept exactly these (local ground truth):

| Local profile | `Psd=` | Identity bytes | Notes |
| --- | --- | --- | --- |
| `kb\1\KB.ini` "AULA F75" (tri-mode, dongle) | `3,0,0,0,0,cd` | `03 00 00 00 00 CD` | = OpenRGB `0xCD` "AULA F75", = open-f75 |
| `kb\wired\KB.ini` "AULA F75 Wired" | `3,0,0,0,0,e1` | `03 00 00 00 00 E1` | not known to OpenRGB or open-f75 |
| `kb\F75KR\KB.ini` "AULA F75 KR" | `3,0,0,0,1,91` | `03 00 00 00 01 91` | Korean layout variant |

Reject everything else (F87 `…00 8F`/`…01 0B`/`…00 A9`, F99 `…00 A4`/`…01 58`/`…00 9A`, H108 `…03 7C` [OF75 `ids.ts`]; LEOBOG `0xA3`, Redragon `0x20`/`0x05` [ORGB devices]).

### 3.2 Unlock / init — none (CONFIRMED)
No unlock or mode-enter command exists. The OEM wired sequence for an effect change is exactly three frames [GOD `fragments/*_frag0..2.bin`]:
1. `06 0A 00 00 01 00 00 02` + 512-byte colour table (write)
2. `06 84 00 00 01 00 80 00` (settings read request; the GET_FEATURE is not in the OUT capture)
3. `06 04 00 00 01 00 80 00` + 128-byte settings page (write)

For per-key the first frame is `06 06 00 00 01 00 7A 01` + 378 bytes instead [GOD `17_self_define_frag0.bin`].
The xevrion "init/handshake" `06 84…` turned out to be this read request [XEV].

### 3.3 Apply / save / commit — none (CONFIRMED for wired)
Writes take effect immediately; there is no separate apply or flash-commit command in the driver's command table [OF75 §6] nor in any OEM capture [GOD]. The settings, colour table and per-key colours survive unplugging and mode switches [SHOU README "Onboard Persistence"] [VINI README "Everything is written to the keyboard right away"] — treat them as **non-volatile (flash)**: don't write them in a loop (wear). Use `0x08` for animation.

- RustamSheoran/shou send a "flash-commit probe" `06 05 0A 01 04 07 …` on wired. Command `0x05` is **SetMacro** (macro buffer write). Never send it (see §10).
- shou's dongle "save" `13 0A 01 00 04 07 …` does not appear in the OEM dongle capture [GOD `wireless_sequence.bin`] nor in the driver's dongle command table [OF75 §15]. UNVERIFIED, don't send.

### 3.4 Driver pacing (use it) — CONFIRMED [OF75 §4, `keyboard.ts`]
`CDevG5KB::ApplySetting` order and waits, on top of the 20 ms per transfer:
1. per-key colour group `0x06`: `Sleep(WaitTm)` = 30 ms first
2. colour table `0x0a`: 120 ms after the previous write
3. settings page: `WaitTm` (30 ms), read `0x84` (retry up to 5×, 100 ms apart, until the page ends in `5A A5`), patch, then 60 ms (or 100 ms after a colour-table/per-key write) before writing `0x04`
4. 500 ms before the next transaction / read-back

open-f75 reports their F75 "sometimes went dark after a change that read back correctly" with only 20 ms pacing; the driver's waits fixed it [OF75 §17].

### 3.5 Keepalive
- Stored settings / effects / per-key (`0x04`, `0x0a`, `0x06`): **no keepalive** (CONFIRMED by the absence of any periodic traffic in the driver and captures).
- Direct frames (`0x08`): the firmware reverts to the stored effect after a few idle seconds (~3–5 s). Resend the current frame at ≥ 1–2 Hz while a static direct image must stay visible (kveld9 uses 1.5 Hz; shou 20 Hz) [KV `docs/PROTOCOL.md` §3, `proto.rs KEEPALIVE_HZ = 1.5`] [SHOU `direct-mode.ts startPreviewKeepalive`]. PROBABLE (two independent hardware observations).
- Dongle: the driver polls "online" status; not needed for lighting writes. See §9.5.

---

## 4. Settings page (128 bytes) — the effect selector

Read: `06 84 00 00 01 00 80 00`. Write: `06 04 00 00 01 00 80 00` + 128 bytes. **Always read-modify-write**: patch only the bytes below, keep everything else exactly as read (the driver does the same; only this region is RMW) [OF75 §4, §7] [BR0 `aula_protocol_patch_cfg`].

| Page offset | Meaning | Write rule | Confidence |
| --- | --- | --- | --- |
| `0x03` | debounce | preserve | field CONFIRMED; encoding disputed (OF75: value 1 = profile default; BR0: `ms − 1`, 0..4) |
| `0x09` | per-key mode flag | `1` iff effect == 21, else `0` | CONFIRMED [OF75 §7] [GOD: `00` fixed-on, `01` self-define] |
| `0x0a` | **active effect id** (§5) | effect id | CONFIRMED [GOD: frame offset 0x12 = `01` fixed, `02` respire, `15` self-define] |
| `0x0b` | `0x20 + per-key colour group` | `0x20` when selecting 21 (F75 has one group); otherwise preserve | CONFIRMED [OF75 §7] [GOD: `20` in every capture] |
| `0x12` | disputed: side-LED mode (OF75, "n/a on F75") vs charging-indicator on/off toggled by Fn+LShift (BR0, live test) | preserve | UNVERIFIED |
| `0x18` | sleep timeout in 30 s units (`SleepHW` 2,3,4,6,8,10,20,30,40; 0 = never) | preserve | CONFIRMED |
| `0x38`–`0x39` | `FF FF` | always `FF FF` | CONFIRMED [OF75] [BR0] [GOD] |
| `0x3a + 2·slot` | effect slot byte 0: **brightness** | 0–9 | CONFIRMED |
| `0x3b + 2·slot` | effect slot byte 1: **`speed << 4 \| colour`** | speed 0–4 (high nibble); colour 0–6 = colour-table slot, 7 = colourful/random | CONFIRMED |
| `0x60`–`0x7d` | unknown firmware data (`09 37 07 47 07 47 07 44 …` in captures) | preserve | — |
| `0x7e`–`0x7f` | `5A A5` | must be present on read and write | CONFIRMED |

Slots: one 2-byte slot per effect **in LedOpt list order**, slot = list position − 1:
effects 1..18 → slot `id − 1` (offsets `0x3a`..`0x5d`), per-key (id 21) → **slot 18** (`0x5e`/`0x5f`), Off (id 0) → no slot [OF75 §7, §9] [BR0 `0x3a + 2*(mode-1)`] [VINI `editLedBlock`] [XEV: Ripples shining at frame offsets 78–79 = page `0x46`–`0x47` = slot 6 ✓].

Factory/default slot contents seen in captures: Fixed on `09 40`/`07 46`, animated effects `09 47` (brightness 9, speed 4, colourful), slots 16–18 `09 37`/`09 47` [GOD] [OF75 §7 owner's board].

Example: the OEM "Fixed on" settings payload (page offsets 0x00–0x7f, frame from `GOD 01_fixed_on_frag2.bin`):
```
00 03 03 01 00 00 04 04 07 00 01 20 01 00 00 00
00 00 02 00 02 01 00 ff 0a 00 00 01 01 00 03 01
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 ff ff 09 40 09 47 09 47
09 47 09 47 09 47 09 47 09 47 09 47 09 47 09 47
09 47 09 47 09 47 09 47 09 47 09 47 09 47 09 47
09 37 07 47 07 47 07 44 07 44 07 44 07 44 07 44
07 44 07 44 04 09 04 04 04 04 04 04 04 04 5a a5
```
(`0x09`=00, `0x0a`=01 Fixed on, `0x0b`=20, `0x18`=0a = 300 s, slot 0 `09 40` = brightness 9, speed 4, colour slot 0.)

### 4.1 Brightness — CONFIRMED
`Light=0..9`, `LightHW=0..9` (identity) [KBINI]. 0 = dark (not "off" mode; the effect stays selected). BR0 confirmed a 0→1 change and exact read-back on a live F75. XEV: `0x09` full, `0x01` visibly dim. For effect id 0 (Off) there is no slot; brightness is irrelevant.

### 4.2 Speed — range CONFIRMED, direction UNVERIFIED
`Speed=0..4`, `SpeedHW=0..4` (identity), stored in the high nibble [KBINI] [OF75]. Values > 4 are clamped by the firmware (`0x57`, `0xF7` look like `0x47`) [XEV].
Direction: xevrion's usbmon diff of the OEM UI shows **slider slowest → `0x40`, fastest → `0x00`** ("lower value = faster") [XEV]. open-f75 assumes 4 = fastest only for its on-screen preview, without device evidence. Implement raw 0–4, label tentatively "0 = fastest", verify on hardware.

### 4.3 Direction — none
Every F75 `LedOpt` line has `direct = 0`; no F75 effect has a direction field [KBINI] [OF75 §9].

### 4.4 Colour nibble — CONFIRMED
Low nibble of slot byte 1: `0..6` selects slot 0..6 of the effect's colour-table row (§6); `7` = colourful/random (`RandomVal = 7`) [OF75 §7] [BR0 `patch_color_selection` "7 selects multicolor"] [GOD wireless: colourful on `0x47`, off `0x40`]. The OEM driver itself only writes 0 or 7 and puts the chosen colour into slot 0; the firmware's Fn shortcuts use 1–6 [OF75 §7].

---

## 5. Effect ids (byte `0x0a`) — CONFIRMED

`KB.ini` comment is `//hw effect speed light direct random color`. **The first number of `LedOptN` (`hw`) is what goes into byte `0x0a`** and is the colour-table row. The second number (`effect`) only selects the driver's on-screen preview animation [OF75 §9, `FUN_00411830`/`FUN_00431ca0`]. Verified by captures: Respire is `LedOpt2 = 2,3,…` and the OEM sends `0x0a = 02`; Self-define is `LedOpt19 = 21,0,…` and the OEM sends `0x15` [GOD]. (The task brief labelled the fields "driverIndex, hwEffectId" — that is reversed.)

Names from local `Text\en\text.xml` `tc_kb_led1..20`; flags from local `KB.ini` (identical in `wired`, `1`, `F75KR`).

| LedOpt | Name (driver) | `0x0a` | Slot (page offset) | speed | light | random | colour | Masked by local `LedMask=0x22120`? | OEM-captured [GOD] |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Fixed_on | 1 | 0 (`0x3a`) | – | ✓ | ✓ | ✓ | | ✓ |
| 2 | Respire | 2 | 1 (`0x3c`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 3 | Rainbow | 3 | 2 (`0x3e`) | ✓ | ✓ | – | – | | ✓ |
| 4 | Flash_away | 4 | 3 (`0x40`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 5 | Raindrops | 5 | 4 (`0x42`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 6 | Rainbow_wheel | 6 | 5 (`0x44`) | ✓ | ✓ | ✓ | ✓ | **yes** (bit 5) | ✓ |
| 7 | Ripples_shining | 7 | 6 (`0x46`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 8 | Stars_twinkle | 8 | 7 (`0x48`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 9 | Shadow_disappear | 9 | 8 (`0x4a`) | ✓ | ✓ | ✓ | ✓ | **yes** (bit 8) | – |
| 10 | Retro_snake | 10 | 9 (`0x4c`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 11 | Neon_stream | 11 | 10 (`0x4e`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 12 | Reaction | 12 | 11 (`0x50`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 13 | Sine_wave | 13 | 12 (`0x52`) | ✓ | ✓ | ✓ | ✓ | | ✓ |
| 14 | Retinue scanning | 14 | 13 (`0x54`) | ✓ | ✓ | ✓ | ✓ | **yes** (bit 13) | – |
| 15 | Rotating windmill | 15 | 14 (`0x56`) | ✓ | ✓ | – | – | | ✓ |
| 16 | Colorful waterfall | 16 | 15 (`0x58`) | ✓ | ✓ | – | – | | ✓ |
| 17 | Blossoming | 17 | 16 (`0x5a`) | ✓ | ✓ | – | – | | ✓ |
| 18 | Rotating storm | 18 | 17 (`0x5c`) | ✓ | ✓ | ✓ | ✓ | **yes** (bit 17) | – |
| 19 | Self-define (per-key) | **21** (`0x15`) | 18 (`0x5e`) | – | – | – | – | | ✓ |
| 20 | OFF | **0** | none | – | – | – | – | | ✓ |

`LedMask`: bit *n* (0-based) hides `LedOpt(n+1)`; the driver then neither lists the effect nor writes its colour-table row [OF75 §9 `FUN_004305d0`]. Bit numbering is confirmed by the captures: with the 2026 driver's `LedMask=0x22100` the OEM offered everything except 9, 14, 18 [GOD]. The **locally installed driver (older build, `OemDrv.exe` 2,633,728 bytes, 2024-05-17, SHA-256 `169d8279…8e45e34e`) uses `0x22120`, which additionally hides Rainbow_wheel (6)**. Rainbow_wheel was captured working from a newer OEM build [GOD `wired/light_effect/rainbow_wheel.pcapng`], so it is safe to offer; open-f75 reports that writing the masked 9/14/18 "could leave the keys dark" — hide 9, 14, 18 by default.

Ranges: brightness 0–9, speed 0–4, colour nibble 0–7 (7 = colourful only where `random = 1`).

### 5.1 Packet recipe: set a built-in effect (wired)
```
page = Read(0x84, len 128)            // check page[126]=0x5A, page[127]=0xA5
page[0x0A] = id                        // table above
page[0x09] = (id == 21) ? 1 : 0
page[0x38] = 0xFF; page[0x39] = 0xFF   // BR0 forces this; they are FF FF already
if (id != 0):
    s = slot(id)                       // id-1 for 1..18, 18 for 21
    page[0x3A + 2*s] = brightness      // 0..9
    page[0x3B + 2*s] = (speed << 4) | colourNibble
Write(0x04, page)                      // frame: 06 04 00 00 01 00 80 00 + page + zeros
wait 500 ms; optionally Read(0x84) and compare
```

---

## 6. Colour table (512 bytes) — custom colour for an effect

Read `06 8A 00 00 01 00 00 02`, write `06 0A 00 00 01 00 00 02` + 512 bytes. Driver name `GetLedRgbTab`/`SetLedRgbTab` [OF75 §8].

Layout — CONFIRMED [OF75 §8] [BR0 `patch_palette offset = mode*21 + index*3`] [GOD `01_fixed_on_frag0.bin` decoded below]:
- 20 rows × 21 bytes = 420 bytes; row *r* = effect id *r*; row 0 (Off) and rows of masked effects are zero.
- Each row = 7 colour slots × 3 bytes, **interleaved R, G, B** (`RGBIndex=0,1,2`).
- `offset(row, slot) = row*21 + slot*3`.
- Bytes 420..505 zero; **506–507 = `5A A5`**; 508–511 zero.
- Default row = red, green, blue, yellow, magenta, cyan, white (slots 0–6).
- Per-key (id 21) would be row 21 = offsets 441–461, which lies inside the zero area; per-key colours do not use this table.

Capture decode (OEM "Fixed on, cyan"): row 1 (payload 21–41) = `00 FF FF | 00 FF 00 | 00 00 FF | FF FF 00 | FF 00 FF | 00 FF FF | FF FF FF` → slot 0 = the custom colour (cyan), slots 1–6 default; row 2 slot 0 = `FF 00 00`; row 9 all zero (masked); `5A A5` at payload 506 (frame 0x202) [GOD].

Recipe: custom colour for effect *id* (with `colour = 1`):
1. read colour table (`0x8a`), check `5A A5` at 506.
2. write RGB into `row = id`, slot 0 (offset `id*21`).
3. write colour table (`0x0a`, 512 bytes) — wait 120 ms before it.
4. RMW the settings page: `0x0a = id`, nibble = 0 (§5.1), write.

Disagreements (do **not** follow): xevrion calls `0x0a`/`0x8a` a per-key table with 4 bytes per key (`RR GG BB 00`); RustamSheoran/shou use it that way for per-key; AulaSofwareLinux read it as 3 bytes/LED. The decompiled driver and the OEM capture show the row layout above [OF75 §8 "AulaSofwareLinux's description is wrong"]. VINI says the RGB table "hasn't been decoded" and emulates custom Fixed-on with per-key mode — workable but unnecessary.

---

## 7. Per-key colours (stored) — "Self-define", effect 21

### 7.1 Packet — CONFIRMED
`06 06 00 00 01 00 7A 01` + **378 bytes** (`0x017A`), parameter = colour group = **0** (`GaoshouGroupNum=1`) [OF75 §10 `SetGame FUN_00499cf0`] [GOD `17_self_define_frag0.bin`] [BR0 `aula_lighting_write_perkey(…6,0,…,378)`].

Layout — **planar**, by LED index (`GameRGBIndex=0,1,2`):
| Payload bytes | Plane |
| --- | --- |
| 0–125 | Red[LED 0..125] |
| 126–251 | Green[LED 0..125] |
| 252–377 | Blue[LED 0..125] |

`offset_R = led`, `offset_G = 126 + led`, `offset_B = 252 + led`. Unused LEDs = 0. No marker, no checksum.
Capture proof: the OEM default preset (`GaoshouKey1` = Esc, W, A, S, D, arrows; `clrGaoshou=255,0,0`) arrives as `FF` at payload 0 (Esc), 9 (A), 14 (W), 15 (S), 21 (D), 77 (Left), 82 (Up), 83 (Down), 89 (Right), all in the red plane [GOD].

### 7.2 Sequence — CONFIRMED (order), PROBABLE (brightness)
1. wait 30 ms, write `0x06` (378 bytes, planar)
2. RMW settings page: `0x0a = 0x15` (21), `0x09 = 0x01`, `0x0b = 0x20`; write after ≥100 ms
   - OEM capture leaves slot 18 (`0x5e`/`0x5f`) as `09 47`. VINI writes the per-key brightness into slot 18 byte 0 (`0x5e`) — PROBABLE that slot 18 brightness scales per-key mode.
3. To leave per-key mode select another effect; the driver always writes `0x09 = 0` for any other effect [OF75 §17].

The driver writes the colours **before** the settings page [OF75 §4]. shou claims (raw-hidraw experiments) the table only "retains" while in self-define, but that was for their mistaken use of `0x0a`; for `0x06` follow the driver order.

Persistence: stored on the keyboard (survives replug) — PROBABLE (driver writes it once on Apply; shou/VINI READMEs). Do not stream with `0x06` (Qyzom's MCHOSE plugin does, at 28 fps, length `0x0180`=384 — flash wear risk unknown; avoid).

### 7.3 Read back — UNVERIFIED layout
`06 86 00 00 01 00 7A 01` → 378 bytes, assumed same planar layout. The OEM driver never reads it [OF75 §10] [VINI]. BR0 read it live ("custom colors return 378 bytes"). Treat failure as "unknown".

### 7.4 LED index map — CONFIRMED
LED index = last field of each `K<n>=` line in `[KEY]` of the local `KB.ini`; `index = column*6 + row`, row 0 = F-key row. The local wired profile is ANSI and agrees with OpenRGB `aula_f75_layout` for every key, and with kveld9's table:

| Row | Keys (LED index) |
| --- | --- |
| F-row | Esc 0, F1 12, F2 18, F3 24, F4 30, F5 36, F6 42, F7 48, F8 54, F9 60, F10 66, F11 72, F12 78 |
| Number | ` 1, 1 7, 2 13, 3 19, 4 25, 5 31, 6 37, 7 43, 8 49, 9 55, 0 61, - 67, = 73, Backspace 79, Delete 85 |
| Q-row | Tab 2, Q 8, W 14, E 20, R 26, T 32, Y 38, U 44, I 50, O 56, P 62, [ 68, ] 74, \ 80, PgUp 86 |
| Home | CapsLock 3, A 9, S 15, D 21, F 27, G 33, H 39, J 45, K 51, L 57, ; 63, ' 69, Enter 81, PgDn 87 |
| Shift | LShift 4, Z 10, X 16, C 22, V 28, B 34, N 40, M 46, , 52, . 58, / 64, RShift 70, Up 82, End 88 |
| Bottom | LCtrl 5, LWin 11, LAlt 17, Space 35, Fn 53, RCtrl 59, Left 77, Down 83, Right 89 |
| Knob | 84 (`K81` "Wheel", no LED) |

Sources: local `kb\wired\KB.ini` `[KEY]`; [ORGB `SinowealthKeyboard10cDevices.cpp aula_f75_layout`]; [KV `docs/PROTOCOL.md` §3.1]. The ISO profile used by open-f75 (`f75-fr`) differs only at RAlt/Fn (53/59) and ISO keys 75/76 [OF75 §12]; `F75KR\KB.ini` maps K75 (59) to VK `0xC4` (Hanja) instead of RCtrl. 80 key LEDs; max index 89; plane size 126.

---

## 8. Live / direct frames (volatile) — `0x08`

Not needed for stored lighting, but it is the only volatile per-key path and the only one safe to send at high rate.

- Frame: `06 08 00 00 01 00 7A 01` + 378 bytes, **interleaved** `R,G,B` at `led*3` [OF75 §11 `FUN_0049a7b0`] [ORGB `SetLEDsDirect` `buf[0x08 + i*3]`] [BR0 `aula_lighting_send_stream`] [KV]. CONFIRMED.
- Interleaved here vs planar for `0x06` — easy to mix up. BR0: "Live frames use INTERLEAVED RGB, unlike stored per-key colors."
- Wired only (driver sends it only when connected by cable; over the dongle it uses cmd `0x88` "music" packets) [OF75 §11].
- Pacing: driver waits 7 ms; OpenRGB 1 ms; Qyzom ≥35 ms; VINI ~50 fps. Recommend 30–40 ms (25–33 fps).
- Keepalive: firmware falls back to the stored effect after ~3–5 s without frames → resend ≥ 1–2 Hz (§3.5).
- Effect pre-selection: the driver does not change the effect before streaming [OF75 §11]; VINI switches to per-key (21) at brightness 9 first; kveld9 says "keyboard must be in its onboard custom/static RGB profile". UNVERIFIED which is required — test.
- kveld9 observed transient `EPIPE` on Linux with zero-padded frames and fills all 510 bytes; OEM/OpenRGB send zero padding without issue. UNVERIFIED; on Windows `HidD_SetFeature` always sends 520 bytes.
- shou's "122 LEDs" (`0x7A`) misreads the length field low byte; the length is `0x017A` = 378 [CAP via OF75 `wired.ts`].
- shou's `ENABLE_SEQUENCE` (reports `0x39`, `0x3c`) is from another firmware; report IDs not declared by the F75 — never send.

---

## 9. 2.4 GHz dongle protocol

### 9.1 Packet — CONFIRMED (framing, checksum)
| Byte | Field |
| --- | --- |
| 0 | `0x13` report ID |
| 1 | command (read = write cmd \| `0x40`) |
| 2 | total packets in this transfer |
| 3 | packet index, 0-based |
| 4 | `(sub << 4) \| len`, len = data bytes in this packet (≤ 14) |
| 5–18 | data (14 bytes, zero padded) |
| 19 | checksum = `(byte0 + … + byte18) & 0xFF` |

Sources: [OF75 §15 "`13 cmd total index (flag<<4|len) data[14] sum`"] [VINI `writePackets`/`checksum`] [GOD `protocol.py checksum`] [QY wireless loop]. Verified on the OEM capture: `13 44 01 00 00 … 58` → `0x13+0x44+0x01 = 0x58` ✓ [GOD `wireless_sequence.bin`].

Read request: `13 (cmd|0x40) 01 00 (sub<<4) 00…00 sum` [VINI `readRequest`]; reply = N input reports with the same layout; reassemble data by index.
Write ack: after each packet the dongle answers an input report with the same cmd and index; cmd bit 7 (or byte 2 bit 7) = checksum error → resend (driver resends; VINI retries 5×, 1 s timeout) [OF75 §15] [VINI `wirelessWrite`].

### 9.2 Commands (lighting only)
| Region | Wired | Dongle write / read | Length → packets |
| --- | --- | --- | --- |
| Settings page | `0x04`/`0x84` | `0x04` / `0x44` | 128 → 10 (`13 04 0A idx …`) |
| Colour table | `0x0a`/`0x8a` | `0x09` / `0x49` | 512 → 37 (`13 09 25 idx …`) |
| Per-key colours | `0x06`/`0x86` | `0x02` / `0x42` | see 9.4 |
| Live LEDs | `0x08` | `0x88` (not acknowledged, 10 ms/packet) | colour-group format, see shou `wireless-mode.ts` |

[OF75 §15 `CDev3632::vftable`] [VINI `WIRELESS` cmds] [GOD capture]. The settings page and colour table bytes and offsets are identical to the wired ones (ten 14-byte fragments line up with §4) [GOD] [SHOU `CFG_TEMPLATE`].

### 9.3 OEM sequence (captured) — CONFIRMED
48 packets: 37× `13 09 25 00..24` colour table (last packet `13 09 25 24 08 00 00 5A A5 …` = 8 bytes, marker at 506), 1× `13 44 01 00 00 …` settings read, 10× `13 04 0A 00..09` settings write (last `13 04 0A 09 02 5A A5 …`). No save/apply packet [GOD `fragments/wireless_sequence.bin`]. Godera3 notes ~171 ms pause before the first settings write (waiting for the read reply) and 60–105 ms before the previous packet.

### 9.4 Dongle per-key — UNVERIFIED
Two incompatible descriptions: VINI pads the planes to 384 bytes (`MATRIX_LEN*3`, 28 packets); shou sends 27 packets of plane data + a 28th with `len 6` = `00 00 5A A5 00 00` (marker at 380). The driver's dongle per-key transfer was not byte-traced and no OEM capture exists (Godera3: self-define "never captured over the dongle"). Do not implement until captured.

### 9.5 Online check — UNVERIFIED
VINI: command `0x07` returns "keyboard linked to receiver". open-f75: "report `0x15`, command 7". Not required for writes.

### 9.6 What works over the dongle
VINI/F75 Studio: effects, per-key and remap work over 2.4G; live/macros don't. shou/RustamSheoran README claims the receiver can't read/write settings — contradicted by Godera3's working OEM capture/replay and by the driver. Trust the driver + capture: settings and colour table work over the dongle (PROBABLE; framing CONFIRMED).

---

## 10. Dangerous / never-send list

Implement as an allowlist, not a blocklist. Allowed wired commands: `0x82`(param 1, len 6), `0x84`/`0x04` (param 0, len 128), `0x8a`/`0x0a` (param 0, len 512), `0x86`/`0x06` (param 0, len 378), optionally `0x08` (param 0, len 378). Dongle: `0x44`/`0x04`, `0x49`/`0x09`, optionally `0x42`/`0x02`, `0x88`.

| What | Why dangerous | Source |
| --- | --- | --- |
| **Any report ID `0x05`** (e.g. `05 75 00 00 00 00`) | Firmware updater: `'u'` reboots the board into its ISP bootloader (enumerates as `0603:1020`), then `E`rase/`W`rite/`R`ead/`U`(burn) over report 5 with 2050-byte data reports. Brick risk | [OF75 `wired.ts` comment, §16 (Update.exe decompiled)] [KV "Report ID 5 Strictly Prohibited"] |
| Any device other than `258A:010C` / `3554:FA09` (esp. `0603:1020` bootloader) | | [OF75] |
| Wired `0x11` ResetDevice (`06 11 00 00 01 00 01 00 00`) | factory reset; the F75 driver never sends it (`CmdReset` unset). Hardware reset = hold Fn+Esc | [OF75 §6, §16] |
| Wired `0x03` SetMatrix / `0x83` | key map; wrong data disables keys incl. Fn | [OF75 §12] |
| Wired `0x05` SetMacro / `0x85` | macro buffer; **shou's "flash-commit probe" `06 05 0A 01 04 07` is a SetMacro frame** | [OF75 §6] [SHOU `webhid.ts`] |
| Wired `0x0b` `0x0c` `0x0d` | screen parameters / image pages / GIF (the F75 has no screen) | [OF75 §6] |
| Wired `0x0e`, `0x0f` | unknown streaming commands | [OF75 §6] |
| Wired reads `0x88`, `0x89`, `0x9d`, `0x9e`, `0x9f` | return MCU code memory (8051 firmware dump) — unknown side effects | [XEV] [OF75 §6 `0x88 GetRealData`] |
| Wired `0x87` battery | harmless read, but hidden for the F75 (`ShowPower=0`) | [OF75 §14] |
| Dongle `0x01`/`0x41` (matrix), `0x03`/`0x43` (macros), `0x06` (reset), `0x0b`, `0x0A` ("save", unverified) | same as wired equivalents | [OF75 §15] [SHOU] |
| Reports `0x39`, `0x3c` | from another firmware (shou's "enable direct mode") | [SHOU `direct-mode.ts`] |
| A settings page or colour table **without** its `5A A5` marker, or one not obtained by read-modify-write | shou's wired `factoryReset` writes an all-zero page except the slots — corrupts debounce/sleep/unknown bytes | [SHOU `webhid.ts factoryReset`] [BR0 marker checks] |
| Masked effects 9, 14, 18 | not dangerous to hardware, but may leave keys dark | [OF75 §9] |
| Continuous `0x04`/`0x0a`/`0x06` writes | presumed flash; use `0x08` for animation | inference |
| Writes before a matching identity read | `258A:010C` is shared with other boards (OpenRGB bricked Redragon boards with reused IDs) | [ORGB comment] [OF75 `ids.ts`] |

The official driver also contains an online firmware update path (`http://120.79.152.79/update/`, unsigned) — irrelevant for us but a reason never to run `Update.exe` [OF75 §16].

---

## 11. Reading current state

- Settings page `0x84` (128 B): live effect (`0x0a`), per-key flag, every effect's brightness/speed/colour nibble, sleep, debounce. CONFIRMED readable (BR0 live, open-f75 owner's board, xevrion). The keyboard's own Fn shortcuts change it, so re-read before every write (VINI polls every 2 s).
- Colour table `0x8a` (512 B): CONFIRMED readable (BR0 live read, marker at 506).
- Per-key `0x86` (378 B): readable on hardware (BR0), layout assumed planar — UNVERIFIED.
- Identity `0x82`; firmware version = USB `bcdDevice`.
- Over the dongle: `0x44`, `0x49` (PROBABLE).

---

## 12. Where sources disagree (and which I trust)

| Topic | Disagreement | Trust | Why |
| --- | --- | --- | --- |
| What `0x0a`/`0x8a` is | colour rows (open-f75, BR0, RustamSheoran comment in OF75) vs per-key 4 B/key (xevrion, shou) vs 3 B/LED (AulaSofwareLinux) | **colour rows** | decompiled builder + OEM capture bytes decode exactly as rows; per-key is `0x06` in the OEM self-define capture |
| Per-key length | 378 (driver, capture, BR0, open-f75) vs 384 (VINI, Qyzom) | **378** | OEM capture header `7A 01` |
| Per-key layout | planar (all) vs — | planar | capture |
| `0x08` LED count | 126/378 (driver, OpenRGB) vs 122 (shou) vs 170 slots (kveld9) | 378-byte, 126 LEDs | driver + capture length field |
| `LedOpt` field order | "driverIndex, hwEffectId" (task brief) vs first field = hw id (KB.ini comment, driver) | **first field = byte 0x0a** | captures |
| Masked effects | 9/14/18 (2026 driver) vs 6/9/14/18 (local older driver) | expose 6, hide 9/14/18 | OEM capture of Rainbow wheel working |
| Speed direction | 0 = fastest (xevrion capture) vs 4 = fastest (open-f75 preview only) | 0 = fastest, verify | only xevrion is from device traffic |
| Debounce encoding (`0x03`) | value 1 = default (OF75) vs ms−1 (BR0) | n/a (preserve) | not lighting |
| Page `0x12` | side LED (OF75) vs charging indicator (BR0 live diff) | BR0, but preserve | BR0 observed the byte flip |
| Settings slot table start | 0x3a (OF75, BR0, VINI, capture) vs "offset 64" (shou README) | 0x3a | capture |
| Identity | only `…CD` (OF75, OpenRGB) vs `…E1`/`…01 91` also valid (local KB.ini) | accept all three | local driver ground truth |
| BT PID | FA07 (brief) vs FA08 (VINI) | unresolved | not used |
| Dongle config possible | no (shou README) vs yes (driver, Godera3 capture/replay, VINI) | yes | capture + decompilation |
| Dongle per-key | 384 padded (VINI) vs 378 + marker packet (shou) | none — capture first | no OEM capture |
| Need for apply/save | shou sends a "save" | none needed | not in driver, not in captures |

Overall source ranking for byte layouts: Godera3 OEM captures (ground truth on the wire) ≈ open-f75 decompilation (newest driver, most thorough, cross-checked) > local KB.ini > br0k3nb (independent decompilation of another driver build + live reads) > VINI (port of F75 Studio, "not tested on real hardware yet") > OpenRGB/kveld9 (direct mode only) > shou/RustamSheoran and xevrion (several wrong guesses; use only their raw observations).

---

## 13. Sources

Local (read-only):
- `[KBINI]` `C:\Program Files (x86)\AULA\F75\Dev\kb\wired\KB.ini`, `…\kb\1\KB.ini`, `…\kb\F75KR\KB.ini` — VID/PID, `Psd=`, `CRC=1`, `MatrixLen=128`, `Light/LightHW`, `Speed/SpeedHW`, `SleepUI/SleepHW`, `RGBIndex`, `GameRGBIndex`, `GaoshouIndex=18`, `GaoshouGroupNum=1`, `GaoshouKey1`, `LedMask=0x22120`, `LedOpt1..20`, `[KEY]` LED indices.
- `C:\Program Files (x86)\AULA\F75\Text\en\text.xml` — `tc_kb_led1..20` effect names.
- `C:\Program Files (x86)\AULA\F75\OemDrv.exe` — size/date/hash only (not analysed, not run).

Repositories (read via raw.githubusercontent.com / GitHub API, 2026-11-03):
- `[OF75]` https://github.com/phantranthienan/open-f75 — `docs/reverse-engineering/report.md` (§1–§19: frame, command table, settings page, colour table, effects, per-key, direct frames, dongle, dangerous paths, pacing), `src/protocol/wired.ts`, `settings.ts`, `color-table.ts`, `per-key.ts`, `effects.ts`, `ids.ts`, `lighting.ts`, `src/keyboard.ts` (pacing), `src/allowlist.ts`, `src/device-check.ts`, `README.md`. License: **none declared** (GitHub `license: null`).
- `[SHOU]` https://github.com/shouryadixitisverycool/aula-f75 (fork of RustamSheoran/aula-f75) — `README.md`, `web/src/lib/protocol.ts` (dongle templates, checksum), `web/src/lib/f75.ts` (wired framing), `web/src/lib/direct-mode.ts` (0x08, keepalive, 0x39/0x3c), `web/src/lib/webhid.ts` (save/commit probe, factoryReset, per-key misuse), `web/src/lib/wireless-mode.ts`. License: **none** (fork and upstream both `null`).
- `[GOD]` https://github.com/Godera3/peripheral-toolkit — `keyboard/README.md`, `keyboard/BUGS.md`, `keyboard/aula_f75/protocol.py`, `core.py`, `device.py`, `effects.py`, captured frames `keyboard/aula_f75/fragments/01_fixed_on_frag{0,1,2}.bin`, `03_respire_frag2.bin`, `17_self_define_frag{0,1,2}.bin`, `wireless_sequence.bin`, `sleep_sequence.bin`; pcapng list under `keyboard/wired/` and `keyboard/wireless/`. License: **none at repo root**; `keyboard/frontend/claude/LICENSE` is **GPL-3.0** (applies to that frontend).
- `[BR0]` https://github.com/br0k3nb/aula-f-series-linux — `src/protocol.c/.h`, `src/lighting_ext.c/.h`, `analysis/report.md`, `analysis/features-status.md`. License: **none**.
- `[VINI]` https://github.com/vinicenter/aula-f75-webdriver — `README.md`, `src/protocol.ts`, `src/device.ts`, `src/controller.ts`. License: **none** (font OFL); derived from trenbolone1122/aula-f75-linux (license not checked).
- `[KV]` https://github.com/kveld9/aulaf75-nativehub — `docs/PROTOCOL.md`, `rust/src/proto.rs` (`KEEPALIVE_HZ`), `rust/src/device.rs`, `LICENSE`. License: **MIT OR Apache-2.0**.
- `[QY]` https://github.com/Qyzom/MchooseG75-OpenRGB — `openrgb/MchoseG75Controller.cpp` (interface/usage selection, planar 126×3 via `0x06`, 384 length, dongle 0x13 loop), `LICENSE`. License: repo `LICENSE` says **MIT**, but the source files carry `SPDX-License-Identifier: GPL-2.0-or-later` (OpenRGB-derived) — treat as GPL-2.0+.
- `[ORGB]` https://gitlab.com/CalcProgrammer1/OpenRGB — `Controllers/SinowealthController/SinowealthControllerDetect.cpp` (identity request, `response[13]`, interface 1 / page `0xFF00` / usage 1, Redragon note), `SinowealthKeyboard10cController/SinowealthKeyboard10cController.cpp` (`0x08` frame), `SinowealthKeyboard10cDevices.cpp` (`aula_f75_layout`, model `0xCD`). License: **GPL-2.0-or-later**.
- `[XEV]` https://xevrion.dev/blogs/aula-f75-linux-reverse-engineering (published 2026-03-30) — descriptor, read sequence, speed byte diff, clamp at 4, memory-dump read commands. Blog text, all rights reserved.

### License note
We implement from the documented facts only (byte layouts, command numbers and sequences are facts / interoperability information). No code from any source may be copied: open-f75, shou/RustamSheoran, br0k3nb, vinicenter and the Godera3 root have no license (all rights reserved by default); OpenRGB and Qyzom's sources are GPL-2.0+ and Godera3's frontend GPL-3.0 (copying would force GPL on F75Studio); kveld9 (MIT/Apache-2.0) is the only one whose code could be reused with attribution. Godera3's captured `.bin` frames are data from AULA's software; use them only as test-vector references, not by redistributing them. AULA's binaries and `KB.ini` files must not be redistributed.

---

## 14. Open questions (need a hardware test)

1. Speed direction (0 or 4 = fastest).
2. Whether slot 18 brightness (`0x5e`) dims per-key mode.
3. Whether `0x08` direct frames require effect 21 to be selected, and the maximum sustainable frame rate.
4. Which identity the user's board returns (`…CD` tri-mode vs `…E1` wired-only).
5. `0x86` read-back layout.
6. Dongle per-key length/marker (needs an OEM capture over 2.4 GHz).
7. Firmware behaviour for masked effects and for effect ids outside the table (don't send them).
