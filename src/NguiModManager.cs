using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using Shared;
using UnityEngine;

namespace GKModManager;

/// <summary>
/// Native-looking UI built by cloning the game's own NGUI screens. Nothing is patched
/// into the game assemblies; the save-slots window supplies the frame, scroll view and
/// row prefab, and the main-menu exit item supplies the entry button.
/// </summary>
internal sealed partial class NguiModManager
{
    private const string WindowPath = "window";
    private const string TitlePath = "window/front panel/header/header label";
    private const string ClosePath = "window/front panel/header/close button";

    private const float WidthScale = 2.05f;
    private const float HeightScale = 2.2f;
    private const int NameFontSize = 11;
    private const int DescriptionFontSize = 10;
    private const int RowMinHeight = 46;
    private const int RowPaddingTop = 8;
    private const int RowPaddingBottom = 10;
    private const int NameToDescriptionGap = 4;
    private const int CheckboxSize = 18;
    private const int CheckboxIconSize = 12;
    private const int CheckboxGutter = 30;
    private const int StatusWidth = 130;
    private const int WheelStep = 56;
    private const uint SteamAppId = 599140;

    private static readonly HashSet<string> SizeScaled = new(StringComparer.Ordinal)
    {
        "window",
        "window/back",
        "window/container",
        "window/scroll drag area",
        "window/front panel/header",
        "window/front panel/header/pixel line",
        "window/front panel/bottom pixel line"
    };

    // Header children keep their native size; only their offsets are recomputed so that the
    // title stays vertically centred and the close button hugs the new right edge.
    private MainMenuGUI _menu;
    private GameObject _entry;
    private GameObject _windowRoot;
    private SaveSlotsMenuGUI _window;
    private UIScrollView _scroll;
    private SimpleUITable _table;
    private SaveSlotGUI _rowPrefab;
    private UILabel _title;
    private readonly List<GameObject> _rows = new();
    private List<ModEntry> _mods = new();

    private bool _failed;
    private float _nextAttempt;
    private int _initAttempts;
    private bool _scrollHooked;
    private Sprite _checkboxOn;
    private Sprite _checkboxOff;
    private Sprite _checkboxFrame;
    private Sprite _checkboxLocked;
    private bool _checkboxSpritesReady;
    private bool _generatedCheckboxArt;
    private Dictionary<string, bool> _enabledWhenOpened;

    internal bool Ready => _entry != null && _windowRoot != null && _window != null;
    internal bool Failed => _failed;

    internal bool TryInitialize()
    {
        if (_failed) return false;
        if (Ready) return true;
        if (Time.unscaledTime < _nextAttempt) return false;
        _nextAttempt = Time.unscaledTime + 1f;

        try
        {
            var elements = GUIElements.me;
            var menu = elements != null ? elements.main_menu : null;
            var saves = elements != null ? elements.saves : null;
            if (menu == null || !menu.is_shown || saves == null || menu.mm_exit == null)
                return false;

            if (_window != null && _window.is_shown)
            {
                _window.SetOnHide(null);
                try { _window.Hide(); } catch { /* ignore */ }
            }

            DestroyClones();
            _menu = menu;
            CreateEntry();
            CreateWindow(saves);
            _initAttempts = 0;
            Plugin.Log.LogInfo("NGUI backend ready.");
            return Ready;
        }
        catch (Exception ex)
        {
            _initAttempts++;
            Plugin.Log.LogWarning($"NGUI backend init attempt {_initAttempts} failed: {ex.Message}");
            DestroyClones();

            if (_initAttempts >= 5)
            {
                _failed = true;
                Plugin.Log.LogError($"NGUI backend disabled after {_initAttempts} attempts, using IMGUI: {ex}");
            }

            return false;
        }
    }

    internal void Shutdown()
    {
        try
        {
            if (_window != null)
            {
                _window.SetOnHide(null);
                if (_window.is_shown) _window.Hide();
            }
        }
        catch
        {
            // The game may already be tearing down its UI.
        }

        DestroyClones();
    }

    private void CreateEntry()
    {
        var source = _menu.mm_exit;
        if (source == null) throw new InvalidOperationException("MainMenuGUI.mm_exit is missing");

        _entry = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
        _entry.name = "mod_manager_entry";
        _entry.transform.SetSiblingIndex(source.transform.GetSiblingIndex());

        var item = _entry.GetComponent<MenuItemGUI>();
        if (item == null) throw new InvalidOperationException("MenuItemGUI missing on cloned entry");

        item.locale_token = Lang.Get("mm.button.open");
        item.Init(_menu);
        item.Show();

        var entryLocalizers = _entry.GetComponentsInChildren<LocalizedLabel>(true);
        foreach (var localized in entryLocalizers)
            ResetLocalizer(localized, item.locale_token);

        if (entryLocalizers.Length == 0)
        {
            var label = _entry.GetComponentInChildren<UILabel>(true);
            if (label != null) label.text = item.locale_token;
        }

        item.on_pressed = new EventDelegate(OpenWindow);
        _menu.buttons_table.Reposition();
    }

    private void CreateWindow(SaveSlotsMenuGUI saves)
    {
        _windowRoot = UnityEngine.Object.Instantiate(saves.gameObject, saves.transform.parent);
        _windowRoot.name = "mod_manager_window";
        _windowRoot.SetActive(true);

        _window = _windowRoot.GetComponent<SaveSlotsMenuGUI>();
        if (_window == null) throw new InvalidOperationException("SaveSlotsMenuGUI missing on cloned window");

        _window.Init();

        _scroll = _windowRoot.GetComponentInChildren<UIScrollView>(true);
        _table = _windowRoot.GetComponentInChildren<SimpleUITable>(true);
        _rowPrefab = _windowRoot.GetComponentInChildren<SaveSlotGUI>(true);
        if (_scroll == null || _table == null || _rowPrefab == null)
            throw new InvalidOperationException("Save-slots window is missing its scroll view, table or row prefab");

        _rowPrefab.gameObject.SetActive(false);

        ScaleWindow();

        _window.UpdateLocalizedLabels();
        _window.SetOnHide(OnWindowHidden, false);

        PrepareHeader();

        NeutralizeNativeActions(_window);
        RefreshPanels();
        LogWindowSummary();

        // The save-slots screen can only be dragged; NGUI's scroll view is not wired to the
        // mouse wheel. Subscribe so the mod list also scrolls with the wheel.
        UICamera.onScroll -= OnCameraScroll;
        UICamera.onScroll += OnCameraScroll;
        _scrollHooked = true;
    }

    /// <summary>
    /// Widgets moved or resized at runtime introduce new materials (fonts), and NGUI only
    /// creates draw calls for them when the panels are explicitly refreshed.
    /// </summary>
    private void RefreshPanels()
    {
        foreach (var panel in _windowRoot.GetComponentsInChildren<UIPanel>(true))
        {
            panel.RebuildAllDrawCalls();
            panel.SetDirty();
            panel.Refresh();
        }
    }

    /// <summary>
    /// UICamera only delivers OnScroll to the hovered object and the save-slots screen is
    /// drag-only, so the wheel is forwarded to the list's scroll view from here. Filtering on
    /// the hovered transform keeps scrolls outside the window out of it.
    /// </summary>
    private void OnCameraScroll(GameObject hovered, float delta)
    {
        if (_window == null || !_window.is_shown || _windowRoot == null) return;
        if (hovered == null) return;
        if (hovered != _windowRoot && !hovered.transform.IsChildOf(_windowRoot.transform)) return;

        ScrollByWheel(delta);
    }

    private void ScrollByWheel(float delta)
    {
        if (_scroll == null || Mathf.Approximately(delta, 0f)) return;

        // Wheel up (positive delta) pulls the content down so earlier rows come into view.
        var step = -Mathf.Sign(delta) * WheelStep;
        _scroll.MoveRelative(new Vector3(0f, step, 0f));
        _scroll.RestrictWithinBounds(true);
    }

    private void LogWindowSummary()
    {
        var back = _windowRoot.transform.Find("window/back")?.GetComponent<UIWidget>();
        var clipWidth = ScrollContentWidth();
        Plugin.Log.LogInfo(
            $"NGUI window: frame={(back != null ? back.width : 0)}x{(back != null ? back.height : 0)}, " +
            $"content width={clipWidth}, rows auto-sized (min {RowMinHeight})");
    }

    /// <summary>
    /// Wires the cloned title bar: the native header, title and close button are kept, only
    /// their text and click handler are replaced.
    /// </summary>
    private void PrepareHeader()
    {
        var titleTransform = _windowRoot.transform.Find(TitlePath);
        if (titleTransform == null)
        {
            var header = _windowRoot.transform.Find("window/front panel/header");
            var headerLabel = header != null ? header.GetComponentInChildren<UILabel>(true) : null;
            titleTransform = headerLabel != null ? headerLabel.transform : null;
        }

        _title = titleTransform != null ? titleTransform.GetComponent<UILabel>() : null;
        if (_title != null)
        {
            var localizers = _title.GetComponentsInChildren<LocalizedLabel>(true);
            foreach (var localized in localizers) ResetLocalizer(localized, Lang.Get("mm.window.title"));
            if (localizers.Length == 0) _title.text = Lang.Get("mm.window.title");
        }
        else
        {
            Plugin.Log.LogWarning($"NGUI: title label not found at {TitlePath}");
        }

        var close = _windowRoot.transform.Find(ClosePath)?.gameObject;
        if (close == null)
        {
            Plugin.Log.LogWarning($"NGUI: close button not found at {ClosePath}");
            return;
        }

        ClearTriggers(close);
        foreach (var navigation in close.GetComponentsInChildren<GamepadNavigationItem>(true))
            navigation.active = false;

        var collider2D = close.GetComponentInChildren<Collider2D>();
        var clickTarget = collider2D != null ? collider2D.gameObject : close;
        UIEventListener.Get(clickTarget).onClick = _ => CloseWindow();
    }

    /// <summary>
    /// NGUI's ResetAnchors() only refreshes the cached rects; the anchor targets keep
    /// re-applying their original layout every LateUpdate. Clearing them is required before
    /// setting explicit sizes and positions.
    /// </summary>
    private static void ClearAnchors(UIRect rect)
    {
        if (rect == null) return;
        if (rect.leftAnchor != null) rect.leftAnchor.target = null;
        if (rect.rightAnchor != null) rect.rightAnchor.target = null;
        if (rect.topAnchor != null) rect.topAnchor.target = null;
        if (rect.bottomAnchor != null) rect.bottomAnchor.target = null;
    }

    private void ScaleWindow()
    {
        var rowPath = _rowPrefab != null ? NodePath(_rowPrefab.transform) : null;

        foreach (var widget in _windowRoot.GetComponentsInChildren<UIWidget>(true))
        {
            var path = NodePath(widget.transform);
            if (string.Equals(path, "dark back", StringComparison.Ordinal)) continue;
            if (rowPath != null &&
                (string.Equals(path, rowPath, StringComparison.Ordinal) ||
                 path.StartsWith(rowPath + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            // Anchors would overwrite the sizes below the next time the GUI recalculates them.
            ClearAnchors(widget);

            // The close button keeps its native size, so scaling the offset of its decorative
            // frame would tear the frame away from the cross and the header would look like it
            // has two close buttons. Everything below the button stays at its authored offset.
            if (path.StartsWith(ClosePath + "/", StringComparison.Ordinal)) continue;

            var position = widget.transform.localPosition;
            widget.transform.localPosition = new Vector3(position.x * WidthScale, position.y * HeightScale, position.z);

            if (!SizeScaled.Contains(path)) continue;

            widget.width = Mathf.RoundToInt(widget.width * WidthScale);
            widget.height = Mathf.RoundToInt(widget.height * HeightScale);

            // Colliders do not follow their widget, and an unscaled drag area would leave most
            // of the list un-draggable.
            var box = widget.GetComponent<BoxCollider2D>();
            if (box != null) box.size = new Vector2(box.size.x * WidthScale, box.size.y * HeightScale);
        }

        var panel = _scroll != null ? _scroll.panel : null;
        if (panel != null)
        {
            ClearAnchors(panel);

            var panelPosition = panel.transform.localPosition;
            panel.transform.localPosition = new Vector3(
                panelPosition.x * WidthScale,
                panelPosition.y * HeightScale,
                panelPosition.z);

            if (panel.clipping == UIDrawCall.Clipping.None)
            {
                var clipWidth = Mathf.RoundToInt(panel.width * WidthScale);
                var clipHeight = Mathf.RoundToInt(panel.height * HeightScale);
                panel.clipping = UIDrawCall.Clipping.SoftClip;
                panel.baseClipRegion = new Vector4(0f, 0f, clipWidth, clipHeight);
            }
            else
            {
                var clip = panel.baseClipRegion;
                panel.baseClipRegion = new Vector4(
                    clip.x * WidthScale,
                    clip.y * HeightScale,
                    clip.z * WidthScale,
                    clip.w * HeightScale);
            }
        }
    }

    private int ScrollContentWidth()
    {
        var panel = _scroll != null ? _scroll.panel : null;
        if (panel == null) return 0;
        return Mathf.RoundToInt(panel.clipping == UIDrawCall.Clipping.None ? panel.width : panel.baseClipRegion.z);
    }

    private string NodePath(Transform transform)
    {
        var parts = new List<string>();
        var current = transform;
        while (current != null && current != _windowRoot.transform)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts.ToArray());
    }

    private void OpenWindow()
    {
        if (_failed || _window == null) return;

        try
        {
            if (_window.is_shown) return;

            // Always open on the mod list, even when the window was last closed from the
            // settings view.
            _settingsMod = null;
            _settingsFields.Clear();

            InvokeBaseOpen(_window, true);
            RefreshRows();
            CaptureEnabledSnapshot();
            SetWindowTitle(Lang.Get("mm.window.title"));
            if (_menu != null && _menu.is_shown) _menu.Hide();

        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.Log.LogError($"NGUI backend open failed, falling back to IMGUI: {ex}");
            try
            {
                if (_window != null)
                {
                    _window.SetOnHide(null);
                    if (_window.is_shown) _window.Hide();
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                if (_menu != null && !_menu.is_shown) _menu.Open(false);
            }
            catch
            {
                // ignore
            }

            DestroyClones();
        }
    }

    private void CloseWindow()
    {
        if (_window == null || !_window.is_shown) return;

        CancelFieldEditing();

        if (!HasPendingChanges())
        {
            _window.Hide();
            return;
        }

        Plugin.Log.LogInfo("NGUI: mod list changed, asking about a restart.");
        GUIElements.me.dialog.OpenYesNo(
            Lang.Get("mm.confirm.restart"),
            () => FinishClose(true),
            () => FinishClose(false));
    }

    private void FinishClose(bool restart)
    {
        if (restart) RestartGame();
        if (_window != null && _window.is_shown) _window.Hide();
    }

    /// <summary>
    /// Compares the on-disk state with the state captured when the window opened, so toggling
    /// a mod twice (back to its original state) does not trigger a restart prompt.
    /// </summary>
    private bool HasPendingChanges()
    {
        if (_enabledWhenOpened == null) return false;

        foreach (var mod in _mods)
        {
            if (!_enabledWhenOpened.TryGetValue(mod.BasePath, out var wasEnabled)) return true;
            if (wasEnabled != mod.Enabled) return true;
        }

        return false;
    }

    private void CaptureEnabledSnapshot()
    {
        var snapshot = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _mods)
            snapshot[mod.BasePath] = mod.Enabled;

        _enabledWhenOpened = snapshot;
    }

    private void RestartGame()
    {
        try
        {
            var executable = FindGameExecutable();
            if (executable == null)
            {
                Plugin.Log.LogError("Restart failed: game executable not found.");
                GUIElements.me.dialog.OpenOK(Lang.Get("mm.error.restart_not_found"));
                return;
            }

            // Graveyard Keeper sets PlayerSettings.forceSingleInstance, so the Unity player holds
            // a "-SingleInstanceMutex-" and a second copy aborts with "Another instance is
            // already running". The relaunch therefore waits for this exact process to exit
            // instead of guessing with a fixed delay.
            const int waitTimeoutSeconds = 60;
            var workingDirectory = Path.GetDirectoryName(executable) ?? string.Empty;
            var currentPid = Process.GetCurrentProcess().Id;

            var viaSteam = CanRestartViaSteam(executable);
            var launch = viaSteam
                ? $"Start-Process 'steam://rungameid/{SteamAppId}'"
                : $"Start-Process -FilePath '{Escape(executable)}' -WorkingDirectory '{Escape(workingDirectory)}'";

            var script =
                $"try {{ Wait-Process -Id {currentPid} -Timeout {waitTimeoutSeconds} -ErrorAction Stop }} catch {{ }}; " +
                launch;

            Plugin.Log.LogInfo(
                $"Restart scheduled: waiting for PID {currentPid} to exit, then " +
                (viaSteam ? $"relaunching through Steam (appid {SteamAppId})" : $"launching {executable} directly"));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });

            Application.Quit();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Restart failed: {ex}");
            GUIElements.me.dialog.OpenOK(string.Format(Lang.Get("mm.error.restart_failed"), ex.Message));
        }
    }

    private static string Escape(string value) => value.Replace("'", "''");

    /// <summary>
    /// Steam's rungameid only helps when the game really is a Steam install AND the client is
    /// already running. A cracked/pirated copy (no steam_api64.dll) is deliberately excluded:
    /// going through Steam would then hit the ownership check and pop the store page instead of
    /// restarting the game.
    /// </summary>
    private static bool CanRestartViaSteam(string executable)
    {
        if (executable.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        var nativeSteamApi = Path.Combine(
            Application.dataPath,
            "Plugins",
            "x86_64",
            "steam_api64.dll");

        if (!File.Exists(nativeSteamApi)) return false;

        Process[] steamProcesses = null;
        try
        {
            steamProcesses = Process.GetProcessesByName("steam");
            return steamProcesses.Length > 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Could not check for a running Steam client: {ex.Message}");
            return false;
        }
        finally
        {
            if (steamProcesses != null)
                foreach (var process in steamProcesses)
                    process.Dispose();
        }
    }

    private static string FindGameExecutable()
    {
        var root = Path.GetDirectoryName(Application.dataPath);
        if (string.IsNullOrEmpty(root)) return null;

        foreach (var name in new[] { "Graveyard Keeper.exe", "GraveyardKeeper.exe" })
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return candidate;
        }

        try
        {
            foreach (var candidate in Directory.GetFiles(root, "*.exe"))
            {
                var fileName = Path.GetFileName(candidate);
                if (fileName.IndexOf("UnityCrashHandler", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return candidate;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Could not enumerate game executables: {ex.Message}");
        }

        return null;
    }

    private void OnWindowHidden()
    {
        if (_menu != null && !_menu.is_shown)
        {
            try
            {
                _menu.Open(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not reopen the main menu: {ex.Message}");
            }
        }

        if (_entry != null)
        {
            var item = _entry.GetComponent<MenuItemGUI>();
            if (item != null) item.Show();
        }
    }

    private void RefreshRows()
    {
        foreach (var row in _rows)
        {
            if (row == null) continue;
            row.SetActive(false);
            UnityEngine.Object.Destroy(row);
        }

        _rows.Clear();

        if (InSettingsView) BuildSettingRows();
        else BuildModRows();

        _table.Reposition();
        _scroll.ResetPosition();
        RefreshPanels();
    }

    private void BuildModRows()
    {
        // Leaving the settings view: drop the references to the rows that have just been destroyed.
        _fieldLabels.Clear();
        _fieldRows.Clear();

        var loaded = new HashSet<string>(Chainloader.PluginInfos.Keys, StringComparer.OrdinalIgnoreCase);
        _mods = ModScanner.Scan(
            BepInEx.Paths.PluginPath,
            MyPluginInfo.PLUGIN_GUID,
            loaded,
            new[] { BepInEx.Paths.BepInExAssemblyDirectory, BepInEx.Paths.PluginPath },
            message => Plugin.Log.LogWarning(message));

        foreach (var mod in _mods)
            CreateRow(mod);

        Plugin.Log.LogInfo($"NGUI window populated with {_mods.Count} mods.");
    }

    private void CreateRow(ModEntry mod)
    {
        var row = UnityEngine.Object.Instantiate(_rowPrefab.gameObject, _table.transform);
        row.name = "mod_row_" + Sanitize(mod.Guid);
        row.SetActive(true);

        var slot = row.GetComponent<SaveSlotGUI>();
        if (slot == null)
        {
            UnityEngine.Object.Destroy(row);
            return;
        }

        slot.Show(null);
        slot.slot_name.text = mod.DisplayName + "  " + mod.Version;
        slot.txt_descr.text = Descriptions.For(mod);
        slot.txt_stats.text = Truncate(mod.Guid, 44);

        var gamepad = row.GetComponent<GamepadNavigationItem>();
        if (gamepad != null) gamepad.active = false;

        // Triggers are cleared before the layout, because the layout adds the options button and
        // clearing afterwards would remove its handler again.
        ClearTriggers(row);
        UIEventListener.Get(row).onClick = _ => OnRowClicked(mod, row);

        ApplyRowLayout(row, slot, mod);
        slot.enabled = false;

        _rows.Add(row);
    }

    /// <summary>
    /// Lays out one row. The row height is derived from the measured, wrapped text height so
    /// long names and descriptions are shown in full instead of being clipped.
    /// </summary>
    private void ApplyRowLayout(GameObject row, SaveSlotGUI slot, ModEntry mod)
    {
        var width = ScrollContentWidth() - 6;
        if (width < 200) width = 480;

        var textLeft = -width * 0.5f + 10f + CheckboxGutter;
        var hint = StatusHint(mod);
        var hasHint = !string.IsNullOrEmpty(hint);
        var hasOptions = SettingsScanner.HasSettings(mod);

        var name = slot.slot_name;
        var reservedRight = (hasHint ? StatusWidth : 0) + (hasOptions ? OptionsArrowSize + 10 : 0);
        MeasureLabel(name, width - CheckboxGutter - reservedRight - 24, NameFontSize);
        var nameHeight = name != null ? Mathf.Max(18, name.height) : 18;

        var descr = slot.txt_descr;
        MeasureLabel(descr, width - CheckboxGutter - 24, DescriptionFontSize);
        var descrHeight = descr != null ? Mathf.Max(14, descr.height) : 14;

        var height = RowPaddingTop + nameHeight + NameToDescriptionGap + descrHeight + RowPaddingBottom;
        if (height < RowMinHeight) height = RowMinHeight;

        var widget = row.GetComponent<UIWidget>();
        if (widget != null)
        {
            ClearAnchors(widget);
            widget.width = width;
            widget.height = height;
        }

        var backWidget = row.transform.Find("back")?.GetComponent<UIWidget>();
        if (backWidget != null)
        {
            ClearAnchors(backWidget);
            backWidget.width = width;
            backWidget.height = height - 2;
        }

        if (name != null)
            name.transform.localPosition = new Vector3(textLeft, height * 0.5f - RowPaddingTop, 0f);

        var status = slot.txt_realtime;
        if (status != null)
        {
            status.gameObject.SetActive(hasHint);

            if (hasHint)
            {
                status.text = hint;
                status.color = StatusColor(mod);
                ClearAnchors(status);
                status.pivot = UIWidget.Pivot.TopRight;
                status.alignment = NGUIText.Alignment.Right;
                status.fontSize = NameFontSize;
                status.width = StatusWidth;
                status.height = 20;
                status.maxLineCount = 1;
                status.overflowMethod = UILabel.Overflow.ClampContent;
                status.transform.localPosition = new Vector3(width * 0.5f - 10f, height * 0.5f - RowPaddingTop, 0f);
            }
        }

        if (descr != null)
        {
            descr.transform.localPosition = new Vector3(
                textLeft,
                height * 0.5f - RowPaddingTop - nameHeight - NameToDescriptionGap,
                0f);
        }

        if (slot.txt_stats != null) slot.txt_stats.gameObject.SetActive(false);

        ApplyCheckbox(slot, row, width, height, mod);

        foreach (var collider in row.GetComponentsInChildren<Collider2D>(true))
            collider.enabled = false;

        var hitbox = row.GetComponent<BoxCollider2D>();
        if (hitbox == null) hitbox = row.AddComponent<BoxCollider2D>();
        hitbox.size = new Vector2(width, height);
        hitbox.offset = Vector2.zero;
        hitbox.enabled = true;

        ApplyOptionsButton(slot, row, width, height, mod);
    }

    /// <summary>
    /// Configures a label to wrap and resize itself, then measures it synchronously so the
    /// row can be sized before the panel builds its geometry.
    /// </summary>
    private static void MeasureLabel(UILabel label, int width, int fontSize)
    {
        if (label == null) return;

        ClearAnchors(label);
        label.pivot = UIWidget.Pivot.TopLeft;
        label.alignment = NGUIText.Alignment.Left;
        label.fontSize = fontSize;
        label.maxLineCount = 0;
        label.overflowMethod = UILabel.Overflow.ResizeHeight;
        label.width = Mathf.Max(40, width);
        label.height = 20;

        label.UpdateNGUIText();
        label.ProcessText(false, true);

        if (label.printedSize.y <= 0f)
            Plugin.Log.LogWarning($"NGUI: could not measure label '{label.name}', the row may be too short.");
    }

    /// <summary>
    /// Draws the on/off indicator on the row. Prefers the game's own art (the golden
    /// selection frame plus the trade window's green tick / red cross); if those sprites
    /// cannot be resolved, a generated 18x18 box is used instead. The manager's own row shows
    /// the game's padlock sprite, since it can never be disabled.
    /// </summary>
    private void ApplyCheckbox(SaveSlotGUI slot, GameObject row, int width, int height, ModEntry mod)
    {
        var button = slot.delete_button;
        if (button == null) return;

        if (!button.activeSelf) button.SetActive(true);
        ClearTriggers(button);

        var box = button.GetComponent<UI2DSprite>();
        if (box == null) return;

        EnsureCheckboxSprites();

        var centerX = -width * 0.5f + 10f + CheckboxSize * 0.5f;
        var centerY = height * 0.5f - RowPaddingTop - CheckboxSize * 0.5f;

        var icon = GetOrCreateCheckboxIcon(row, button);

        if (mod.IsManager)
        {
            // A padlock replaces the box entirely: it reads as "on, but not yours to change".
            var lockSprite = _checkboxLocked ?? _checkboxFrame ?? _checkboxOn;
            ConfigureCheckboxSprite(box, lockSprite, CheckboxSize, 150, centerX, centerY);
            if (icon != null) icon.gameObject.SetActive(false);
        }
        else
        {
            var stateSprite = mod.Enabled ? _checkboxOn : _checkboxOff;
            var boxSprite = _checkboxFrame ?? stateSprite;
            ConfigureCheckboxSprite(box, boxSprite, CheckboxSize, 150, centerX, centerY);

            if (icon != null)
            {
                var iconSprite = _checkboxFrame != null ? stateSprite : null;
                icon.gameObject.SetActive(iconSprite != null);
                if (iconSprite != null)
                    ConfigureCheckboxSprite(icon, iconSprite, CheckboxIconSize, 151, centerX, centerY);
            }
        }

        foreach (var collider in button.GetComponents<Collider2D>())
            collider.enabled = false;
    }

    private static void ConfigureCheckboxSprite(UI2DSprite sprite, Sprite image, int size, int depth, float x, float y)
    {
        if (image != null) sprite.sprite2D = image;

        ClearAnchors(sprite);
        sprite.width = size;
        sprite.height = size;
        sprite.depth = depth;
        sprite.transform.localPosition = new Vector3(x, y, 0f);
    }

    private static UI2DSprite GetOrCreateCheckboxIcon(GameObject row, GameObject button)
    {
        var existing = row.transform.Find("mod_check_icon");
        if (existing != null) return existing.GetComponent<UI2DSprite>();

        var clone = UnityEngine.Object.Instantiate(button, row.transform);
        clone.name = "mod_check_icon";
        clone.SetActive(true);

        foreach (var collider in clone.GetComponents<Collider2D>())
            collider.enabled = false;

        return clone.GetComponent<UI2DSprite>();
    }

    private void EnsureCheckboxSprites()
    {
        if (_checkboxSpritesReady) return;
        _checkboxSpritesReady = true;

        var frame = FindGameSprite("icon_frame_small_selection");
        var yes = FindGameSprite("trade_button_yes");
        var no = FindGameSprite("trade_button_no");
        var locked = FindGameSprite("ui_reply_lock_gray");

        _checkboxLocked = locked;

        if (frame != null && yes != null)
        {
            _checkboxFrame = frame;
            _checkboxOn = yes;
            _checkboxOff = no;
            _generatedCheckboxArt = false;
            Plugin.Log.LogInfo(
                $"NGUI: using the game's own checkbox art (frame + tick{(no != null ? " + cross" : string.Empty)}{(locked != null ? " + lock" : string.Empty)}).");
            return;
        }

        Plugin.Log.LogWarning(
            $"NGUI: game checkbox art not found (frame={frame != null}, tick={yes != null}, cross={no != null}, lock={locked != null}); using generated sprites.");

        _checkboxFrame = null;
        _checkboxOff = CreateCheckboxSprite(false);
        _checkboxOn = CreateCheckboxSprite(true);
        _generatedCheckboxArt = true;
    }

    private static readonly string[] SpriteSearchPrefixes =
    {
        "",
        "Sprites/",
        "Sprite/",
        "GUI/",
        "UI/",
        "Icons/",
        "Icon/",
        "Atlases/"
    };

    private static Sprite FindGameSprite(string name)
    {
        foreach (var prefix in SpriteSearchPrefixes)
        {
            var sprite = Resources.Load<Sprite>(prefix + name);
            if (sprite != null) return sprite;
        }

        foreach (var candidate in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (candidate != null && string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private void DestroyCheckboxSprites()
    {
        _checkboxSpritesReady = false;

        if (!_generatedCheckboxArt)
        {
            _checkboxFrame = null;
            _checkboxOn = null;
            _checkboxOff = null;
            _checkboxLocked = null;
            return;
        }

        DestroySprite(ref _checkboxOn);
        DestroySprite(ref _checkboxOff);
        _checkboxFrame = null;
        _checkboxLocked = null;
    }

    private static void DestroySprite(ref Sprite sprite)
    {
        if (sprite == null) return;

        var texture = sprite.texture;
        UnityEngine.Object.Destroy(sprite);
        if (texture != null) UnityEngine.Object.Destroy(texture);
        sprite = null;
    }

    private static Sprite CreateCheckboxSprite(bool isChecked)
    {
        const int size = 18;

        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = isChecked ? "gkmm_checkbox_on" : "gkmm_checkbox_off"
        };

        var border = new Color32(0xC8, 0xB4, 0x84, 0xFF);
        var fill = new Color32(0x2E, 0x33, 0x3D, 0xFF);
        var shadow = new Color32(0x1A, 0x1D, 0x23, 0xFF);
        var tick = new Color32(0x9C, 0xD0, 0x6A, 0xFF);

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var onEdge = x == 0 || y == 0 || x == size - 1 || y == size - 1;
            var innerEdge = x == 1 || y == 1 || x == size - 2 || y == size - 2;

            Color32 color;
            if (onEdge) color = border;
            else if (innerEdge) color = shadow;
            else color = fill;

            texture.SetPixel(x, y, color);
        }

        if (isChecked)
        {
            var points = new[]
            {
                (4, 9), (5, 8), (6, 7), (7, 6), (8, 7), (9, 8), (10, 9), (11, 10), (12, 11), (13, 12),
                (4, 8), (5, 7), (6, 6), (7, 5), (8, 6), (9, 7), (10, 8), (11, 9), (12, 10), (13, 11)
            };

            foreach (var point in points)
            {
                if (point.Item1 < 2 || point.Item1 > size - 3) continue;
                if (point.Item2 < 2 || point.Item2 > size - 3) continue;
                texture.SetPixel(point.Item1, point.Item2, tick);
            }
        }

        texture.Apply(false, false);
        return Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            1f);
    }

    private void OnRowClicked(ModEntry mod, GameObject row)
    {
        try
        {
            Sounds.OnGUIClick();

            if (mod.IsManager)
            {
                GUIElements.me.dialog.OpenOK(Lang.Get("mm.note.manager"));
                return;
            }

            if (mod.Enabled)
            {
                var dependents = ModToggler.FindHardDependents(mod, _mods);
                if (dependents.Count > 0)
                {
                    var names = string.Join(", ", dependents.Select(entry => entry.DisplayName).ToArray());
                    var message = string.Format(Lang.Get("mm.confirm.disable"), mod.DisplayName, names);
                    GUIElements.me.dialog.OpenYesNo(message, () => ApplyToggle(mod, row), null);
                    return;
                }
            }

            ApplyToggle(mod, row);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"NGUI row click failed: {ex}");
        }
    }

    private void ApplyToggle(ModEntry mod, GameObject row)
    {
        if (!ModToggler.TrySetEnabled(mod, !mod.Enabled, out var error))
        {
            var message = string.Format(Lang.Get("mm.error.toggle"), mod.DisplayName, error);
            GUIElements.me.dialog.OpenOK(message);
            return;
        }

        Plugin.Log.LogInfo($"{(mod.Enabled ? "Enabled" : "Disabled")} [{mod.Guid}] via NGUI.");
        UpdateRow(row, mod);
    }

    private void UpdateRow(GameObject row, ModEntry mod)
    {
        var slot = row != null ? row.GetComponent<SaveSlotGUI>() : null;
        if (slot == null) return;

        // Toggling can add or remove the status hint (e.g. "restart needed"), which changes
        // both the name width and possibly the row height, so the row is laid out again.
        ApplyRowLayout(row, slot, mod);
        _table.Reposition();
        RefreshPanels();
    }

    private static string StatusHint(ModEntry mod)
    {
        // Nothing to print: the box shows enabled/disabled (a padlock for the locked manager
        // row) and pending changes are reported by the restart prompt when the window closes.
        return string.Empty;
    }

    private static Color StatusColor(ModEntry mod) => new Color(0.92f, 0.78f, 0.35f);

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? string.Empty;
        return value.Substring(0, max - 1) + "\u2026";
    }

    private static string Sanitize(string value) =>
        string.IsNullOrEmpty(value) ? "unknown" : value.Replace('.', '_');

    private static void ResetLocalizer(LocalizedLabel localized, string text)
    {
        localized.token = text;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LocalizedLabel);
        type.GetField("_initialized", flags)?.SetValue(localized, false);
        type.GetField("_lng_token", flags)?.SetValue(localized, string.Empty);

        localized.Localize();
    }

    private static void ClearTriggers(GameObject root)
    {
        foreach (var listener in root.GetComponentsInChildren<UIEventListener>(true))
        {
            listener.onSubmit = null;
            listener.onClick = null;
            listener.onDoubleClick = null;
            listener.onHover = null;
            listener.onPress = null;
            listener.onSelect = null;
            listener.onScroll = null;
            listener.onDragStart = null;
            listener.onDrag = null;
            listener.onDragOver = null;
            listener.onDragOut = null;
            listener.onDragEnd = null;
            listener.onDrop = null;
            listener.onKey = null;
            listener.onTooltip = null;
        }

        foreach (var button in root.GetComponentsInChildren<UIButton>(true))
            button.onClick.Clear();

        foreach (var trigger in root.GetComponentsInChildren<UIEventTrigger>(true))
        {
            trigger.onHoverOver.Clear();
            trigger.onHoverOut.Clear();
            trigger.onPress.Clear();
            trigger.onRelease.Clear();
            trigger.onSelect.Clear();
            trigger.onDeselect.Clear();
            trigger.onClick.Clear();
            trigger.onDoubleClick.Clear();
            trigger.onDragStart.Clear();
            trigger.onDragEnd.Clear();
            trigger.onDragOver.Clear();
            trigger.onDragOut.Clear();
            trigger.onDrag.Clear();
        }
    }

    private static void InvokeBaseOpen(BaseGUI window, bool playSound)
    {
        var method = typeof(BaseGUI).GetMethod(
            "Open",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(bool) },
            null);

        if (method == null) throw new MissingMethodException("BaseGUI.Open(bool)");
        method.Invoke(window, new object[] { playSound });
    }

    private GameObject FindCloseButton()
    {
        var transform = _windowRoot != null ? _windowRoot.transform.Find(ClosePath) : null;
        if (transform != null) return transform.gameObject;

        if (_windowRoot == null) return null;

        foreach (var button in _windowRoot.GetComponentsInChildren<UIButton>(true))
        {
            if (button.name.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0)
                return button.gameObject;
        }

        return null;
    }

    private void NeutralizeNativeActions(SaveSlotsMenuGUI window)
    {
        var field = typeof(BaseGUI).GetField(
            "gamekey_delegates",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var delegates = field != null
            ? field.GetValue(window) as Dictionary<GameKey, GJCommons.BoolDelegate>
            : null;

        if (delegates == null)
        {
            Plugin.Log.LogWarning("NGUI: could not replace the save-slot key handlers; menu keys stay native.");
            return;
        }

        delegates[GameKey.Back] = () =>
        {
            if (HandleBack()) return true;

            CloseWindow();
            return true;
        };
        delegates[GameKey.Select] = () => true;
        delegates[GameKey.Option1] = () => true;
        delegates[GameKey.Option2] = () => true;
    }

    private void DestroyClones()
    {
        if (_scrollHooked)
        {
            UICamera.onScroll -= OnCameraScroll;
            _scrollHooked = false;
        }

        DestroySettingArt();

        if (_windowRoot != null) UnityEngine.Object.Destroy(_windowRoot);
        if (_entry != null) UnityEngine.Object.Destroy(_entry);

        DestroyCheckboxSprites();
        _enabledWhenOpened = null;

        _windowRoot = null;
        _window = null;
        _entry = null;
        _scroll = null;
        _table = null;
        _rowPrefab = null;
        _title = null;
        _rows.Clear();
    }
}
