# Surprise 2.1

One Windows application: **Surprise.exe** configures and plays a surprise when a controller connects. No separate Setup or generated executor is needed. The player and .NET runtime are bundled; media is referenced by path.

## First launch

1. Choose **Видео**, **Изображение**, or **Пиксельарт · PNG-кадры**.
2. Select your own media. For pixel art add one or more PNGs, then use the up/down buttons to order them.
3. Choose playback settings. Pixel animation defaults to **1 FPS**. Set **Сколько секунд крутить цикл** and, optionally, an appearance effect.
4. Choose the screen and object size. **Расположить на рабочем столе…** shows the first frame; drag it and resize its corner. Hold **Shift** while resizing to keep its aspect ratio. **Отцентрировать** centers the object on the selected display. **Превью сюрприза** runs the actual playback without saving.
5. Choose any controller or a connected controller model and the cooldown.
6. Review, preview, and choose **Сохранить и запустить**.

Already connected controllers do not trigger. Disconnect and reconnect to show the surprise. USB and Bluetooth can expose different model IDs.

## Window and background behavior

Normal launch opens the control window and starts waiting for a controller. Its plain-language status shows whether it is waiting, paused, editing, or playing. Buttons play a manual preview, stop playback, pause/resume detection, edit settings, or fully exit.

Closing the window leaves Surprise running **without a tray icon**. A first-close notice explains this. Launch the same executable again to restore the running window; it does not start a second listener. **Завершить Surprise** exits completely. Task Manager can also terminate it.

Editing and previewing settings pause controller triggers. Settings apply only after **Сохранить**. **Отмена** discards the draft. There is no one-shot mode. Startup records current controller connections as a baseline; arrivals while paused, editing, playing, cooling down, or locked are dropped.

Media and appearance effects use square rounded cards with icons. The selected option's description appears below the row. Saved settings use category navigation with a fixed Save/Cancel footer; the first-run wizard keeps Back/Next navigation.

## Diagnostic log

`Surprise.log` is written in UTF-8 beside the running `Surprise.exe`, independent of the current working directory or media location. The control window's **Диагностика** card shows the application directory and buttons to open that folder or the log. The executable directory must be writable; a logging failure is shown in this card and does not crash playback.

The log includes version, executable/settings paths, OS/runtime/architecture, graphics rendering tier and displays, settings load/save, controller identities and connection/trigger decisions, session state, pause/editing, media file metadata and PNG decoding/cache/timing, reveal completion, frame changes, playback stop/completion, autostart changes, mpv process/IPC/backend output, and exception stacks. It contains local file paths and controller names; nothing is uploaded automatically.

At 4 MiB the file rotates to `Surprise.log.1`, with up to three previous files (`.1`–`.3`). For debugging on another PC, send the current log and rotated files if present, together with the time and description of the failure.

## Media and transparency

- Video: MP4, WebM, MKV, MOV, M4V, AVI, WMV. Fullscreen video plays once to the end. Overlay video loops for the chosen duration.
- Image: PNG, JPEG, BMP, GIF, APNG, WebP. It appears for the chosen duration; animated files loop.
- Pixel art: separate static PNG frames with the same canvas dimensions. The first frame appears with **Нет**, **Случайное появление клеток**, **Волна**, **Сборка из разлёта**, or **Сборка снизу вверх**. Subsequent frames loop in the selected order without repeating the reveal.

Pixel art and images appear above the desktop without application chrome. PNG alpha is preserved; clicks pass through to the desktop. Proportions are kept inside the configured rectangle. Pixel art is scaled without smoothing. A file with an opaque background keeps that background.

**Cycle duration starts after the reveal completes.** A 2-second reveal plus a 10-second cycle lasts 12 seconds. One PNG works as a static pixel-art object with an optional reveal.

Large expanded pixel-art PNGs are decoded scanline by scanline. A uniform grid is recovered from all color and alpha boundaries without losing image detail. The original 30,000 × 24,000 PNG is never allocated as a full GPU texture. Effect tiles use the original image as their texture, preserving detail and semitransparency. Their size can be set in source-image pixels; 0 automatically chooses tiles visible at the selected display size. This setting does not resample the assembled image. Supported pixel-art PNGs are non-interlaced, 8-bit RGB/RGBA, grayscale, grayscale-alpha, or indexed-color. For other PNG variants, export static RGBA 8-bit without interlacing. Arbitrary photographs may not have a meaningful pixel grid.

## Files and portability

`settings.json` is saved beside `Surprise.exe`. Existing settings are backed up to `settings.json.bak` before saving. Files are not copied, embedded, modified, or deleted. Paths inside the app folder are relative to the settings file; other paths are absolute. Move the entire folder with any relative media to keep those links valid. External links must still exist on the destination PC.

Missing or invalid files produce an error with the filename; open settings and select the replacement. Corrupt settings are retained until you explicitly save a corrected configuration. Put the application in a folder you can write to, rather than Program Files.

## Autostart

**Запускать при входе в Windows** adds/removes a per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Surprise` entry. No administrator access, service, or scheduled task is needed. Automatic launch uses `--background` and does not open the window when settings are valid. It is off by default. Moving Surprise requires enabling autostart again at the new location; that replaces the old path. Exiting the application does not disable autostart.

## Build and verification

Windows 10 version 2004+ or Windows 11, Direct3D 11, and desktop composition are required. Build with the .NET 10 SDK:

```powershell
./build/publish.ps1 -Offline
```

Without `-Offline`, missing checksum-pinned mpv/runtime dependencies can be downloaded. The output is `artifacts/release/win-x64/Surprise.exe`, README, and license notices. Do not distribute the historical Setup/Executor outputs. Their source projects remain for compatibility and regression tests; the new product entry point is `src/Surprise.App`.

```powershell
dotnet build Surprise.sln -c Release -m:1 -p:NuGetAudit=false
dotnet run --project tests/Surprise.Tests -c Release -p:BuildInParallel=false -p:NuGetAudit=false
dotnet run --project tests/Surprise.AnimationTests -c Release -p:BuildInParallel=false -p:NuGetAudit=false
```

The animation suite uses both original `local/1.png` and `local/2.png`, checks logical grids, alpha, timing and corruption handling, and renders effect stages under `artifacts/animation-qa`. Developer-only `--settings <path>` isolates test settings; `--check-backend <report.json>` verifies embedded player extraction without opening the UI.

`--render-ui <output-directory>` renders wizard/settings/control layouts offscreen at two window widths for visual QA, without showing a window or using Computer Use. It also checks square choice-card geometry and records clipped text in `layout-report.json`.
