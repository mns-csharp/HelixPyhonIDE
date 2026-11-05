using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.CudaEmulator.Tests
{
    /// <summary>
    /// The bridge process a Python program talks to when it has no GPU
    /// (<c>helix_cuda_bridge.exe</c>), driven over its real stdin/stdout protocol.
    ///
    /// <para>
    /// Exercised as a PROCESS rather than by calling the session class directly, because the
    /// things most likely to break are the things only a process has: the line framing, the
    /// base64 round trip, and the rule that stdout carries protocol and nothing else. A kernel's
    /// <c>printf</c> written to stdout instead of returned inside a reply would desynchronise the
    /// stream, and a unit test of the session object would never see it.
    /// </para>
    /// </summary>
    [TestClass]
    public class CudaBridgeProtocolTests
    {
        private Process _bridge;

        private static string BridgeExecutable()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                {
                    string path = Path.Combine(dir.FullName, "artifacts", "bin",
                        "HelixStudio.CudaBridge", "Debug", "net472", "helix_cuda_bridge.exe");
                    return File.Exists(path) ? path : null;
                }
                dir = dir.Parent;
            }
            return null;
        }

        [TestInitialize]
        public void Start()
        {
            string executable = BridgeExecutable();
            if (executable == null)
                Assert.Inconclusive("helix_cuda_bridge.exe is not built; build the solution first.");

            _bridge = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                },
            };
            _bridge.Start();
        }

        [TestCleanup]
        public void Stop()
        {
            if (_bridge == null) return;
            try
            {
                if (!_bridge.HasExited)
                {
                    _bridge.StandardInput.WriteLine("{\"op\":\"quit\"}");
                    if (!_bridge.WaitForExit(4000)) _bridge.Kill();
                }
            }
            catch { }
            finally { _bridge.Dispose(); _bridge = null; }
        }

        private JObject Call(string json)
        {
            _bridge.StandardInput.WriteLine(json);
            _bridge.StandardInput.Flush();
            string line = _bridge.StandardOutput.ReadLine();
            Assert.IsNotNull(line, "the bridge exited without replying");
            return JObject.Parse(line);
        }

        private static string Base64Ints(params int[] values)
        {
            var bytes = new byte[values.Length * 4];
            System.Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        private static int[] IntsFromBase64(string text, int count)
        {
            byte[] bytes = Convert.FromBase64String(text);
            var values = new int[count];
            System.Buffer.BlockCopy(bytes, 0, values, 0, count * 4);
            return values;
        }

        private const string TripleKernel =
            "extern \\\"C\\\" __global__ void triple(int* v)"
            + "{ int i = blockIdx.x*blockDim.x + threadIdx.x; v[i] = v[i]*3; }";

        [TestMethod]
        public void Ping_NamesTheEmulator()
        {
            JObject reply = Call("{\"op\":\"ping\"}");
            Assert.IsTrue((bool)reply["ok"]);
            StringAssert.Contains((string)reply["emulator"], "Emulator");
        }

        [TestMethod]
        public void AKernelRunsOverEveryLaneOfTheGrid()
        {
            Assert.IsTrue((bool)Call("{\"op\":\"module\",\"source\":\"" + TripleKernel + "\"}")["ok"]);
            Assert.IsTrue((bool)Call("{\"op\":\"malloc\",\"bytes\":32}")["ok"]);
            Assert.IsTrue((bool)Call("{\"op\":\"upload\",\"ptr\":\"d0\",\"data\":\""
                + Base64Ints(1, 2, 3, 4, 5, 6, 7, 8) + "\"}")["ok"]);

            JObject launch = Call("{\"op\":\"launch\",\"module\":\"m0\",\"kernel\":\"triple\","
                + "\"grid\":[2,1,1],\"block\":[4,1,1],\"args\":[{\"ptr\":\"d0\"}]}");
            Assert.IsTrue((bool)launch["ok"], (string)launch["error"]);

            JObject download = Call("{\"op\":\"download\",\"ptr\":\"d0\",\"bytes\":32}");
            CollectionAssert.AreEqual(new[] { 3, 6, 9, 12, 15, 18, 21, 24 },
                IntsFromBase64((string)download["data"], 8),
                "2 blocks x 4 threads should have covered all eight elements");
        }

        [TestMethod]
        public void DeviceMemorySurvivesBetweenLaunches()
        {
            // The reason the bridge is a long-lived session at all. Two kernels over one buffer is
            // the ordinary shape of GPU code; if the emulator were rebuilt per launch the second
            // kernel would read zeroes and nothing would report it.
            Call("{\"op\":\"module\",\"source\":\"" + TripleKernel + "\"}");
            Call("{\"op\":\"malloc\",\"bytes\":32}");
            Call("{\"op\":\"upload\",\"ptr\":\"d0\",\"data\":\"" + Base64Ints(1, 1, 1, 1, 1, 1, 1, 1) + "\"}");

            for (int i = 0; i < 3; i++)
                Assert.IsTrue((bool)Call("{\"op\":\"launch\",\"module\":\"m0\",\"kernel\":\"triple\","
                    + "\"grid\":[2,1,1],\"block\":[4,1,1],\"args\":[{\"ptr\":\"d0\"}]}")["ok"]);

            int[] values = IntsFromBase64((string)Call("{\"op\":\"download\",\"ptr\":\"d0\",\"bytes\":32}")["data"], 8);
            foreach (int value in values)
                Assert.AreEqual(27, value, "three launches of x3 over a persistent buffer");
        }

        [TestMethod]
        public void APointerIsTypedByTheKernelsOwnSignature()
        {
            // The buffer is allocated as raw BYTES -- Python asked for a size, not a type. Only the
            // kernel's parameter says whether those bytes are floats, and reading them as the
            // wrong type gives the right memory interpreted wrongly, which looks like arithmetic
            // that is subtly off rather than like an error.
            const string scale =
                "extern \\\"C\\\" __global__ void scale(float* v)"
                + "{ int i = blockIdx.x*blockDim.x + threadIdx.x; v[i] = v[i] * 2.0f; }";
            Call("{\"op\":\"module\",\"source\":\"" + scale + "\"}");
            Call("{\"op\":\"malloc\",\"bytes\":16}");

            var floats = new float[] { 1.5f, 2.5f, 3.5f, 4.5f };
            var bytes = new byte[16];
            System.Buffer.BlockCopy(floats, 0, bytes, 0, 16);
            Call("{\"op\":\"upload\",\"ptr\":\"d0\",\"data\":\"" + Convert.ToBase64String(bytes) + "\"}");

            Assert.IsTrue((bool)Call("{\"op\":\"launch\",\"module\":\"m0\",\"kernel\":\"scale\","
                + "\"grid\":[1,1,1],\"block\":[4,1,1],\"args\":[{\"ptr\":\"d0\"}]}")["ok"]);

            byte[] back = Convert.FromBase64String((string)Call("{\"op\":\"download\",\"ptr\":\"d0\",\"bytes\":16}")["data"]);
            var result = new float[4];
            System.Buffer.BlockCopy(back, 0, result, 0, 16);
            CollectionAssert.AreEqual(new[] { 3.0f, 5.0f, 7.0f, 9.0f }, result);
        }

        [TestMethod]
        public void ScalarArgumentsKeepTheirIntOrFloatNess()
        {
            const string axpy =
                "extern \\\"C\\\" __global__ void axpy(float a, float* v, int n)"
                + "{ int i = blockIdx.x*blockDim.x + threadIdx.x; if (i < n) v[i] = a * v[i]; }";
            Call("{\"op\":\"module\",\"source\":\"" + axpy + "\"}");
            Call("{\"op\":\"malloc\",\"bytes\":16}");

            var floats = new float[] { 1f, 2f, 3f, 4f };
            var bytes = new byte[16];
            System.Buffer.BlockCopy(floats, 0, bytes, 0, 16);
            Call("{\"op\":\"upload\",\"ptr\":\"d0\",\"data\":\"" + Convert.ToBase64String(bytes) + "\"}");

            Assert.IsTrue((bool)Call("{\"op\":\"launch\",\"module\":\"m0\",\"kernel\":\"axpy\","
                + "\"grid\":[1,1,1],\"block\":[4,1,1],"
                + "\"args\":[{\"f\":0.5},{\"ptr\":\"d0\"},{\"i\":4}]}")["ok"]);

            byte[] back = Convert.FromBase64String((string)Call("{\"op\":\"download\",\"ptr\":\"d0\",\"bytes\":16}")["data"]);
            var result = new float[4];
            System.Buffer.BlockCopy(back, 0, result, 0, 16);
            CollectionAssert.AreEqual(new[] { 0.5f, 1.0f, 1.5f, 2.0f }, result,
                "0.5 must arrive as a float; as an integer 0 it would zero the array");
        }

        [TestMethod]
        public void AKernelThatDoesNotCompile_ReportsDiagnosticsRatherThanDying()
        {
            JObject reply = Call("{\"op\":\"module\",\"source\":\"__global__ void broken( { }\"}");
            Assert.IsFalse((bool)reply["ok"]);
            Assert.IsNotNull(reply["error"]);
            // The process must still be answering: the Python side is mid-call inside somebody's
            // program and needs to raise at the call site, not find a dead pipe.
            Assert.IsTrue((bool)Call("{\"op\":\"ping\"}")["ok"], "the bridge died on a bad kernel");
        }

        [TestMethod]
        public void AnUnknownKernelName_IsReportedNotGuessed()
        {
            Call("{\"op\":\"module\",\"source\":\"" + TripleKernel + "\"}");
            JObject reply = Call("{\"op\":\"launch\",\"module\":\"m0\",\"kernel\":\"nope\","
                + "\"grid\":[1,1,1],\"block\":[1,1,1],\"args\":[]}");
            Assert.IsFalse((bool)reply["ok"]);
            StringAssert.Contains((string)reply["error"], "nope");
        }

        [TestMethod]
        public void APrintfInAKernelComesBackInTheReply_NotOnStdout()
        {
            // stdout is the protocol channel. A kernel's printf written there would be read as the
            // next reply and desynchronise every call after it.
            const string chatty =
                "extern \\\"C\\\" __global__ void chatty(int* v)"
                + "{ printf(\\\"hello from lane %d\\\\n\\\", (int)threadIdx.x); v[0] = 1; }";
            Call("{\"op\":\"module\",\"source\":\"" + chatty + "\"}");
            Call("{\"op\":\"malloc\",\"bytes\":4}");

            JObject launch = Call("{\"op\":\"launch\",\"module\":\"m0\",\"kernel\":\"chatty\","
                + "\"grid\":[1,1,1],\"block\":[2,1,1],\"args\":[{\"ptr\":\"d0\"}]}");
            Assert.IsTrue((bool)launch["ok"], (string)launch["error"]);
            StringAssert.Contains((string)launch["output"] ?? string.Empty, "hello from lane");

            // And the stream is still in step.
            Assert.IsTrue((bool)Call("{\"op\":\"ping\"}")["ok"]);
        }

        [TestMethod]
        public void OutputIsNotRepeatedOnTheNextLaunch()
        {
            // The emulator's sink only accumulates, so a naive read would replay every earlier
            // kernel's printf on every later launch.
            const string chatty =
                "extern \\\"C\\\" __global__ void chatty(int* v)"
                + "{ printf(\\\"tick\\\\n\\\"); v[0] = 1; }";
            Call("{\"op\":\"module\",\"source\":\"" + chatty + "\"}");
            Call("{\"op\":\"malloc\",\"bytes\":4}");
            string launch = "{\"op\":\"launch\",\"module\":\"m0\",\"kernel\":\"chatty\","
                + "\"grid\":[1,1,1],\"block\":[1,1,1],\"args\":[{\"ptr\":\"d0\"}]}";

            string first = (string)Call(launch)["output"] ?? string.Empty;
            string second = (string)Call(launch)["output"] ?? string.Empty;
            Assert.AreEqual(1, CountOccurrences(first, "tick"));
            Assert.AreEqual(1, CountOccurrences(second, "tick"),
                "the second launch replayed the first launch's output");
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        [TestMethod]
        public void AnOversizedUploadIsRefused()
        {
            Call("{\"op\":\"module\",\"source\":\"" + TripleKernel + "\"}");
            Call("{\"op\":\"malloc\",\"bytes\":8}");
            JObject reply = Call("{\"op\":\"upload\",\"ptr\":\"d0\",\"data\":\""
                + Base64Ints(1, 2, 3, 4, 5, 6, 7, 8) + "\"}");
            Assert.IsFalse((bool)reply["ok"], "32 bytes should not fit in an 8-byte allocation");
        }
    }
}
