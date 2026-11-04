# Surprise implementation history

**Version 2 supersedes the historical plan below.** The current product is one `Surprise.exe`, built from `src/Surprise.App`. It uses a first-run wizard, an everyday control window, adjacent `settings.json`, linked media, per-user opt-in autostart, and no tray or one-shot UI. Pixel-art PNG sequences preserve alpha, have a configurable cycle duration/FPS, and reveal only the first frame before looping. See README.md for current behavior and build commands. The older Setup/Executor projects are retained for regression coverage, not distributed.

Version 1.2 update: all playback now uses bundled mpv/FFmpeg over private JSON IPC. The executor embeds and checksums the player, extracts it to a temporary folder, and owns its lifetime through a kill-on-close Windows job. A transparent overlay mode adds screen selection, placement presets, a draggable/resizable placement editor, physical-pixel dimensions/coordinates, duration, and click-through. The overlay uses mpv's D3D11 alpha output with no border and no focus activation; standard media uses mpv's normal player window. Files must already have transparency. The two application EXEs remain self-contained. Read README.md for current usage and THIRD-PARTY.txt for player source/license information. This supersedes the historical media design below.

Status: implemented. Build and usage instructions are in README.md. The implementation uses Windows Gaming Input with an XInput connected-count fallback, explicit current-user pipe permissions, and Windows GDI+ GIF animation. Native HID/SetupAPI physical-identity correlation, event-only triggers, and a Startup-shortcut alternative remain future extensions; selected matching uses WGI identities and documented PC/port limitations.

Version 1.1 update: the wizard has four steps plus a completion page, advanced settings are collapsed, and a saved controller needs only one reconnect to verify. Video uses the modern Windows.Media.Playback.MediaPlayer engine with a GPU composition surface inside the app, replacing legacy WPF MediaElement. The current maow.mp4 (now VP9/AAC) plays on the tested PC without conversion; separate AV1/AAC, H.264/AAC, and WebM fixtures verify additional formats. Codec availability still depends on Windows. The README and current source supersede historical UI/media descriptions below.

## Recommended design

Build a Windows desktop solution using C# and .NET 10 LTS. Publish two self-contained Windows x64 executables:

- `Surprise.Setup.exe`: setup wizard, configuration, preview, pause/resume, repair, and removal.
- `Surprise.Executor.exe`: controller learning, background controller detection, and media playback.

The executor has explicit `--learn`, `--watch`, and `--play` modes. Both applications request ordinary user privileges. The executor includes a small tray menu for status, pause, opening setup when available, and exit.

Use Task Scheduler to start `Surprise.Executor.exe --watch` when the configuring user signs in. Start it immediately after successful setup too. The controller connection itself is detected by the executor, rather than inferred from a driver-specific Event Log entry.

This is the recommended compromise for broad controller support, portability, and minimal permissions. It requires one background user process while armed. A Task Scheduler event-only solution is possible only when a controller/driver produces a repeatable, readable log event for each connection; that is not assumed across USB, Bluetooth, and wireless adapters. Event-only support is deferred from the initial version.

## Controller coverage and matching

Support USB, Bluetooth, and wireless dongles through the controller interfaces Windows exposes. The wizard offers two trigger rules:

1. Any supported controller connects.
2. A selected controller connects, learned by disconnecting and reconnecting it.

Start with `Windows.Gaming.Input.RawGameController` enumeration and added/removed notifications. During implementation, verify these APIs from the unpackaged desktop executor and add a bounded XInput connection-state polling fallback for controllers not exposed through the primary API. Use native device notifications and SetupAPI/HID information where needed to correlate physical devices and distinguish controller interfaces from unrelated devices.

The detection probe comes first, before the full UI. It must verify actual connection changes, including a controller reconnecting while its wireless dongle stays plugged in. A paired Bluetooth device or a present dongle does not alone establish that a controller is connected.

Learning runs inside `Surprise.Executor.exe --learn`; setup receives structured results over a current-user-restricted named pipe. This avoids treating application-specific controller IDs produced by Setup as if they belonged to Executor.

Persist the best available identity: executor-specific controller ID, vendor/product identifiers, and correlated container/serial information when available. Do not treat vendor/product IDs as unique to an individual controller. Show the matching strength in setup. If the hardware does not expose enough identity to distinguish two identical controllers, offer a clearly described model/port rule or Any controller mode. Exact-device matching across different PCs, USB ports, or USB/Bluetooth transports is not guaranteed; record multiple connection profiles when needed.

“All controllers” means controllers usable through supported Windows APIs. Proprietary hardware, unusual drivers, and virtual controllers may need an adapter or cannot be distinguished from physical controllers. Advertise verified coverage, rather than an unconditional hardware guarantee.

## Wizard

1. **Choose setup or manage an existing installation.** Display armed/paused status and the existing configuration, if present.
2. **Choose the trigger.** Any controller, or disconnect/reconnect a selected controller. Display detected candidates, connection method where known, and matching limitations. Require a second reconnect test for a selected device.
3. **Choose media.** Select a local video or animated GIF. Validate the file and preview it before allowing installation. Media type and display mode are independent: either video or GIF can be fullscreen or windowed.
4. **Choose playback.** Select monitor, fit/fill, volume for video, and once/repeat behavior. Video defaults to one play; GIF defaults to looping for a configurable duration. Escape and Alt+F4 close playback. Closing playback does not launch another program.
5. **Choose storage.** Default to `%LOCALAPPDATA%\Surprise\<installation-id>\`. Offer a user-writable custom folder and portable export. Locate the matching executor beside setup automatically; if absent, ask for `Surprise.Executor.exe`, validate its product/protocol version, and copy it to the target folder.
6. **Review and arm.** Show the trigger rule, media, runtime folder, startup task, and account used. Copy files, register the task, start the watcher, and confirm its health. Installation failures roll back owned changes; updates preserve the previous working configuration until the replacement succeeds.

Selecting an additional third-party EXE is unnecessary for the requested goal and is excluded from the initial version. Setup prepares configuration and copies a prebuilt executor; it does not compile or generate a new executable on the destination PC.

## Playback implementation

Use WPF for the wizard and playback window. Use the Windows media stack through WPF `MediaElement` for video and a dedicated GIF renderer that correctly handles frame delays, transparency, and disposal/compositing. Select and pin the GIF implementation after evaluating its license and compatibility with single-file publishing.

Video formats depend on the Windows codecs/features installed. Recommend MP4/H.264 with AAC audio, validate actual playback on the destination PC, and provide an actionable error for unsupported files. Do not install codecs or alter system media settings.

Only show media in the configuring user's active, unlocked session. Drop connection events while locked or disconnected; do not replay them unexpectedly at unlock. Playback closes at end/duration or on user dismissal and restores any app-owned window/cursor state. Change only the player's volume, never the system volume.

## Trigger behavior

- Startup establishes a connection baseline and does not fire merely because a controller is already connected.
- Trigger on a disconnected-to-connected transition, with short enumeration settling/debounce and a default 30-second cooldown.
- Consolidate multiple interfaces for one device into a single connection event where identity allows it.
- Allow only one playback at a time; do not queue a burst of surprises.
- Offer repeat on reconnect or one-shot until manually rearmed.
- Reconcile state after sleep/resume and suppress enumeration churn from causing an immediate replay.
- Preview/test mode does not arm startup, consume the one-shot rule, or mutate the live trigger state.

## Storage and portability

The release download contains the two EXEs. Installed media and configuration are data files, so the installed folder contains more than two files:

```text
Surprise.Setup.exe              # Can be run from the download folder
Surprise.Executor.exe           # Original distributable

%LOCALAPPDATA%\Surprise\<installation-id>\
    Surprise.Executor.exe       # Installed runtime copy
    config.json                 # Versioned configuration; relative asset paths
    install-manifest.json       # Ownership, task identity, installed file inventory
    media\surprise.<extension>  # Copied media, independent of the original path
    logs\                      # Small, bounded local diagnostics
```

Portable export creates a folder/ZIP containing the two EXEs, configuration, and media with relative paths. On a new PC, run Setup to learn/validate the controller and register startup for that account. Scheduled task registrations and machine-specific controller identity do not travel automatically. Direct `--play` works without registering startup.

Use self-contained single-file publishing with native libraries bundled for extraction. No .NET SDK/runtime installation is required on the destination. Windows x64 is the initial target; Windows ARM64 needs a separate publish. WPF trimming/AOT is not part of this plan. Standard .NET native extraction can leave a runtime cache; installation removal must not delete caches shared with unrelated applications.

## Permissions and Windows integration

Register one uniquely named task for the current user with `InteractiveToken` and least privilege, through the Task Scheduler COM API. Store no password. Scope its logon trigger to the configuring user's SID. Use an absolute runtime path and correctly quoted arguments; use a single-instance guard. Configure it for a long-running watcher, including appropriate execution-time and battery settings, without waking the PC.

Use normal inherited per-user folder permissions. No Windows service, driver, machine-wide registry entry, elevated task, Event Log provider/channel changes, or policy changes are required. Enterprise restrictions may still deny task registration; report the error and offer manual watcher launch. A per-user Startup shortcut can be offered as an explicit alternative if needed.

Changes affect only the configuring account. An installation manifest records precisely what setup owns. Do not overwrite unrelated tasks or files, even if a name collides.

## Pause, removal, and recovery

Pause disables startup and stops the owned watcher/playback through authenticated per-user IPC. Resume re-enables startup and starts the watcher with a fresh baseline.

Removal disables/deletes the owned scheduled task (or alternative Startup shortcut), stops owned processes, and removes owned media/configuration/runtime/log files. Keep the user's original media and unrelated files. Check resolved paths and ownership before deleting anything; do not blindly delete a selected custom folder. If a running executable or a file lock prevents cleanup, report the exact leftovers with a retry option. Setup normally runs outside the runtime folder so it can remove the installed executor after shutdown.

Removal restores application-created startup behavior and files. Windows may retain normal execution history or shared runtime caches; this is not a promise to erase all evidence of execution.

## Solution layout and implementation order

```text
Surprise.sln
src/Surprise.Setup/       # WPF wizard and management UI
src/Surprise.Executor/    # Detection/learning, tray, playback
src/Surprise.Core/        # Configuration, identity, trigger state, IPC contracts
src/Surprise.Windows/     # Task Scheduler, native device/session integration
tests/Surprise.Tests/     # Meaningful state/identity/installation tests
build/                   # Reproducible publish and packaging scripts
```

Core and Windows are libraries bundled into the two published EXEs; they do not create additional program EXEs.

1. Validate controller detection, identity, and user-level task registration using a small diagnostic implementation. Confirm gaps before promising device coverage.
2. Implement configuration and the trigger state machine, playback, and executor learning/watch/play modes.
3. Implement the setup wizard, transactional installation, management, and removal.
4. Publish the two self-contained EXEs, verify portable export, and document use/build instructions.

## Validation required before delivery

- Build and publish on Windows; verify the release has exactly the two application EXEs and that neither needs a separately installed .NET runtime.
- Verify setup/removal as a standard user, task restart after sign-out/sign-in, and immediate arming after installation.
- Test actual USB, Bluetooth, and dongle reconnects with representative available hardware; identify any hardware cases that were not tested.
- Test Any controller and selected-device rules, two identical controllers where available, duplicate notifications, cooldown, one-shot, startup baseline, and resume/lock behavior.
- Preview actual MP4/GIF playback, GIF timing/disposal, fullscreen/windowed, display scaling, monitor removal, Escape/Alt+F4, and invalid media.
- Verify paths with spaces/non-ASCII characters, portable export/import, missing/moved runtime, interrupted installation, removal with files locked, and preservation of originals/unrelated files.
- Unit-test state transitions, device matching limits, config validation, and rollback/ownership decisions. Hardware-dependent behavior requires real integration testing and will be reported separately from simulated tests.

## Reference basis

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Task security contexts](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)
- [Interactive task logon](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_logon_type)
- [Event-trigger subscriptions](https://learn.microsoft.com/en-us/windows/win32/taskschd/eventtrigger-subscription)
- [Raw game controllers](https://learn.microsoft.com/en-us/windows/uwp/gaming/raw-game-controller)
- [Application/port-specific controller identity](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.rawgamecontroller.nonroamableid)
- [Native device notifications](https://learn.microsoft.com/en-us/windows/win32/devio/registering-for-device-notification)
- [USB container identity](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-usb-devices-are-assigned-container-ids)
- [Single-file deployment and runtime extraction](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [WPF multimedia](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/multimedia-overview)

## Approval scope

Approve implementation of this two-EXE design, with a user-level background listener started by a sign-in task, support for all three connection methods through supported Windows controller APIs, and the setup/storage/playback/removal workflow above. No startup task is registered on this PC merely by approving source-code implementation; live installation testing will be explicitly identified when performed.
