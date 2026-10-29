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
internal static class SettingValues
{
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
