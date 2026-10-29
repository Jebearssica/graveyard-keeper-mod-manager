using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Shared;
using UnityEngine;

namespace GKModManager;

/// <summary>
/// Settings view. It lives inside the same NGUI window as the mod list: rows are cloned from the
/// save-slot row, and the controls use the game's own tick/cross, padlock and arrow art so the
/// screen matches the options menu the game already ships.
/// </summary>
internal sealed partial class NguiModManager
{
    private const int SettingsValueWidth = 150;
    private const int SettingsArrowSize = 18;
    private const int SettingsArrowGap = 4;
    private const int SettingsArrowDepth = 152;
    private const int SettingsValueDepth = 151;
    private const int OptionsArrowSize = 16;

    private ModEntry _settingsMod;
    private readonly List<SettingField> _settingsFields = new();
    private readonly List<KeyValuePair<SettingField, UILabel>> _fieldLabels = new();
    private readonly List<KeyValuePair<SettingField, GameObject>> _fieldRows = new();

    private SettingField _editingField;
    private string _editBuffer = string.Empty;
    private SettingField _listeningField;

    private Sprite _arrowLeft;
    private Sprite _arrowRight;
    private bool _arrowSpritesReady;
    private bool _generatedArrowArt;

    private bool InSettingsView => _settingsMod != null;

    private void OpenSettings(ModEntry mod)
    {
        if (mod == null || _window == null) return;

        _settingsMod = mod;
        CancelFieldEditing();
        _settingsFields.Clear();
        _settingsFields.AddRange(SettingsScanner.Collect(mod, Plugin.Log.LogWarning));

        RefreshRows();
        SetWindowTitle(string.Format(Lang.Get("mm.settings.title"), mod.DisplayName));
        Plugin.Log.LogInfo($"NGUI: opened settings for [{mod.Guid}] with {_settingsFields.Count} fields.");
    }

    private void BackToModList()
    {
        if (!InSettingsView) return;

        CancelFieldEditing();
        _settingsMod = null;
        _settingsFields.Clear();
        RefreshRows();
        SetWindowTitle(Lang.Get("mm.window.title"));
    }

    private void SetWindowTitle(string text)
    {
        if (_title == null) return;

        var localizers = _title.GetComponentsInChildren<LocalizedLabel>(true);
        foreach (var localized in localizers) ResetLocalizer(localized, text);
        if (localizers.Length == 0) _title.text = text;
    }

    // ---- row construction -------------------------------------------------

    private void BuildSettingRows()
    {
        _fieldLabels.Clear();
        _fieldRows.Clear();

        CreateBackRow();

        if (_settingsFields.Count == 0)
        {
            CreateMessageRow(Lang.Get("mm.settings.empty"), Lang.Get("mm.settings.empty.desc"));
            return;
        }

        string currentSection = null;
        foreach (var field in _settingsFields)
        {
            var entry = FieldEntry(field);
            var section = entry != null ? entry.Definition.Section : string.Empty;
            if (!string.Equals(currentSection, section, StringComparison.Ordinal))
            {
                currentSection = section;
                CreateSectionRow(section);
            }

            CreateSettingRow(field);
        }
    }

    private static ConfigEntryBase FieldEntry(SettingField field) => field.Tag as ConfigEntryBase;

    private void CreateBackRow()
    {
        var row = CreateSettingShell(Lang.Get("mm.settings.back"), Lang.Get("mm.settings.back.desc"), out var slot);
        if (row == null) return;

        ApplySettingLayout(row, slot, null, hasIndicator: true, hasArrows: false, valueText: string.Empty);

        var widget = row.GetComponent<UIWidget>();
        var height = widget != null ? widget.height : RowMinHeight;
        var width = widget != null ? widget.width : ScrollContentWidth() - 6;

        EnsureArrowSprites();
        EnsureCheckboxSprites();
        var icon = slot.delete_button != null ? slot.delete_button.GetComponent<UI2DSprite>() : null;
        if (icon != null)
        {
            if (!icon.gameObject.activeSelf) icon.gameObject.SetActive(true);
            ConfigureCheckboxSprite(icon, _arrowLeft ?? _checkboxFrame ?? _checkboxOn, CheckboxSize, SettingsArrowDepth,
                -width * 0.5f + 10f + CheckboxSize * 0.5f,
                height * 0.5f - RowPaddingTop - CheckboxSize * 0.5f);
            foreach (var collider in icon.GetComponents<Collider2D>()) collider.enabled = false;
        }

        UIEventListener.Get(row).onClick = _ =>
        {
            Sounds.OnGUIClick();
            BackToModList();
        };

        _rows.Add(row);
    }

    private void CreateSectionRow(string section)
    {
        if (string.IsNullOrEmpty(section)) return;

        var row = CreateSettingShell(section, string.Empty, out var slot);
        if (row == null) return;

        ApplySettingLayout(row, slot, null, hasIndicator: false, hasArrows: false, valueText: string.Empty);

        if (slot.slot_name != null) slot.slot_name.color = new Color(0.85f, 0.78f, 0.55f);
        if (slot.txt_descr != null) slot.txt_descr.gameObject.SetActive(false);

        _rows.Add(row);
    }

    private void CreateMessageRow(string title, string description)
    {
        var row = CreateSettingShell(title, description, out var slot);
        if (row == null) return;

        ApplySettingLayout(row, slot, null, hasIndicator: false, hasArrows: false, valueText: string.Empty);
        _rows.Add(row);
    }

    private GameObject CreateSettingShell(string name, string description, out SaveSlotGUI slot)
    {
        slot = null;
        if (_rowPrefab == null || _table == null) return null;

        var row = UnityEngine.Object.Instantiate(_rowPrefab.gameObject, _table.transform);
        row.name = "mod_setting_row";
        row.SetActive(true);

        slot = row.GetComponent<SaveSlotGUI>();
        if (slot == null)
        {
            UnityEngine.Object.Destroy(row);
            return null;
        }

        slot.Show(null);
        if (slot.slot_name != null) slot.slot_name.text = name;
        if (slot.txt_descr != null) slot.txt_descr.text = description;
        if (slot.txt_stats != null) slot.txt_stats.gameObject.SetActive(false);

        var gamepad = row.GetComponent<GamepadNavigationItem>();
        if (gamepad != null) gamepad.active = false;

        ClearTriggers(row);
        slot.enabled = false;
        return row;
    }

    private void CreateSettingRow(SettingField field)
    {
        var row = CreateSettingShell(field.Label, field.Description, out var slot);
        if (row == null) return;

        ApplySettingLayout(row, slot, field, hasIndicator: field.Kind == SettingKind.Toggle,
            hasArrows: field.HasArrows, valueText: field.CurrentText());

        var widget = row.GetComponent<UIWidget>();
        var width = widget != null ? widget.width : ScrollContentWidth() - 6;
        var height = widget != null ? widget.height : RowMinHeight;

        UIEventListener.Get(row).onClick = _ => OnSettingActivated(field);
        AddValueEditTarget(row, slot, field);

        if (field.HasArrows)
        {
            EnsureArrowSprites();

            var decX = width * 0.5f - 12f - SettingsArrowSize * 1.5f - SettingsArrowGap;
            var incX = width * 0.5f - 12f - SettingsArrowSize * 0.5f;
            var arrowY = height * 0.5f - RowPaddingTop - SettingsArrowSize * 0.5f + 2f;

            AddSettingArrow(slot, row, "setting_dec", _arrowLeft, decX, arrowY, SettingsArrowSize, () => StepField(field, -1));
            AddSettingArrow(slot, row, "setting_inc", _arrowRight, incX, arrowY, SettingsArrowSize, () => StepField(field, 1));
        }

        _fieldRows.Add(new KeyValuePair<SettingField, GameObject>(field, row));
        if (slot.txt_realtime != null)
            _fieldLabels.Add(new KeyValuePair<SettingField, UILabel>(field, slot.txt_realtime));

        _rows.Add(row);
    }

    /// <summary>
    /// Shared geometry for every settings row. Mirrors <see cref="ApplyRowLayout"/> but reserves
    /// room for the right-aligned value and the two arrow buttons.
    /// </summary>
    private void ApplySettingLayout(GameObject row, SaveSlotGUI slot, SettingField field,
        bool hasIndicator, bool hasArrows, string valueText)
    {
        var width = ScrollContentWidth() - 6;
        if (width < 200) width = 480;

        var gutter = hasIndicator ? CheckboxGutter : 12;
        var arrows = hasArrows ? SettingsArrowSize * 2 + SettingsArrowGap + 8 : 0;
        var valueWidth = Mathf.Clamp(width / 3, 70, SettingsValueWidth);
        var textLeft = -width * 0.5f + 12f + gutter;
        var textWidth = width - 12 - gutter - valueWidth - arrows - 14;

        var name = slot.slot_name;
        MeasureLabel(name, textWidth, NameFontSize);
        var nameHeight = name != null ? Mathf.Max(18, name.height) : 18;

        var descr = slot.txt_descr;
        var hasDescription = descr != null && !string.IsNullOrEmpty(descr.text);
        if (descr != null) descr.gameObject.SetActive(hasDescription);

        var descrHeight = 0;
        if (hasDescription)
        {
            MeasureLabel(descr, width - 12 - gutter - 14, DescriptionFontSize);
            descrHeight = Mathf.Max(14, descr.height);
        }

        var height = RowPaddingTop + nameHeight + RowPaddingBottom;
        if (descrHeight > 0) height += NameToDescriptionGap + descrHeight;
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

        if (hasDescription)
            descr.transform.localPosition = new Vector3(
                textLeft,
                height * 0.5f - RowPaddingTop - nameHeight - NameToDescriptionGap,
                0f);

        var value = slot.txt_realtime;
        if (value != null)
        {
            var showValue = field != null && !string.IsNullOrEmpty(valueText);
            value.gameObject.SetActive(showValue);

            if (showValue)
            {
                ClearAnchors(value);
                value.pivot = UIWidget.Pivot.TopRight;
                value.alignment = NGUIText.Alignment.Right;
                value.fontSize = NameFontSize;
                value.width = valueWidth;
                value.height = 20;
                value.maxLineCount = 1;
                value.overflowMethod = UILabel.Overflow.ClampContent;
                value.color = field.ReadOnly
                    ? new Color(0.68f, 0.68f, 0.68f)
                    : new Color(0.94f, 0.89f, 0.72f);
                value.depth = SettingsValueDepth;
                value.text = valueText;
                value.transform.localPosition = new Vector3(
                    width * 0.5f - 12f - arrows,
                    height * 0.5f - RowPaddingTop,
                    0f);
            }
        }

        if (slot.delete_button != null)
        {
            var button = slot.delete_button;
            var box = button.GetComponent<UI2DSprite>();

            if (hasIndicator && field != null && box != null)
            {
                if (!button.activeSelf) button.SetActive(true);
                EnsureCheckboxSprites();

                var centerX = -width * 0.5f + 10f + CheckboxSize * 0.5f;
                var centerY = height * 0.5f - RowPaddingTop - CheckboxSize * 0.5f;
                var icon = GetOrCreateCheckboxIcon(row, button);

                bool state;
                try
                {
                    state = (bool)field.Read();
                }
                catch
                {
                    state = false;
                }

                var stateSprite = state ? _checkboxOn : _checkboxOff;
                var boxSprite = _checkboxFrame ?? stateSprite;

                ConfigureCheckboxSprite(box, boxSprite, CheckboxSize, 150, centerX, centerY);

                if (icon != null)
                {
                    var iconSprite = _checkboxFrame != null ? stateSprite : null;
                    icon.gameObject.SetActive(iconSprite != null);
                    if (iconSprite != null)
                        ConfigureCheckboxSprite(icon, iconSprite, CheckboxIconSize, 151, centerX, centerY);
                }

                foreach (var collider in button.GetComponents<Collider2D>()) collider.enabled = false;
            }
            else if (!hasIndicator)
            {
                button.SetActive(false);
            }
        }

        foreach (var collider in row.GetComponentsInChildren<Collider2D>(true))
            collider.enabled = false;

        var hitbox = row.GetComponent<BoxCollider2D>();
        if (hitbox == null) hitbox = row.AddComponent<BoxCollider2D>();
        hitbox.size = new Vector2(width, height);
        hitbox.offset = Vector2.zero;
        hitbox.enabled = true;
    }

    /// <summary>
    /// Numbers, choices and text are edited by clicking the value itself; the value gets its own
    /// collider so that click lands on the value instead of toggling the row.
    /// </summary>
    private void AddValueEditTarget(GameObject row, SaveSlotGUI slot, SettingField field)
    {
        if (field.ReadOnly) return;
        if (field.Kind != SettingKind.Number && field.Kind != SettingKind.Text && field.Kind != SettingKind.Choice) return;
        if (slot.txt_realtime == null || !slot.txt_realtime.gameObject.activeSelf) return;

        var target = slot.txt_realtime.gameObject;

        var collider = target.GetComponent<BoxCollider2D>();
        if (collider == null) collider = target.AddComponent<BoxCollider2D>();
        collider.size = new Vector2(Mathf.Max(60, slot.txt_realtime.width), 22f);
        collider.offset = Vector2.zero;
        collider.enabled = true;

        UIEventListener.Get(target).onClick = _ =>
        {
            Sounds.OnGUIClick();
            BeginFieldEdit(field);
        };
    }

    private void AddSettingArrow(SaveSlotGUI slot, GameObject row, string name, Sprite art, float x, float y,
        int size, Action onClick)
    {
        if (slot.delete_button == null) return;

        var existing = row.transform.Find(name);
        var go = existing != null
            ? existing.gameObject
            : UnityEngine.Object.Instantiate(slot.delete_button, row.transform);

        go.name = name;
        go.SetActive(true);

        ClearTriggers(go);

        var sprite = go.GetComponent<UI2DSprite>();
        if (sprite == null) return;

        ConfigureCheckboxSprite(sprite, art, size, SettingsArrowDepth, x, y);

        foreach (var collider in go.GetComponents<Collider2D>()) collider.enabled = false;

        var box = go.GetComponent<BoxCollider2D>();
        if (box == null) box = go.AddComponent<BoxCollider2D>();
        box.size = new Vector2(size + 8f, size + 8f);
        box.offset = Vector2.zero;
        box.enabled = true;

        UIEventListener.Get(go).onClick = _ =>
        {
            Sounds.OnGUIClick();
            onClick();
        };
    }

    /// <summary>
    /// Adds the small arrow that opens a mod's settings on the mod list row. Mods that are not
    /// loaded have no config object, so they get no arrow.
    /// </summary>
    private void ApplyOptionsButton(SaveSlotGUI slot, GameObject row, int width, int height, ModEntry mod)
    {
        if (slot.delete_button == null || !SettingsScanner.HasSettings(mod)) return;

        EnsureArrowSprites();
        EnsureCheckboxSprites();

        var x = width * 0.5f - 14f - OptionsArrowSize * 0.5f;
        var y = height * 0.5f - RowPaddingTop - NameFontSize * 0.5f;
        AddSettingArrow(slot, row, "mod_options", _arrowRight ?? _checkboxOn, x, y, OptionsArrowSize,
            () => OpenSettings(mod));
    }

    private void EnsureArrowSprites()
    {
        if (_arrowSpritesReady) return;
        _arrowSpritesReady = true;

        _arrowLeft = FindGameSprite("trade_wndw_arrow_left");
        _arrowRight = FindGameSprite("trade_wndw_arrow_right");

        if (_arrowLeft == null) _arrowLeft = FindGameSprite("small_arrow");
        if (_arrowRight == null) _arrowRight = FindGameSprite("small_arrow");

        if (_arrowLeft == null)
        {
            _arrowLeft = CreateArrowSprite(false);
            _generatedArrowArt = true;
        }

        if (_arrowRight == null)
        {
            _arrowRight = CreateArrowSprite(true);
            _generatedArrowArt = true;
        }

        if (_generatedArrowArt)
            Plugin.Log.LogWarning("NGUI: arrow sprites not found, using generated chevrons for the arrow buttons.");
    }

    /// <summary>Fallback chevron, used only when the game's own arrow sprites cannot be resolved.</summary>
    private static Sprite CreateArrowSprite(bool pointRight)
    {
        const int size = 18;

        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = pointRight ? "gkmm_arrow_right" : "gkmm_arrow_left"
        };

        var clear = new Color32(0, 0, 0, 0);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            texture.SetPixel(x, y, clear);

        var color = new Color32(0xE0, 0xD2, 0xA6, 0xFF);
        for (var i = 0; i < 6; i++)
        {
            var x = pointRight ? 5 + i : size - 6 - i;
            var top = 8 - i;
            var bottom = 9 + i;
            if (top >= 0) texture.SetPixel(x, top, color);
            if (bottom < size) texture.SetPixel(x, bottom, color);
        }

        texture.Apply(false, false);
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 1f);
    }

    // ---- interaction ------------------------------------------------------

    private void OnSettingActivated(SettingField field)
    {
        try
        {
            Sounds.OnGUIClick();
            if (field.ReadOnly) return;

            switch (field.Kind)
            {
                case SettingKind.Toggle:
                    field.Write(!(bool)field.Read());
                    UpdateFieldRow(field);
                    break;

                case SettingKind.Choice:
                    field.Cycle(1);
                    UpdateFieldRow(field);
                    break;

                case SettingKind.Number:
                    field.StepBy(1);
                    UpdateFieldRow(field);
                    break;

                case SettingKind.Key:
                    BeginKeyCapture(field);
                    break;

                case SettingKind.Text:
                    BeginFieldEdit(field);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"NGUI: could not change setting '{field.Label}': {ex}");
        }
    }

    private void StepField(SettingField field, int direction)
    {
        try
        {
            if (field.Kind == SettingKind.Choice) field.Cycle(direction);
            else field.StepBy(direction);

            UpdateFieldRow(field);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"NGUI: could not change setting '{field.Label}': {ex}");
        }
    }

    private void UpdateFieldRow(SettingField field)
    {
        var label = _fieldLabels.FirstOrDefault(entry => ReferenceEquals(entry.Key, field)).Value;
        if (label != null) label.text = field.CurrentText();

        RefreshPanels();
    }

    private void BeginFieldEdit(SettingField field)
    {
        if (field.ReadOnly) return;

        _listeningField = null;
        _editingField = field;
        _editBuffer = field.Kind == SettingKind.Text
            ? field.Read() as string ?? string.Empty
            : field.CurrentText();

        var label = _fieldLabels.FirstOrDefault(entry => ReferenceEquals(entry.Key, field)).Value;
        if (label != null) label.text = field.EditText(_editBuffer) + "_";

        var row = _fieldRows.FirstOrDefault(entry => ReferenceEquals(entry.Key, field)).Value;
        var slot = row != null ? row.GetComponent<SaveSlotGUI>() : null;
        if (slot != null && slot.txt_descr != null)
            slot.txt_descr.text = Lang.Get("mm.settings.edit.desc");

        RefreshPanels();
    }

    private void BeginKeyCapture(SettingField field)
    {
        _editingField = null;
        _listeningField = field;

        var label = _fieldLabels.FirstOrDefault(entry => ReferenceEquals(entry.Key, field)).Value;
        if (label != null) label.text = Lang.Get("mm.settings.presskey");

        var row = _fieldRows.FirstOrDefault(entry => ReferenceEquals(entry.Key, field)).Value;
        var slot = row != null ? row.GetComponent<SaveSlotGUI>() : null;
        if (slot != null && slot.txt_descr != null)
            slot.txt_descr.text = Lang.Get("mm.settings.presskey.desc");

        RefreshPanels();
    }

    private void CancelFieldEditing()
    {
        if (_editingField == null && _listeningField == null) return;

        var editing = _editingField;
        var listening = _listeningField;
        _editingField = null;
        _listeningField = null;
        _editBuffer = string.Empty;

        if (editing != null) UpdateFieldRow(editing);
        if (listening != null) UpdateFieldRow(listening);

        var row = editing != null
            ? _fieldRows.FirstOrDefault(entry => ReferenceEquals(entry.Key, editing)).Value
            : listening != null
                ? _fieldRows.FirstOrDefault(entry => ReferenceEquals(entry.Key, listening)).Value
                : null;

        var slot = row != null ? row.GetComponent<SaveSlotGUI>() : null;
        var field = editing ?? listening;
        if (slot != null && slot.txt_descr != null && field != null)
            slot.txt_descr.text = field.Description;

        RefreshPanels();
    }

    /// <summary>
    /// Called from the plugin's OnGUI pass. Only runs while a value is being typed or a key bind is
    /// being captured; the event is consumed so nothing else reacts to the keystroke.
    /// </summary>
    internal void HandleKeyboardEvent(Event current)
    {
        if (current == null || current.type != EventType.KeyDown) return;

        if (_editingField != null)
        {
            HandleEditKey(current);
            current.Use();
            return;
        }

        if (_listeningField != null)
        {
            HandleKeyCapture(current);
            current.Use();
        }
    }

    private void HandleEditKey(Event current)
    {
        var field = _editingField;
        if (field == null) return;

        if (current.keyCode == KeyCode.Escape)
        {
            CancelFieldEditing();
            return;
        }

        if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
        {
            CommitFieldEdit();
            return;
        }

        if (current.keyCode == KeyCode.Backspace)
        {
            if (_editBuffer.Length > 0) _editBuffer = _editBuffer.Substring(0, _editBuffer.Length - 1);
            UpdateEditLabel(field);
            return;
        }

        var character = current.character;
        if (character == 0 || char.IsControl(character)) return;
        if (_editBuffer.Length >= 96) return;

        _editBuffer += character;
        UpdateEditLabel(field);
    }

    private void UpdateEditLabel(SettingField field)
    {
        var label = _fieldLabels.FirstOrDefault(entry => ReferenceEquals(entry.Key, field)).Value;
        if (label != null) label.text = field.EditText(_editBuffer) + "_";

        RefreshPanels();
    }

    private void CommitFieldEdit()
    {
        var field = _editingField;
        if (field == null) return;

        var text = _editBuffer;
        _editingField = null;
        _editBuffer = string.Empty;

        try
        {
            if (!field.TryApplyText(text))
            {
                Plugin.Log.LogWarning($"NGUI: '{text}' is not a valid value for '{field.Label}'.");
                GUIElements.me.dialog.OpenOK(string.Format(Lang.Get("mm.settings.invalid"), text, field.Label));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"NGUI: could not apply '{text}' to '{field.Label}': {ex}");
        }

        RestoreRowDescription(field);
        UpdateFieldRow(field);
    }

    private void HandleKeyCapture(Event current)
    {
        var field = _listeningField;
        if (field == null) return;

        if (current.keyCode == KeyCode.Escape)
        {
            CancelFieldEditing();
            return;
        }

        _listeningField = null;

        try
        {
            if (current.keyCode == KeyCode.Backspace || current.keyCode == KeyCode.Delete)
            {
                if (field.ValueType == typeof(KeyboardShortcut))
                    field.Write(KeyboardShortcut.Empty);
                else
                    field.Write(KeyCode.None);
            }
            else if (current.keyCode != KeyCode.None)
            {
                if (field.ValueType == typeof(KeyboardShortcut))
                    field.Write(new KeyboardShortcut(current.keyCode, CurrentModifiers()));
                else
                    field.Write(current.keyCode);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"NGUI: could not bind a key to '{field.Label}': {ex}");
        }

        RestoreRowDescription(field);
        UpdateFieldRow(field);
    }

    private static KeyCode[] CurrentModifiers()
    {
        var modifiers = new List<KeyCode>(3);
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) modifiers.Add(KeyCode.LeftControl);
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) modifiers.Add(KeyCode.LeftShift);
        if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) modifiers.Add(KeyCode.LeftAlt);
        return modifiers.ToArray();
    }

    private void RestoreRowDescription(SettingField field)
    {
        var row = _fieldRows.FirstOrDefault(entry => ReferenceEquals(entry.Key, field)).Value;
        var slot = row != null ? row.GetComponent<SaveSlotGUI>() : null;
        if (slot != null && slot.txt_descr != null) slot.txt_descr.text = field.Description;
    }

    private void DestroySettingArt()
    {
        if (_generatedArrowArt)
        {
            DestroySprite(ref _arrowLeft);
            DestroySprite(ref _arrowRight);
            _generatedArrowArt = false;
        }

        _arrowLeft = null;
        _arrowRight = null;
        _arrowSpritesReady = false;
        _settingsFields.Clear();
        _fieldLabels.Clear();
        _fieldRows.Clear();
        _settingsMod = null;
        _editingField = null;
        _listeningField = null;
        _editBuffer = string.Empty;
    }
}
