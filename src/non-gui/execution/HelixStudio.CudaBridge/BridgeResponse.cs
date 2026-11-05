using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.CudaBridge
{
    /// <summary>
    /// One reply on the bridge's stdout: always a single line of JSON, always carrying <c>ok</c>.
    ///
    /// <para>
    /// A failure is a normal reply with <c>ok:false</c> and a reason, never a non-zero exit or a
    /// message on stderr. The Python side is inside somebody's program, in the middle of what
    /// looks to them like a CUDA call, so it has to be able to raise a decent exception at the
    /// call site. A bridge that died instead would surface as a broken pipe several frames away
    /// from the kernel that actually failed.
    /// </para>
    /// </summary>
    internal sealed class BridgeResponse
    {
        private readonly JObject _payload;

        private BridgeResponse(bool ok)
        {
            _payload = new JObject { ["ok"] = ok };
        }

        public static BridgeResponse Ok()
        {
            return new BridgeResponse(true);
        }

        public static BridgeResponse Failure(string reason)
        {
            var response = new BridgeResponse(false);
            response._payload["error"] = reason ?? "unknown error";
            return response;
        }

        public BridgeResponse With(string key, string value)
        {
            _payload[key] = value;
            return this;
        }

        public BridgeResponse With(string key, bool value)
        {
            _payload[key] = value;
            return this;
        }

        public BridgeResponse With(string key, long value)
        {
            _payload[key] = value;
            return this;
        }

        public BridgeResponse With(string key, IEnumerable<string> values)
        {
            var array = new JArray();
            foreach (string value in values) array.Add(value);
            _payload[key] = array;
            return this;
        }

        /// <summary>One line, no newlines inside — the Python side reads line by line.</summary>
        public string ToLine()
        {
            return _payload.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
