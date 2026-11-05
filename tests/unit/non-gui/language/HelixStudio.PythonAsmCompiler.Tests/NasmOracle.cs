using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// The acceptance oracle for the Python-to-NASM compiler: translate a function, assemble it
    /// with NASM, link it against a driver, RUN the executable, and compare its answer with what
    /// CPython computes for the same call.
    ///
    /// Comparing against CPython rather than against an expected constant written by hand is the
    /// whole point. Hand-written expectations encode what the author BELIEVES Python does, and the
    /// places where a naive x86 backend goes wrong — floor division and modulo on mixed signs,
    /// arithmetic shift, short-circuit evaluation order, NaN comparison — are exactly the places
    /// where that belief is most likely to be mistaken.
    ///
    /// Doubles are compared as their raw 64-bit patterns, never as decimal text. Two values that
    /// print the same can differ in the last place, and a compiler that is wrong in the last place
    /// is still wrong.
    /// </summary>
    internal static class NasmOracle
    {
        public static long AssertMatchesCPython(string source, string function, params long[] args)
        {
            return AssertMatches(source, function, args.Select(a => (object)a).ToArray());
        }

        /// <summary>
        /// Compile <paramref name="source"/>, call <paramref name="function"/> with
        /// <paramref name="args"/> (each a <c>long</c> or a <c>double</c>) in the produced
        /// executable, and assert the result equals CPython's. Returns the agreed value — the raw
        /// IEEE bits when the function returns a float.
        /// </summary>
        public static long AssertMatches(string source, string function, params object[] args)
        {
            string missing = NasmToolchain.MissingTool();
            if (missing != null)
                Assert.Inconclusive("execution oracle needs " + missing + "; the emitter was not exercised");

            TranslationResult result = PythonToNasm.Translate(source, function + ".py");
            Assert.IsTrue(result.Succeeded, "translation refused: " + result.RefusalReason);

            TranslatedFunction signature = result.Signatures.FirstOrDefault(s => s.Name == function);
            Assert.IsNotNull(signature, "the translated module does not define '" + function + "'");
            Assert.AreEqual(signature.ParameterTypes.Count, args.Length,
                "the test called '" + function + "' with the wrong number of arguments");

            long native = RunNative(result.Assembly, function, signature, args);
            long expected = RunCPython(source, function, signature, args);
            Assert.AreEqual(expected, native,
                "the assembled code disagrees with CPython for " + function
                + "(" + string.Join(", ", args.Select(Format)) + ")"
                + (signature.ReturnType == "float"
                    ? "; as doubles: expected " + BitConverter.Int64BitsToDouble(expected).ToString("R")
                      + ", got " + BitConverter.Int64BitsToDouble(native).ToString("R")
                    : ""));
            return native;
        }

        /// <summary>
        /// The phase-6 contract, in full: compile, read the assembly back into Python, compile
        /// THAT, run it, and compare with CPython running the ORIGINAL source.
        ///
        /// Comparing the recovered text against the original would be the wrong test — a compiler
        /// legitimately loses <c>+=</c>, <c>elif</c> and <c>while True</c>, none of which changes
        /// what the program computes. What must survive is behaviour. The second decompilation is
        /// asserted to be a fixed point, because a round trip that keeps drifting is one that will
        /// eventually drift into a different program.
        /// </summary>
        public static string AssertRoundTrips(string source, string function, params object[] args)
        {
            string missing = NasmToolchain.MissingTool();
            if (missing != null)
                Assert.Inconclusive("round-trip oracle needs " + missing);

            TranslationResult forward = PythonToNasm.Translate(source, function + ".py");
            Assert.IsTrue(forward.Succeeded, "translation refused: " + forward.RefusalReason);

            DecompileResult back = NasmToPython.Decompile(forward.Assembly);
            Assert.IsTrue(back.Succeeded,
                "decompilation refused: " + back.RefusalReason
                + (back.Line > 0 ? " (asm line " + back.Line + ")" : ""));

            TranslationResult again = PythonToNasm.Translate(back.Source, function + "-recovered.py");
            Assert.IsTrue(again.Succeeded,
                "the recovered source does not compile: " + again.RefusalReason
                + Environment.NewLine + back.Source);

            DecompileResult twice = NasmToPython.Decompile(again.Assembly);
            Assert.IsTrue(twice.Succeeded, "the recovered assembly does not decompile: " + twice.RefusalReason);
            Assert.AreEqual(back.Source, twice.Source,
                "the round trip is not a fixed point; a second pass changed the program");

            TranslatedFunction signature = again.Signatures.FirstOrDefault(s => s.Name == function);
            Assert.IsNotNull(signature, "the recovered module does not define '" + function + "'");

            long native = RunNative(again.Assembly, function, signature, args);
            long expected = RunCPython(source, function, signature, args);
            Assert.AreEqual(expected, native,
                "the recovered program disagrees with the original for " + function
                + "(" + string.Join(", ", args.Select(Format)) + ")" + Environment.NewLine + back.Source);
            return back.Source;
        }

        private static string Format(object value)
        {
            return value is double d
                ? d.ToString("R", CultureInfo.InvariantCulture)
                : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The 64 bits an argument occupies, whatever they mean.</summary>
        private static long Bits(object value)
        {
            return value is double d
                ? BitConverter.DoubleToInt64Bits(d)
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        /// <summary>Assemble, link and execute; returns the value the program printed.</summary>
        public static long RunNative(string assembly, string function, TranslatedFunction signature,
                                     object[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-nasm", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string targetAsm = Path.Combine(dir, "target.asm");
                string driverAsm = Path.Combine(dir, "driver.asm");
                File.WriteAllText(targetAsm, assembly, new UTF8Encoding(false));
                File.WriteAllText(driverAsm, Driver(function, signature, args), new UTF8Encoding(false));

                Assemble(dir, targetAsm, "target.obj");
                Assemble(dir, driverAsm, "driver.obj");

                string exe = Path.Combine(dir, "helix.exe");
                Run(NasmToolchain.Link, dir,
                    "-nologo", "-subsystem:console", "-entry:mainCRTStartup", "-out:" + exe,
                    Path.Combine(dir, "driver.obj"), Path.Combine(dir, "target.obj"),
                    NasmToolchain.Kernel32Lib);

                string stdout = Run(exe, dir);
                long value;
                if (!long.TryParse(stdout.Trim(), NumberStyles.AllowLeadingSign,
                                   CultureInfo.InvariantCulture, out value))
                    Assert.Fail("the program did not print a number; it printed: '" + stdout + "'");
                return value;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { /* the OS still has the exe */ }
            }
        }

        private static void Assemble(string dir, string sourceFile, string objName)
        {
            Run(NasmToolchain.Nasm, dir, "-f", "win64", sourceFile, "-o", Path.Combine(dir, objName));
        }

        /// <summary>What CPython itself answers for the same call — the ground truth.</summary>
        public static long RunCPython(string source, string function, TranslatedFunction signature,
                                      object[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-nasm", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string script = Path.Combine(dir, "oracle.py");
                var text = new StringBuilder(source);
                text.AppendLine();
                text.AppendLine("import struct as _s");
                text.AppendLine("def _d(bits): return _s.unpack('<d', _s.pack('<q', bits))[0]");

                // Arguments travel as raw bit patterns so no decimal spelling sits between the two
                // sides of the comparison.
                var actuals = new string[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    long bits = Bits(args[i]);
                    actuals[i] = signature.ParameterTypes[i] == "float"
                        ? "_d(" + bits.ToString(CultureInfo.InvariantCulture) + ")"
                        : bits.ToString(CultureInfo.InvariantCulture);
                }

                text.AppendLine("_r = " + function + "(" + string.Join(", ", actuals) + ")");
                if (signature.ReturnType == "float")
                {
                    // The compiled function always returns a double, so an int result from Python
                    // is widened the same way the emitted `return` widens it.
                    text.AppendLine("print(_s.unpack('<q', _s.pack('<d', float(_r)))[0])");
                }
                else
                {
                    text.AppendLine("print(1 if _r is True else 0 if _r is False else 0 if _r is None else _r)");
                }
                File.WriteAllText(script, text.ToString(), new UTF8Encoding(false));

                string stdout = Run(NasmToolchain.Python, dir, script).Trim();
                return long.Parse(stdout, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        private static string Run(string exe, string workingDirectory, params string[] arguments)
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = string.Join(" ", arguments.Select(Quote)),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                // Both pipes are drained concurrently. Reading them in sequence deadlocks as soon
                // as a tool writes enough to the pipe we are not reading yet.
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    Assert.Fail(Path.GetFileName(exe) + " did not finish within 120s");
                }
                string stdout = outTask.Result, stderr = errTask.Result;
                if (p.ExitCode != 0)
                    Assert.Fail(Path.GetFileName(exe) + " exited " + p.ExitCode
                                + Environment.NewLine + stdout + Environment.NewLine + stderr);
                return stdout;
            }
        }

        private static string Quote(string argument)
        {
            return argument.IndexOf(' ') >= 0 ? "\"" + argument + "\"" : argument;
        }

        private static readonly string[] ArgRegisters = { "rcx", "rdx", "r8", "r9" };
        private static readonly string[] FloatArgRegisters = { "xmm0", "xmm1", "xmm2", "xmm3" };

        /// <summary>
        /// A freestanding win64 entry point that calls the translated function and prints the
        /// 64-bit result. It writes through WriteFile rather than the CRT so the linked image
        /// depends on nothing but kernel32 — the emitted code must stand on its own.
        ///
        /// A double result is printed as its bit pattern, not as text: formatting a double in
        /// assembly would introduce exactly the rounding this oracle exists to detect.
        /// </summary>
        private static string Driver(string function, TranslatedFunction signature, object[] args)
        {
            var s = new StringBuilder();
            s.AppendLine("default rel");
            s.AppendLine("extern GetStdHandle");
            s.AppendLine("extern WriteFile");
            s.AppendLine("extern ExitProcess");
            s.AppendLine("extern " + function);
            s.AppendLine("global mainCRTStartup");
            s.AppendLine("section .bss");
            s.AppendLine("buf:     resb 32");
            s.AppendLine("written: resq 1");
            s.AppendLine("section .text");
            s.AppendLine("mainCRTStartup:");
            s.AppendLine("    and     rsp, -16");     // the entry point inherits an unknown alignment

            // Room for the callee's shadow space, any stack arguments, and WriteFile's fifth
            // argument later on. Rounded to 16 so RSP is still aligned at every call.
            int outgoing = 8 * Math.Max(4, args.Length);
            int reserve = ((outgoing + 15) / 16) * 16;
            if (reserve < 64) reserve = 64;
            s.AppendLine("    sub     rsp, " + reserve);

            // Stack arguments first: they travel through RAX, which the register arguments below
            // must not have been loaded into yet.
            for (int i = args.Length - 1; i >= ArgRegisters.Length; i--)
            {
                s.AppendLine("    mov     rax, " + Immediate(Bits(args[i])));
                s.AppendLine("    mov     qword [rsp+" + (8 * i) + "], rax");
            }
            for (int i = 0; i < args.Length && i < ArgRegisters.Length; i++)
            {
                s.AppendLine("    mov     rax, " + Immediate(Bits(args[i])));
                s.AppendLine(signature.ParameterTypes[i] == "float"
                    ? "    movq    " + FloatArgRegisters[i] + ", rax"
                    : "    mov     " + ArgRegisters[i] + ", rax");
            }
            s.AppendLine("    call    " + function);
            if (signature.ReturnType == "float") s.AppendLine("    movq    rax, xmm0");

            // RAX -> decimal, written backwards from the end of the buffer.
            s.AppendLine("    lea     rdi, [buf+31]");
            s.AppendLine("    mov     byte [rdi], 10");
            s.AppendLine("    dec     rdi");
            s.AppendLine("    xor     r13, r13");
            s.AppendLine("    test    rax, rax");
            s.AppendLine("    jns     .positive");
            s.AppendLine("    neg     rax");
            s.AppendLine("    mov     r13, 1");
            s.AppendLine(".positive:");
            s.AppendLine("    mov     rcx, 10");
            s.AppendLine(".digits:");
            s.AppendLine("    xor     rdx, rdx");
            s.AppendLine("    div     rcx");
            s.AppendLine("    add     rdx, '0'");
            s.AppendLine("    mov     [rdi], dl");
            s.AppendLine("    dec     rdi");
            s.AppendLine("    test    rax, rax");
            s.AppendLine("    jnz     .digits");
            s.AppendLine("    test    r13, r13");
            s.AppendLine("    jz      .nosign");
            s.AppendLine("    mov     byte [rdi], '-'");
            s.AppendLine("    dec     rdi");
            s.AppendLine(".nosign:");
            s.AppendLine("    inc     rdi");
            s.AppendLine("    lea     rax, [buf+32]");
            s.AppendLine("    sub     rax, rdi");
            s.AppendLine("    mov     r14, rax");
            s.AppendLine("    mov     r15, rdi");
            s.AppendLine("    mov     ecx, -11");     // STD_OUTPUT_HANDLE
            s.AppendLine("    call    GetStdHandle");
            s.AppendLine("    mov     rcx, rax");
            s.AppendLine("    mov     rdx, r15");
            s.AppendLine("    mov     r8, r14");
            s.AppendLine("    lea     r9, [written]");
            s.AppendLine("    mov     qword [rsp+32], 0");
            s.AppendLine("    call    WriteFile");
            s.AppendLine("    xor     ecx, ecx");
            s.AppendLine("    call    ExitProcess");
            return s.ToString();
        }

        private static string Immediate(long bits)
        {
            return "0x" + bits.ToString("X16", CultureInfo.InvariantCulture);
        }
    }
}
