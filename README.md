# ExplorerTabUtility

Keep Windows 11 Explorer windows together as tabs, including file selections from **Show in folder / Reveal in Explorer**.

This is sheetau's independently maintained version of [w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility), based on v2.5.0 (`fad736353210d36595c6e6caa77ac7ad9129b3ee`).

## Version 3.0

- Native WinUI 3 settings, shortcut recording, and tab search. Settings use one page with **Shortcuts** and **Preferences**, an integrated title bar, and Windows light/dark styling.
- Shortcut changes are saved immediately. Add a shortcut with the button below the list; expand a row to edit or delete it.
- Standard Windows notification-area menu, with checkmarks for enabled features.
- Updated application icon. Removed the old UI framework, custom themes, About page, manual profile-saving controls, media, and unused packaging files.
- Existing profiles and preferences are read from the same settings file. WinUI 3 replaces the previous .NET Framework / WPF builds.

## Run

Windows 11 with native File Explorer tabs is required. Extract the entire archive and run `ExplorerTabUtility.exe`. The portable builds include .NET 10 and the Windows App SDK; keep the accompanying files alongside the executable. Stop any other version first.

The first launch opens settings. Later launches run in the notification area; open settings from the tray menu, a configured shortcut, or by running the executable again. Closing settings keeps the utility running. Use **Exit** in the tray menu to quit.

- **Window hook** converts new Explorer windows into tabs. Ctrl+Shift bypasses conversion.
- **Reuse tabs** switches to an existing folder tab and applies incoming file selections.
- Keyboard/mouse shortcuts support opening, duplicating, reopening, detaching, navigating, searching tabs, and snapping windows. Expand a shortcut to choose its scope and action.
- Preferences also control startup, the tray icon, update notifications, and remembering/restoring locations.

Settings are stored in `%APPDATA%/ExplorerTabUtility/settings.json`. Profiles from previous versions are retained, including numeric legacy action values. The single-instance mutex is compatible with the previous utility.

Downloads and update checks use [this repository's Releases](https://github.com/sheetau/ExplorerTabUtility/releases). Upstream's WinGet/Chocolatey packages install the upstream version.

## Explorer request handling

Reveal requests retain their source window until the Shell reports initial enumeration complete. Selected paths are then restored, focused, and scrolled into view in the destination tab. Destination navigation starts while the source loads. New and reused tabs accept incoming selections, including inactive-tab requests.

Known folder-only requests skip selection capture. With an existing target and Window hook enabled, unmodified left clicks on the identified **File Explorer** taskbar jump-list command and uniquely resolved pinned folders open tabs directly. Ambiguous names, file items, keyboard/modified clicks, unknown layouts, and the pin/unpin control retain normal Windows behavior. No Shell registration is replaced.

Selection capture and restoration use Shell events, without a fixed capture delay. A ten-second failure deadline preserves and reveals the original native window if conversion cannot complete. Network folders, third-party namespace providers, and modified Explorer implementations can behave differently.

## Build

Install Visual Studio with the **.NET desktop development** workload, a Windows SDK, and the .NET 10 SDK. From a Visual Studio Developer PowerShell:

```powershell
msbuild ExplorerTabUtility.sln /restore /p:Configuration=Release
```

Use Visual Studio's full `MSBuild.exe`: Shell COM references require `ResolveComReference`, which `dotnet build` does not support. The default architecture is x64. To produce a portable build:

```powershell
msbuild ExplorerTabUtility/ExplorerTabUtility.csproj /restore /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishDir=../publish/winui/x64/
```

Replace `win-x64` with `win-arm64` and the destination with `../publish/winui/arm64/` for ARM64. GitHub Actions builds both architectures and uploads unsigned ZIPs. Tags beginning with `v` also create a GitHub release.

## Focused acceptance checks

These checks require an interactive Windows 11 desktop and other utility instances stopped. They use an isolated settings directory and temporary fixture folders, then close their fixture windows. They do not run on CI.

```powershell
msbuild tests/ExplorerTabUtility.IntegrationTests/ExplorerTabUtility.IntegrationTests.csproj /restore /p:Configuration=Release
& tests/ExplorerTabUtility.IntegrationTests/bin/Release/net10.0-windows10.0.19041.0/win-x64/ExplorerTabUtility.IntegrationTests.exe --smoke
```

The smoke check covers shortcut autosave, a known folder-only request, new/reused `/select` requests with different extensions, and Shell API multi-selection. Omit `--smoke` for the full selection/navigation/shutdown suite. `--taskbar` and `--taskbar-pinned "C:\path\to\folder"` are opt-in checks requiring a real user click after READY. Recorded results and limitations are in [VALIDATION.md](VALIDATION.md).

## Attribution

The original application and features are by w4po and upstream contributors, under the [MIT license](LICENSE). The source snapshot was imported without upstream Git history into this independent repository. Selection handling was informed by upstream issues [#71](https://github.com/w4po/ExplorerTabUtility/issues/71), [#105](https://github.com/w4po/ExplorerTabUtility/issues/105), [#106](https://github.com/w4po/ExplorerTabUtility/issues/106), [#126](https://github.com/w4po/ExplorerTabUtility/issues/126), [#128](https://github.com/w4po/ExplorerTabUtility/issues/128), and [PR #144](https://github.com/w4po/ExplorerTabUtility/pull/144). This implementation uses Shell events in place of that PR's capture polling.
