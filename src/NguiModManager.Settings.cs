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
    private const int SettingsArrowRightPadding = 12;
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
    private Sprite _enterArrow;
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

        var width = Mathf.RoundToInt(RowWidth(row));
        if (width <= 0) width = ScrollContentWidth() - 6;

        DrawSettingArrows(slot, row, field, width);

        UIEventListener.Get(row).onClick = _ => OnSettingRowClicked(field, row, width);
        TrackPress(row);

        _fieldRows.Add(new KeyValuePair<SettingField, GameObject>(field, row));
        if (slot.txt_realtime != null)
            _fieldLabels.Add(new KeyValuePair<SettingField, UILabel>(field, slot.txt_realtime));

        _rows.Add(row);
    }

    /// <summary>
    /// Click zones of a settings row, as fractions of the row width. Layout and hit testing read
    /// the same offsets, so the drawn arrows and the arrow click zones cannot drift apart.
    /// </summary>
    private static RowZones SettingsZones(int width)
    {
        if (width <= 0) return new RowZones(0.0, 1.0, 1.0, 1.0);

        var valueWidth = Mathf.Clamp(width / 3, 70, SettingsValueWidth);

        // Fractions measured from the right edge, so they map straight onto the drawing offsets.
        var arrowsStart = 1.0 - (double)SettingsArrowsLeft / width;
        var arrowsMiddle = 1.0 - (SettingsDecOffset + SettingsIncOffset) / 2.0 / width;
        var valueStart = arrowsStart - (double)valueWidth / width;
        var toggleEnd = (double)ToggleStripWidth / width;

        return new RowZones(
            Mathf.Clamp01((float)toggleEnd),
            Mathf.Clamp01((float)arrowsStart),
            Mathf.Clamp01((float)valueStart),
            Mathf.Clamp01((float)arrowsMiddle));
    }

    /// <summary>Distance from the right edge to the centre of the left (decrease) arrow.</summary>
    private static int SettingsDecOffset => SettingsArrowRightPadding + Mathf.RoundToInt(SettingsArrowSize * 1.5f) + SettingsArrowGap;

    /// <summary>Distance from the right edge to the centre of the right (increase) arrow.</summary>
    private static int SettingsIncOffset => SettingsArrowRightPadding + SettingsArrowSize / 2;

    /// <summary>Distance from the right edge to the left edge of the decrease arrow's box.</summary>
    private static int SettingsArrowsLeft => SettingsDecOffset + SettingsArrowSize / 2;

    /// <summary>
    /// One handler per settings row: the position decides between the check box, the value column,
    /// the two arrows and the row itself.
    /// </summary>
    private void OnSettingRowClicked(SettingField field, GameObject row, int width)
    {
        try
        {
            var action = RowHitTest.ResolveSettingRow(ClickFraction(row), SettingsZones(width), field.Kind,
                field.ReadOnly);

            switch (action)
            {
                case RowAction.Toggle:
                    Sounds.OnGUIClick();
                    field.Write(!(bool)field.Read());
                    UpdateFieldRow(field);
                    break;
                case RowAction.Decrease:
                    Sounds.OnGUIClick();
                    StepField(field, -1);
                    break;
                case RowAction.Increase:
                    Sounds.OnGUIClick();
                    StepField(field, 1);
                    break;
                case RowAction.Open:
                    Sounds.OnGUIClick();
                    ActivateField(field);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"NGUI: could not change setting '{field.Label}': {ex}");
        }
    }

    /// <summary>The row's own action, used when neither the check box nor the arrows were hit.</summary>
    private void ActivateField(SettingField field)
    {
        FinishPendingEdit();
        if (field.ReadOnly) return;

        switch (field.Kind)
        {
            case SettingKind.Toggle:
                field.Write(!(bool)field.Read());
                UpdateFieldRow(field);
                break;
            case SettingKind.Choice:
                // Typing an exact name only makes sense from the value column, which reports Open
                // as well; the row body simply steps forward.
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

    /// <summary>Draws the two stepper arrows. They are decoration; the row handler does the work.</summary>
    private void DrawSettingArrows(SaveSlotGUI slot, GameObject row, SettingField field, int width)
    {
        if (!field.HasArrows || slot.delete_button == null) return;

        EnsureArrowSprites();

        var widget = row.GetComponent<UIWidget>();
        var height = widget != null ? widget.height : RowMinHeight;
        var arrowWidth = SettingsArrowSize;
        var arrowHeight = Mathf.RoundToInt(SettingsArrowSize * 30f / 24f);

        var decX = width * 0.5f - SettingsDecOffset;
        var incX = width * 0.5f - SettingsIncOffset;
        var arrowY = height * 0.5f - RowPaddingTop - arrowHeight * 0.5f + 2f;

        ConfigureArrowVisual(slot, row, "setting_dec", _arrowLeft, decX, arrowY, arrowWidth, arrowHeight);
        ConfigureArrowVisual(slot, row, "setting_inc", _arrowRight, incX, arrowY, arrowWidth, arrowHeight);
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

        // The arrow zone width comes from the same helper the click handler uses, so the drawn
        // arrows and their click zones always line up.
        var arrows = hasArrows ? SettingsArrowsLeft : 0;
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
                    width * 0.5f - arrows,
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

    /// <summary>Creates or updates one of the small arrow sprites. It carries no collider.</summary>
    private void ConfigureArrowVisual(SaveSlotGUI slot, GameObject row, string name, Sprite art,
        float x, float y, int width, int height)
    {
        var existing = row.transform.Find(name);
        var go = existing != null
            ? existing.gameObject
            : UnityEngine.Object.Instantiate(slot.delete_button, row.transform);

        go.name = name;
        go.SetActive(true);

        ClearTriggers(go);
        foreach (var collider in go.GetComponents<Collider2D>()) collider.enabled = false;

        var sprite = go.GetComponent<UI2DSprite>();
        if (sprite == null) return;

        sprite.sprite2D = art;
        ClearAnchors(sprite);
        sprite.width = width;
        sprite.height = height;
        sprite.depth = SettingsArrowDepth;
        sprite.transform.localPosition = new Vector3(x, y, 0f);
    }

    /// <summary>
    /// Draws the chevron that tells the player a mod has options. It is decoration only: the row's
    /// own click handler opens the options, so there is no second hit target to mis-hit.
    /// </summary>
    private void ApplyOptionsHint(SaveSlotGUI slot, GameObject row, int width, int height, bool hasOptions)
    {
        if (slot.delete_button == null) return;

        var existing = row.transform.Find("mod_options");
        if (!hasOptions)
        {
            if (existing != null) existing.gameObject.SetActive(false);
            return;
        }

        EnsureArrowSprites();

        var x = width * 0.5f - 16f - OptionsArrowSize * 0.5f;
        var y = height * 0.5f - RowPaddingTop - NameFontSize * 0.5f - 1f;
        ConfigureArrowVisual(slot, row, "mod_options", _enterArrow, x, y, OptionsArrowSize, OptionsArrowSize);
    }

    private void EnsureArrowSprites()
    {
        if (_arrowSpritesReady) return;
        _arrowSpritesReady = true;

        // Paging chevrons from the trade window, drawn at their native 24:30 aspect.
        _arrowLeft = FindGameSprite("trade_wndw_arrow_left");
        _arrowRight = FindGameSprite("trade_wndw_arrow_right");

        // A solid triangle reads better as "open this", and being square it scales cleanly.
        _enterArrow = FindGameSprite("ui_reply_arrow");

        if (_arrowLeft == null) _arrowLeft = FindGameSprite("small_arrow");
        if (_arrowRight == null) _arrowRight = FindGameSprite("small_arrow");
        if (_enterArrow == null) _enterArrow = _arrowRight;

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

        if (_enterArrow == null)
        {
            _enterArrow = CreateArrowSprite(true);
            _generatedArrowArt = true;
        }

        if (_generatedArrowArt)
            Plugin.Log.LogWarning("NGUI: arrow sprites not found, using generated chevrons for the arrow buttons.");
        else
            Plugin.Log.LogInfo(
                $"NGUI: arrow art left='{_arrowLeft.name}' right='{_arrowRight.name}' enter='{_enterArrow.name}'");
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

    private void StepField(SettingField field, int direction)
    {
        try
        {
            FinishPendingEdit();

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

        FinishPendingEdit();

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

    /// <summary>
    /// Applies whatever is being typed before another action runs, so clicking straight from one
    /// value to another does not silently drop the text that was entered.
    /// </summary>
    private void FinishPendingEdit()
    {
        if (_editingField != null) CommitFieldEdit();
        else if (_listeningField != null) CancelFieldEditing();
    }

    /// <summary>
    /// Esc / gamepad Back: the first press cancels an edit, the second leaves the settings view.
    /// Returns false when the caller should close the window.
    /// </summary>
    private bool HandleBack()
    {
        if (_editingField != null || _listeningField != null)
        {
            CancelFieldEditing();
            return true;
        }

        if (!InSettingsView) return false;

        BackToModList();
        return true;
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
            if (_enterArrow != _arrowRight) DestroySprite(ref _enterArrow);
            _generatedArrowArt = false;
        }

        _arrowLeft = null;
        _arrowRight = null;
        _enterArrow = null;
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
