using System;
using System.Collections.Generic;
using System.Globalization;
using HelixPythonNamespace.CudaEmulator;
using HelixPythonNamespace.CudaVirtualCompiler;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;
using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.CudaBridge
{
    /// <summary>
    /// One emulator plus the device memory a single Python process has allocated in it.
    ///
    /// <para>
    /// The state is the whole reason this is a long-lived session rather than a command per
    /// launch. Real CUDA code allocates once, copies up, launches several kernels over the same
    /// buffers, and copies down at the end; a fresh emulator per launch would lose the device
    /// memory between steps, so every program that does more than one thing would silently
    /// compute from zeroes.
    /// </para>
    /// <para>
    /// Modules are cached by source text. A Python program that builds a kernel inside a loop --
    /// which is exactly what the runtime-assembly style does -- would otherwise recompile the same
    /// source on every iteration, and compilation dominates the cost of a small kernel.
    /// </para>
    /// </summary>
    internal sealed class BridgeSession
    {
        private readonly Dictionary<string, ModuleEntry> _modulesBySource =
            new Dictionary<string, ModuleEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, ModuleEntry> _modulesByHandle =
            new Dictionary<string, ModuleEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Pointer> _pointers =
            new Dictionary<string, Pointer>(StringComparer.Ordinal);

        private readonly StringOutputSink _sink = new StringOutputSink();
        private CudaEmulator.CudaEmulator _emulator;
        private int _reportedOutput;
        private int _nextModule;
        private int _nextPointer;

        private sealed class ModuleEntry
        {
            public string Handle;
            public string Source;
            public CudaCompileResult Compiled;
        }

        /// <summary>
        /// The IDE's kernel debugger, when one is attached. Null for every ordinary run, and the
        /// launch path is written so that null costs nothing: no hook is installed, so the emulator
        /// never makes a per-statement callback.
        /// </summary>
        public KernelDebugChannel Debug { get; set; }

        /// <summary>
        /// Everything the emulator has printed since the last call.
        ///
        /// StringOutputSink only accumulates -- it has no reset -- so the read position is kept
        /// here. Returning the whole buffer each time would repeat every earlier kernel's printf
        /// on every subsequent launch.
        /// </summary>
        public string DrainOutput()
        {
            string all = _sink.Text ?? string.Empty;
            if (_reportedOutput >= all.Length) return string.Empty;
            string fresh = all.Substring(_reportedOutput);
            _reportedOutput = all.Length;
            return fresh;
        }

        // ---- modules ---------------------------------------------------------

        /// <summary>
        /// Compile CUDA source and return a handle, or the diagnostics that stopped it.
        ///
        /// <para>
        /// The emulator is created from the FIRST module compiled and then reused, because the
        /// device memory lives inside it. A program that loads a second kernel module keeps the
        /// buffers it already allocated -- which is what CUDA does, and what a program that
        /// uploads once and then launches two different kernels over the same data requires.
        /// </para>
        /// </summary>
        public BridgeResponse CompileModule(string source)
        {
            if (string.IsNullOrEmpty(source))
                return BridgeResponse.Failure("empty kernel source");

            ModuleEntry cached;
            if (_modulesBySource.TryGetValue(source, out cached))
                return BridgeResponse.Ok().With("module", cached.Handle).With("cached", true);

            CudaCompileResult compiled;
            try { compiled = CudaVirtualCompiler.CudaVirtualCompiler.CompileSource(source, "<python>"); }
            catch (Exception ex) { return BridgeResponse.Failure("compile threw: " + ex.Message); }

            var errors = new List<string>();
            if (compiled != null && compiled.Diagnostics != null)
                foreach (CudaCompileDiagnostic diagnostic in compiled.Diagnostics)
                    if (diagnostic.Severity >= HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error)
                        errors.Add(diagnostic.ToString());

            if (errors.Count > 0)
                return BridgeResponse.Failure("kernel did not compile").With("diagnostics", errors);

            var entry = new ModuleEntry
            {
                Handle = "m" + _nextModule.ToString(CultureInfo.InvariantCulture),
                Source = source,
                Compiled = compiled,
            };
            _nextModule++;
            _modulesBySource[source] = entry;
            _modulesByHandle[entry.Handle] = entry;

            if (_emulator == null)
                _emulator = new CudaEmulator.CudaEmulator(compiled, _sink);

            return BridgeResponse.Ok().With("module", entry.Handle).With("cached", false);
        }

        // ---- device memory ---------------------------------------------------

        public BridgeResponse Malloc(long bytes)
        {
            if (bytes < 0) return BridgeResponse.Failure("negative allocation");
            if (_emulator == null) return BridgeResponse.Failure("no module compiled yet");

            Pointer pointer = _emulator.Mem.Allocate(bytes, MemSpace.Device, ElemType.Byte, 1, "py");
            string handle = "d" + _nextPointer.ToString(CultureInfo.InvariantCulture);
            _nextPointer++;
            _pointers[handle] = pointer;
            return BridgeResponse.Ok().With("ptr", handle);
        }

        public BridgeResponse Free(string handle)
        {
            Pointer pointer;
            if (!_pointers.TryGetValue(handle ?? string.Empty, out pointer))
                return BridgeResponse.Failure("unknown pointer: " + handle);
            try { _emulator.Mem.Free(pointer, SourceSpan.Empty); }
            catch (Exception ex) { return BridgeResponse.Failure("free failed: " + ex.Message); }
            _pointers.Remove(handle);
            return BridgeResponse.Ok();
        }

        /// <summary>Host -> device: raw bytes straight into the buffer's backing array.</summary>
        public BridgeResponse CopyToDevice(string handle, byte[] data)
        {
            byte[] target = BufferBytes(handle);
            if (target == null) return BridgeResponse.Failure("unknown pointer: " + handle);
            if (data == null) data = new byte[0];
            if (data.Length > target.Length)
                return BridgeResponse.Failure("copy of " + data.Length + " bytes into a "
                    + target.Length + "-byte allocation");
            Array.Copy(data, target, data.Length);
            return BridgeResponse.Ok().With("bytes", data.Length);
        }

        /// <summary>Device -> host.</summary>
        public BridgeResponse CopyToHost(string handle, int bytes)
        {
            byte[] source = BufferBytes(handle);
            if (source == null) return BridgeResponse.Failure("unknown pointer: " + handle);
            int count = bytes <= 0 || bytes > source.Length ? source.Length : bytes;
            var copy = new byte[count];
            Array.Copy(source, copy, count);
            return BridgeResponse.Ok().With("data", Convert.ToBase64String(copy));
        }

        private byte[] BufferBytes(string handle)
        {
            Pointer pointer;
            if (_emulator == null || !_pointers.TryGetValue(handle ?? string.Empty, out pointer))
                return null;
            CudaEmulator.Buffer buffer = _emulator.Mem.Find(pointer.Buffer);
            return buffer == null || buffer.Freed ? null : buffer.Bytes;
        }

        // ---- launch ----------------------------------------------------------

        /// <summary>
        /// Launch <paramref name="kernelName"/> from <paramref name="moduleHandle"/> over the given
        /// grid and block, binding <paramref name="arguments"/> in order.
        /// </summary>
        public BridgeResponse Launch(string moduleHandle, string kernelName,
            Dim3 grid, Dim3 block, IList<BridgeArgument> arguments, long dynamicSharedBytes)
        {
            ModuleEntry module;
            if (!_modulesByHandle.TryGetValue(moduleHandle ?? string.Empty, out module))
                return BridgeResponse.Failure("unknown module: " + moduleHandle);
            if (_emulator == null) return BridgeResponse.Failure("no emulator");

            FunctionDecl kernel = FindKernel(module.Compiled, kernelName);
            if (kernel == null)
                return BridgeResponse.Failure("no kernel named '" + kernelName + "' in that module");

            var bound = new List<Value>();
            for (int i = 0; i < arguments.Count; i++)
            {
                BridgeArgument argument = arguments[i];
                if (argument.IsPointer)
                {
                    Pointer pointer;
                    if (!_pointers.TryGetValue(argument.PointerHandle ?? string.Empty, out pointer))
                        return BridgeResponse.Failure("argument " + i + ": unknown pointer "
                            + argument.PointerHandle);
                    // Re-typed to the parameter's element type. A buffer is allocated as raw bytes
                    // because Python asked for a size, not a type; the kernel's own signature is
                    // what says whether those bytes are ints or floats, and indexing it as the
                    // wrong type reads the right memory the wrong way.
                    pointer.ElemType = ParameterElementType(kernel, i, out pointer.ElemSize);
                    bound.Add(new Value(pointer));
                }
                else if (argument.IsFloat) bound.Add(new Value(argument.DoubleValue));
                else bound.Add(new Value(argument.LongValue));
            }

            // Ask the debugger whether this launch is interesting. It answers with a hook only
            // when something could actually stop it -- a breakpoint that maps into this module, or
            // a standing request to break on kernel entry -- so an attached debugger costs a
            // kernel with no breakpoints in it nothing at all.
            IDebugHook hook = null;
            Dim3 debugThread = default(Dim3);
            Dim3 debugBlock = default(Dim3);
            KernelDebugChannel debug = Debug;
            if (debug != null && debug.IsAttached)
            {
                hook = debug.BeginKernel(moduleHandle, module.Source, kernelName, grid, block);
                debugThread = debug.DebugThread;
                debugBlock = debug.DebugBlock;
            }

            EmulationResult result;
            try
            {
                result = _emulator.LaunchKernel(kernel, grid, block, bound, dynamicSharedBytes,
                                                hook, debugThread, debugBlock);
            }
            catch (Exception ex) { return BridgeResponse.Failure("launch threw: " + ex.Message); }
            finally { if (hook != null && debug != null) debug.EndKernel(kernelName); }

            if (result == null || !result.Ok)
            {
                string reason = result != null && result.Error != null
                    ? result.Error.ErrorMessage
                    : "kernel failed";
                return BridgeResponse.Failure(reason).With("output", DrainOutput());
            }
            return BridgeResponse.Ok().With("output", DrainOutput());
        }

        private static FunctionDecl FindKernel(CudaCompileResult compiled, string name)
        {
            if (compiled == null || compiled.SymbolTable == null) return null;
            List<Symbol> symbols = compiled.SymbolTable.FindFunctions(name ?? string.Empty);
            for (int i = 0; i < symbols.Count; i++)
                if (symbols[i].FuncDecl != null && symbols[i].FuncDecl.HasBody)
                    return symbols[i].FuncDecl;
            return null;
        }

        /// <summary>
        /// The element type of the kernel's <paramref name="index"/>-th parameter, so a device
        /// buffer is indexed as what the kernel declares it to be. Falls back to 4-byte ints, the
        /// commonest case, when the signature cannot be read.
        /// </summary>
        private static ElemType ParameterElementType(FunctionDecl kernel, int index, out uint elemSize)
        {
            elemSize = 4;
            if (kernel == null || kernel.Params == null || index >= kernel.Params.Count)
                return ElemType.Int32;

            CxxType type = kernel.Params[index].Type;
            string text = (type != null ? type.Base ?? string.Empty : string.Empty).ToLowerInvariant();
            if (text.Contains("double")) { elemSize = 8; return ElemType.Float64; }
            if (text.Contains("float")) { elemSize = 4; return ElemType.Float32; }
            if (text.Contains("long")) { elemSize = 8; return ElemType.Int64; }
            if (text.Contains("short")) { elemSize = 2; return ElemType.Int16; }
            if (text.Contains("char") || text.Contains("byte")) { elemSize = 1; return ElemType.Byte; }
            elemSize = 4;
            return ElemType.Int32;
        }
    }

    /// <summary>One kernel argument as the Python side described it.</summary>
    internal sealed class BridgeArgument
    {
        public bool IsPointer;
        public string PointerHandle;
        public bool IsFloat;
        public double DoubleValue;
        public long LongValue;
    }
}
