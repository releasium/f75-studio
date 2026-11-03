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
