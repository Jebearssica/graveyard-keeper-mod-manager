using Shared;

namespace GKModManager;

internal static class Descriptions
{
    internal static string For(ModEntry mod)
    {
        if (Lang.TryGet($"mod.{mod.Guid}.desc", out var localized) && !string.IsNullOrWhiteSpace(localized))
            return localized;

        // Every mod in this collection stamps its project name into AssemblyDescription,
        // which tells the player nothing - only use it when it is actually descriptive.
        if (!string.IsNullOrWhiteSpace(mod.Description) &&
            !string.Equals(mod.Description, mod.AssemblyName, System.StringComparison.OrdinalIgnoreCase))
            return mod.Description;

        return Lang.Get("mm.desc.none");
    }

    internal static string Category(ModEntry mod) =>
        Lang.TryGet($"mod.{mod.Guid}.category", out var category) ? category : string.Empty;

    internal static string Url(ModEntry mod) =>
        Lang.TryGet($"mod.{mod.Guid}.url", out var url) ? url : string.Empty;
}
