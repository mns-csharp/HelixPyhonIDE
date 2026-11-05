using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// Human-readable C/C++ library documentation. Signatures deliberately use canonical
    /// programmer-facing types instead of implementation typedefs and vendor macros.
    /// </summary>
    public static class CppCatalog
    {
        public sealed class Entry
        {
            public Entry(string signature, string documentation)
            {
                Signature = signature;
                Documentation = documentation;
            }

            public string Signature { get; private set; }
            public string Documentation { get; private set; }
        }

        public static Entry Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Entry entry;
            return Entries.TryGetValue(name, out entry) ? entry : null;
        }

        private static readonly Dictionary<string, Entry> Entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal)
        {
            ["printf"] = new Entry(
                "int printf(const char* format, ...)",
                "Writes formatted output to stdout.\n\nParameters:\n  format - format string\n  ...    - values consumed by format specifiers\nReturns: number of characters written, or a negative value on error."),
            ["malloc"] = new Entry(
                "void* malloc(size_t byte_count)",
                "Allocates byte_count bytes of uninitialized storage.\n\nParameters:\n  byte_count - number of bytes to allocate\nReturns: allocated storage, or nullptr on failure. Release it with free."),
            ["free"] = new Entry(
                "void free(void* memory)",
                "Releases storage returned by malloc, calloc, or realloc. Passing nullptr has no effect."),
            ["memcpy"] = new Entry(
                "void* memcpy(void* destination, const void* source, size_t byte_count)",
                "Copies byte_count bytes between non-overlapping memory regions.\n\nUse memmove when the regions may overlap.\nReturns: destination."),
            ["move"] = new Entry(
                "template<class T> remove_reference_t<T>&& std::move(T&& value)",
                "Converts value to an xvalue so move-aware overloads can be selected. It does not move anything by itself."),
            ["make_unique"] = new Entry(
                "template<class T, class... Args> std::unique_ptr<T> std::make_unique(Args&&... args)",
                "Creates a T owned by a unique_ptr using the supplied constructor arguments. Available since C++14."),
            ["make_shared"] = new Entry(
                "template<class T, class... Args> std::shared_ptr<T> std::make_shared(Args&&... args)",
                "Creates a T and its shared ownership control block in one allocation. Available since C++11."),
            ["sort"] = new Entry(
                "template<class RandomIt> void std::sort(RandomIt first, RandomIt last)",
                "Sorts the half-open range [first, last) in ascending order. Average complexity: O(N log N)."),
            ["begin"] = new Entry(
                "iterator std::begin(Container& container)",
                "Returns an iterator to the first element of a container or array."),
            ["end"] = new Entry(
                "iterator std::end(Container& container)",
                "Returns an iterator one past the last element of a container or array."),
        };
    }
}
