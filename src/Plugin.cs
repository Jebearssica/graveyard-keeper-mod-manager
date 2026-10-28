using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Shared;
using UnityEngine;

namespace GKModManager;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance { get; private set; }
    internal static TimestampedLogger Log { get; private set; }

    internal static ConfigEntry<int> MenuButtonX { get; private set; }
    internal static ConfigEntry<int> MenuButtonY { get; private set; }
    internal static ConfigEntry<string> WindowRect { get; private set; }

    private ModManagerWindow _imgui;
    private NguiModManager _ngui;
    private bool _nguiEnabled;

    private void Awake()
    {
        Instance = this;
        Log = new TimestampedLogger(Logger);
        Lang.Init(Assembly.GetExecutingAssembly(), Log);

        MenuButtonX = Config.Bind(
            "Interface",
            "Menu button X",
            16,
            "Horizontal offset of the IMGUI MODS button (only used when the native NGUI window is disabled).");

        MenuButtonY = Config.Bind(
            "Interface",
            "Menu button Y",
            16,
            "Vertical offset of the IMGUI MODS button (only used when the native NGUI window is disabled).");

        WindowRect = Config.Bind(
            "Interface",
            "Window rect",
            string.Empty,
            "Saved IMGUI window position, format x;y;width;height. Leave empty to centre it.");

        var useNgui = Config.Bind(
            "Interface",
            "Use native NGUI window",
            true,
            "Clone the game's save-slots screen for the mod list instead of drawing an IMGUI window. Falls back to IMGUI automatically.");

        _nguiEnabled = useNgui.Value;
        _imgui = new ModManagerWindow(Paths.PluginPath, MyPluginInfo.PLUGIN_GUID);
        _ngui = new NguiModManager();
        Log.LogInfo($"Mod Manager ready. Plugin directory: {Paths.PluginPath}");
    }

    private void OnGUI()
    {
        if (_nguiEnabled)
        {
            if (!_ngui.Ready && !_ngui.Failed) _ngui.TryInitialize();
            if (_ngui.Ready) return;
        }

        _imgui?.OnGUI();
    }

    private void OnDestroy()
    {
        _ngui?.Shutdown();
        _imgui?.Shutdown();
    }
}
