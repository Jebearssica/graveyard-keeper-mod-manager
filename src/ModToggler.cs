using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GKModManager;

internal static class ModToggler
{
    /// <summary>Renames the plugin DLL to/from the .disabled suffix. Returns false with a message on failure.</summary>
    internal static bool TrySetEnabled(ModEntry mod, bool enabled, out string error)
    {
        error = null;

        if (mod.IsManager)
        {
            error = "the manager cannot disable itself";
            return false;
        }

        var basePath = mod.BasePath;
        var enabledPath = basePath;
        var disabledPath = basePath + ModEntry.DisabledSuffix;

        try
        {
            if (enabled)
            {
                if (!File.Exists(enabledPath) && File.Exists(disabledPath))
                    File.Move(disabledPath, enabledPath);

                if (!File.Exists(enabledPath))
                {
                    error = "file not found";
                    return false;
                }
            }
            else
            {
                if (File.Exists(enabledPath))
                    File.Move(enabledPath, disabledPath);

                if (!File.Exists(disabledPath))
                {
                    error = "file not found";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        mod.SetEnabled(enabled);
        return true;
    }

    /// <summary>Enabled mods that declare a hard dependency on <paramref name="mod"/>.</summary>
    internal static List<ModEntry> FindHardDependents(ModEntry mod, IEnumerable<ModEntry> all) =>
        all.Where(other =>
                !ReferenceEquals(other, mod) &&
                other.Enabled &&
                other.Dependencies.Any(dependency =>
                    dependency.Hard &&
                    string.Equals(dependency.Guid, mod.Guid, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>Hard dependencies of <paramref name="mod"/> that are not installed or are disabled.</summary>
    internal static List<string> FindMissingHardDependencies(ModEntry mod, IEnumerable<ModEntry> all)
    {
        var enabled = new HashSet<string>(
            all.Where(entry => entry.Enabled).Select(entry => entry.Guid),
            StringComparer.OrdinalIgnoreCase);

        return mod.Dependencies
            .Where(dependency => dependency.Hard && !enabled.Contains(dependency.Guid))
            .Select(dependency => dependency.Guid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Names of enabled mods that declare <paramref name="guid"/> as an incompatibility.</summary>
    internal static List<ModEntry> FindIncompatible(ModEntry mod, IEnumerable<ModEntry> all) =>
        all.Where(other =>
                !ReferenceEquals(other, mod) &&
                other.Enabled &&
                other.Incompatibilities.Any(guid =>
                    string.Equals(guid, mod.Guid, StringComparison.OrdinalIgnoreCase)))
            .ToList();
}
