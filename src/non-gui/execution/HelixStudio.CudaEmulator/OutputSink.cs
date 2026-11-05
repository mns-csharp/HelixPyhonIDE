namespace HelixPythonNamespace.CudaEmulator
{
    /// <summary>
    /// Where emulated printf / messages go. The IDE supplies a sink that streams to
    /// the output console; tests use a StringOutputSink and assert on the captured
    /// text. Implementations must be thread-safe (kernel threads call Write). Port of
    /// the C++ helix::cuda_emulator::OutputSink (cuda_emulator/output_sink.h).
    /// </summary>
    public interface IOutputSink
    {
        void Write(string text);
    }
}
