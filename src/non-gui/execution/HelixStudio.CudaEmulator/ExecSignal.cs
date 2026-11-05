namespace HelixPythonNamespace.CudaEmulator
{
    /// <summary>
    /// Result of executing a statement. Normal control flow uses this signal rather
    /// than exceptions (exceptions are reserved for RuntimeError faults). Port of the
    /// C++ helix::cuda_emulator::ExecSignal (cuda_emulator/control_flow.h).
    /// </summary>
    public sealed class ExecSignal
    {
        public enum Kind { Normal, Break, Continue, Return }

        public readonly Kind SignalKind;
        public readonly Value ReturnValue;

        private ExecSignal(Kind kind, Value returnValue)
        {
            SignalKind = kind;
            ReturnValue = returnValue;
        }

        public static ExecSignal Normal() { return new ExecSignal(Kind.Normal, new Value()); }
        public static ExecSignal Break() { return new ExecSignal(Kind.Break, new Value()); }
        public static ExecSignal Continue() { return new ExecSignal(Kind.Continue, new Value()); }
        public static ExecSignal Return(Value v) { return new ExecSignal(Kind.Return, v); }

        public bool IsNormal { get { return SignalKind == Kind.Normal; } }
        public bool IsReturn { get { return SignalKind == Kind.Return; } }
    }
}
