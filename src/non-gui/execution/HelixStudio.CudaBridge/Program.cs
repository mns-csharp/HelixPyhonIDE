using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.CudaEmulator;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.CudaBridge
{
    /// <summary>
    /// <c>helix_cuda_bridge.exe</c> — runs CUDA kernels for a Python process on the CPU emulator.
    ///
    /// <para>
    /// Started once by the <c>helix_cuda</c> Python package and talked to over stdin/stdout, one
    /// JSON object per line. It exists so a Python program that expects a GPU can run on a machine
    /// that has none: the Python side presents the CuPy/PyCUDA surface the program already calls,
    /// and every allocation, copy and launch arrives here instead of a driver.
    /// </para>
    ///
    /// <para>
    /// <b>Why a separate process rather than a library.</b> The emulator is .NET and the program is
    /// CPython; embedding either in the other means a native interop dependency in the user's
    /// environment, which is the one thing a "run without installing CUDA" feature cannot afford.
    /// A pipe needs nothing installed on either side.
    /// </para>
    ///
    /// <para>
    /// <b>Protocol.</b> Request and reply are both one line of JSON. Every reply carries
    /// <c>ok</c>; a failure adds <c>error</c>. Binary payloads are base64, because the transport is
    /// a text line.
    /// </para>
    /// <code>
    /// {"op":"module","source":"__global__ void k(...){...}"}   -> {"ok":true,"module":"m0"}
    /// {"op":"malloc","bytes":32}                                -> {"ok":true,"ptr":"d0"}
    /// {"op":"upload","ptr":"d0","data":"&lt;base64&gt;"}              -> {"ok":true,"bytes":32}
    /// {"op":"launch","module":"m0","kernel":"k",
    ///  "grid":[2,1,1],"block":[4,1,1],
    ///  "args":[{"ptr":"d0"},{"i":8}]}                           -> {"ok":true,"output":"..."}
    /// {"op":"download","ptr":"d0","bytes":32}                   -> {"ok":true,"data":"&lt;base64&gt;"}
    /// {"op":"free","ptr":"d0"}                                  -> {"ok":true}
    /// {"op":"ping"}                                             -> {"ok":true,"emulator":"..."}
    /// </code>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // stdout carries the protocol and nothing else. Anything the emulator prints is
            // returned inside a reply, never written here, or a kernel's printf would be parsed
            // as a command and desynchronise the stream.
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            var session = new BridgeSession();

            // Dial the IDE back if it is debugging this run, and let it place its kernel
            // breakpoints BEFORE the first Python request is served. Done here, not lazily at the
            // first launch, because a breakpoint that arrives after the kernel it belongs to has
            // already run is a breakpoint the user watched not work.
            var debug = new KernelDebugChannel();
            if (debug.TryAttach()) session.Debug = debug;

            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                BridgeResponse response;
                try { response = Dispatch(session, line); }
                catch (Exception ex) { response = BridgeResponse.Failure(ex.GetType().Name + ": " + ex.Message); }
                if (response == null) break;           // "quit"
                output.WriteLine(response.ToLine());
            }
            debug.Dispose();
            return 0;
        }

        /// <summary>Null means the peer asked to stop.</summary>
        private static BridgeResponse Dispatch(BridgeSession session, string line)
        {
            JObject request;
            try { request = JObject.Parse(line); }
            catch (Exception ex) { return BridgeResponse.Failure("bad request: " + ex.Message); }

            string op = (string)request["op"] ?? string.Empty;
            switch (op)
            {
                case "ping":
                    return BridgeResponse.Ok()
                        .With("emulator", CudaCppEmulatorToolchain.DisplayName + " " + CudaCppEmulatorToolchain.Version);

                case "quit":
                    return null;

                case "module":
                    return session.CompileModule((string)request["source"]);

                case "malloc":
                    return session.Malloc((long?)request["bytes"] ?? 0L);

                case "free":
                    return session.Free((string)request["ptr"]);

                case "upload":
                    return session.CopyToDevice((string)request["ptr"],
                        DecodeBase64((string)request["data"]));

                case "download":
                    return session.CopyToHost((string)request["ptr"], (int?)request["bytes"] ?? 0);

                case "launch":
                    return Launch(session, request);

                // Nothing to do: the emulator runs a launch to completion before replying, so the
                // program is already synchronised by the time it can ask. Answered rather than
                // rejected, because real code calls it and should not have to know that.
                case "sync":
                    return BridgeResponse.Ok();

                default:
                    return BridgeResponse.Failure("unknown op: " + op);
            }
        }

        private static BridgeResponse Launch(BridgeSession session, JObject request)
        {
            Dim3 grid = ReadDim(request["grid"]);
            Dim3 block = ReadDim(request["block"]);

            var arguments = new List<BridgeArgument>();
            var array = request["args"] as JArray;
            if (array != null)
                foreach (JToken token in array)
                {
                    var item = token as JObject;
                    if (item == null) return BridgeResponse.Failure("malformed argument");

                    if (item["ptr"] != null)
                        arguments.Add(new BridgeArgument { IsPointer = true, PointerHandle = (string)item["ptr"] });
                    else if (item["f"] != null)
                        arguments.Add(new BridgeArgument { IsFloat = true, DoubleValue = (double)item["f"] });
                    else if (item["i"] != null)
                        arguments.Add(new BridgeArgument { LongValue = (long)item["i"] });
                    else
                        return BridgeResponse.Failure("argument has none of ptr/i/f");
                }

            return session.Launch((string)request["module"], (string)request["kernel"],
                grid, block, arguments, (long?)request["shared"] ?? 0L);
        }

        /// <summary>A [x,y,z] triple; missing dimensions default to 1, as CUDA's do.</summary>
        private static Dim3 ReadDim(JToken token)
        {
            var array = token as JArray;
            if (array == null) return Dim3.One();
            ulong x = array.Count > 0 ? (ulong)array[0] : 1UL;
            ulong y = array.Count > 1 ? (ulong)array[1] : 1UL;
            ulong z = array.Count > 2 ? (ulong)array[2] : 1UL;
            return new Dim3(x == 0 ? 1UL : x, y == 0 ? 1UL : y, z == 0 ? 1UL : z);
        }

        private static byte[] DecodeBase64(string text)
        {
            if (string.IsNullOrEmpty(text)) return new byte[0];
            try { return Convert.FromBase64String(text); }
            catch (FormatException) { return new byte[0]; }
        }
    }
}
