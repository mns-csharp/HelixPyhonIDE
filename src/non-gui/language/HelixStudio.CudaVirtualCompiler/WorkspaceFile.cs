namespace HelixPythonNamespace.CudaVirtualCompiler
{
    /// <summary>
    /// Per-file entry for CompileSolution. Port of the C++
    /// helix::cuda_vcompiler::WorkspaceFile.
    /// </summary>
    public sealed class WorkspaceFile
    {
        public string FilePath = string.Empty;
        public string TranslationUnitName = string.Empty;  // optional logical name; defaults to FilePath
    }

    /// <summary>
    /// Per-file result returned by CompileSolution. Port of the C++
    /// helix::cuda_vcompiler::CompileFileResult.
    /// </summary>
    public sealed class CompileFileResult
    {
        public string FilePath;
        public string TranslationUnitName;
        public CudaCompileResult Result;

        public CompileFileResult(string filePath, string translationUnitName, CudaCompileResult result)
        {
            FilePath = filePath;
            TranslationUnitName = translationUnitName;
            Result = result;
        }
    }
}
