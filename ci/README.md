# Building without the game

The plugin links against `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll` from a
Graveyard Keeper install. Those files cannot be redistributed, and a CI machine does not have
the game, so `ci/refs` contains hand-checked stand-ins: declaration-only classes with the same
names, signatures, enum values and default parameter values as the real ones.

`refs/` only has to contain what the plugin actually links against, which is why it is small.
It is *not* a stub of the game — it is a stub of the ~30 types and ~100 members this one
plugin touches.

## How it is produced

`ci/stubgen` reads a compiled `GKModManager.dll`, walks its IL, and emits the declaration-only
C# for every game type and member referenced from `Assembly-CSharp` and
`Assembly-CSharp-firstpass`:

```powershell
dotnet run --project ci/stubgen -c Release -- `
    src/bin/Release/GKModManager.dll ci/refs `
    "C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper\Graveyard Keeper_Data\Managed" `
    Assembly-CSharp Assembly-CSharp-firstpass
```

Regenerate it whenever the plugin starts using a new game API, then check the result in.

## Using it

```powershell
dotnet build src/GKModManager.csproj -c Release -p:UseGameRefs=false -p:BepInExCore=<BepInEx>/core
```

Unity assemblies come from the `UnityEngine.Modules` NuGet package, BepInEx from an extracted
BepInEx 5 release. Because `ci/refs` replaces the publicized game assemblies, the build also
adds `ci/IgnoresAccessChecksTo.cs`, which is exactly what `BepInEx.AssemblyPublicizer` generates
during a normal build.

## Why it is trustworthy

Rebuilding `GKModManager.dll` against `ci/refs` and against the real game assemblies produces
the same IL, byte for byte, for every type and method. With the same .NET SDK the two builds
differ only in the assembly metadata hash. The stubs are not an approximation: if a signature,
enum value or default argument were wrong, the emitted IL would differ.

If you change `ci/refs` by hand, verify it that way before checking the change in.
