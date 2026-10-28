using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using BepInEx.Bootstrap;
using Shared;
using UnityEngine;

namespace GKModManager;

internal sealed class ModManagerWindow
{
    private const int WindowId = 0x4D4D; // "MM"
    private const float WindowWidth = 880f;
    private const float WindowHeight = 620f;

    private readonly string _pluginsRoot;
    private readonly string _managerGuid;
    private readonly List<BlockedCamera> _blockedCameras = new();

    private List<ModEntry> _mods = new();
    private bool _show;
    private bool _rectLoaded;
    private string _search = string.Empty;
    private string _error;
    private Vector2 _scroll;
    private Rect _rect;
    private ModEntry _pendingDisable;
    private List<ModEntry> _pendingDependents;
    private MainMenuGUI _mainMenu;
    private float _nextMenuLookup;

    private GUIStyle _titleStyle;
    private GUIStyle _nameStyle;
    private GUIStyle _descStyle;
    private GUIStyle _smallStyle;
    private GUIStyle _badgeStyle;
    private GUIStyle _menuButtonStyle;
    private GUIStyle _rowStyle;
    private Texture2D _windowTexture;
    private Texture2D _rowTexture;

    internal ModManagerWindow(string pluginsRoot, string managerGuid)
    {
        _pluginsRoot = pluginsRoot;
        _managerGuid = managerGuid;
    }

    internal void Shutdown()
    {
        if (_rectLoaded) SaveRect();
        SetWindowOpen(false);
    }

    internal void OnGUI()
    {
        // GUIElements.me falls back to FindObjectOfType while the instance is missing,
        // so only probe once a second while the game is still loading.
        if (_mainMenu == null && Time.unscaledTime >= _nextMenuLookup)
        {
            _nextMenuLookup = Time.unscaledTime + 1f;
            var elements = GUIElements.me;
            _mainMenu = elements != null ? elements.main_menu : null;
        }

        var mainMenu = _mainMenu;
        if (mainMenu == null || !mainMenu.is_shown)
        {
            if (_show) SetWindowOpen(false);
            return;
        }

        try
        {
            EnsureStyles();

            if (!_show)
            {
                DrawMenuButton();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                SetWindowOpen(false);
                return;
            }

            DrawWindow();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Mod Manager UI error: {ex}");
            SetWindowOpen(false);
        }
    }

    private void DrawMenuButton()
    {
        var rect = new Rect(Plugin.MenuButtonX.Value, Plugin.MenuButtonY.Value, 132f, 40f);
        if (GUI.Button(rect, Lang.Get("mm.button.open"), _menuButtonStyle))
        {
            Scan();
            SetWindowOpen(true);
        }
    }

    private void DrawWindow()
    {
        if (!_rectLoaded) LoadRect();

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        var previousColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = previousColor;

        _rect = GUILayout.Window(WindowId, _rect, DrawContents, GUIContent.none);
    }

    private void DrawContents(int id)
    {
        GUI.DrawTexture(
            new Rect(0f, 0f, WindowWidth + 24f, WindowHeight + 44f),
            _windowTexture);

        GUILayout.BeginVertical(GUILayout.Width(WindowWidth), GUILayout.Height(WindowHeight));

        GUILayout.BeginHorizontal();
        GUILayout.Label(Lang.Get("mm.window.title"), _titleStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(Lang.Get("mm.button.close"), GUILayout.Width(100f), GUILayout.Height(26f)))
            SetWindowOpen(false);
        GUILayout.EndHorizontal();

        var enabled = _mods.Count(entry => entry.Enabled);
        var pending = _mods.Count(entry => entry.Enabled != entry.LoadedThisSession);
        GUILayout.Label(string.Format(Lang.Get("mm.stats"), _mods.Count, enabled, pending), _descStyle);

        if (!string.IsNullOrEmpty(_error))
            GUILayout.Label($"<color=#ff9090>{_error}</color>", _descStyle);
        else if (pending > 0)
            GUILayout.Label($"<color=#ffcf70>{Lang.Get("mm.tip.restart")}</color>", _descStyle);

        if (_pendingDisable != null)
            DrawDisableConfirmation();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(Lang.Get("mm.button.refresh"), GUILayout.Width(100f)))
            Scan();

        GUILayout.Label(Lang.Get("mm.label.search"), GUILayout.Width(56f));
        _search = GUILayout.TextField(_search ?? string.Empty, GUILayout.ExpandWidth(true));

        if (GUILayout.Button(Lang.Get("mm.button.enable_all"), GUILayout.Width(110f)))
            SetAll(true);
        if (GUILayout.Button(Lang.Get("mm.button.disable_all"), GUILayout.Width(110f)))
            SetAll(false);
        GUILayout.EndHorizontal();

        _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUILayout.ExpandHeight(true));

        var visible = Filtered();
        foreach (var mod in visible)
            DrawRow(mod);

        if (visible.Count == 0)
            GUILayout.Label(Lang.Get("mm.list.empty"), _descStyle);

        GUILayout.EndScrollView();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(Lang.Get("mm.button.open_folder"), GUILayout.Width(170f)))
            OpenPath(_pluginsRoot);
        if (GUILayout.Button(Lang.Get("mm.button.open_log"), GUILayout.Width(140f)))
            OpenPath(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"));
        GUILayout.FlexibleSpace();
        GUILayout.Label(Lang.Get("mm.footer.hint"), _smallStyle);
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 28f));
    }

    private void DrawDisableConfirmation()
    {
        GUILayout.BeginVertical(_rowStyle);
        var dependents = string.Join(", ", _pendingDependents.Select(entry => entry.DisplayName).ToArray());
        GUILayout.Label(
            $"<color=#ffcf70>{string.Format(Lang.Get("mm.confirm.disable"), _pendingDisable.DisplayName, dependents)}</color>",
            _descStyle);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(Lang.Get("mm.button.confirm"), GUILayout.Width(120f)))
        {
            var target = _pendingDisable;
            _pendingDisable = null;
            _pendingDependents = null;
            ApplyToggle(target, false);
        }

        if (GUILayout.Button(Lang.Get("mm.button.cancel"), GUILayout.Width(120f)))
        {
            _pendingDisable = null;
            _pendingDependents = null;
        }

        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private void DrawRow(ModEntry mod)
    {
        GUILayout.BeginVertical(_rowStyle);

        GUILayout.BeginHorizontal();
        var newEnabled = GUILayout.Toggle(mod.Enabled, GUIContent.none, GUILayout.Width(24f), GUILayout.Height(22f));
        if (newEnabled != mod.Enabled) RequestToggle(mod, newEnabled);

        GUILayout.Label($"{mod.DisplayName}  <color=#8fc7ff>{mod.Version}</color>", _nameStyle, GUILayout.ExpandWidth(true));
        GUILayout.Label(StatusText(mod), _badgeStyle, GUILayout.Width(200f));
        GUILayout.EndHorizontal();

        GUILayout.Label(Descriptions.For(mod), _descStyle);

        var missing = mod.Enabled
            ? ModToggler.FindMissingHardDependencies(mod, _mods)
            : new List<string>();
        if (missing.Count > 0)
        {
            GUILayout.Label(
                $"<color=#ff9f7f>{string.Format(Lang.Get("mm.warn.missing"), string.Join(", ", missing.ToArray()))}</color>",
                _descStyle);
        }

        var incompatible = mod.Enabled
            ? ModToggler.FindIncompatible(mod, _mods)
            : new List<ModEntry>();
        if (incompatible.Count > 0)
        {
            GUILayout.Label(
                $"<color=#ff9f7f>{string.Format(Lang.Get("mm.warn.incompatible"), string.Join(", ", incompatible.Select(entry => entry.DisplayName).ToArray()))}</color>",
                _descStyle);
        }

        var category = Descriptions.Category(mod);
        var meta = string.IsNullOrEmpty(category)
            ? $"{mod.Guid}  ·  {mod.FileName}"
            : $"{mod.Guid}  ·  {mod.FileName}  ·  {category}";
        GUILayout.Label(meta, _smallStyle);

        if (mod.IsManager)
            GUILayout.Label(Lang.Get("mm.note.manager"), _smallStyle);

        var url = Descriptions.Url(mod);
        if (!string.IsNullOrEmpty(url))
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.Get("mm.button.nexus"), GUILayout.Width(90f), GUILayout.Height(20f)))
                OpenUrl(url);
            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();
        GUILayout.Space(4f);
    }

    private void RequestToggle(ModEntry mod, bool enabled)
    {
        if (mod.IsManager) return;

        if (enabled)
        {
            ApplyToggle(mod, true);
            return;
        }

        var dependents = ModToggler.FindHardDependents(mod, _mods);
        if (dependents.Count == 0)
        {
            ApplyToggle(mod, false);
            return;
        }

        _pendingDisable = mod;
        _pendingDependents = dependents;
    }

    private void ApplyToggle(ModEntry mod, bool enabled)
    {
        _error = null;
        if (!ModToggler.TrySetEnabled(mod, enabled, out var error))
        {
            _error = string.Format(Lang.Get("mm.error.toggle"), mod.DisplayName, error);
            return;
        }

        Plugin.Log.LogInfo($"{(enabled ? "Enabled" : "Disabled")} [{mod.Guid}] {mod.FileName}");
    }

    private void SetAll(bool enabled)
    {
        _pendingDisable = null;
        _pendingDependents = null;

        foreach (var mod in _mods.Where(entry => !entry.IsManager && entry.Enabled != enabled).ToList())
            ApplyToggle(mod, enabled);
    }

    private void SetWindowOpen(bool open)
    {
        if (_show == open) return;

        _show = open;
        if (open)
        {
            BlockMenuInput(true);
            return;
        }

        _pendingDisable = null;
        _pendingDependents = null;
        SaveRect();
        BlockMenuInput(false);
    }

    private void BlockMenuInput(bool block)
    {
        if (block)
        {
            if (_blockedCameras.Count > 0) return;

            foreach (var camera in UnityEngine.Object.FindObjectsOfType<UICamera>())
            {
                if (camera == null) continue;
                _blockedCameras.Add(new BlockedCamera(camera));
                camera.useMouse = false;
                camera.useTouch = false;
                camera.useKeyboard = false;
            }

            return;
        }

        foreach (var blocked in _blockedCameras)
            blocked.Restore();

        _blockedCameras.Clear();
    }

    private void Scan()
    {
        _error = null;
        try
        {
            var loaded = new HashSet<string>(Chainloader.PluginInfos.Keys, StringComparer.OrdinalIgnoreCase);
            _mods = ModScanner.Scan(
                _pluginsRoot,
                _managerGuid,
                loaded,
                new[] { BepInEx.Paths.BepInExAssemblyDirectory, _pluginsRoot },
                message => Plugin.Log.LogWarning(message));
            Plugin.Log.LogInfo($"Scanned {_mods.Count} mods ({_mods.Count(entry => entry.Enabled)} enabled).");
        }
        catch (Exception ex)
        {
            _error = string.Format(Lang.Get("mm.error.scan"), ex.Message);
            Plugin.Log.LogError(ex.ToString());
        }
    }

    private List<ModEntry> Filtered()
    {
        if (string.IsNullOrWhiteSpace(_search)) return _mods;

        var query = _search.Trim();
        return _mods.Where(entry =>
                entry.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                Descriptions.For(entry).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
    }

    private void LoadRect()
    {
        _rectLoaded = true;
        _rect = new Rect(
            Mathf.Max(0f, (Screen.width - WindowWidth - 20f) / 2f),
            Mathf.Max(0f, (Screen.height - WindowHeight - 40f) / 2f),
            WindowWidth + 20f,
            WindowHeight + 40f);

        var saved = Plugin.WindowRect.Value;
        if (string.IsNullOrEmpty(saved)) return;

        var parts = saved.Split(';');
        if (parts.Length != 4) return;

        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) &&
            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            width = Mathf.Max(480f, width);
            height = Mathf.Max(320f, height);
            x = Mathf.Clamp(x, 240f - width, Screen.width - 240f);
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 80f));
            _rect = new Rect(x, y, width, height);
        }
    }

    private void SaveRect()
    {
        var value = string.Join(
            ";",
            _rect.x.ToString("0.##", CultureInfo.InvariantCulture),
            _rect.y.ToString("0.##", CultureInfo.InvariantCulture),
            _rect.width.ToString("0.##", CultureInfo.InvariantCulture),
            _rect.height.ToString("0.##", CultureInfo.InvariantCulture));

        if (Plugin.WindowRect.Value != value) Plugin.WindowRect.Value = value;
    }

    private void EnsureStyles()
    {
        if (_titleStyle != null) return;

        _windowTexture = MakeTexture(new Color(0.12f, 0.13f, 0.15f, 1f));
        _rowTexture = MakeTexture(new Color(0.18f, 0.19f, 0.22f, 1f));

        var text = new Color(0.95f, 0.95f, 0.95f);
        var muted = new Color(0.68f, 0.68f, 0.68f);

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            richText = true,
            normal = { textColor = text }
        };

        _nameStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            richText = true,
            normal = { textColor = text }
        };

        _descStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            wordWrap = true,
            richText = true,
            normal = { textColor = new Color(0.86f, 0.86f, 0.86f) }
        };

        _smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            wordWrap = true,
            richText = true,
            normal = { textColor = muted }
        };

        _badgeStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleRight,
            richText = true,
            normal = { textColor = text }
        };

        _menuButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold
        };

        _rowStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(10, 10, 8, 8),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0)
        };
        _rowStyle.normal.background = _rowTexture;
    }

    private static Texture2D MakeTexture(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Could not open {path}: {ex.Message}");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Application.OpenURL(url);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Could not open {url}: {ex.Message}");
        }
    }

    private static string StatusText(ModEntry mod)
    {
        if (mod.IsManager)
            return $"<color=#8fc7ff>{Lang.Get("mm.status.locked")}</color>";

        if (mod.Enabled && mod.LoadedThisSession)
            return $"<color=#8fdc8f>{Lang.Get("mm.status.loaded")}</color>";

        if (!mod.Enabled && mod.LoadedThisSession)
            return $"<color=#ffb86b>{Lang.Get("mm.status.disabled_restart")}</color>";

        if (mod.Enabled)
            return $"<color=#8fc7ff>{Lang.Get("mm.status.enabled_restart")}</color>";

        return $"<color=#a0a0a0>{Lang.Get("mm.status.disabled")}</color>";
    }

    private readonly struct BlockedCamera
    {
        private readonly UICamera _camera;
        private readonly bool _useMouse;
        private readonly bool _useTouch;
        private readonly bool _useKeyboard;

        internal BlockedCamera(UICamera camera)
        {
            _camera = camera;
            _useMouse = camera.useMouse;
            _useTouch = camera.useTouch;
            _useKeyboard = camera.useKeyboard;
        }

        internal void Restore()
        {
            if (_camera == null) return;
            _camera.useMouse = _useMouse;
            _camera.useTouch = _useTouch;
            _camera.useKeyboard = _useKeyboard;
        }
    }
}
