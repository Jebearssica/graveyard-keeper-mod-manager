using System;
using System.Collections.Generic;
using System.IO;

namespace GKModManager;

internal sealed class ModEntry
{
    internal const string DisabledSuffix = ".disabled";

    internal string Guid { get; set; } = string.Empty;
    internal string Name { get; set; } = string.Empty;
    internal string Version { get; set; } = string.Empty;
    internal string AssemblyName { get; set; } = string.Empty;
    internal string AssemblyPath { get; set; } = string.Empty;
    internal string TypeName { get; set; } = string.Empty;
    internal string Description { get; set; } = string.Empty;
    internal bool IsManager { get; set; }
    internal bool LoadedThisSession { get; set; }
    internal List<ModDependency> Dependencies { get; } = new();
    internal List<string> Incompatibilities { get; } = new();

    internal bool DisabledBySuffix =>
        AssemblyPath.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    internal bool Enabled => !DisabledBySuffix;

    internal string FileName => Path.GetFileName(AssemblyPath);

    internal string BasePath => DisabledBySuffix
        ? AssemblyPath.Substring(0, AssemblyPath.Length - DisabledSuffix.Length)
        : AssemblyPath;

    internal string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Path.GetFileNameWithoutExtension(BasePath)
        : Name.TrimStart('!');

    internal string SearchText => $"{DisplayName}\n{Guid}\n{FileName}\n{Description}";

    internal void SetEnabled(bool enabled)
    {
        var basePath = BasePath;
        AssemblyPath = enabled ? basePath : basePath + DisabledSuffix;
    }
}

internal readonly struct ModDependency
{
    internal ModDependency(string guid, bool hard)
    {
        Guid = guid;
        Hard = hard;
    }

    internal string Guid { get; }
    internal bool Hard { get; }
}
