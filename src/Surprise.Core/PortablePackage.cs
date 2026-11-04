using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Surprise.Core;

/// <summary>
/// A portable surprise is a published executor with a ZIP payload and a fixed footer appended after the PE bytes.
/// The host prefix is unchanged so the Windows and .NET loaders keep using the original image; the footer is read from the end of the file.
/// Layout, little-endian: uint64 zipLength, uint32 zipCrc32, uint32 formatVersion, 8-byte magic "SURP0001".
/// </summary>
public static class PortablePackage
{
    public const uint FormatVersion = 1;
    public const int FooterLength = 24;
    public static ReadOnlySpan<byte> Magic => "SURP0001"u8;
    private const long MinimumHostLength = 64;
    private const long MaximumZipLength = 8L * 1024 * 1024 * 1024;

    public static void Append(Stream output, Stream zipPayload)
    {
        if (output is null || zipPayload is null) throw new ArgumentNullException(output is null ? nameof(output) : nameof(zipPayload));
        if (!output.CanWrite || !output.CanSeek) throw new ArgumentException("Package output must be a writable, seekable stream.");
        uint crc = 0xFFFFFFFFu;
        long length = 0;
        byte[] buffer = new byte[81920];
        int read;
        while ((read = zipPayload.Read(buffer, 0, buffer.Length)) > 0)
        {
            length += read;
            if (length > MaximumZipLength) throw new InvalidDataException("Portable payload is too large.");
            crc = UpdateCrc(crc, buffer.AsSpan(0, read));
            output.Write(buffer, 0, read);
        }
        if (length < 22) throw new InvalidDataException("Portable payload is not a ZIP archive.");
        Span<byte> footer = stackalloc byte[FooterLength];
        BinaryPrimitives.WriteUInt64LittleEndian(footer, (ulong)length);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], crc ^ 0xFFFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[12..], FormatVersion);
        Magic.CopyTo(footer[16..]);
        output.Write(footer);
    }

    /// <summary>False when the file has no footer magic. Throws when the magic is present but the package is corrupt.</summary>
    public static bool HasPayload(string packagePath)
    {
        using var stream = Open(packagePath);
        return TryReadFooter(stream, out _);
    }

    public static IReadOnlyList<string> Entries(string packagePath)
    {
        using var stream = Open(packagePath);
        if (!TryReadFooter(stream, out var footer)) throw new InvalidDataException("This executable has no portable payload.");
        using var zip = OpenZip(stream, footer);
        return zip.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).Select(entry => NormalizeEntry(entry.FullName)).ToArray();
    }

    public static void Extract(string packagePath, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("Destination is required.", nameof(destinationDirectory));
        using var stream = Open(packagePath);
        if (!TryReadFooter(stream, out var footer)) throw new InvalidDataException("This executable has no portable payload.");
        Directory.CreateDirectory(destinationDirectory);
        using var zip = OpenZip(stream, footer);
        if (zip.Entries.Count == 0) throw new InvalidDataException("Portable payload is empty.");
        bool sawConfig = false, sawMedia = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                string directory = NormalizeEntry(entry.FullName.TrimEnd('/', '\\'));
                if (directory is "media" or "third-party") continue;
                throw new InvalidDataException("Unexpected portable payload directory.");
            }
            string relative = NormalizeEntry(entry.FullName);
            if (!seen.Add(relative)) throw new InvalidDataException("Duplicate portable payload entry.");
            if (!IsAllowed(relative)) throw new InvalidDataException("Unexpected portable payload entry: " + relative);
            if (entry.Length < 0 || entry.Length > MaximumZipLength) throw new InvalidDataException("Portable payload entry is too large.");
            string target = SafePaths.ResolveUnder(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(file);
            if (relative.Equals("config.json", StringComparison.OrdinalIgnoreCase)) sawConfig = true;
            if (relative.StartsWith("media/surprise", StringComparison.OrdinalIgnoreCase)) sawMedia = true;
        }
        if (!sawConfig || !sawMedia) throw new InvalidDataException("Portable payload is missing configuration or media.");
    }

    private static FileStream Open(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) throw new ArgumentException("Package path is required.", nameof(packagePath));
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Portable package was not found.", packagePath);
        return new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static bool TryReadFooter(Stream stream, out Footer footer)
    {
        footer = default;
        if (stream.Length < FooterLength + MinimumHostLength) return false;
        Span<byte> buffer = stackalloc byte[FooterLength];
        stream.Position = stream.Length - FooterLength;
        stream.ReadExactly(buffer);
        if (!buffer[16..].SequenceEqual(Magic)) return false;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]);
        if (version != FormatVersion) throw new InvalidDataException("Unsupported portable package version.");
        ulong zipLength = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        if (zipLength < 22 || zipLength > (ulong)MaximumZipLength || zipLength > (ulong)(stream.Length - FooterLength - MinimumHostLength))
            throw new InvalidDataException("Portable package length is invalid.");
        stream.Position = 0;
        Span<byte> mz = stackalloc byte[2];
        stream.ReadExactly(mz);
        if (mz[0] != (byte)'M' || mz[1] != (byte)'Z') throw new InvalidDataException("Portable package is not attached to an executable.");
        long start = stream.Length - FooterLength - (long)zipLength;
        stream.Position = start;
        uint crc = 0xFFFFFFFFu;
        byte[] block = new byte[81920];
        long remaining = (long)zipLength;
        while (remaining > 0)
        {
            int count = stream.Read(block, 0, (int)Math.Min(block.Length, remaining));
            if (count == 0) throw new EndOfStreamException("Portable payload ended early.");
            crc = UpdateCrc(crc, block.AsSpan(0, count));
            remaining -= count;
        }
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]);
        if ((crc ^ 0xFFFFFFFFu) != expected) throw new InvalidDataException("Portable package checksum does not match.");
        stream.Position = start;
        Span<byte> signature = stackalloc byte[4];
        stream.ReadExactly(signature);
        if (signature[0] != 0x50 || signature[1] != 0x4B || signature[2] != 0x03 || signature[3] != 0x04)
            throw new InvalidDataException("Portable payload is not a ZIP archive.");
        footer = new Footer(start, (long)zipLength);
        return true;
    }

    private static ZipArchive OpenZip(Stream stream, Footer footer)
    {
        stream.Position = footer.Start;
        return new ZipArchive(new WindowStream(stream, footer.Start, footer.Length), ZipArchiveMode.Read, leaveOpen: true);
    }

    private static string NormalizeEntry(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) throw new InvalidDataException("Empty payload path.");
        string name = fullName.Replace('\\', '/').Trim();
        if (name.Contains(':') || name.StartsWith('/')) throw new InvalidDataException("Absolute payload path.");
        string[] parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is ".." or ".")) throw new InvalidDataException("Payload path escapes the package.");
        return string.Join('/', parts);
    }

    private static bool IsAllowed(string relative)
    {
        if (relative.Equals("config.json", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("THIRD-PARTY.txt", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("third-party/GPL-2.0.txt", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("third-party/GPL-3.0.txt", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!relative.StartsWith("media/surprise", StringComparison.OrdinalIgnoreCase)) return false;
        string extension = Path.GetExtension(relative);
        return SurpriseConfig.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && relative.Equals("media/surprise" + extension, StringComparison.OrdinalIgnoreCase);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static readonly uint[] CrcTable = CreateCrcTable();
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private readonly record struct Footer(long Start, long Length);

    private sealed class WindowStream : Stream
    {
        private readonly Stream inner;
        private readonly long start, length;
        public WindowStream(Stream inner, long start, long length)
        {
            this.inner = inner; this.start = start; this.length = length;
            inner.Position = start;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => inner.Position - start;
            set
            {
                if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value));
                inner.Position = start + value;
            }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = length - Position;
            if (remaining <= 0) return 0;
            if (count > remaining) count = (int)remaining;
            return inner.Read(buffer, offset, count);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = next;
            return next;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
