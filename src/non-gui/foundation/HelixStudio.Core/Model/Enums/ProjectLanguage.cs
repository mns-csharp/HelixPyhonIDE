namespace HelixPythonNamespace.Core.Model.Enums
{
    /// <summary>
    /// Language a <see cref="ProjectModel"/>'s source is written in. Drives which
    /// analysis/rename/scaffolding services a caller should use for that project
    /// (see <c>HelixPythonNamespace.Workspace.Python.*</c> vs <c>HelixPythonNamespace.Workspace.Cuda.Cpp*</c>).
    /// </summary>
    public enum ProjectLanguage
    {
        Python = 0,

        /// <summary>
        /// A native C++/CUDA-C project imported from a CMakeLists.txt (see
        /// <see cref="ProjectKind.CudaExecutable"/>/<see cref="ProjectKind.CudaLibrary"/>).
        /// Covers BOTH plain C++ and CUDA targets — whether a given project actually contains
        /// CUDA kernels is a per-project property (presence of .cu/.cuh sources), not a separate
        /// enum value, so every existing <c>== Cpp</c> dispatch stays valid for both.
        /// </summary>
        Cpp = 1,

        // Rust = 2, CSharp = 3, Java = 4 and Julia = 5 were removed when HelixPython was carved
        // out as the Python product (see PRODUCT.md): they ship in HelixRust / HelixCSharp /
        // HelixJava / HelixJulia, and their workspace engines were never forked into this tree.
        // The numeric values stay retired rather than being reused, so a solution file written by
        // the combined build cannot silently reappear here as the wrong language.
    }
}
