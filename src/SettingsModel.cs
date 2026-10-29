using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace GKModManager;

/// <summary>
/// Builds <see cref="SettingField"/> lists from the <see cref="ConfigFile"/> of loaded plugins.
/// Nothing is cached: the config of a plugin can grow at runtime, so the list is rebuilt every
/// time the settings view is opened.
/// </summary>
internal static class SettingsScanner
{
    private const string AttributesTypeName = "ConfigurationManagerAttributes";

    internal static ConfigFile ConfigOf(string guid)
    {
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(guid, out var info))
            {
                info = Chainloader.PluginInfos.Values.FirstOrDefault(p =>
                    string.Equals(p.Metadata != null ? p.Metadata.GUID : null, guid,
                        StringComparison.OrdinalIgnoreCase));
            }

            var instance = info != null ? info.Instance : null;
            return instance != null ? instance.Config : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the mod is loaded and exposes at least one config entry.</summary>
    internal static bool HasSettings(ModEntry mod)
    {
        if (mod == null || mod.DisabledBySuffix) return false;

        var config = ConfigOf(mod.Guid);
        try
        {
            return config != null && ((IDictionary<ConfigDefinition, ConfigEntryBase>)config).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static List<SettingField> Collect(ModEntry mod, Action<string> log)
    {
        if (mod == null) return new List<SettingField>();

        var config = ConfigOf(mod.Guid);
        if (config == null)
        {
            log?.Invoke($"no loaded plugin instance for [{mod.Guid}], settings are unavailable.");
            return new List<SettingField>();
        }

        try
        {
            return CollectFromEntries(((IDictionary<ConfigDefinition, ConfigEntryBase>)config).Values, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"could not read the config of [{mod.Guid}]: {ex.Message}");
            return new List<SettingField>();
        }
    }

    /// <summary>Turns config entries into displayable fields, grouped by section and ordered.</summary>
    internal static List<SettingField> CollectFromEntries(IEnumerable<ConfigEntryBase> source, Action<string> log)
    {
        var fields = new List<SettingField>();

        var ordered = (source ?? Enumerable.Empty<ConfigEntryBase>())
            .Where(entry => entry != null)
            .Select(entry => new { Entry = entry, Meta = ReadMeta(entry) })
            .Where(item => item.Meta.Browsable != false)
            .OrderBy(item => item.Entry.Definition.Section, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Meta.Order ?? 0)
            .ThenBy(item => item.Entry.Definition.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var item in ordered)
        {
            try
            {
                AddFields(fields, item.Entry, item.Meta);
            }
            catch (Exception ex)
            {
                log?.Invoke($"could not describe setting [{item.Entry.Definition}]: {ex.Message}");
            }
        }

        return fields;
    }

    private sealed class Meta
    {
        internal bool? Browsable;
        internal bool? ReadOnly;
        internal int? Order;
        internal string DisplayName;
        internal string Description;
    }

    /// <summary>
    /// Reads the optional ConfigurationManagerAttributes tag. The tag type is defined by whichever
    /// mod created the setting, so it is matched by name and its fields are read by reflection.
    /// </summary>
    private static Meta ReadMeta(ConfigEntryBase entry)
    {
        var meta = new Meta();

        var tags = entry.Description != null ? entry.Description.Tags : null;
        if (tags == null) return meta;

        foreach (var tag in tags)
        {
            if (tag == null) continue;

            var type = tag.GetType();
            if (!string.Equals(type.Name, AttributesTypeName, StringComparison.Ordinal)) continue;

            meta.Browsable = ReadField<bool?>(type, tag, "Browsable") ?? meta.Browsable;
            meta.ReadOnly = ReadField<bool?>(type, tag, "ReadOnly") ?? meta.ReadOnly;
            meta.Order = ReadField<int?>(type, tag, "Order") ?? meta.Order;

            var name = ReadField<string>(type, tag, "DispName");
            if (!string.IsNullOrEmpty(name)) meta.DisplayName = name;

            var description = ReadField<string>(type, tag, "Description");
            if (!string.IsNullOrEmpty(description)) meta.Description = description;
        }

        return meta;
    }

    private static T ReadField<T>(Type type, object instance, string name)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
        if (field == null || field.FieldType != typeof(T)) return default;

        try
        {
            return (T)field.GetValue(instance);
        }
        catch
        {
            return default;
        }
    }

    private static void AddFields(List<SettingField> fields, ConfigEntryBase entry, Meta meta)
    {
        var type = entry.SettingType;
        var label = string.IsNullOrEmpty(meta.DisplayName) ? entry.Definition.Key : meta.DisplayName;
        var description = !string.IsNullOrEmpty(meta.Description)
            ? meta.Description
            : entry.Description != null ? entry.Description.Description : string.Empty;
        var readOnly = meta.ReadOnly == true;

        if (type == typeof(bool))
        {
            AddToggleField(fields, entry, label, description, readOnly, meta.Order ?? 0,
                () => entry.BoxedValue, value => Set(entry, value));
            return;
        }

        if (type == typeof(KeyboardShortcut) || type == typeof(KeyCode))
        {
            fields.Add(new SettingField
            {
                Kind = SettingKind.Key,
                ValueType = type,
                Tag = entry,
                Label = label,
                Description = description,
                ReadOnly = readOnly,
                Order = meta.Order ?? 0,
                Read = () => entry.BoxedValue,
                Write = value => Set(entry, value)
            });
            return;
        }

        if (type.IsEnum && type.GetCustomAttributes(typeof(FlagsAttribute), false).Length > 0)
        {
            AddFlagFields(fields, entry, label, description, readOnly, meta.Order ?? 0);
            return;
        }

        if (TryGetRange(entry, out var min, out var max))
        {
            AddNumberField(fields, entry, label, description, readOnly, meta.Order ?? 0,
                () => entry.BoxedValue, value => Set(entry, value),
                SettingValues.IsInteger(type), min, max, type);
            return;
        }

        if (TryGetList(entry, out var choices))
        {
            AddChoiceField(fields, entry, label, description, readOnly, meta.Order ?? 0, choices,
                () => entry.BoxedValue, value => Set(entry, value));
            return;
        }

        if (type.IsEnum)
        {
            AddChoiceField(fields, entry, label, description, readOnly, meta.Order ?? 0,
                Enum.GetValues(type).Cast<object>().ToArray(),
                () => entry.BoxedValue, value => Set(entry, value));
            return;
        }

        if (type == typeof(Color)) { AddColorFields(fields, entry, label, description, readOnly, meta.Order ?? 0); return; }
        if (type == typeof(Color32)) { AddColor32Fields(fields, entry, label, description, readOnly, meta.Order ?? 0); return; }
        if (type == typeof(Vector3)) { AddVectorFields(fields, entry, label, description, readOnly, meta.Order ?? 0, 3); return; }
        if (type == typeof(Vector2)) { AddVectorFields(fields, entry, label, description, readOnly, meta.Order ?? 0, 2); return; }

        if (SettingValues.IsNumeric(type))
        {
            AddNumberField(fields, entry, label, description, readOnly, meta.Order ?? 0,
                () => entry.BoxedValue, value => Set(entry, value),
                SettingValues.IsInteger(type), null, null, type);
            return;
        }

        if (type == typeof(string))
        {
            fields.Add(new SettingField
            {
                Kind = SettingKind.Text,
                ValueType = type,
                Tag = entry,
                Label = label,
                Description = description,
                ReadOnly = readOnly,
                Order = meta.Order ?? 0,
                Read = () => entry.BoxedValue ?? string.Empty,
                Write = value => Set(entry, value ?? string.Empty)
            });
            return;
        }

        // Types this view cannot edit are still listed, so the user can see that the setting exists
        // and find it in the .cfg file.
        fields.Add(new SettingField
        {
            Kind = SettingKind.Text,
            ValueType = type,
            Tag = entry,
            Label = label,
            Description = string.IsNullOrEmpty(description)
                ? Text("mm.settings.readonly")
                : description + "\n" + Text("mm.settings.readonly"),
            ReadOnly = true,
            Order = meta.Order ?? 0,
            Read = () => entry.BoxedValue,
            Write = value => Set(entry, value)
        });
    }

    private static void AddToggleField(List<SettingField> fields, ConfigEntryBase entry, string label,
        string description, bool readOnly, int order, Func<object> read, Action<object> write)
    {
        fields.Add(new SettingField
        {
            Kind = SettingKind.Toggle,
            ValueType = typeof(bool),
            Tag = entry,
            Label = label,
            Description = description,
            ReadOnly = readOnly,
            Order = order,
            Read = read,
            Write = write
        });
    }

    private static void AddFlagFields(List<SettingField> fields, ConfigEntryBase entry, string label,
        string description, bool readOnly, int order)
    {
        var type = entry.SettingType;

        foreach (var flag in Enum.GetValues(type).Cast<object>())
        {
            var bit = Convert.ToInt64(flag);
            if (bit == 0) continue;

            var captured = flag;
            var capturedBit = bit;

            AddToggleField(fields, entry, label + " \u00b7 " + captured, description, readOnly, order,
                () =>
                {
                    var current = Convert.ToInt64(entry.BoxedValue);
                    return (current & capturedBit) == capturedBit;
                },
                value =>
                {
                    var current = Convert.ToInt64(entry.BoxedValue);
                    var next = (bool)value ? current | capturedBit : current & ~capturedBit;
                    Set(entry, Enum.ToObject(type, next));
                });
        }
    }

    private static void AddColorFields(List<SettingField> fields, ConfigEntryBase entry, string label,
        string description, bool readOnly, int order)
    {
        var names = new[] { "R", "G", "B", "A" };
        for (var channel = 0; channel < names.Length; channel++)
        {
            var index = channel;
            AddNumberField(fields, entry, label + " \u00b7 " + names[channel], description, readOnly, order,
                () =>
                {
                    var color = (Color)entry.BoxedValue;
                    if (index == 0) return color.r * 255.0;
                    if (index == 1) return color.g * 255.0;
                    if (index == 2) return color.b * 255.0;
                    return color.a * 255.0;
                },
                value =>
                {
                    var color = (Color)entry.BoxedValue;
                    var channelValue = Mathf.Clamp(Mathf.RoundToInt((float)Convert.ToDouble(value)), 0, 255) / 255f;
                    if (index == 0) color.r = channelValue;
                    else if (index == 1) color.g = channelValue;
                    else if (index == 2) color.b = channelValue;
                    else color.a = channelValue;
                    Set(entry, color);
                },
                true, 0, 255);
        }
    }

    private static void AddColor32Fields(List<SettingField> fields, ConfigEntryBase entry, string label,
        string description, bool readOnly, int order)
    {
        var names = new[] { "R", "G", "B", "A" };
        for (var channel = 0; channel < names.Length; channel++)
        {
            var index = channel;
            AddNumberField(fields, entry, label + " \u00b7 " + names[channel], description, readOnly, order,
                () =>
                {
                    var color = (Color32)entry.BoxedValue;
                    if (index == 0) return (double)color.r;
                    if (index == 1) return color.g;
                    if (index == 2) return color.b;
                    return color.a;
                },
                value =>
                {
                    var color = (Color32)entry.BoxedValue;
                    var channelValue = (byte)Mathf.Clamp(Mathf.RoundToInt((float)Convert.ToDouble(value)), 0, 255);
                    if (index == 0) color.r = channelValue;
                    else if (index == 1) color.g = channelValue;
                    else if (index == 2) color.b = channelValue;
                    else color.a = channelValue;
                    Set(entry, color);
                },
                true, 0, 255);
        }
    }

    private static void AddVectorFields(List<SettingField> fields, ConfigEntryBase entry, string label,
        string description, bool readOnly, int order, int components)
    {
        var names = new[] { "X", "Y", "Z" };
        for (var component = 0; component < components; component++)
        {
            var index = component;
            AddNumberField(fields, entry, label + " \u00b7 " + names[component], description, readOnly, order,
                () =>
                {
                    var vector = (Vector3)entry.BoxedValue;
                    if (index == 0) return (double)vector.x;
                    if (index == 1) return vector.y;
                    return vector.z;
                },
                value =>
                {
                    var componentValue = (float)Convert.ToDouble(value);
                    if (entry.SettingType == typeof(Vector2))
                    {
                        var vector2 = (Vector2)entry.BoxedValue;
                        if (index == 0) vector2.x = componentValue; else vector2.y = componentValue;
                        Set(entry, vector2);
                        return;
                    }

                    var vector3 = (Vector3)entry.BoxedValue;
                    if (index == 0) vector3.x = componentValue;
                    else if (index == 1) vector3.y = componentValue;
                    else vector3.z = componentValue;
                    Set(entry, vector3);
                },
                false, null, null);
        }
    }

    private static void AddNumberField(List<SettingField> fields, ConfigEntryBase entry, string label,
        string description, bool readOnly, int order, Func<object> read, Action<object> write,
        bool isInteger, double? min, double? max, Type convertTo = null)
    {
        var field = new SettingField
        {
            Kind = SettingKind.Number,
            ValueType = entry.SettingType,
            Tag = entry,
            Label = label,
            Description = description,
            ReadOnly = readOnly,
            Order = order,
            IsInteger = isInteger,
            Read = read,
            // Composite fields (colour channels, vector components) store their own value type;
            // plain numeric entries have to be converted back to the entry's exact type.
            Write = convertTo == null ? write : value => write(SettingValues.ConvertNumber(value, convertTo))
        };

        if (min.HasValue) field.Min = min.Value;
        if (max.HasValue) field.Max = max.Value;

        if (isInteger)
        {
            field.Step = 1;
        }
        else if (min.HasValue && max.HasValue && max.Value > min.Value)
        {
            field.Step = SettingValues.NiceStep((max.Value - min.Value) / 50.0);
        }
        else
        {
            field.Step = 0.1;
        }

        fields.Add(field);
    }

    private static void AddChoiceField(List<SettingField> fields, ConfigEntryBase entry, string label,
        string description, bool readOnly, int order, object[] choices,
        Func<object> read, Action<object> write)
    {
        fields.Add(new SettingField
        {
            Kind = SettingKind.Choice,
            ValueType = entry.SettingType,
            Tag = entry,
            Label = label,
            Description = description,
            ReadOnly = readOnly,
            Order = order,
            Choices = choices,
            Read = read,
            Write = write
        });
    }

    private static string Text(string key)
    {
        try
        {
            return Shared.Lang.Get(key);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void Set(ConfigEntryBase entry, object value)
    {
        entry.BoxedValue = value;

        try
        {
            var file = entry.ConfigFile;
            if (file != null && !file.IsReadOnly) file.Save();
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogWarning($"could not save [{entry.Definition}]: {ex.Message}");
        }
    }

    private static bool TryGetRange(ConfigEntryBase entry, out double min, out double max)
    {
        min = 0;
        max = 0;

        var acceptable = entry.Description != null ? entry.Description.AcceptableValues : null;
        if (acceptable == null) return false;

        var type = acceptable.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(AcceptableValueRange<>)) return false;

        try
        {
            var minProperty = type.GetProperty("MinValue");
            var maxProperty = type.GetProperty("MaxValue");
            if (minProperty == null || maxProperty == null) return false;

            min = Convert.ToDouble(minProperty.GetValue(acceptable, null));
            max = Convert.ToDouble(maxProperty.GetValue(acceptable, null));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetList(ConfigEntryBase entry, out object[] choices)
    {
        choices = null;

        var acceptable = entry.Description != null ? entry.Description.AcceptableValues : null;
        if (acceptable == null) return false;

        var type = acceptable.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(AcceptableValueList<>)) return false;

        try
        {
            var property = type.GetProperty("AcceptableValues");
            var values = property != null ? property.GetValue(acceptable, null) as IEnumerable : null;
            if (values == null) return false;

            var list = values.Cast<object>().ToArray();
            if (list.Length == 0) return false;

            choices = list;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
