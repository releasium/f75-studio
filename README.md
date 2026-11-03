# F75 Studio

Windows tray app for the Aula F75 keyboard: a visual AutoHotkey v2 hotkey builder and script manager. The lighting editor arrives in Plan 2.

## Requirements

- Windows 10/11, .NET 8 Desktop Runtime
- AutoHotkey v2 (https://www.autohotkey.com)

## Build and run

```powershell
dotnet test
dotnet run --project src/F75Studio.App
```

## Publish a single exe

```powershell
dotnet publish src/F75Studio.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Run `publish\F75Studio.exe`. Use `--tray` to start hidden in the tray.

## Files

- Settings and hotkey cards: `%AppData%\F75Studio\`
- Generated script: `Documents\F75Studio\hotkeys.ahk` (rewritten on every Apply; do not edit by hand)
- Logs: `%AppData%\F75Studio\logs\`

## Notes

- F75 Studio only tracks scripts it started itself. Scripts already running from elsewhere (for example from Explorer or a startup shortcut) show as stopped.
- Saving a script keeps the previous version as `<name>.ahk.bak` and, on the first save only, the untouched original as `<name>.ahk.orig`.
- The AutoHotkey integration tests return early (and pass) if AutoHotkey v2 is not installed, so a green run on such a machine does not exercise them.
