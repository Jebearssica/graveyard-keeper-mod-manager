// Compile-time stand-in for what BepInEx.AssemblyPublicizer.MSBuild generates during a
// normal build. Members such as GameSettings._cur_lng stay private in the shipped game,
// so the plugin needs this runtime access-check exemption in every build.

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Assembly-CSharp")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Assembly-CSharp-firstpass")]

namespace System.Runtime.CompilerServices
{
    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : System.Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
        }
    }
}
