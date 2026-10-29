# Mod Manager for Graveyard Keeper

An in-game mod list for **Graveyard Keeper** (Steam / GOG, BepInEx 5).

It adds a **MODS** entry to the main menu that opens a list of every installed BepInEx plugin, shows what each one does, and lets you turn it on or off with a single click. The window is built from the game's own UI so it looks and behaves like a native screen.

![game](https://img.shields.io/badge/game-Graveyard%20Keeper%201.407-informational)
![loader](https://img.shields.io/badge/loader-BepInEx%205.4.x-blue)
![license](https://img.shields.io/badge/license-GPL--3.0-green)

## Features

- **Native-looking window** - the list reuses the game's own frame, fonts, scroll view and dialogs, and follows your resolution and UI scale.
- **Click the check box to load or unload** - the box on the left of a row is the on/off switch; clicking it never opens anything else. A tick means enabled, a cross means disabled, and the manager's own row shows a padlock.
- **Click the row for its options** - mods that expose settings open their options screen when you click anywhere else on the row. Mods with nothing to configure keep toggling.
- **Readable descriptions** - long mod names and descriptions wrap onto multiple lines instead of being cut off, and each row grows to fit.
- **Scrolls like you expect** - the list follows the mouse wheel and can also be dragged.
- **Options in the game's own style** - the settings screen uses the game's window, arrows and tick marks: click the arrows to step a value, click the value to type a new one, click a key bind to rebind it.
- **Dependency safety** - disabling a mod that another enabled mod hard-depends on asks for confirmation first.
- **Restart prompt** - when you close the window after changing something, the manager offers to restart the game for you.
- **English and Simplified Chinese** - the interface and the built-in mod descriptions follow the game's language setting.
- **No patching** - the game's assemblies are never modified and no code is injected into them.

## Requirements

| | |
|---|---|
| Game | Graveyard Keeper 1.405 (GOG) or 1.407 (Steam) |
| Mod loader | [BepInEx 5.4.x (x64, Mono)](https://github.com/BepInEx/BepInEx/releases) |

BepInEx 5 must already be installed and working (the game should show `BepInEx ...` in the bottom corner of the main menu).

## Installation

1. Install BepInEx 5 if you have not already, and run the game once so BepInEx creates its folders.
2. Download `GKModManager_v<version>.zip` from the [Releases](../../releases) page.
3. Extract it **into the game folder** - the folder that contains `Graveyard Keeper.exe`.

   The archive is laid out so the files land in the right place:

   ```
   Graveyard Keeper\
   └─ BepInEx\
      └─ plugins\
         └─ GKModManager\
            ├─ GKModManager.dll
            └─ lang\
   ```

4. Start the game. A **MODS** (or **模组**) entry appears in the main menu.

## Usage

1. On the main menu, click **MODS**.
2. The window lists every plugin found under `BepInEx\plugins`, each with its name, version and a short description.
3. **Load or unload a mod** by clicking the check box at the start of its row.
   - ✔ green tick - the mod is enabled and will load on the next start
   - ✖ red cross - the mod is disabled and will be skipped
   - a padlock - the manager itself, which cannot be disabled from inside the manager
4. **Open a mod's options** by clicking anywhere else on its row. A row shows a `›` hint when the mod has settings; mods without settings simply toggle instead, so there is never an empty screen to open.
5. Close the window with the close button, `Esc`, or the gamepad **Back** button.

### Mod options

Clicking a mod's row (anywhere but its check box) opens that mod's options:

- `✔` / `✖` — on/off settings. Click the box, or the row.
- `‹` `›` — step a number or cycle an enum. Click the value in the middle to type an exact value.
- key bindings — click the row, then press the key you want. Backspace clears it.
- colours and vectors are listed one channel/component per row.
- flag settings (for example a log-level mask) are split into one row per flag.
- `Esc` or **Back to the mod list** returns to the list; the close button closes the window.

Changes are saved to the mod's `.cfg` immediately and take effect at once for most settings. Settings
are only offered for mods that are currently loaded, so a disabled mod has to be enabled and the game
restarted before its options appear.

If you changed anything, you are asked whether to restart now. Choose **Yes** to restart immediately, or **No** to keep playing and restart yourself later.

> A restart is always required: Windows cannot unload an assembly that is already running, so the new state only takes effect on the next launch.

## Configuration

Settings live in `BepInEx\config\gyk.modmanager.cfg`:

| Setting | Default | Description |
|---|---|---|
| `Use native NGUI window` | `true` | Use the native-looking window. Set to `false` to use the simpler fallback window instead. |
| `Menu button X` | `16` | Fallback window only: horizontal position of the button. |
| `Menu button Y` | `16` | Fallback window only: vertical position of the button. |
| `Window rect` | *(empty)* | Fallback window only: remembered window position and size. |

If the native window cannot be created on your install, the manager logs the reason and switches to the fallback window automatically.

## How enabling and disabling works

BepInEx 5 only loads files ending in `.dll`, so the manager simply renames the plugin file:

| State | File name |
|---|---|
| Enabled | `MyMod.dll` |
| Disabled | `MyMod.dll.disabled` |

Nothing is written to a list or a database, so you can always undo a change by hand in Explorer. The manager never touches files outside `BepInEx\plugins`.

## Safety

- The manager cannot disable itself - it owns the window you are clicking.
- Disabling a mod that an enabled mod declares as a **hard** dependency asks for confirmation, because BepInEx will skip the dependent mod on the next start.
- If a file cannot be renamed (for example because an antivirus holds it open), the row is left unchanged and the error is shown in a dialog.
- The mod never modifies the game's assemblies, saves or settings.

## Troubleshooting

**The MODS entry does not appear.**
Check `BepInEx\LogOutput.log`. The most common causes are a missing BepInEx installation, or `GKModManager.dll` having been renamed to `GKModManager.dll.disabled`.

If the log says the assembly could not be loaded, Windows may be blocking it - see below.

**The window does not appear but the entry does.**
Open the config file and set `Use native NGUI window = false`. The fallback window will be used instead. Please also report it, including the `NGUI` lines from the log.

**Windows blocks the DLL.**
Like every BepInEx plugin this one is unsigned. On Windows 11 with Smart App Control enabled, a newly downloaded unsigned DLL can be rejected with `0x800711C7` ("Application Control policy has blocked this file"). The mod does not try to bypass code-integrity policies; if this happens you will need to allow the file through your security configuration, or use a machine where Smart App Control is off.

**A mod is still loaded after I disabled it.**
That is expected - the change takes effect on the next start. The restart prompt is there to help with exactly that.

## Building from source

The mod is written in C# and targets .NET Framework 4.8.1. You need the .NET SDK and a Graveyard Keeper installation with BepInEx 5 already installed (the build references the game's assemblies and `BepInEx\core`).

```powershell
dotnet build src/GKModManager.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper"
```

`GameDir` defaults to the standard Steam path. When the folder contains `BepInEx\plugins`, the build deploys straight into it; otherwise the output goes to `src\bin\Release`.

Releases are built by [`.github/workflows/build.yml`](.github/workflows/build.yml) on every `v*` tag. Because the game's assemblies cannot be redistributed, CI compiles against the declaration-only stubs in [`ci/refs`](ci/refs) instead:

```powershell
dotnet build src/GKModManager.csproj -c Release -p:UseGameRefs=false -p:BepInExCore=<BepInEx>\core
```

See [`ci/README.md`](ci/README.md) for how those stubs are generated and why a build against them is identical to a build against the real game.

## Compatibility

- Works with any BepInEx 5 plugin, including ones that do not know about this mod.
- Only DLLs under `BepInEx\plugins` are managed. Preloader patchers in `BepInEx\patchers` are out of scope.
- Reinstalling or updating a mod through a mod manager such as Vortex can restore the `.dll` name and re-enable it.

## Credits

- Built-in mod descriptions are short summaries derived from each mod's public README / NexusMods description, so that the list is useful without leaving the game. They are informational only; all mods remain the property of their respective authors.
- Thanks to the BepInEx team and the Graveyard Keeper modding community.

## License

GPL-3.0. See [LICENSE](LICENSE).

---

## 简体中文

**Graveyard Keeper 模组管理器**（需要 BepInEx 5）

主菜单会多出一个 **模组** 按钮，点开就能看到已安装的全部 BepInEx 插件，每一行显示模组名称、版本和一句话说明。

- **勾选框**：绿色对勾表示已启用，红色叉号表示已禁用，锁头表示管理器自身（不可被禁用）
- **点击整行**即可切换启用状态
- 关闭窗口时如果改动过，会询问是否**立即重启游戏**
- 界面与说明文字会跟随游戏语言自动切换中英文
- 不修改游戏本体文件，启用/禁用只是把 `模组.dll` 改名为 `模组.dll.disabled`

安装：把压缩包解压到包含 `Graveyard Keeper.exe` 的游戏根目录即可（需要先装好 BepInEx 5）。
