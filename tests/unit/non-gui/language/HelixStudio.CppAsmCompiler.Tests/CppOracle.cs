using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.CppAsmCompiler.Tests
{
    /// <summary>
    /// The acceptance oracle for the C++-to-NASM compiler: translate a function, assemble it with
    /// NASM, link it against a driver, RUN the executable, and compare its answer with what MSVC
    /// computes when it compiles the SAME C++ source.
    ///
    /// Comparing against a real C++ compiler rather than against an expected constant written by
    /// hand is the whole point. Hand-written expectations encode what the author BELIEVES C++ does,
    /// and the places where a naive x86 backend goes wrong — truncating division and modulo on
    /// mixed signs, arithmetic vs logical shift, 32-bit overflow, short-circuit evaluation order,
    /// NaN comparison, post- vs pre-increment — are exactly the places where that belief is most
    /// likely to be mistaken.
    ///
    /// Doubles are compared as their raw 64-bit patterns, never as decimal text. Two values that
    /// print the same can differ in the last place, and a compiler that is wrong in the last place
    /// is still wrong.
    /// </summary>
    internal static class CppOracle
    {
        /// <summary>
        /// Compile <paramref name="source"/>, call <paramref name="function"/> with
        /// <paramref name="args"/> (each an <c>int</c> or a <c>double</c>) in the produced
        /// executable, and assert the result equals MSVC's. Returns the agreed value — the raw
        /// IEEE bits when the function returns a double.
        /// </summary>
        public static long AssertMatchesCpp(string source, string function, params object[] args)
        {
            string missing = CppToolchain.MissingTool();
            if (missing != null)
                Assert.Inconclusive("execution oracle needs " + missing + "; the emitter was not exercised");

            TranslationResult result = CppToNasm.Translate(source, function + ".cpp");
            Assert.IsTrue(result.Succeeded, "translation refused: " + result.RefusalReason
                          + (result.Line > 0 ? " (line " + result.Line + ")" : ""));

            TranslatedFunction signature = result.Signatures.FirstOrDefault(s => s.Name == function);
            Assert.IsNotNull(signature, "the translated unit does not define '" + function + "'");
            Assert.AreEqual(signature.Parameters.Count, args.Length,
                "the test called '" + function + "' with the wrong number of arguments");

            long native = RunNative(result.Assembly, function, signature, args);
            long expected = RunCpp(source, function, signature, args);
            Assert.AreEqual(expected, native,
                "the assembled code disagrees with MSVC for " + function
                + "(" + string.Join(", ", args.Select(Format)) + ")"
                + (signature.Returns == "double"
                    ? "; as doubles: expected " + BitConverter.Int64BitsToDouble(expected).ToString("R")
                      + ", got " + BitConverter.Int64BitsToDouble(native).ToString("R")
                    : ""));
            return native;
        }

        /// <summary>
        /// The round-trip contract in full: compile to assembly, read the assembly back into C++,
        /// compile THAT with MSVC, run it, and compare with MSVC running the ORIGINAL source.
        ///
        /// Comparing the recovered TEXT against the original would be the wrong test — a compiler
        /// legitimately loses `for`, `+=` and `else if`, none of which changes what the program
        /// computes. What must survive is behaviour. The second pass is asserted to be a fixed
        /// point, because a round trip that keeps drifting is one that will eventually drift into
        /// a different program.
        /// </summary>
        public static string AssertRoundTripsCpp(string source, string function, params object[] args)
        {
            string missing = CppToolchain.MissingTool();
            if (missing != null) Assert.Inconclusive("round-trip oracle needs " + missing);

            TranslationResult forward = CppToNasm.Translate(source, function + ".cpp");
            Assert.IsTrue(forward.Succeeded, "translation refused: " + forward.RefusalReason
                          + (forward.Line > 0 ? " (line " + forward.Line + ")" : ""));

            DecompileResult back = NasmToCpp.Decompile(forward.Assembly);
            Assert.IsTrue(back.Succeeded,
                "decompilation refused: " + back.RefusalReason
                + (back.Line > 0 ? " (asm line " + back.Line + ")" : "")
                + Environment.NewLine + forward.Assembly);

            TranslationResult again = CppToNasm.Translate(back.Source, function + "-recovered.cpp");
            Assert.IsTrue(again.Succeeded,
                "the recovered source does not re-compile: " + again.RefusalReason
                + (again.Line > 0 ? " (line " + again.Line + ")" : "")
                + Environment.NewLine + back.Source);

            DecompileResult twice = NasmToCpp.Decompile(again.Assembly);
            Assert.IsTrue(twice.Succeeded, "the recovered assembly does not decompile: " + twice.RefusalReason);
            Assert.AreEqual(back.Source, twice.Source,
                "the round trip is not a fixed point; a second pass changed the program."
                + Environment.NewLine + "---- first pass ----" + Environment.NewLine + back.Source
                + Environment.NewLine + "---- second pass ----" + Environment.NewLine + twice.Source);

            TranslatedFunction signature = forward.Signatures.First(s => s.Name == function);
            long expected = RunCpp(source, function, signature, args);
            long actual = RunCpp(back.Source, function, signature, args);
            Assert.AreEqual(expected, actual,
                "the recovered C++ computes something different for " + function
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
            string dir = NewTempDir();
            try
            {
                string targetAsm = Path.Combine(dir, "target.asm");
                string driverAsm = Path.Combine(dir, "driver.asm");
                File.WriteAllText(targetAsm, assembly, new UTF8Encoding(false));
                File.WriteAllText(driverAsm, Driver(function, signature, args), new UTF8Encoding(false));

                Run(CppToolchain.Nasm, dir, "-f", "win64", targetAsm, "-o", Path.Combine(dir, "target.obj"));
                Run(CppToolchain.Nasm, dir, "-f", "win64", driverAsm, "-o", Path.Combine(dir, "driver.obj"));

                string exe = Path.Combine(dir, "helix.exe");
                Run(CppToolchain.Link, dir,
                    "-nologo", "-subsystem:console", "-entry:mainCRTStartup", "-out:" + exe,
                    Path.Combine(dir, "driver.obj"), Path.Combine(dir, "target.obj"),
                    CppToolchain.Kernel32Lib);

                string stdout = Run(exe, dir);
                long value;
                if (!long.TryParse(stdout.Trim(), NumberStyles.AllowLeadingSign,
                                   CultureInfo.InvariantCulture, out value))
                    Assert.Fail("the program did not print a number; it printed: '" + stdout + "'");
                return value;
            }
            finally { TryDelete(dir); }
        }

        /// <summary>What MSVC itself answers for the same call — the ground truth.</summary>
        public static long RunCpp(string source, string function, TranslatedFunction signature,
                                  object[] args)
        {
            string dir = NewTempDir();
            try
            {
                var text = new StringBuilder(source);
                text.AppendLine();
                text.AppendLine("#include <cstdio>");
                text.AppendLine("#include <cstring>");
                text.AppendLine("int main() {");

                // Arguments travel as raw bit patterns so no decimal spelling sits between the two
                // sides of the comparison.
                var actuals = new string[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    long bits = Bits(args[i]);
                    if (signature.Parameters[i] == "double")
                    {
                        text.AppendLine("    unsigned long long b" + i + " = "
                                        + unchecked((ulong)bits).ToString(CultureInfo.InvariantCulture) + "ULL;");
                        text.AppendLine("    double a" + i + "; std::memcpy(&a" + i + ", &b" + i + ", 8);");
                        actuals[i] = "a" + i;
                    }
                    else
                    {
                        actuals[i] = "(int)(" + bits.ToString(CultureInfo.InvariantCulture) + ")";
                    }
                }

                text.AppendLine("    auto r = " + function + "(" + string.Join(", ", actuals) + ");");
                if (signature.Returns == "double")
                {
                    // Printed as bits, never as text: formatting would hide a last-place difference.
                    text.AppendLine("    double d = r; long long o; std::memcpy(&o, &d, 8);");
                    text.AppendLine("    std::printf(\"%lld\\n\", o);");
                }
                else
                {
                    text.AppendLine("    std::printf(\"%lld\\n\", (long long)r);");
                }
                text.AppendLine("    return 0;");
                text.AppendLine("}");

                string cpp = Path.Combine(dir, "oracle.cpp");
                File.WriteAllText(cpp, text.ToString(), new UTF8Encoding(false));

                // cl.exe needs the toolset environment (INCLUDE/LIB) that vcvars64.bat sets, so it
                // is invoked through a shell that sources it first.
                string exe = Path.Combine(dir, "oracle.exe");
                // cl's own diagnostics are NOT redirected: when the oracle fails to build, its
                // message is the only thing that says why, and hiding it turns a one-line fix into
                // a debugging session.
                string command = "call \"" + CppToolchain.VcVars + "\" >nul && cl /nologo /EHsc "
                                 + "\"" + cpp + "\" /Fe:\"" + exe + "\" /Fo:\"" + dir + "\\obj_\"";
                RunShell(command, dir);
                if (!File.Exists(exe)) Assert.Inconclusive("cl.exe did not produce an executable");

                string stdout = Run(exe, dir).Trim();
                return long.Parse(stdout, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
            finally { TryDelete(dir); }
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-cppasm", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* the OS still has the exe */ }
        }

        private static void RunShell(string command, string workingDirectory)
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = "/c " + command,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // vcvars64.bat shells out to vswhere.exe to locate the toolset, and vswhere lives in
            // the VS Installer directory, which is not on PATH in a test host. Without it vcvars
            // fails and cl.exe is never found — a harness failure that looks exactly like a
            // compiler failure.
            string installer = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer";
            if (Directory.Exists(installer))
                psi.EnvironmentVariables["PATH"] = installer + ";" + psi.EnvironmentVariables["PATH"];
            Execute(psi, "cl.exe");
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
            return Execute(psi, Path.GetFileName(exe));
        }

        private static string Execute(ProcessStartInfo psi, string what)
        {
            using (var p = Process.Start(psi))
            {
                // Both pipes are drained concurrently. Reading them in sequence deadlocks as soon
                // as a tool writes enough to the pipe we are not reading yet.
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    Assert.Fail(what + " did not finish within 120s");
                }
                string stdout = outTask.Result, stderr = errTask.Result;
                if (p.ExitCode != 0)
                    Assert.Fail(what + " exited " + p.ExitCode
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
        /// A double result is printed as its bit pattern; an int result is sign-extended out of
        /// EAX first, because the emitted function only defines the low 32 bits.
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
                s.AppendLine(signature.Parameters[i] == "double"
                    ? "    movq    " + FloatArgRegisters[i] + ", rax"
                    : "    mov     " + ArgRegisters[i] + ", rax");
            }
            s.AppendLine("    call    " + function);
            s.AppendLine(signature.Returns == "double" ? "    movq    rax, xmm0"
                                                       : "    movsxd  rax, eax");

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
