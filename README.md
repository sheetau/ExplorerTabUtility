# ExplorerTabUtility

This is sheetau's independently maintained version of [w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility), based on v2.5.0 (`fad736353210d36595c6e6caa77ac7ad9129b3ee`). It keeps Explorer's windows as tabs while preserving the file selection from **Show in folder / Reveal in Explorer** requests.

## What changed in 2.5.2

- Present a new or reused destination tab as soon as navigation is issued, before waiting for source selection capture or destination enumeration.
- Utility-originated folder opens with no selection return immediately after requesting navigation. They skip the enumeration wait entirely.
- External Explorer windows still keep their native request alive in the background: an empty selection at registration cannot distinguish a folder-only open from a delayed reveal request.

## Selection handling introduced in 2.5.1

- Keep the source Explorer alive until its initial folder enumeration completes. Capture full item paths, including hidden extensions, then restore the selection and keyboard focus, and bring the selected file into view in the destination tab.
- Prepare the destination while the source loads. Selection capture and restoration are driven by Shell `EnumDone` events, without a fixed capture delay or selection retry loop.
- Apply incoming selections when reusing a tab. Observe selection changes in inactive tabs to handle `SHOpenFolderAndSelectItems` requests that create no new window.
- Reuse newly completed tabs immediately. Keep location records current even when session restoration is disabled.
- Keep Shell COM objects on one message-pumping STA, run continuations outside COM event callbacks, and detach event sinks on success, cancellation, timeout, and shutdown.
- Preserve and reveal the original native window if conversion fails. The ten-second deadline is a failure boundary; successful opens finish on events.

## Run

Windows 11 with native File Explorer tabs is required. Extract a build archive and run `ExplorerTabUtility.exe`. Stop any other version first; settings and the single-instance mutex remain compatible with upstream.

Enable **Window Hook** to convert new Explorer windows into tabs. Enable **Reuse Tabs** to switch to an existing folder tab and apply incoming file selections. The original keyboard/mouse profiles, duplicate/reopen/detach actions, tab search, and session settings are retained. Ctrl+Shift bypasses conversion as in upstream.

Downloads for this version belong to [this repository's Releases](https://github.com/sheetau/ExplorerTabUtility/releases). Upstream's WinGet/Chocolatey packages install the upstream version. This repository's update checker uses its own releases.

- `NetFW4.8.1`: requires .NET Framework 4.8.1 (available on current Windows 11).
- `Net9.0_FrameworkDependent`: requires the .NET 9 Windows Desktop runtime.

## Build

Install Visual Studio with the **.NET desktop development** workload, the .NET 9 SDK, and the .NET Framework 4.8.1 targeting pack. Use a Visual Studio Developer PowerShell:

```powershell
msbuild ExplorerTabUtility.sln /restore /p:Configuration=Release
```

Use full-framework `MSBuild.exe`: the Shell COM references require `ResolveComReference`, which `dotnet build` does not support. Both `net9.0-windows` and `net481` are built by the solution. Executables are in `ExplorerTabUtility/bin/Release/<framework>/`.

For a portable x64 Framework archive:

```powershell
msbuild ExplorerTabUtility/ExplorerTabUtility.csproj /restore /t:Publish /p:Configuration=Release /p:TargetFramework=net481 /p:RuntimeIdentifier=win-x64 /p:PublishDir=../publish/net481/x64/
```

GitHub Actions builds both frameworks and all three architectures, and uploads unsigned ZIP artifacts. Tags beginning with `v` also publish the ZIPs as a GitHub release. No upstream signing or package-publishing credentials are required.

## Real Explorer acceptance tests

Run these explicitly on an interactive Windows 11 desktop, with other ExplorerTabUtility processes stopped. The harness opens temporary fixture folders, uses a dedicated Explorer window, reads back selected paths and the active native tab, and closes its fixture windows. It backs up and restores the settings file. It does not run automatically on CI because these tests require a desktop session.

```powershell
msbuild tests/ExplorerTabUtility.IntegrationTests/ExplorerTabUtility.IntegrationTests.csproj /restore /p:Configuration=Release
dotnet tests/ExplorerTabUtility.IntegrationTests/bin/Release/net9.0-windows/ExplorerTabUtility.IntegrationTests.dll
& tests/ExplorerTabUtility.IntegrationTests/bin/Release/net481/ExplorerTabUtility.IntegrationTests.exe
```

Coverage: known folder-only requests without a selection wait; new and reused `/select` requests; same basenames with different extensions; Shell API single and multiple selection; subfolder selection; Japanese, spaces and `#` in paths; ordinary empty folders; inactive-tab activation including repeated identical selection; immediate reuse; reuse disabled; and cancellation during conversion. See [VALIDATION.md](VALIDATION.md) for the recorded results.

Explorer still performs its native navigation; this utility does not replace Shell registrations or install a registry interceptor. Network folders, third-party namespace extensions, and modified Explorer implementations can behave differently. A view that does not provide the required enumeration event is left as its native window instead of losing the reveal request.

## Attribution

The original application, UI, assets, and features are by w4po and upstream contributors, under the [MIT license](LICENSE). The source snapshot was imported without upstream Git history into this independent repository. Selection handling was informed by upstream issues [#71](https://github.com/w4po/ExplorerTabUtility/issues/71), [#105](https://github.com/w4po/ExplorerTabUtility/issues/105), [#106](https://github.com/w4po/ExplorerTabUtility/issues/106), [#126](https://github.com/w4po/ExplorerTabUtility/issues/126), [#128](https://github.com/w4po/ExplorerTabUtility/issues/128), and [PR #144](https://github.com/w4po/ExplorerTabUtility/pull/144). This implementation uses Shell events in place of that PR's capture polling.
