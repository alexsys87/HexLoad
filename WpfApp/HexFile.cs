using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HexLoad
{
    /// <summary>
    /// Firmware image: one contiguous data block plus the absolute address of its first byte.
    /// Gaps between segments of the source file are filled with 0xFF (erased flash state).
    /// </summary>
    public sealed class FirmwareImage
    {
        public byte[] Data { get; }
        public uint BaseAddress { get; }

        /// <summary>True if the source file carried addresses (HEX, S-record, TI-TXT); false for raw binary.</summary>
        public bool HasAddress { get; }

        public IReadOnlyList<string> Warnings { get; }

        public FirmwareImage(byte[] data, uint baseAddress, bool hasAddress, IReadOnlyList<string>? warnings = null)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            BaseAddress = baseAddress;
            HasAddress = hasAddress;
            Warnings = warnings ?? Array.Empty<string>();
        }

        public uint EndAddress => Data.Length == 0 ? BaseAddress : BaseAddress + (uint)(Data.Length - 1);

        /// <summary>Returns a new image containing only the bytes at or above the given address.</summary>
        public FirmwareImage SliceFrom(uint address)
        {
            if (address <= BaseAddress) return this;
            if (address > EndAddress) throw new ArgumentOutOfRangeException(nameof(address));

            int skip = (int)(address - BaseAddress);
            var data = new byte[Data.Length - skip];
            Buffer.BlockCopy(Data, skip, data, 0, data.Length);
            return new FirmwareImage(data, address, HasAddress, Warnings);
        }
    }

    /// <summary>Firmware file parse error.</summary>
    public sealed class HexFileFormatException : Exception
    {
        public HexFileFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// Reading and writing of Intel HEX / Motorola S-record / TI-TXT / C array.
    /// </summary>
    public static class HexFile
    {
        /// <summary>Guard against allocating huge buffers from a broken or sparse file.</summary>
        public const long MaxImageSize = 64L * 1024 * 1024;

        private const byte ErasedByte = 0xFF;
        private const int MaxWarnings = 32;

        // ==================== Intel HEX parser ====================

        public static FirmwareImage ParseIntelHex(IReadOnlyList<string> lines)
        {
            var builder = new SegmentBuilder();
            var warnings = new List<string>();
            uint upperBase = 0;
            bool sawEof = false;
            byte[] rec = new byte[255 + 5];

            for (int n = 0; n < lines.Count; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0) continue;
                if (line[0] != ':')
                {
                    Warn(warnings, $"line {n + 1}: skipped (no ':')");
                    continue;
                }

                // ':' + LL + AAAA + TT + data + CC
                if (line.Length < 11 || (line.Length & 1) == 0)
                {
                    Warn(warnings, $"line {n + 1}: invalid record length");
                    continue;
                }

                int byteCount;
                try { byteCount = ParseByte(line, 1); }
                catch (FormatException) { Warn(warnings, $"line {n + 1}: non-hex characters"); continue; }

                int need = 11 + byteCount * 2;
                if (line.Length < need)
                {
                    Warn(warnings, $"line {n + 1}: truncated record");
                    continue;
                }

                int sum = 0;
                bool bad = false;
                for (int i = 0; i < byteCount + 5; i++)   // LL AAAA TT + data + CC
                {
                    int b;
                    try { b = ParseByte(line, 1 + i * 2); }
                    catch (FormatException) { bad = true; break; }
                    rec[i] = (byte)b;
                    sum += b;
                }
                if (bad) { Warn(warnings, $"line {n + 1}: non-hex characters"); continue; }
                if ((sum & 0xFF) != 0)
                {
                    Warn(warnings, $"line {n + 1}: checksum error, record skipped");
                    continue;
                }

                uint offset = (uint)((rec[1] << 8) | rec[2]);
                int type = rec[3];

                switch (type)
                {
                    case 0x00:  // data
                        if (byteCount > 0)
                            builder.Add(upperBase + offset, rec, 4, byteCount);
                        break;

                    case 0x01:  // end of file
                        sawEof = true;
                        break;

                    case 0x02:  // extended segment address
                        if (byteCount != 2) { Warn(warnings, $"line {n + 1}: invalid type 02 record length"); break; }
                        upperBase = (uint)((rec[4] << 8) | rec[5]) << 4;
                        break;

                    case 0x04:  // extended linear address
                        if (byteCount != 2) { Warn(warnings, $"line {n + 1}: invalid type 04 record length"); break; }
                        upperBase = (uint)((rec[4] << 8) | rec[5]) << 16;
                        break;

                    case 0x03:  // start segment address
                    case 0x05:  // start linear address
                        break;  // entry point is not used by the bootloader

                    default:
                        Warn(warnings, $"line {n + 1}: unknown record type 0x{type:X2}");
                        break;
                }

                if (sawEof) break;
            }

            if (!sawEof) Warn(warnings, "end-of-file record (:00000001FF) not found");
            if (builder.IsEmpty) throw new HexFileFormatException("Intel HEX file contains no data.");

            return builder.Build(warnings);
        }

        // ==================== Motorola S-record parser ====================

        public static FirmwareImage ParseSRecord(IReadOnlyList<string> lines)
        {
            var builder = new SegmentBuilder();
            var warnings = new List<string>();
            byte[] rec = new byte[256];

            for (int n = 0; n < lines.Count; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0) continue;
                if (line[0] != 'S' && line[0] != 's')
                {
                    Warn(warnings, $"line {n + 1}: skipped (no 'S')");
                    continue;
                }
                if (line.Length < 4) { Warn(warnings, $"line {n + 1}: record too short"); continue; }

                char type = line[1];
                int addrBytes = type switch { '1' => 2, '2' => 3, '3' => 4, _ => 0 };
                if (addrBytes == 0) continue;   // S0/S4..S9 carry no data

                int byteCount;
                try { byteCount = ParseByte(line, 2); }
                catch (FormatException) { Warn(warnings, $"line {n + 1}: non-hex characters"); continue; }

                if (line.Length < 4 + byteCount * 2)
                {
                    Warn(warnings, $"line {n + 1}: truncated record");
                    continue;
                }

                int dataBytes = byteCount - addrBytes - 1;   // minus checksum
                if (dataBytes <= 0) continue;                // record without data

                int sum = 0;
                bool bad = false;
                for (int i = 0; i < byteCount + 1; i++)      // count + address + data + checksum
                {
                    int b;
                    try { b = ParseByte(line, 2 + i * 2); }
                    catch (FormatException) { bad = true; break; }
                    rec[i] = (byte)b;
                    sum += b;
                }
                if (bad) { Warn(warnings, $"line {n + 1}: non-hex characters"); continue; }
                if ((sum & 0xFF) != 0xFF)
                {
                    Warn(warnings, $"line {n + 1}: checksum error, record skipped");
                    continue;
                }

                uint address = 0;
                for (int i = 0; i < addrBytes; i++)
                    address = (address << 8) | rec[1 + i];

                builder.Add(address, rec, 1 + addrBytes, dataBytes);
            }

            if (builder.IsEmpty) throw new HexFileFormatException("S-record file contains no data.");
            return builder.Build(warnings);
        }

        // ==================== TI-TXT parser ====================

        public static FirmwareImage ParseTiTxt(IReadOnlyList<string> lines)
        {
            var builder = new SegmentBuilder();
            var warnings = new List<string>();
            var chunk = new List<byte>(256);
            uint chunkStart = 0;
            bool haveSection = false;

            void FlushChunk()
            {
                if (chunk.Count > 0)
                {
                    byte[] arr = chunk.ToArray();
                    builder.Add(chunkStart, arr, 0, arr.Length);
                    chunkStart += (uint)arr.Length;
                    chunk.Clear();
                }
            }

            for (int n = 0; n < lines.Count; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0) continue;

                if (line[0] == 'q' || line[0] == 'Q') break;   // end of file

                if (line[0] == '@')
                {
                    FlushChunk();
                    string addrStr = line.Substring(1).Trim();
                    if (!uint.TryParse(addrStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out chunkStart))
                        throw new HexFileFormatException($"line {n + 1}: invalid address '{addrStr}'.");
                    haveSection = true;
                    continue;
                }

                if (!haveSection)
                {
                    Warn(warnings, $"line {n + 1}: data before the first '@' directive, skipped");
                    continue;
                }

                bool stop = false;
                foreach (string part in line.Split(SpaceSeparators, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Length == 1 && (part[0] == 'q' || part[0] == 'Q')) { stop = true; break; }
                    if (part.Length != 2 || !byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                        throw new HexFileFormatException($"line {n + 1}: invalid byte '{part}'.");
                    chunk.Add(b);
                }
                if (stop) break;
            }

            FlushChunk();
            if (builder.IsEmpty) throw new HexFileFormatException("TI-TXT file contains no data.");
            return builder.Build(warnings);
        }

        private static readonly char[] SpaceSeparators = { ' ', '\t' };

        // ==================== Intel HEX writer ====================

        public static string GenerateIntelHex(byte[] data, uint baseAddress, int bytesPerLine = 16)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (bytesPerLine < 1 || bytesPerLine > 255) throw new ArgumentOutOfRangeException(nameof(bytesPerLine));

            var sb = new StringBuilder(data.Length * 3 + 64);
            uint upper = 0;   // upper 16 address bits start at 0; a type 04 record is emitted when they change
            int pos = 0;

            while (pos < data.Length)
            {
                uint addr = baseAddress + (uint)pos;
                uint hi = addr >> 16;

                if (hi != upper)
                {
                    // Extended linear address record
                    int cs = 0x02 + 0x00 + 0x00 + 0x04 + (int)((hi >> 8) & 0xFF) + (int)(hi & 0xFF);
                    sb.Append(":02000004")
                      .Append(((hi >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture))
                      .Append((hi & 0xFF).ToString("X2", CultureInfo.InvariantCulture))
                      .Append(((-cs) & 0xFF).ToString("X2", CultureInfo.InvariantCulture))
                      .Append('\n');
                    upper = hi;
                }

                // a single record must not cross a 64 KB boundary
                int toBoundary = (int)(0x10000 - (addr & 0xFFFF));
                int chunk = Math.Min(Math.Min(bytesPerLine, data.Length - pos), toBoundary);

                int lo16 = (int)(addr & 0xFFFF);
                int checksum = chunk + ((lo16 >> 8) & 0xFF) + (lo16 & 0xFF) /* + type 0x00 */;

                sb.Append(':')
                  .Append(chunk.ToString("X2", CultureInfo.InvariantCulture))
                  .Append(lo16.ToString("X4", CultureInfo.InvariantCulture))
                  .Append("00");

                for (int i = 0; i < chunk; i++)
                {
                    byte b = data[pos + i];
                    sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                    checksum += b;
                }

                sb.Append(((-checksum) & 0xFF).ToString("X2", CultureInfo.InvariantCulture)).Append('\n');
                pos += chunk;
            }

            sb.Append(":00000001FF\n");
            return sb.ToString();
        }

        // ==================== Motorola S-record writer ====================

        public static string GenerateSRecord(byte[] data, uint baseAddress, int bytesPerLine = 16)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (bytesPerLine < 1 || bytesPerLine > 250) throw new ArgumentOutOfRangeException(nameof(bytesPerLine));

            uint endAddress = data.Length == 0 ? baseAddress : baseAddress + (uint)(data.Length - 1);

            // Smallest sufficient address width: S1/S2/S3 with matching terminators S9/S8/S7
            int addrBytes = endAddress <= 0xFFFF ? 2 : endAddress <= 0xFFFFFF ? 3 : 4;
            char dataType = addrBytes == 2 ? '1' : addrBytes == 3 ? '2' : '3';
            char endType = addrBytes == 2 ? '9' : addrBytes == 3 ? '8' : '7';

            var sb = new StringBuilder(data.Length * 3 + 64);

            // S0 header record with the module name
            AppendSRecord(sb, '0', 0, 2, HeaderName, 0, HeaderName.Length);

            int pos = 0;
            while (pos < data.Length)
            {
                int chunk = Math.Min(bytesPerLine, data.Length - pos);
                AppendSRecord(sb, dataType, baseAddress + (uint)pos, addrBytes, data, pos, chunk);
                pos += chunk;
            }

            // Terminator; start address = image base address
            AppendSRecord(sb, endType, baseAddress, addrBytes, Array.Empty<byte>(), 0, 0);
            return sb.ToString();
        }

        private static readonly byte[] HeaderName = Encoding.ASCII.GetBytes("HEXLOAD");

        private static void AppendSRecord(StringBuilder sb, char type, uint address, int addrBytes,
                                          byte[] data, int offset, int count)
        {
            int byteCount = addrBytes + count + 1;   // address + data + checksum
            int sum = byteCount;

            sb.Append('S').Append(type).Append(byteCount.ToString("X2", CultureInfo.InvariantCulture));

            for (int i = addrBytes - 1; i >= 0; i--)
            {
                int b = (int)((address >> (i * 8)) & 0xFF);
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                sum += b;
            }

            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                sum += b;
            }

            sb.Append(((~sum) & 0xFF).ToString("X2", CultureInfo.InvariantCulture)).Append('\n');
        }

        // ==================== TI-TXT writer ====================

        public static string GenerateTiTxt(byte[] data, uint baseAddress, int bytesPerLine = 16)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (bytesPerLine < 1) throw new ArgumentOutOfRangeException(nameof(bytesPerLine));

            var sb = new StringBuilder(data.Length * 3 + 32);
            sb.Append('@').Append(baseAddress.ToString("X4", CultureInfo.InvariantCulture)).Append('\n');

            for (int pos = 0; pos < data.Length; pos += bytesPerLine)
            {
                int chunk = Math.Min(bytesPerLine, data.Length - pos);
                for (int i = 0; i < chunk; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(data[pos + i].ToString("X2", CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }

            sb.Append("q\n");
            return sb.ToString();
        }

        // ==================== C array writer ====================

        public static string GenerateCArray(byte[] data, uint baseAddress, string arrayName, int bytesPerLine = 16)
        {
            ArgumentNullException.ThrowIfNull(data);
            string name = SanitizeIdentifier(arrayName);

            var sb = new StringBuilder(data.Length * 6 + 256);
            sb.Append("/* Generated by HexLoad */\n")
              .Append("/* Base address: 0x").Append(baseAddress.ToString("X8", CultureInfo.InvariantCulture))
              .Append(", size: ").Append(data.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes */\n\n")
              .Append("#define ").Append(name).Append("_ADDRESS 0x")
              .Append(baseAddress.ToString("X8", CultureInfo.InvariantCulture)).Append("UL\n")
              .Append("#define ").Append(name).Append("_SIZE ")
              .Append(data.Length.ToString(CultureInfo.InvariantCulture)).Append("UL\n\n")
              .Append("const unsigned char ").Append(name).Append('[')
              .Append(data.Length.ToString(CultureInfo.InvariantCulture)).Append("] = {\n");

            for (int pos = 0; pos < data.Length; pos += bytesPerLine)
            {
                int chunk = Math.Min(bytesPerLine, data.Length - pos);
                sb.Append("    ");
                for (int i = 0; i < chunk; i++)
                {
                    sb.Append("0x").Append(data[pos + i].ToString("X2", CultureInfo.InvariantCulture));
                    if (pos + i < data.Length - 1) sb.Append(',');
                    if (i < chunk - 1) sb.Append(' ');
                }
                sb.Append('\n');
            }

            sb.Append("};\n");
            return sb.ToString();
        }

        /// <summary>Turns an arbitrary string into a valid C identifier.</summary>
        public static string SanitizeIdentifier(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "firmware";

            var sb = new StringBuilder(name.Length + 1);
            foreach (char c in name)
                sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');

            if (sb[0] >= '0' && sb[0] <= '9') sb.Insert(0, '_');
            return sb.ToString();
        }

        // ==================== Format detection ====================

        public enum FileFormat { Binary, IntelHex, SRecord, TiTxt }

        /// <summary>Detects the format by extension and, if ambiguous, by the first lines of the file.</summary>
        public static FileFormat DetectFormat(string path, byte[] head)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".hex":
                case ".ihx":
                case ".ihex": return FileFormat.IntelHex;
                case ".s19":
                case ".s28":
                case ".s37":
                case ".mot":
                case ".srec": return FileFormat.SRecord;
                case ".bin":
                case ".img":
                case ".rom": return FileFormat.Binary;
            }

            // .txt / .ti-txt / no extension - look at the content
            foreach (byte b in head)
            {
                if (b == 0) return FileFormat.Binary;      // NUL byte => not a text file
            }

            string text = Encoding.ASCII.GetString(head);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ':') return FileFormat.IntelHex;
                if (line[0] == '@') return FileFormat.TiTxt;
                if ((line[0] == 'S' || line[0] == 's') && line.Length > 2 && Uri.IsHexDigit(line[2]))
                    return FileFormat.SRecord;
                break;
            }
            return FileFormat.Binary;
        }

        // ==================== Internals ====================

        private static void Warn(List<string> warnings, string message)
        {
            if (warnings.Count < MaxWarnings) warnings.Add(message);
            else if (warnings.Count == MaxWarnings) warnings.Add("... (further warnings suppressed)");
        }

        private static int ParseByte(string s, int index)
        {
            int hi = HexDigit(s[index]);
            int lo = HexDigit(s[index + 1]);
            if (hi < 0 || lo < 0) throw new FormatException();
            return (hi << 4) | lo;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return -1;
        }

        /// <summary>Collects scattered record segments into one contiguous image.</summary>
        private sealed class SegmentBuilder
        {
            private readonly List<(uint Address, byte[] Data)> _blocks = new();
            private uint _min = uint.MaxValue;
            private uint _max;

            public bool IsEmpty => _blocks.Count == 0;

            public void Add(uint address, byte[] source, int offset, int length)
            {
                if (length <= 0) return;

                // 32-bit address space overflow guard
                if (address > uint.MaxValue - (uint)(length - 1))
                    throw new HexFileFormatException($"Record at 0x{address:X8} crosses the 32-bit address limit.");

                var copy = new byte[length];
                Buffer.BlockCopy(source, offset, copy, 0, length);
                _blocks.Add((address, copy));

                if (address < _min) _min = address;
                uint end = address + (uint)(length - 1);
                if (end > _max) _max = end;
            }

            public FirmwareImage Build(List<string> warnings)
            {
                if (IsEmpty) throw new HexFileFormatException("File contains no data.");

                long size = (long)_max - _min + 1;
                if (size > MaxImageSize)
                    throw new HexFileFormatException(
                        $"Image size {size} bytes (0x{_min:X8}..0x{_max:X8}) exceeds the limit of {MaxImageSize} bytes. " +
                        "The file probably contains unrelated memory regions.");

                var data = new byte[size];
                data.AsSpan().Fill(ErasedByte);   // unfilled gaps = erased flash

                long covered = 0;
                foreach (var (address, block) in _blocks)
                {
                    Buffer.BlockCopy(block, 0, data, (int)(address - _min), block.Length);
                    covered += block.Length;
                }

                if (covered < size)
                    Warn(warnings, $"image contains {size - covered} bytes of gaps, filled with 0xFF");

                return new FirmwareImage(data, _min, hasAddress: true, warnings);
            }
        }
    }
}
