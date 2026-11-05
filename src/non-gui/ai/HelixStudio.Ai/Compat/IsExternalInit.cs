namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marker the C# compiler requires to emit or consume <c>init</c>-only setters.
    ///
    /// It ships in the BCL from net5.0 onward; on net472 it does not exist, so any assembly using
    /// `init` must declare it itself. Without this type the Anthropic SDK's request objects cannot
    /// be constructed at all — the compiler reports
    /// <c>error CS0518: predefined type 'System.Runtime.CompilerServices.IsExternalInit' is not defined</c>.
    ///
    /// Deliberately <c>internal</c>: if two assemblies in one process each declared it publicly the
    /// duplicate definitions would collide. Every project that needs `init` on net472 declares its
    /// own internal copy — that is the documented pattern, not a workaround.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
