namespace HelixPythonNamespace.Core.IO
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Text;

    /// <summary>
    /// The one binary file format every design canvas saves: <c>*.canvas</c> (Task 10i, user request
    /// during testing).
    ///
    /// <para><b>An envelope, not a data model.</b> The payload is the designer's own serializer output,
    /// byte for byte: the CG designer's <c>CgSimulationRepository</c> JSON (kind <c>cg-mc</c>) or the GUI
    /// designer's <c>GuiFormRepository</c> JSON (<c>gui-designer</c>). Each designer keeps its own
    /// version field and migrations. The envelope adds what a bare JSON file cannot say: which designer owns it, and
    /// whether it arrived whole.</para>
    ///
    /// <para><b>Byte layout, format version 1.</b> Every number is little-endian.</para>
    /// <code>
    /// offset  size  field
    ///      0     8  magic, ASCII "HXCANVAS" (48 58 43 41 4E 56 41 53)
    ///      8     2  format version, uint16 = 1
    ///     10     1  kind length K, 1..32
    ///     11     K  kind, ASCII [a-z0-9-], e.g. "cg-mc"
    ///   11+K     4  stored length L = the number of bytes that follow, uint32, at most 64 MiB
    ///   15+K     4  CRC-32 of those L bytes (IEEE 802.3, the zip/PNG CRC), uint32
    ///   19+K     L  the payload (UTF-8, no BOM) as one GZip member (RFC 1952)
    /// </code>
    /// <para>L and the CRC describe the STORED bytes, so a cut file and a flipped bit are found before
    /// anything is decompressed; GZip's own CRC covers the expanded bytes.</para>
    ///
    /// <para><b>Reading is all or nothing.</b> <see cref="Decode"/> returns the complete, checked payload
    /// or throws <see cref="CanvasFileException"/>, whose message names the file and the reason: a caller
    /// never gets half a design. <b>Writing is atomic.</b> <see cref="WriteAtomic"/> writes a temporary
    /// file in the same folder and swaps it in, so a crash leaves the old file or the new one, never a
    /// torn one.</para>
    /// </summary>
    public static class CanvasFile
    {
        public const string Extension = ".canvas";
        public const int FormatVersion = 1;
        public const int MaxKindLength = 32;
        public const int MaxPayloadBytes = 64 * 1024 * 1024;

        private const string MagicText = "HXCANVAS";
        private const int FixedHeaderBytes = 19;   // 8 + 2 + 1 + 4 + 4: everything but the kind

        private static readonly uint[] CrcTable = BuildCrcTable();

        public static bool IsCanvasPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>1 to 32 characters of a-z, 0-9 and '-': short, stable, and the same in every locale.</summary>
        public static bool IsValidKind(string kind)
        {
            if (string.IsNullOrEmpty(kind) || kind.Length > MaxKindLength) return false;
            foreach (char c in kind)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
            return true;
        }

        public static byte[] Encode(string kind, byte[] payload)
        {
            if (!IsValidKind(kind))
                throw new ArgumentException("A canvas kind is 1 to " + MaxKindLength + " characters of a-z, 0-9 and '-': \"" + kind + "\".", "kind");
            if (payload == null) throw new ArgumentNullException("payload");
            if (payload.Length > MaxPayloadBytes)
                throw new ArgumentException("The design is " + payload.Length + " bytes; a canvas file holds at most " + MaxPayloadBytes + ".", "payload");

            byte[] stored;
            using (var compressed = new MemoryStream())
            {
                using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
                    gzip.Write(payload, 0, payload.Length);
                stored = compressed.ToArray();
            }
            if (stored.Length > MaxPayloadBytes)
                throw new ArgumentException("The design is too large for a canvas file.", "payload");

            using (var file = new MemoryStream())
            {
                using (var header = new BinaryWriter(file, Encoding.ASCII, true))   // BinaryWriter is always little-endian
                {
                    header.Write(Encoding.ASCII.GetBytes(MagicText));
                    header.Write((ushort)FormatVersion);
                    header.Write((byte)kind.Length);
                    header.Write(Encoding.ASCII.GetBytes(kind));
                    header.Write((uint)stored.Length);
                    header.Write(Crc32(stored));
                    header.Write(stored);
                }
                return file.ToArray();
            }
        }

        public static CanvasContent Decode(byte[] file, string fileName)
        {
            if (file == null) throw new ArgumentNullException("file");
            fileName = string.IsNullOrEmpty(fileName) ? "(unnamed)" : fileName;

            for (int i = 0; i < MagicText.Length && i < file.Length; i++)
                if (file[i] != (byte)MagicText[i])
                    throw new CanvasFileException(fileName, "it is not a canvas file (it does not start with " + MagicText + ")");
            if (file.Length < 11) throw new CanvasFileException(fileName, "the file is cut short (its header is incomplete)");

            // The version first: a newer format may lay out everything after it differently.
            int version = file[8] | (file[9] << 8);
            if (version == 0) throw new CanvasFileException(fileName, "the header is damaged (canvas format 0 does not exist)");
            if (version > FormatVersion)
                throw new CanvasFileException(fileName, "it was saved by a newer HelixPython (canvas format " + version
                    + "; this build reads format " + FormatVersion + ")");

            int kindLength = file[10];
            if (kindLength < 1 || kindLength > MaxKindLength)
                throw new CanvasFileException(fileName, "the header is damaged (the design kind is not readable)");
            if (file.Length < FixedHeaderBytes + kindLength)
                throw new CanvasFileException(fileName, "the file is cut short (its header is incomplete)");
            string kind = Encoding.ASCII.GetString(file, 11, kindLength);
            if (!IsValidKind(kind)) throw new CanvasFileException(fileName, "the header is damaged (the design kind is not readable)");

            uint length = ReadUInt32(file, 11 + kindLength);
            uint expectedCrc = ReadUInt32(file, 15 + kindLength);
            if (length > MaxPayloadBytes)
                throw new CanvasFileException(fileName, "the header is damaged (it gives " + length
                    + " bytes of design data; the limit is " + MaxPayloadBytes + ")");

            int start = FixedHeaderBytes + kindLength;
            long have = file.Length - start;
            if (have < length)
                throw new CanvasFileException(fileName, "the file is cut short (it holds " + have + " of " + length + " bytes of design data)");
            if (have > length)
                throw new CanvasFileException(fileName, "the file is damaged (it has " + (have - length) + " extra byte(s) after the design data)");

            var stored = new byte[length];
            Buffer.BlockCopy(file, start, stored, 0, (int)length);
            uint actualCrc = Crc32(stored);
            if (actualCrc != expectedCrc)
                throw new CanvasFileException(fileName, "the design data is damaged (checksum " + expectedCrc.ToString("X8")
                    + " in the header, " + actualCrc.ToString("X8") + " in the file)");

            return new CanvasContent(kind, version, Expand(stored, fileName));
        }

        public static CanvasContent Read(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            return Decode(File.ReadAllBytes(path), Path.GetFileName(path));
        }

        /// <summary>
        /// <see cref="Read(string)"/> for a designer's own Open: a canvas of another kind is refused
        /// with a reason that names both kinds and the route that would open it.
        /// </summary>
        public static CanvasContent Read(string path, string expectedKind, string designName)
        {
            CanvasContent content = Read(path);
            if (!string.Equals(content.Kind, expectedKind, StringComparison.Ordinal))
                throw new CanvasFileException(Path.GetFileName(path), "it holds a \"" + content.Kind + "\" design, not a "
                    + designName + " (open it with File > Open)");
            return content;
        }

        public static string UnknownKindReason(string kind)
        {
            return "it holds a \"" + kind + "\" design, and no designer in this build opens that kind";
        }

        /// <summary>True when the file's first eight bytes are the magic. Reads those eight bytes only.</summary>
        public static bool StartsWithMagic(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[MagicText.Length];
                    int read = 0, n;
                    while (read < head.Length && (n = stream.Read(head, read, head.Length - read)) > 0) read += n;
                    return read == head.Length && Encoding.ASCII.GetString(head) == MagicText;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        /// <summary>The kind in the header, or null for anything that is not a readable version-1 header.
        /// Never throws: the Solution Explorer asks this for every row it offers a designer verb on.</summary>
        public static string PeekKind(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[11 + MaxKindLength];
                    int read = 0, n;
                    while (read < head.Length && (n = stream.Read(head, read, head.Length - read)) > 0) read += n;
                    if (read < 12 || Encoding.ASCII.GetString(head, 0, MagicText.Length) != MagicText) return null;
                    if ((head[8] | (head[9] << 8)) != FormatVersion) return null;
                    int kindLength = head[10];
                    if (kindLength < 1 || kindLength > MaxKindLength || read < 11 + kindLength) return null;
                    string kind = Encoding.ASCII.GetString(head, 11, kindLength);
                    return IsValidKind(kind) ? kind : null;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        /// <summary>Encodes first, so a bad kind or an oversized design changes nothing on disk; then
        /// <see cref="ReplaceAtomically"/>.</summary>
        public static void WriteAtomic(string path, string kind, byte[] payload)
        {
            byte[] file = Encode(kind, payload);
            ReplaceAtomically(path, stream => stream.Write(file, 0, file.Length));
        }

        /// <summary>
        /// Writes through <paramref name="write"/> into a temporary file in the SAME folder, flushes it
        /// to the disk, then swaps it in (<see cref="File.Replace(string, string, string)"/>, or a move
        /// for a new file). A failure at any point leaves the old file as it was and no temporary file.
        /// </summary>
        public static void ReplaceAtomically(string path, Action<Stream> write)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (write == null) throw new ArgumentNullException("write");
            string full = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(full) ?? string.Empty;
            if (directory.Length > 0) Directory.CreateDirectory(directory);
            // Same folder, so the swap is a rename on one volume, never a copy across two.
            string temp = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);   // on the disk before the swap, not in a cache
                }
                if (File.Exists(full)) File.Replace(temp, full, null);
                else File.Move(temp, full);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static uint Crc32(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static byte[] Expand(byte[] stored, string fileName)
        {
            try
            {
                using (var gzip = new GZipStream(new MemoryStream(stored, false), CompressionMode.Decompress))
                using (var expanded = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (expanded.Length + n > MaxPayloadBytes)
                            throw new CanvasFileException(fileName, "the design data is damaged (it expands to more than " + MaxPayloadBytes + " bytes)");
                        expanded.Write(buffer, 0, n);
                    }
                    return expanded.ToArray();
                }
            }
            catch (InvalidDataException)
            {
                throw new CanvasFileException(fileName, "the design data is damaged (it cannot be decompressed)");
            }
        }
    }

    /// <summary>A checked, complete <c>*.canvas</c> file: its kind, its format version and the designer's payload.</summary>
    public sealed class CanvasContent
    {
        public CanvasContent(string kind, int version, byte[] payload)
        {
            Kind = kind;
            Version = version;
            Payload = payload;
        }

        public string Kind { get; }
        public int Version { get; }
        public byte[] Payload { get; }
    }

    /// <summary>A <c>*.canvas</c> file that is refused. The message is the one the user sees:
    /// <c>Could not open "&lt;file name&gt;": &lt;reason&gt;.</c></summary>
    public sealed class CanvasFileException : IOException
    {
        public CanvasFileException(string fileName, string reason)
            : base("Could not open \"" + fileName + "\": " + reason + ".")
        {
            FileName = fileName;
            Reason = reason;
        }

        public string FileName { get; }
        public string Reason { get; }
    }
}
