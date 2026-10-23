# Validation

## Release 3.0.1: tray and in-app updates

The user confirmed that the repaired tray menu stays open, dismisses on an outside click, and can be opened again. Version-4 notification callbacks now handle WM_CONTEXTMENU only, queue the menu after the Shell callback, and avoid showing/hiding the owner window or restoring another application's focus.

Update checks run at startup and every 12 hours when enabled, and can also be invoked from the tray. Stable GitHub releases are filtered by version and process architecture. An in-app action downloads the matching ZIP, checks the published size and SHA-256 digest, validates archive paths and the package manifest, then launches the staged application as its own update helper. The helper waits for the running process to exit, backs up and replaces owned files, and waits for the new settings window to load before confirming success. Failed startup restores the previous files and restarts that version. Settings remain outside the application directory. Unknown user files are retained; obsolete manifest-owned files and successful staging data are removed.

Focused checks passed for version equality/downgrades/prereleases, architecture and digest filtering, replacement and removal of owned files, rollback, locked destinations, and archive path traversal rejection. Real staged-helper checks passed for update/restart, settings and user-file retention, cache cleanup, and rollback/restart after corrupting the settings XBF embedded in the PRI. External XBF and icon corruption were insufficient failure fixtures because Windows used its embedded/fallback resources; the final rollback check corrupts the embedded page itself.

The x64 and ARM64 portable packages use the WinUI component packages without unused AI/ML/search/widgets payloads. Release symbols and repository documentation are excluded; runtime files, the MIT license, and an ownership manifest are retained. New installations enable update checks by default; existing preferences remain respected.

Update-only checks (no Explorer fixtures):

```powershell
& tests/ExplorerTabUtility.IntegrationTests/bin/Release/net10.0-windows10.0.19041.0/win-x64/ExplorerTabUtility.IntegrationTests.exe --updates
& tests/ExplorerTabUtility.IntegrationTests/bin/Release/net10.0-windows10.0.19041.0/win-x64/ExplorerTabUtility.IntegrationTests.exe --update-lifecycle publish/3.0.1/x64
```

The lifecycle check requires other utility instances stopped. Package the publish folder with `tools/Package.ps1` first to generate its ownership manifest. Build/acceptance commands below describe the current WinUI version; sections labelled 2.5.x or 3.0.0 retain their historical results.

## WinUI 3 migration (3.0.0)

Tested on 2026-10-23 on Windows 11 x64, with Visual Studio MSBuild 18.9 and .NET SDK 10.0.400. The solution builds without warnings or errors. The application uses Microsoft.WindowsAppSDK 2.5.1 and .NET 10; the previous WPF/.NET Framework targets have been removed.

Focused runtime checks passed: shortcut add/edit/remove autosave with stable IDs; known folder-only direct tab navigation; new `/select`; reused `/select` switching between identical basenames with different extensions; and Shell API multi-selection. Observed times were 431 ms for the folder-only method return, 2992 ms for a new reveal tab, 1729 ms for the reused reveal, and 208 ms for multi-selection. These are single-run observations. The full historical suite below was not rerun for this UI change.

The new settings window was launched with isolated settings and read through native UI Automation. Both sections, standard toggles, 76-pixel shortcut headers, four-pixel row spacing, compact toggle widths, and the integrated title/caption controls were present. The user reviewed the screen and requested matching card dimensions/corners, reduced spacing, and the integrated title bar; those changes were applied. Native shortcut recording, expanded-row appearance, tray interactions, and ARM64 execution have not been exhaustively verified through automated UI input.

The taskbar discovery code now reads native UI Automation interfaces directly, removing the old WPF automation dependency. The launch routing and pinned-folder resolution logic are unchanged; prior real user click results are recorded below. Settings and profile storage use the original location and field names, and tests use a separate temporary directory.

Portable x64 and ARM64 builds were published with both runtimes included. The publish target explicitly includes generated XBF files and the application PRI index; omitting these caused a startup parse failure during validation and was corrected. The x64 archive was extracted and launched successfully with the existing three user profiles. Relaunching while running opened settings in the existing process. Both ZIPs passed CRC checks. ARM64 was built, but was not executed on ARM64 hardware.

The earlier sections below describe previous releases and their previous runtimes. Use the current commands in [README.md](README.md) to repeat checks on 3.0.

## Pinned folders (2.5.4): focused validation

At the user's request, validation for this change was limited to building both targets and one real pinned-folder interaction; the 13 existing scenarios were not rerun. The user clicked the pinned Downloads row in the actual taskbar context menu. The Framework 4.8.1 harness observed the direct route returning in 424 ms, verified that the active target tab was the exact expected folder, and observed zero native source windows. Thirteen visible pinned-folder rows were resolved from Quick Access. This timing is a single observation, not a guarantee.

The menu's Pinned group exposes display names but no target paths. The resolver obtains folder-only items marked `System.Home.IsPinned` from the Shell Quick Access namespace, retains their full parsing paths, and accepts only unique name-to-path matches. Virtual folder parsing paths are retained too. Pin data is read on a temporary STA while UI Automation stays on a worker. Mouse callbacks use cached paths and geometry. The trailing two row-heights are reserved for the pin/unpin control; scrolling and native menu actions invalidate the snapshot to avoid stale coordinates.

To repeat only the new acceptance check:

```powershell
& tests/ExplorerTabUtility.IntegrationTests/bin/Release/net481/ExplorerTabUtility.IntegrationTests.exe --taskbar-pinned "C:\path\to\pinned-folder"
```

After READY, reopen the taskbar menu and click the text of that pinned folder. The existing 2.5.3 default-launch and 2.5.2 selection test results below are historical results, not additional 2.5.4 runs.

## Results from earlier versions


Tested on 2026-10-23 on Windows 11 build 26200, x64, using Visual Studio MSBuild 18.9 and .NET SDK 10.0.400. Both the .NET 9 Windows Desktop and .NET Framework 4.8.1 targets were built and exercised on the real desktop.

The solution builds without warnings or errors. The real Explorer harness passed all 13 scenarios on each runtime:

| Scenario | .NET 9 | .NET Framework 4.8.1 |
| --- | --- | --- |
| Utility-originated folder-only request returns without waiting for enumeration | Pass | Pass |
| `/select` creates a tab and selects the file | Pass | Pass |
| `/select` reuses a tab and changes the selection between `sample.txt` and `sample.md` | Pass | Pass |
| `SHOpenFolderAndSelectItems` multi-selection | Pass | Pass |
| Shell API subfolder selection | Pass | Pass |
| Shell API selection in a newly created tab | Pass | Pass |
| Ordinary empty folder merges into the target window | Pass | Pass |
| Incoming selection activates the correct inactive tab | Pass | Pass |
| Repeating the identical selection activates an inactive tab | Pass | Pass |
| First reveal into a fresh folder | Pass | Pass |
| Immediate second reveal reuses that tab | Pass | Pass |
| Disabling reuse creates an additional tab with the requested selection | Pass | Pass |
| Shutting down during conversion reveals and preserves the native source | Pass | Pass |

The test fixture contains Japanese text, spaces, and `#` in its path. Assertions read full selected paths through Shell COM, check the actual active `ShellTabWindowClass` handle, and ensure converted fixture tabs share a single window. The test runner backs up and restores the existing settings file and closes only its own fixture windows.

Normal request completion is event driven. Native Explorer startup and navigation still take time: observed end-to-end new tab requests took roughly 2.5–5 seconds on this desktop; direct requests to already loaded tabs typically took 35–450 ms. These are observations, not performance guarantees. There is no fixed selection capture delay or restoration polling in the implementation.

The folder view emitted `DocumentComplete`, followed by the initial selection change, followed by `EnumDone`. A destination monitor also checks the expected navigation, so a previous Home enumeration cannot satisfy a new folder request. Event sinks are detached with `using`/`finally` and asynchronous task continuations keep restoration out of enumeration callbacks.

Virtual/network folder behavior, other Windows builds, third-party Shell namespace providers, and Explorer restarts were not exhaustively tested. A missing enumeration event reaches a ten-second failure deadline and leaves the native source window available. An ordinary successful open does not wait for that deadline.

Build and rerun instructions are in [README.md](README.md).

In 2.5.2 the destination window is brought forward immediately after issuing navigation, even while a possible external selection request is still being captured. Utility-originated folder-only requests skip enumeration waits altogether. The new folder-only test measured method return at 373 ms on .NET 9 and 1160 ms on .NET Framework 4.8.1 on this run, including tab creation and native navigation setup. These observations do not establish a speedup ratio against 2.5.1; the change removes the enumeration dependency from that path. External folder opens remain indistinguishable from delayed reveals with the current Shell registration data, so their source is retained until capture completes, while the destination is already presented.

## Taskbar File Explorer launch (2.5.3)

The user operated the real Windows taskbar jump list while a passive diagnostic recorded Shell/WinEvents. The launch item was identified as `ControlType.ListItem`, automation ID `Open`, under the `SystemItemList` list in ShellExperienceHost's `Windows.UI.Core.CoreWindow`. The original route registered a new This PC window with zero selected items, and then converted it into a tab.

With the direct route enabled, real user clicks were intercepted in both runtime targets. The .NET 9 run returned in 408 ms with zero native source windows created. A final .NET Framework 4.8.1 run returned in 512 ms; the stricter acceptance harness waited for the launch callback to complete, checked that the target's active tab was the configured default folder, and observed zero new CabinetWClass windows. These are single-run observations, not a performance guarantee.

All 13 automated selection/navigation/shutdown scenarios were rerun successfully on both runtimes. Cancellation handling was also tightened so pending registration callbacks do not query disposed Shell objects during shutdown. The taskbar clicks were performed by the user while native window creation was observed through a WinEvent hook.

To repeat the opt-in manual acceptance test on an interactive desktop with other utility instances stopped:

```powershell
dotnet tests/ExplorerTabUtility.IntegrationTests/bin/Release/net9.0-windows/ExplorerTabUtility.IntegrationTests.dll --taskbar
& tests/ExplorerTabUtility.IntegrationTests/bin/Release/net481/ExplorerTabUtility.IntegrationTests.exe --taskbar
```

After READY, reopen the Explorer taskbar context menu and left-click its File Explorer launch item once. The harness forces a new tab in its own target, waits for the default folder to become active, and fails if a native source window was created. It restores settings and closes its fixture windows afterward.

The optimized click is deliberately limited to an identified English/Japanese launch item, no held modifiers, an existing target window, and Window Hook enabled. Recent/pinned folder destinations, other apps, keyboard activation, modified clicks, and unknown layouts/languages continue through Windows normally. No pinned shortcut or registry entry is edited.

## Building and checking the current version

## Build

Install Visual Studio with the **.NET desktop development** workload, a Windows SDK, and the .NET 10 SDK. From a Visual Studio Developer PowerShell:

```powershell
msbuild ExplorerTabUtility.sln /restore /p:Configuration=Release
```

Use Visual Studio's full `MSBuild.exe`: Shell COM references require `ResolveComReference`, which `dotnet build` does not support. The default architecture is x64. To produce a portable build:

```powershell
msbuild ExplorerTabUtility/ExplorerTabUtility.csproj /restore /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishDir=../publish/3.0.1/x64/
```

Replace `win-x64` with `win-arm64` and the destination with `../publish/3.0.1/arm64/` for ARM64. GitHub Actions builds both architectures and uploads unsigned ZIPs. Tags beginning with `v` also create a GitHub release.

## Focused acceptance checks

These checks require an interactive Windows 11 desktop and other utility instances stopped. They use an isolated settings directory and temporary fixture folders, then close their fixture windows. They do not run on CI.

```powershell
msbuild tests/ExplorerTabUtility.IntegrationTests/ExplorerTabUtility.IntegrationTests.csproj /restore /p:Configuration=Release
& tests/ExplorerTabUtility.IntegrationTests/bin/Release/net10.0-windows10.0.19041.0/win-x64/ExplorerTabUtility.IntegrationTests.exe --smoke
```

The smoke check covers shortcut autosave, a known folder-only request, new/reused `/select` requests with different extensions, and Shell API multi-selection. Omit `--smoke` for the full selection/navigation/shutdown suite. `--taskbar` and `--taskbar-pinned "C:\path\to\folder"` are opt-in checks requiring a real user click after READY. Recorded results and limitations are in [VALIDATION.md](VALIDATION.md).
