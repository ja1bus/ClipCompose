# ClipCompose

English | [日本語](README.md)

ClipCompose is a lightweight tray app for Windows 11. It calls up saved templates (prompts and boilerplate), inserts the clipboard text at `{{clipboard}}` only when a template needs it, and copies or automatically pastes the result. Use it for prompts for generative AI such as Copilot, or for standard phrases in email and chat.

This repository contains the source code of the publicly released ClipCompose.

## Features

- Two levels of categories and templates: create, edit, duplicate, reorder and search them on the Templates tab of Settings
- Write `{{clipboard}}` in a template, and it is replaced with the clipboard text when the template runs. A template without `{{clipboard}}` is copied as is, and the clipboard is not read
- Chooser: pick from one list with category headings and press Enter. Collapse categories, search by template or category name, show the template text, pin a template, and reorder with keys or by dragging
- Pinned template: pin one template and run it with the pinned mode hotkey without opening the chooser
- Hotkeys (normal mode and pinned mode, not set by default) and optional automatic paste into the window you were using when you pressed the hotkey
- If the source app marked the copied text as excluded from clipboard history (for example), the result gets the same marks
- Export the categories you choose, import (add) categories you choose from a file, and restore by replacing all templates (JSON)
- Start when you sign in to Windows (Microsoft Store version, off by default)
- English and Japanese user interface (follows the Windows display language, or can be set in Settings)

ClipCompose does not monitor the clipboard or keep a history. Templates and settings are stored only on this PC, and ClipCompose sends nothing to the internet (see the privacy policy below). It does not send anything to AI services or get answers from them.

## Requirements

- Windows 11 (64-bit, x64)
- The Microsoft Store version includes .NET, so nothing else needs to be installed.
- To build and run from the source in this repository, the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) is required.

## Installation

The official distribution channel is the Microsoft Store (free).

- Microsoft Store: https://apps.microsoft.com/detail/9MWJ11BSD5PG

This GitHub repository does not distribute executables (binaries).

## Basic usage

1. When started, ClipCompose stays in the notification area (no window is shown).
2. Right-click the tray icon > Settings…, and on the Templates tab create categories and templates. Write `{{clipboard}}` where the copied text should go.
3. Copy the text you want to insert, click the tray icon to open the chooser, select a template and press Enter. The result is copied; paste it with `Ctrl+V`.
4. If you set hotkeys on the Hotkeys and behavior tab of Settings, you can open the chooser from any app and paste automatically.
5. To quit, choose Exit from the tray menu.

For details, see the user manual.

- User manual: https://www.ja1bus.com/clipcompose/en/manual/

## Building

### Requirements

- Windows 11 x64
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) (verified: 10.0.402; `global.json` in the root selects a 10.0 SDK)
- The `dotnet` CLI is enough (Visual Studio is not required).
- The app itself has no NuGet dependencies. Only the tests use the NuGet packages `Microsoft.NET.Test.Sdk`, `xunit` and `xunit.runner.visualstudio` (restored from nuget.org on the first build).

### Commands

Run these in the repository root.

```powershell
dotnet build ClipCompose.csproj
dotnet test Tests/ClipCompose.Tests.csproj
dotnet run --project ClipCompose.csproj
```

When started with `dotnet run`, the app stays in the notification area. To quit, choose Exit from the tray menu. Templates and settings are saved in `%LOCALAPPDATA%\ClipCompose\`.

To create a framework-dependent build (win-x64), run the following. The output goes to `publish\FrameworkDependent\` and requires the .NET 10 Desktop Runtime (x64).

```powershell
dotnet publish ClipCompose.csproj -c Release -p:PublishProfile=FrameworkDependent
```

The output also contains `LICENSE` and `THIRD-PARTY-NOTICES.md` (shown from the About tab of Settings).

### Structure

| Path | Contents |
|---|---|
| `ClipCompose.csproj` | The app (WPF, `net10.0-windows`). Only the tray icon uses WinForms `NotifyIcon` |
| `Core/` | Logic independent of the UI and Win32 (composition, template management, storage, settings) and the UI strings (English `Strings.resx`, Japanese `Strings.ja.resx`) |
| `Tests/` | Unit tests for `Core` (xUnit) |
| `Services/`, `Views/`, `Interop/` | Clipboard, hotkeys, paste and tray handling, windows, and Win32 API declarations |
| `icon.png`, `Resources/icon.ico` | App icon (`pwsh -File tools/make-icon.ps1` creates `icon.ico` from `icon.png`) |

The tooling that builds the Microsoft Store package (MSIX) is not included in this repository. The `ClipComposeMsix` condition in `ClipCompose.csproj` and `Services/PackageInfo.cs` are for the Store version.

Comments in the source code are mostly in Japanese. "仕様書 §N" in comments refers to a section of the development specification, which is not included in this repository.

## License

Released under the [MIT License](LICENSE).

Copyright (c) 2026 ja1bus (© ja1bus)

For third-party software, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## No warranty

This software is provided "as is", without warranty of any kind, express or implied. The author is not liable for any damages arising from the use of this software (see [LICENSE](LICENSE) for details).

## Links

- Web page: https://www.ja1bus.com/clipcompose/en/
- User manual: https://www.ja1bus.com/clipcompose/en/manual/
- Release notes: https://www.ja1bus.com/clipcompose/en/changelog/
- Privacy policy: https://www.ja1bus.com/clipcompose/en/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9MWJ11BSD5PG
- Contact: contact@ja1bus.com
