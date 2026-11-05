using System.Collections.Generic;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// FR-KB-06: a documentation catalog for the CUDA runtime/driver API and device intrinsics — the
    /// CUDA counterpart to <c>PythonCatalog</c>. Feeds <see cref="CompletionItem.Documentation"/> (and
    /// thus Quick Info / the doc panel) with a real summary, parameter descriptions, and return info,
    /// rather than a bare signature. Entries are static today; a JSON/versioned loader (mirroring
    /// PythonCatalog) is a straightforward follow-up.
    /// </summary>
    public static class CudaCatalog
    {
        /// <summary>Rich documentation for a CUDA symbol (multi-line: summary, parameters, returns), or null.</summary>
        public static string GetDoc(string name)
        {
            return string.IsNullOrEmpty(name) ? null : (Docs.TryGetValue(name, out string d) ? d : null);
        }

        public static bool Has(string name) => !string.IsNullOrEmpty(name) && Docs.ContainsKey(name);

        private static readonly Dictionary<string, string> Docs = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["cudaMalloc"] =
                "Allocates size bytes of linear memory on the device.\n\n" +
                "Parameters:\n" +
                "  devPtr - pointer to the allocated device memory (out)\n" +
                "  size   - requested allocation size in bytes\n" +
                "Returns: cudaError_t - cudaSuccess, or cudaErrorMemoryAllocation on failure.",
            ["cudaMallocManaged"] =
                "Allocates memory that is automatically managed by the Unified Memory system\n" +
                "(accessible from host and device).\n\n" +
                "Parameters:\n" +
                "  devPtr - pointer to the allocated managed memory (out)\n" +
                "  size   - requested allocation size in bytes\n" +
                "Returns: cudaError_t.",
            ["cudaFree"] =
                "Frees device memory previously allocated by cudaMalloc / cudaMallocManaged.\n\n" +
                "Parameters:\n" +
                "  devPtr - device pointer to free\n" +
                "Returns: cudaError_t.",
            ["cudaMemcpy"] =
                "Copies count bytes between host and device memory.\n\n" +
                "Parameters:\n" +
                "  dst   - destination memory address\n" +
                "  src   - source memory address\n" +
                "  count - size in bytes to copy\n" +
                "  kind  - direction of the copy:\n" +
                "            cudaMemcpyHostToDevice, cudaMemcpyDeviceToHost,\n" +
                "            cudaMemcpyDeviceToDevice, cudaMemcpyHostToHost, cudaMemcpyDefault\n" +
                "Returns: cudaError_t (cudaSuccess on success). This call is synchronous w.r.t. the host.",
            ["cudaMemcpyAsync"] =
                "Asynchronous variant of cudaMemcpy: the copy is queued on the given stream and may\n" +
                "overlap with other work.\n\n" +
                "Parameters:\n" +
                "  dst, src, count, kind - as cudaMemcpy\n" +
                "  stream                - stream to enqueue the copy on (0 = default stream)\n" +
                "Returns: cudaError_t.",
            ["cudaMemset"] =
                "Fills the first count bytes of device memory with the constant byte value.\n\n" +
                "Parameters:\n" +
                "  devPtr - device memory to set\n" +
                "  value  - byte value to set (low 8 bits used)\n" +
                "  count  - number of bytes to set\n" +
                "Returns: cudaError_t.",
            ["cudaDeviceSynchronize"] =
                "Blocks the host until all previously issued device work has completed.\n" +
                "Returns: cudaError_t - the error of any preceding asynchronous launch.",
            ["cudaGetLastError"] =
                "Returns the last error produced by a runtime call and RESETS it to cudaSuccess.\n" +
                "Returns: cudaError_t. Use cudaPeekAtLastError to read without resetting.",
            ["cudaStreamCreate"] =
                "Creates a new asynchronous stream.\n\n" +
                "Parameters:\n" +
                "  pStream - pointer to the new stream handle (out)\n" +
                "Returns: cudaError_t.",
            ["__syncthreads"] =
                "Barrier: waits until every thread in the block reaches this point, and makes all\n" +
                "global/shared memory accesses before it visible to the whole block.\n" +
                "Must be reached by ALL threads in the block (no divergence) or behavior is undefined.",
            ["__syncwarp"] =
                "Synchronizes the threads named by mask within a warp and reconverges them.\n\n" +
                "Parameters:\n" +
                "  mask - 32-bit lane participation mask (default 0xffffffff = all lanes).",
            ["__threadfence"] =
                "Memory fence: ensures writes made by the calling thread before the fence are visible\n" +
                "to all threads in the device before any writes made after it.",
            ["atomicAdd"] =
                "Atomically computes *address += val and returns the OLD value, without interference\n" +
                "from other threads.\n\n" +
                "Parameters:\n" +
                "  address - location in global or shared memory\n" +
                "  val     - value to add\n" +
                "Returns: the value at address before the add.",
            ["atomicCAS"] =
                "Atomic compare-and-swap: if *address == compare, writes val; returns the OLD value.\n\n" +
                "Parameters:\n" +
                "  address - location in global or shared memory\n" +
                "  compare - value to compare against\n" +
                "  val     - value to store on a match\n" +
                "Returns: the value at address before the operation.",
            ["threadIdx"] =
                "Built-in uint3: this thread's index within its block (.x, .y, .z).",
            ["blockIdx"] =
                "Built-in uint3: this block's index within the grid (.x, .y, .z).",
            ["blockDim"] =
                "Built-in dim3: the block dimensions (threads per block) (.x, .y, .z).",
            ["gridDim"] =
                "Built-in dim3: the grid dimensions (blocks per grid) (.x, .y, .z).",
            ["warpSize"] =
                "Built-in int: the warp size in threads (32 on current architectures).",
        };
    }
}
