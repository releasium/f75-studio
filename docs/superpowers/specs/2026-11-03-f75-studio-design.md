# F75 Studio — Design Spec

Date: 2026-11-03
Status: Approved design, pending implementation plan

## Goal

A Windows 11 tray application for the **Aula F75** keyboard that provides:

1. A modern **lighting editor** (effects, colors, brightness, speed, per-key colors, profiles) that drives the keyboard directly over USB.
2. A **visual hotkey builder** that generates AutoHotkey v2 scripts and runs them with the user's installed AutoHotkey, plus a manager for existing `.ahk` scripts.

## Environment facts

- Windows 11, .NET 8 SDK installed.
- AutoHotkey v2 installed at `C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe`.
- Existing scripts: `Documents\autokeyscript.ahk` (CapsLock vim-navigation layer, AHK v2), plus near-empty `autokeyscript-1.ahk`, `autokeyscript-2.ahk`, `Documents\AutoHotkey\Untitled.ahk`.
- The F75 is currently paired over **Bluetooth** (HID VID `0x3554`, PID `0xFA07`). Lighting control is expected to require **USB cable or 2.4 GHz dongle**; exact USB VID/PID to be confirmed during capture.

## Decisions

| Topic | Decision |
|---|---|
| Lighting control | Reverse-engineer the official Aula driver's USB HID protocol from captures |
| Hotkeys | Visual builder that generates AHK v2 and runs installed AutoHotkey |
| Stack | C# / .NET 8, WPF, WPF-UI (Fluent, Mica), H.NotifyIcon (tray), HidSharp (HID), AvalonEdit (code editor) |
| Working title | F75 Studio |

## Architecture

Single .NET 8 solution at `C:\Users\ckkos\Documents\Something\F75Studio\`.

| Project | Responsibility | Depends on |
|---|---|---|
| `F75Studio.App` | WPF UI, tray icon, navigation, pages, single-instance + CLI args | all below |
| `F75Studio.Lighting` | `LightingState`, `LightingProfile`, effect definitions, key layout, `IKeyboardDevice` interface, `FakeKeyboardDevice` | — |
| `F75Studio.Lighting.Aula` | `AulaF75Device : IKeyboardDevice` — device discovery and packet encoding via HidSharp, derived from captures | Lighting |
| `F75Studio.Hotkeys` | `Hotkey` model, `AhkGenerator`, `AhkImporter`, `AhkRunner`, script discovery | — |
| `F75Studio.Tests` | xUnit tests for generator, importer, encoder | Lighting, Lighting.Aula, Hotkeys |

### Key interfaces

```csharp
public interface IKeyboardDevice
{
    DeviceStatus Status { get; }            // NotFound | BluetoothReadOnly | ConnectedUsb
    event EventHandler<DeviceStatus> StatusChanged;
    IReadOnlyList<EffectDefinition> SupportedEffects { get; }
    Task ApplyAsync(LightingState state, CancellationToken ct = default);
    Task SaveToOnboardAsync(CancellationToken ct = default);   // no-op if unsupported
}
```

`LightingState`: effect id, primary color(s), brightness (0–100), speed (0–100), direction (where applicable), per-key color map (key id → RGB) for Custom mode.

`Hotkey`: id, group, enabled, trigger (AHK hotkey string, e.g. `^!k`, `CapsLock & h`), optional `#HotIf WinActive("ahk_exe ...")` scope, action (typed: SendKeys, Remap, TypeText, Run, Media, SwitchLightingProfile, CustomCode).

## App shell

- Starts minimized to tray; optional "Start with Windows" (HKCU Run key).
- Tray menu: Open, lighting profile quick-switch submenu, per-script toggles, Pause all hotkeys, Exit.
- Main window: WPF-UI `FluentWindow` with Mica, follows system dark/light theme, left `NavigationView` with Keyboard, Hotkeys, Scripts, Settings.
- Closing the window hides to tray; Exit from tray quits and stops app-launched AHK processes.
- Single instance (named mutex + named pipe). `F75Studio.exe --profile "<name>"` forwards the profile switch to the running instance.

## Screens

### Keyboard (lighting editor)
- Vector-drawn F75 75% layout; each keycap renders its current color.
- Effect picker populated from `IKeyboardDevice.SupportedEffects` (final list from captures; expected: Static, Breathing, Wave, Reactive, Custom per-key, etc.).
- Controls: color wheel, hex input, recent swatches, brightness, speed, direction.
- Custom mode: click / drag-select keys, then apply color. Preset groups: All, WASD, F-row, Arrows, Modifiers.
- Profiles: create, rename, duplicate, delete; live apply on change; "Save to keyboard" when onboard save is supported.
- Status chip: Connected (USB) / Bluetooth — read-only / Not found.

### Hotkeys (visual builder)
- Card list grouped by group name; each card shows trigger → action and an enable toggle.
- Trigger recorder captures pressed combination and renders AHK syntax; supports modifiers, `A & B` custom combos, single-key remaps, and optional per-app scope.
- Actions: Send keys, Remap key, Type text, Run program/URL, Media/volume, Switch lighting profile (generates `Run '"<app path>" --profile "<name>"'`), Custom AHK code.
- Live AHK v2 preview pane. "Apply" writes `Documents\F75Studio\hotkeys.ahk` and (re)starts it.

### Scripts
- Discovers `*.ahk` under `Documents` (top level + `Documents\AutoHotkey` + `Documents\F75Studio`).
- Each shows running/stopped state; Start/Stop/Restart; "Run at startup" (app launches it on start).
- AvalonEdit editor with AHK syntax highlighting; "Open in VS Code" if `code` is on PATH.
- "Import into builder": `AhkImporter` converts simple one-line hotkeys (`trigger::Send "..."`, `trigger::Return`, `a::b`) into cards; anything else becomes a Custom AHK block attached to the group.

### Settings
- Start with Windows, theme override, AutoHotkey path (auto-detected), scripts folder, device diagnostics (enumerated HID interfaces, VID/PID, connection type).

## Data & storage

- `%AppData%\F75Studio\settings.json`, `profiles.json`, `hotkeys.json`.
- `hotkeys.json` is the source of truth; `hotkeys.ahk` is regenerated on every Apply and carries a "generated — do not edit" header.
- User scripts are modified only on explicit Save in the editor; previous content copied to `<name>.ahk.bak` first.

## Data flow

- **Lighting:** editor → `LightingState` → debounced (~30 ms) `IKeyboardDevice.ApplyAsync` → `AulaF75Device` encodes HID reports → keyboard. Profiles persist to `profiles.json`.
- **Hotkeys:** cards → `hotkeys.json` → `AhkGenerator` → `hotkeys.ahk` → validate → `AhkRunner` (re)starts `AutoHotkey64.exe` and tracks the process.
- **Hotkey → lighting:** generated AHK runs `F75Studio.exe --profile "X"` → named pipe → running instance applies the profile.

## Error handling

- Device not found / Bluetooth only: editor fully usable for designing; banner explains state; auto-reconnect on HID device arrival.
- HID write failure: one retry, then toast notification; never crash.
- AutoHotkey not found: Settings prompts for path; builder still saves `hotkeys.json`.
- Generated script errors: validate by launching AHK with `/ErrorStdOut` and `/validate`; parse the error line and map it back to the originating card.
- Unhandled exceptions are logged to `%AppData%\F75Studio\logs\` and shown as a toast.

## Reverse-engineering procedure (requires the user)

1. Connect the F75 by USB cable (or 2.4 GHz dongle). Install the official Aula F75 driver, Wireshark, and USBPcap.
2. Follow a capture checklist (one `.pcapng` per step): driver launch/handshake; static red; static green; static blue; brightness 0/50/100; speed min/max; each effect once; Custom mode with one key set to a known color; Custom mode with all keys; save to onboard memory.
3. Analysis produces a protocol document (`docs/protocol/aula-f75.md`): interface/usage page, report ID, packet length, command bytes, checksum, key-index map.
4. `AulaF75Device` implements the encoder; tests assert byte-for-byte equality with captured packets.

## Testing

- `AhkGenerator`: golden-file tests per action type and for `#HotIf` scoping.
- `AhkImporter`: round-trip test using a copy of `autokeyscript.ahk` as a fixture.
- Aula encoder: byte-exact tests against packets extracted from captures.
- `FakeKeyboardDevice` lets the full UI run without hardware.
- Manual verification on the real F75 for each effect and per-key mode.

## Build order

1. Solution skeleton, tray shell, navigation, settings persistence.
2. Hotkeys: model, generator, runner, builder UI; Scripts page with importer and editor. (Usable on its own.)
3. Lighting editor UI and profiles against `FakeKeyboardDevice`.
4. Captures → protocol doc → `AulaF75Device` → hardware verification.

## Out of scope (v1)

- Keymap/firmware remapping on the keyboard itself (handled via AHK instead).
- Firmware updates.
- Audio-reactive or screen-sync lighting.
- Other keyboard models.
