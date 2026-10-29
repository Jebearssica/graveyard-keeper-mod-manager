using System;

// Minimal stand-in for the localisation call the fields make. Only the keys the editor uses are
// translated; everything else falls back to the key itself.

namespace Shared
{
    internal static class Lang
    {
        internal static string Get(string key)
        {
            switch (key)
            {
                case "mm.settings.on": return "On";
                case "mm.settings.off": return "Off";
                case "mm.settings.presskey": return "Press a key";
                default: return key;
            }
        }
    }
}
