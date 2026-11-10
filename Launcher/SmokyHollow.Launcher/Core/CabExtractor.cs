using System.IO.Compression;
using System.Text;

namespace SmokyHollow.Launcher.Core;

/// <summary>
/// Unpacks Microsoft Cabinet (.cab, "MSCF") files - the format the original TSO installer ships its
/// game files in (Data1.cab). Handles stored and MSZIP-compressed data, and cabinet sets that continue
/// across Data1.cab, Data2.cab, ...
///
/// MSZIP note: every 32 KB block is its own deflate stream, but it may refer back to the previous block's
/// output. .NET's DeflateStream can't be handed a starting dictionary, so each block is decoded behind a
/// small "stored" deflate block holding the last 32 KB of output. That primes the history; the primer
/// bytes are then thrown away.
/// </summary>
public sealed class CabExtractor
{
    private const uint Signature = 0x4643534D; // "MSCF"
    private const ushort FlagPrevCabinet = 0x0001;
    private const ushort FlagNextCabinet = 0x0002;
    private const ushort FlagReservePresent = 0x0004;
    private const ushort ContinuedFromPrev = 0xFFFD;
    private const ushort ContinuedToNext = 0xFFFE;
    private const ushort ContinuedPrevAndNext = 0xFFFF;
    private const int HistorySize = 32 * 1024;

    private sealed class Cabinet
    {
        public string Path = "";
        public ushort Flags;
        public byte DataReserve;
        public string? NextName;
        public readonly List<FolderPart> Folders = new();
        public readonly List<CabFile> Files = new();
    }

    private sealed class FolderPart
    {
        public Cabinet Cabinet = null!;
        public uint DataStart;
        public ushort DataBlocks;
        public ushort Compression;
    }

    private sealed class CabFile
    {
        public string Name = "";
        public uint Size;
        public uint Offset;
        public ushort Folder;
    }

    private sealed class Folder
    {
        public readonly List<FolderPart> Parts = new();
        public readonly List<CabFile> Files = new();
        public int CompressionType => Parts[0].Compression & 0x000F;
    }

    /// <summary>Extracts every file in the cabinet set that starts with <paramref name="firstCabPath"/>.</summary>
    /// <param name="progress">Called with (bytes written, total bytes).</param>
    public void Extract(string firstCabPath, string destination, Action<long, long>? progress, CancellationToken ct)
    {
        var cabinets = ReadCabinetSet(firstCabPath);
        var folders = BuildFolders(cabinets);

        long total = folders.Sum(f => f.Files.Sum(x => (long)x.Size));
        long written = 0;
        Directory.CreateDirectory(destination);
        string destRoot = Path.GetFullPath(destination);

        foreach (var folder in folders)
        {
            if (folder.Files.Count == 0) continue;
            int type = folder.CompressionType;
            if (type != 0 && type != 1)
                throw new NotSupportedException(
                    $"This cabinet uses a compression type ({type}) the launcher doesn't support yet.");

            ExtractFolder(folder, type, destRoot, n =>
            {
                written += n;
                progress?.Invoke(written, total);
            }, ct);
        }
    }

    // ---------------------------------------------------------------- reading the cabinet headers

    private static List<Cabinet> ReadCabinetSet(string firstCabPath)
    {
        var list = new List<Cabinet>();
        string dir = Path.GetDirectoryName(Path.GetFullPath(firstCabPath)) ?? ".";
        string? path = firstCabPath;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (path != null)
        {
            if (!seen.Add(Path.GetFullPath(path)))
                throw new InvalidDataException("The cabinet set loops back on itself.");
            var cab = ReadCabinet(path);
            list.Add(cab);
            if ((cab.Flags & FlagNextCabinet) == 0 || string.IsNullOrEmpty(cab.NextName)) break;
            path = FindSibling(dir, cab.NextName)
                   ?? throw new FileNotFoundException($"Missing the next part of the game files: {cab.NextName}");
        }
        return list;
    }

    private static string? FindSibling(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (File.Exists(exact)) return exact;
        // Linux file systems are case-sensitive; installers often aren't consistent about case.
        return Directory.EnumerateFiles(dir)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }

    private static Cabinet ReadCabinet(string path)
    {
        var cab = new Cabinet { Path = path };
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (br.ReadUInt32() != Signature)
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't a cabinet file.");
        br.ReadUInt32();                  // reserved1
        br.ReadUInt32();                  // cbCabinet
        br.ReadUInt32();                  // reserved2
        uint filesOffset = br.ReadUInt32();
        br.ReadUInt32();                  // reserved3
        br.ReadByte(); br.ReadByte();     // version minor / major
        ushort folderCount = br.ReadUInt16();
        ushort fileCount = br.ReadUInt16();
        cab.Flags = br.ReadUInt16();
        br.ReadUInt16();                  // setID
        br.ReadUInt16();                  // iCabinet

        byte folderReserve = 0;
        if ((cab.Flags & FlagReservePresent) != 0)
        {
            ushort headerReserve = br.ReadUInt16();
            folderReserve = br.ReadByte();
            cab.DataReserve = br.ReadByte();
            fs.Seek(headerReserve, SeekOrigin.Current);
        }
        if ((cab.Flags & FlagPrevCabinet) != 0) { ReadCString(br); ReadCString(br); }
        if ((cab.Flags & FlagNextCabinet) != 0) { cab.NextName = ReadCString(br); ReadCString(br); }

        for (int i = 0; i < folderCount; i++)
        {
            var part = new FolderPart
            {
                Cabinet = cab,
                DataStart = br.ReadUInt32(),
                DataBlocks = br.ReadUInt16(),
                Compression = br.ReadUInt16()
            };
            if (folderReserve > 0) fs.Seek(folderReserve, SeekOrigin.Current);
            cab.Folders.Add(part);
        }

        fs.Seek(filesOffset, SeekOrigin.Begin);
        for (int i = 0; i < fileCount; i++)
        {
            var file = new CabFile
            {
                Size = br.ReadUInt32(),
                Offset = br.ReadUInt32(),
                Folder = br.ReadUInt16()
            };
            br.ReadUInt16();              // date
            br.ReadUInt16();              // time
            ushort attribs = br.ReadUInt16();
            byte[] nameBytes = ReadCStringBytes(br);
            file.Name = (attribs & 0x80) != 0
                ? Encoding.UTF8.GetString(nameBytes)
                : Encoding.Latin1.GetString(nameBytes);
            cab.Files.Add(file);
        }
        return cab;
    }

    private static byte[] ReadCStringBytes(BinaryReader br)
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            byte b = br.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
            if (bytes.Count > 1024) throw new InvalidDataException("Bad name in cabinet file.");
        }
        return bytes.ToArray();
    }

    private static string ReadCString(BinaryReader br) => Encoding.Latin1.GetString(ReadCStringBytes(br));

    /// <summary>Joins folders that continue from one cabinet into the next, and assigns files to them.</summary>
    private static List<Folder> BuildFolders(List<Cabinet> cabinets)
    {
        var result = new List<Folder>();
        Folder? carried = null;

        foreach (var cab in cabinets)
        {
            if (cab.Folders.Count == 0) continue;
            var local = new Folder[cab.Folders.Count];
            for (int i = 0; i < cab.Folders.Count; i++)
            {
                if (i == 0 && carried != null && (cab.Flags & FlagPrevCabinet) != 0)
                {
                    carried.Parts.Add(cab.Folders[0]);
                    local[0] = carried;
                }
                else
                {
                    var f = new Folder();
                    f.Parts.Add(cab.Folders[i]);
                    result.Add(f);
                    local[i] = f;
                }
            }

            foreach (var file in cab.Files)
            {
                // Files continued from an earlier cabinet were already listed there.
                if (file.Folder == ContinuedFromPrev || file.Folder == ContinuedPrevAndNext) continue;
                Folder target = file.Folder == ContinuedToNext
                    ? local[^1]
                    : (file.Folder < local.Length
                        ? local[file.Folder]
                        : throw new InvalidDataException("A file points at a folder that doesn't exist."));
                target.Files.Add(file);
            }

            carried = (cab.Flags & FlagNextCabinet) != 0 ? local[^1] : null;
        }
        return result;
    }

    // ---------------------------------------------------------------- unpacking a folder

    private static IEnumerable<(byte[] Data, int Uncompressed)> ReadBlocks(Folder folder)
    {
        byte[]? pending = null;
        foreach (var part in folder.Parts)
        {
            using var fs = File.OpenRead(part.Cabinet.Path);
            using var br = new BinaryReader(fs);
            fs.Seek(part.DataStart, SeekOrigin.Begin);

            for (int i = 0; i < part.DataBlocks; i++)
            {
                br.ReadUInt32();                        // checksum
                ushort compressed = br.ReadUInt16();
                ushort uncompressed = br.ReadUInt16();
                if (part.Cabinet.DataReserve > 0) fs.Seek(part.Cabinet.DataReserve, SeekOrigin.Current);
                byte[] data = br.ReadBytes(compressed);
                if (data.Length != compressed) throw new EndOfStreamException("The game files are cut short.");

                if (pending != null)
                {
                    var joined = new byte[pending.Length + data.Length];
                    Buffer.BlockCopy(pending, 0, joined, 0, pending.Length);
                    Buffer.BlockCopy(data, 0, joined, pending.Length, data.Length);
                    data = joined;
                    pending = null;
                }

                // A block split across two cabinets reports 0 here; the rest is in the next cabinet.
                if (uncompressed == 0) { pending = data; continue; }
                yield return (data, uncompressed);
            }
        }
        if (pending != null) throw new EndOfStreamException("The last part of the game files is missing.");
    }

    private static void ExtractFolder(Folder folder, int compression, string destRoot, Action<long> wrote, CancellationToken ct)
    {
        var files = folder.Files.OrderBy(f => f.Offset).ToList();
        var history = new byte[HistorySize];
        int historyLength = 0;

        long position = 0;                  // offset in the folder's uncompressed data
        int index = 0;                      // next file to write
        FileStream? output = null;
        long remaining = 0;
        var written = new List<(CabFile File, string Path)>();
        var copies = new List<(CabFile File, CabFile Source, string SourcePath)>();

        try
        {
            foreach (var (data, size) in ReadBlocks(folder))
            {
                ct.ThrowIfCancellationRequested();
                byte[] block = compression == 0
                    ? data
                    : InflateMsZip(data, size, history, historyLength);
                if (block.Length != size) throw new InvalidDataException("A block of game files didn't unpack to the right size.");

                if (compression == 1) RememberHistory(block, history, ref historyLength);

                int at = 0;
                while (at < block.Length)
                {
                    if (output == null)
                    {
                        // Pick up the next file, handling empty files and files that repeat earlier data.
                        while (index < files.Count && (files[index].Size == 0 || files[index].Offset < position))
                        {
                            var f = files[index];
                            if (f.Size == 0) CreateEmpty(destRoot, f.Name);
                            else
                            {
                                var src = written.LastOrDefault(w => w.File.Offset <= f.Offset &&
                                                                     w.File.Offset + w.File.Size >= f.Offset + f.Size);
                                if (src.File == null) throw new InvalidDataException("Overlapping files in the cabinet.");
                                copies.Add((f, src.File, src.Path));
                            }
                            index++;
                        }
                        if (index >= files.Count) break;

                        var next = files[index];
                        if (position < next.Offset)
                        {
                            long skip = Math.Min(next.Offset - position, block.Length - at);
                            at += (int)skip;
                            position += skip;
                            continue;
                        }

                        string target = SafePath(destRoot, next.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                        written.Add((next, target));
                        remaining = next.Size;
                    }

                    int n = (int)Math.Min(remaining, block.Length - at);
                    output.Write(block, at, n);
                    at += n;
                    position += n;
                    remaining -= n;
                    wrote(n);

                    if (remaining == 0)
                    {
                        output.Dispose();
                        output = null;
                        index++;
                    }
                }
                if (index >= files.Count && output == null) break;
            }
        }
        finally
        {
            output?.Dispose();
        }

        // Anything still waiting means the data ran out early.
        for (; index < files.Count; index++)
        {
            if (files[index].Size == 0) { CreateEmpty(destRoot, files[index].Name); continue; }
            throw new EndOfStreamException($"The game files ended before {files[index].Name} was complete.");
        }

        foreach (var (file, source, sourcePath) in copies)
        {
            string target = SafePath(destRoot, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = File.OpenRead(sourcePath);
            src.Seek(file.Offset - source.Offset, SeekOrigin.Begin);
            using var dst = File.Create(target);
            CopyBytes(src, dst, file.Size);
            wrote(file.Size);
        }
    }

    private static void CopyBytes(Stream src, Stream dst, long count)
    {
        var buf = new byte[81920];
        while (count > 0)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            dst.Write(buf, 0, n);
            count -= n;
        }
    }

    private static void CreateEmpty(string destRoot, string name)
    {
        string target = SafePath(destRoot, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, Array.Empty<byte>());
    }

    /// <summary>Turns a cabinet name like "TSOClient\tuning.dat" into a path inside the destination.</summary>
    private static string SafePath(string destRoot, string name)
    {
        var parts = name.Split('\\', '/').Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0 || parts.Any(p => p == "." || p == ".." || p.Contains(':')))
            throw new InvalidDataException($"Unsafe file name in cabinet: {name}");
        string full = Path.GetFullPath(Path.Combine(destRoot, Path.Combine(parts)));
        string root = destRoot.EndsWith(Path.DirectorySeparatorChar) ? destRoot : destRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe file name in cabinet: {name}");
        return full;
    }

    // ---------------------------------------------------------------- MSZIP

    private static byte[] InflateMsZip(byte[] data, int expected, byte[] history, int historyLength)
    {
        if (data.Length < 2 || data[0] != (byte)'C' || data[1] != (byte)'K')
            throw new InvalidDataException("Bad MSZIP block in the game files.");

        using var input = new MemoryStream(data.Length + historyLength + 8);
        if (historyLength > 0)
        {
            // Stored, non-final deflate block carrying the history: header bits 000 (+ padding) = one zero
            // byte, then LEN and its one's complement, then the bytes themselves.
            input.WriteByte(0x00);
            input.WriteByte((byte)(historyLength & 0xFF));
            input.WriteByte((byte)(historyLength >> 8));
            input.WriteByte((byte)(~historyLength & 0xFF));
            input.WriteByte((byte)((~historyLength >> 8) & 0xFF));
            input.Write(history, 0, historyLength);
        }
        input.Write(data, 2, data.Length - 2);
        input.Position = 0;

        var output = new byte[historyLength + expected];
        using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < output.Length)
            {
                int n = inflate.Read(output, got, output.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != output.Length) throw new InvalidDataException("A block of game files was cut short.");
        }

        if (historyLength == 0) return output;
        var block = new byte[expected];
        Buffer.BlockCopy(output, historyLength, block, 0, expected);
        return block;
    }

    private static void RememberHistory(byte[] block, byte[] history, ref int historyLength)
    {
        if (block.Length >= HistorySize)
        {
            Buffer.BlockCopy(block, block.Length - HistorySize, history, 0, HistorySize);
            historyLength = HistorySize;
            return;
        }
        int keep = Math.Min(historyLength, HistorySize - block.Length);
        if (keep > 0) Buffer.BlockCopy(history, historyLength - keep, history, 0, keep);
        Buffer.BlockCopy(block, 0, history, keep, block.Length);
        historyLength = keep + block.Length;
    }
}
