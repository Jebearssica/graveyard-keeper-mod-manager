using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace GKModManager;

internal static class ModScanner
{
    private const string PluginAttribute = "BepInEx.BepInPlugin";
    private const string DependencyAttribute = "BepInEx.BepInDependency";
    private const string IncompatibilityAttribute = "BepInEx.BepInIncompatibility";
    private const string DescriptionAttribute = "System.Reflection.AssemblyDescriptionAttribute";

    internal static List<ModEntry> Scan(
        string pluginsRoot,
        string managerGuid,
        ISet<string> loadedGuids,
        IEnumerable<string> assemblySearchPaths,
        Action<string> log)
    {
        var found = new List<ModEntry>();
        if (!Directory.Exists(pluginsRoot))
        {
            log?.Invoke($"Plugin directory not found: {pluginsRoot}");
            return found;
        }

        using var resolver = new DefaultAssemblyResolver();
        if (assemblySearchPaths != null)
        {
            foreach (var directory in assemblySearchPaths)
            {
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    resolver.AddSearchDirectory(directory);
            }
        }

        foreach (var path in EnumeratePluginFiles(pluginsRoot))
        {
            foreach (var entry in ReadEntries(path, managerGuid, resolver, log))
            {
                entry.LoadedThisSession = loadedGuids.Contains(entry.Guid);
                found.Add(entry);
            }
        }

        return Deduplicate(found, log);
    }

    private static IEnumerable<string> EnumeratePluginFiles(string root)
    {
        // BepInEx itself only globs "*.dll", so the .disabled suffix is what keeps a mod unloaded.
        foreach (var path in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
            yield return path;

        foreach (var path in Directory.GetFiles(root, "*.dll.disabled", SearchOption.AllDirectories))
            yield return path;
    }

    private static IEnumerable<ModEntry> ReadEntries(
        string path,
        string managerGuid,
        IAssemblyResolver resolver,
        Action<string> log)
    {
        AssemblyDefinition assembly = null;
        try
        {
            assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false,
                AssemblyResolver = resolver
            });
        }
        catch (BadImageFormatException)
        {
            yield break;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not read {path}: {ex.Message}");
            yield break;
        }

        using (assembly)
        {
            var assemblyDescription = ReadAssemblyDescription(assembly);
            foreach (var type in assembly.MainModule.Types)
            {
                var pluginAttribute = FindAttribute(type.CustomAttributes, PluginAttribute);
                if (pluginAttribute == null) continue;
                if (!IsUnityPlugin(type)) continue;

                var args = pluginAttribute.ConstructorArguments;
                if (args.Count < 3) continue;

                var entry = new ModEntry
                {
                    Guid = args[0].Value as string ?? string.Empty,
                    Name = args[1].Value as string ?? string.Empty,
                    Version = args[2].Value as string ?? string.Empty,
                    AssemblyName = assembly.Name.Name,
                    AssemblyPath = path,
                    TypeName = type.FullName,
                    Description = assemblyDescription,
                    IsManager = string.Equals(args[0].Value as string, managerGuid, StringComparison.OrdinalIgnoreCase)
                };

                if (string.IsNullOrEmpty(entry.Guid)) continue;

                ReadDependencies(type, entry);
                ReadIncompatibilities(type, entry);
                yield return entry;
            }
        }
    }

    private static void ReadDependencies(TypeDefinition type, ModEntry entry)
    {
        foreach (var attribute in type.CustomAttributes)
        {
            if (attribute.AttributeType.FullName != DependencyAttribute) continue;

            var args = attribute.ConstructorArguments;
            if (args.Count == 0) continue;

            var guid = args[0].Value as string;
            if (string.IsNullOrEmpty(guid)) continue;

            var hard = true;
            if (args.Count > 1)
            {
                var flags = Convert.ToInt32(args[1].Value);
                hard = (flags & 1) != 0; // BepInDependency.DependencyFlags.HardDependency
            }

            entry.Dependencies.Add(new ModDependency(guid, hard));
        }
    }

    private static void ReadIncompatibilities(TypeDefinition type, ModEntry entry)
    {
        foreach (var attribute in type.CustomAttributes)
        {
            if (attribute.AttributeType.FullName != IncompatibilityAttribute) continue;

            var args = attribute.ConstructorArguments;
            if (args.Count == 0) continue;

            var guid = args[0].Value as string;
            if (!string.IsNullOrEmpty(guid)) entry.Incompatibilities.Add(guid);
        }
    }

    private static string ReadAssemblyDescription(AssemblyDefinition assembly)
    {
        var attribute = FindAttribute(assembly.CustomAttributes, DescriptionAttribute);
        return attribute?.ConstructorArguments.Count > 0
            ? attribute.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;
    }

    // Mirrors Chainloader.ToPluginInfo: a [BepInPlugin] on a type that is not a
    // BaseUnityPlugin subclass is not a plugin BepInEx would ever load.
    private static bool IsUnityPlugin(TypeDefinition type)
    {
        if (type.IsInterface || type.IsAbstract) return false;

        var current = type;
        for (var depth = 0; depth < 12 && current != null; depth++)
        {
            if (current.FullName == "BepInEx.BaseUnityPlugin") return true;

            var baseReference = current.BaseType;
            if (baseReference == null) return false;

            try
            {
                current = baseReference.Resolve();
            }
            catch
            {
                // Can't resolve an intermediate base (e.g. a dependency outside the
                // search paths); keep the plugin rather than hiding it from the list.
                return true;
            }
        }

        return false;
    }

    private static CustomAttribute FindAttribute(IEnumerable<CustomAttribute> attributes, string fullName) =>
        attributes.FirstOrDefault(attribute => attribute.AttributeType.FullName == fullName);

    private static List<ModEntry> Deduplicate(List<ModEntry> entries, Action<string> log)
    {
        var result = new List<ModEntry>();

        foreach (var group in entries.GroupBy(entry => entry.Guid, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(entry => entry.Enabled)
                .ThenByDescending(entry => ParseVersion(entry.Version))
                .ToList();

            var keep = ordered[0];
            result.Add(keep);

            foreach (var duplicate in ordered.Skip(1))
            {
                log?.Invoke(
                    $"Ignoring duplicate plugin {keep.Guid} at {duplicate.AssemblyPath} (using {keep.AssemblyPath})");
            }
        }

        return result
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var version) ? version : new Version(0, 0);
}
