using System;
using Shared;

namespace GKModManager;

/// <summary>How a config entry is edited in the native settings view.</summary>
internal enum SettingKind
{
    /// <summary>On/off, drawn as the game's tick/cross.</summary>
    Toggle,

    /// <summary>One of a fixed set of values, stepped through with the arrow buttons.</summary>
    Choice,

    /// <summary>A number, stepped with the arrow buttons or typed into the value field.</summary>
    Number,

    /// <summary>Free text, typed into the value field.</summary>
    Text,

    /// <summary>A key bind, captured from the next key press.</summary>
    Key
}

/// <summary>
/// One editable line in the settings view. Composite settings (colours, vectors, flag enums) are
/// expanded into several fields, each with its own <see cref="Read"/> / <see cref="Write"/> so the
/// view does not need to know about the underlying type.
/// </summary>
/// <remarks>
/// This type is deliberately free of Unity and BepInEx types, so the editing rules can be
/// exercised by ci/settings-tests without a game install.
/// </remarks>
internal sealed class SettingField
{
    internal SettingKind Kind { get; set; }
    internal Type ValueType { get; set; }
    internal object Tag { get; set; }
    internal string Label { get; set; } = string.Empty;
    internal string Description { get; set; } = string.Empty;
    internal bool ReadOnly { get; set; }
    internal int Order { get; set; }

    /// <summary>Fixed set of values for <see cref="SettingKind.Choice"/>.</summary>
    internal object[] Choices { get; set; }

    internal double Min { get; set; } = double.NegativeInfinity;
    internal double Max { get; set; } = double.PositiveInfinity;
    internal double Step { get; set; } = 1.0;
    internal bool IsInteger { get; set; }

    internal Func<object> Read { get; set; }
    internal Action<object> Write { get; set; }

    internal bool HasArrows => !ReadOnly && (Kind == SettingKind.Number || Kind == SettingKind.Choice);

    /// <summary>Value shown right-aligned on the row.</summary>
    internal string CurrentText()
    {
        object value;
        try
        {
            value = Read();
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }

        switch (Kind)
        {
            case SettingKind.Toggle:
                return (bool)value ? Lang.Get("mm.settings.on") : Lang.Get("mm.settings.off");
            case SettingKind.Number:
                return FormatNumber(Convert.ToDouble(value));
            case SettingKind.Key:
                var key = value?.ToString();
                return string.IsNullOrEmpty(key) || key == "None" ? "-" : key;
            default:
                var text = value?.ToString();
                return string.IsNullOrEmpty(text) ? "-" : text;
        }
    }

    private string FormatNumber(double value)
    {
        if (IsInteger) return ((long)Math.Round(value)).ToString();
        if (Math.Abs(value) >= 1000) return value.ToString("0");
        if (Math.Abs(value) >= 10) return value.ToString("0.##");
        if (Math.Abs(value) >= 0.01) return value.ToString("0.###");
        return value.ToString("0.####");
    }

    internal void StepBy(int direction)
    {
        if (Kind != SettingKind.Number || ReadOnly) return;

        var current = Convert.ToDouble(Read());
        var next = current + direction * Step;

        if (!double.IsNegativeInfinity(Min) && next < Min) next = Min;
        if (!double.IsPositiveInfinity(Max) && next > Max) next = Max;
        if (IsInteger) next = Math.Round(next);
        if (Math.Abs(next - current) < double.Epsilon) return;

        Write(next);
    }

    internal void Cycle(int direction)
    {
        if (Kind != SettingKind.Choice || ReadOnly) return;
        if (Choices == null || Choices.Length == 0) return;

        var current = Read();
        var index = Array.FindIndex(Choices, candidate => Equals(candidate, current));
        if (index < 0)
        {
            var text = current?.ToString();
            index = Array.FindIndex(Choices, candidate =>
                string.Equals(candidate?.ToString(), text, StringComparison.Ordinal));
        }

        index = index < 0 ? 0 : (index + direction + Choices.Length) % Choices.Length;
        Write(Choices[index]);
    }

    /// <summary>Parses a typed value and stores it, returning false when the text is not valid.</summary>
    internal bool TryApplyText(string text)
    {
        if (ReadOnly) return false;

        switch (Kind)
        {
            case SettingKind.Text:
                Write(text ?? string.Empty);
                return true;

            case SettingKind.Number:
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) &&
                    !double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.CurrentCulture, out number))
                    return false;

                if (!double.IsNegativeInfinity(Min) && number < Min) number = Min;
                if (!double.IsPositiveInfinity(Max) && number > Max) number = Max;
                if (IsInteger) number = Math.Round(number);
                Write(number);
                return true;

            case SettingKind.Choice:
                var index = Array.FindIndex(Choices ?? Array.Empty<object>(),
                    candidate => string.Equals(candidate?.ToString(), text, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return false;
                Write(Choices[index]);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Text shown in the value column while the field is being edited or key-captured.</summary>
    internal string EditText(string buffer) =>
        Kind == SettingKind.Key ? Lang.Get("mm.settings.presskey") : buffer ?? string.Empty;
}

/// <summary>Value conversions shared by the scanner and the editor.</summary>
internal static class SettingValues{
    /// <summary>
    /// Converts an edited double back to the exact type the config entry stores.
    /// <c>ConfigEntry&lt;T&gt;.BoxedValue</c> casts instead of converting, so an int entry has to be
    /// handed an int and a float entry a float.
    /// </summary>
    internal static object ConvertNumber(object value, Type target)
    {
        var number = Convert.ToDouble(value);

        if (target == typeof(float)) return (float)number;
        if (target == typeof(double)) return number;
        if (target == typeof(decimal)) return (decimal)number;

        return Convert.ChangeType(Math.Round(number), target, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Rounds a raw step up to the next 1/2/5 x 10^n so arrows move in readable values.</summary>
    internal static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 0.1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;

        double multiplier;
        if (normalized <= 1) multiplier = 1;
        else if (normalized <= 2) multiplier = 2;
        else if (normalized <= 5) multiplier = 5;
        else multiplier = 10;

        return multiplier * magnitude;
    }

    internal static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
        type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    internal static bool IsInteger(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
        type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);
}

/// <summary>What a click on a row should do, decided from where on the row it landed.</summary>
internal enum RowAction
{
    /// <summary>No action (read-only row, or a click outside the row).</summary>
    Ignore,

    /// <summary>Flip an on/off state: load/unload a mod, or switch a boolean setting.</summary>
    Toggle,

    /// <summary>Open a mod's options, or start typing the value of a setting.</summary>
    Open,

    /// <summary>Step or cycle the value backwards.</summary>
    Decrease,

    /// <summary>Step or cycle the value forwards.</summary>
    Increase
}

/// <summary>
/// Click zones of one row as fractions of its width, measured from the left edge (0) to the
/// right edge (1). A zone that does not apply is left at its neutral value: 0 for
/// <see cref="ToggleEnd"/>, 1 for <see cref="ArrowsStart"/> and <see cref="ValueStart"/>.
/// </summary>
internal readonly struct RowZones
{
    internal RowZones(double toggleEnd, double arrowsStart, double valueStart, double arrowsMiddle)
    {
        ToggleEnd = toggleEnd;
        ArrowsStart = arrowsStart;
        ValueStart = valueStart;
        ArrowsMiddle = arrowsMiddle;
    }

    internal double ToggleEnd { get; }
    internal double ArrowsStart { get; }
    internal double ValueStart { get; }

    /// <summary>Boundary between the decrease and increase halves of the arrow zone.</summary>
    internal double ArrowsMiddle { get; }

    internal static RowZones ModRow(double toggleEnd) => new RowZones(toggleEnd, 1.0, 1.0, 1.0);
}

/// <summary>
/// Decides what a click does, purely from its horizontal position inside the row. Turned into its
/// own type so the rule "clicking the check box always toggles and never opens the options" can be
/// verified without the game running.
/// </summary>
internal static class RowHitTest
{
    /// <summary>
    /// A click on a mod row: the check box column toggles, the rest opens the options. Rows with
    /// nothing to configure keep toggling everywhere, because there is no options screen to open.
    /// </summary>
    internal static RowAction ResolveModRow(double t, in RowZones zones, bool hasOptions)
    {
        if (double.IsNaN(t) || t < 0.0 || t > 1.0) return RowAction.Ignore;
        if (t <= zones.ToggleEnd) return RowAction.Toggle;
        return hasOptions ? RowAction.Open : RowAction.Toggle;
    }

    /// <summary>A click on a setting row: check box, value column, the two arrows, or the rest.</summary>
    internal static RowAction ResolveSettingRow(double t, in RowZones zones, SettingKind kind, bool readOnly)
    {
        if (readOnly) return RowAction.Ignore;
        if (double.IsNaN(t) || t < 0.0 || t > 1.0) return RowAction.Ignore;

        if (zones.ArrowsStart < 1.0 && t >= zones.ArrowsStart)
            return t < zones.ArrowsMiddle ? RowAction.Decrease : RowAction.Increase;

        if (zones.ToggleEnd > 0.0 && t <= zones.ToggleEnd && kind == SettingKind.Toggle)
            return RowAction.Toggle;

        if (zones.ValueStart < 1.0 && t >= zones.ValueStart &&
            (kind == SettingKind.Number || kind == SettingKind.Text || kind == SettingKind.Choice))
            return RowAction.Open;

        return RowAction.Open;
    }
}
