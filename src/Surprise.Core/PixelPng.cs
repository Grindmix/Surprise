using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Surprise.Core;

public sealed record PixelFrame(int Width, int Height, int CellSize, int SourceWidth, int SourceHeight, uint[] Colors);

// Scanline decoding keeps even 30,000 x 24,000 PNGs out of full-size bitmap/GPU allocations.
// Colors are straight-alpha 0xAARRGGBB. Transparent RGB is normalized before grid detection.
public static class PixelPng
{
    private sealed record Run(int End, uint Color);
    private sealed record Row(int Y, Run[] Runs);
    public static PixelFrame Read(string path, int cellSize = 0, CancellationToken cancellation = default, Action<string>? diagnostics = null)
    {
        using var file = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8]; file.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("Это не PNG: " + Path.GetFileName(path));
        int width = 0, height = 0, channels = 0, type = 0; byte[]? palette = null, alpha = null;
        using var compressed = new MemoryStream(); bool ended = false, header = false;
        Span<byte> chunkHeader = stackalloc byte[8]; Span<byte> checksum = stackalloc byte[4];
        while (!ended)
        {
            cancellation.ThrowIfCancellationRequested();
            file.ReadExactly(chunkHeader);
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkHeader));
            string name = Encoding.ASCII.GetString(chunkHeader[4..]);
            if (length > 128 * 1024 * 1024 || length < 0) throw new InvalidDataException("Слишком большой PNG-блок.");
            byte[] data = new byte[length]; file.ReadExactly(data); file.ReadExactly(checksum);
            uint crc = Crc(chunkHeader[4..], 0xffffffff); crc = Crc(data, crc) ^ 0xffffffff;
            if (crc != BinaryPrimitives.ReadUInt32BigEndian(checksum)) throw new InvalidDataException("PNG повреждён: контрольная сумма " + name);
            switch (name)
            {
                case "IHDR":
                    if (header || length != 13) throw new InvalidDataException("Неверный заголовок PNG."); header = true;
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data)); height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)));
                    type = data[9]; channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                    if (width is < 1 or > 65536 || height is < 1 or > 65536 || (long)width * height > 1_000_000_000 || data[8] != 8 || channels == 0 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                        throw new InvalidDataException("Для пиксельарта нужен PNG 8 бит без interlace (RGB, RGBA, серый или палитра). Экспортируй PNG в RGBA 8 бит.");
                    break;
                case "PLTE": palette = data; break;
                case "tRNS": alpha = data; break;
                case "acTL": throw new InvalidDataException("Для покадровой анимации выбери отдельные статические PNG, не APNG.");
                case "IDAT": if (!header || compressed.Length + length > 128 * 1024 * 1024) throw new InvalidDataException("Неверные или слишком большие данные PNG."); compressed.Write(data); break;
                case "IEND": ended = true; break;
            }
        }
        if (!header || compressed.Length == 0 || cellSize < 0) throw new InvalidDataException("PNG не содержит изображения.");
        if (type == 3 && (palette is null || palette.Length % 3 != 0)) throw new InvalidDataException("Отсутствует палитра PNG.");
        int stride = checked(width * channels), grid = Gcd(width, height), totalRuns = 0;
        byte[] previous = new byte[stride], current = new byte[stride];
        var rows = new List<Row>();
        var runLengths = new Dictionary<int, int>();
        int outputWidth = cellSize > 0 ? (width + cellSize - 1) / cellSize : 0;
        int outputHeight = cellSize > 0 ? (height + cellSize - 1) / cellSize : 0;
        if ((long)outputWidth * outputHeight > 1_000_000) throw new InvalidDataException("Слишком много клеток. Увеличь размер клетки.");
        uint[]? sampled = cellSize > 0 ? new uint[outputWidth * outputHeight] : null;
        compressed.Position = 0; using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested(); int filter = zlib.ReadByte(); zlib.ReadExactly(current);
            Unfilter(current, previous, channels, filter);
            if (sampled is not null)
            {
                int gy = y / cellSize;
                if (y == Math.Min(height - 1, gy * cellSize + cellSize / 2))
                    for (int gx = 0; gx < outputWidth; gx++) sampled[gy * outputWidth + gx] = Color(current, Math.Min(width - 1, gx * cellSize + cellSize / 2), type, channels, palette, alpha);
            }
            else if (y == 0 || !current.AsSpan().SequenceEqual(previous))
            {
                var runs = new List<Run>(); uint color = Color(current, 0, type, channels, palette, alpha);
                for (int x = 1; x < width; x++)
                {
                    uint next = Color(current, x, type, channels, palette, alpha);
                    if (next == color) continue;
                    grid = Gcd(grid, x); runs.Add(new(x, color)); color = next;
                }
                runs.Add(new(width, color));
                if (diagnostics is not null) { int start = 0; foreach (var run in runs) { int length = run.End - start; start = run.End; if ((run.Color >> 24) > 127) runLengths[length] = runLengths.GetValueOrDefault(length) + 1; } }
                if (rows.Count == 0 || !runs.SequenceEqual(rows[^1].Runs))
                {
                    if (y > 0) grid = Gcd(grid, y);
                    totalRuns += runs.Count;
                    if (totalRuns > 2_000_000) throw new InvalidDataException("Не удалось найти крупную сетку пиксельарта. Задай размер клетки вручную.");
                    rows.Add(new(y, runs.ToArray()));
                }
            }
            (previous, current) = (current, previous);
        }
        if (zlib.ReadByte() != -1) throw new InvalidDataException("Лишние данные изображения PNG.");
        if (sampled is not null) return new(outputWidth, outputHeight, cellSize, width, height, sampled);
        outputWidth = width / grid; outputHeight = height / grid;
        diagnostics?.Invoke("Grid=" + grid + "; rows=" + rows.Count + "; frequent runs=" + string.Join(", ", runLengths.OrderByDescending(p => p.Value).Take(30).Select(p => p.Key + ":" + p.Value)));
        if ((long)outputWidth * outputHeight > 8_000_000) throw new InvalidDataException($"PNG содержит слишком много разных пикселей даже после точного восстановления сетки ({grid} пикс.). Уменьши исходное разрешение при экспорте.");
        sampled = new uint[outputWidth * outputHeight]; int rowIndex = 0;
        for (int gy = 0; gy < outputHeight; gy++)
        {
            int sy = gy * grid + grid / 2;
            while (rowIndex + 1 < rows.Count && rows[rowIndex + 1].Y <= sy) rowIndex++;
            int runIndex = 0;
            for (int gx = 0; gx < outputWidth; gx++)
            {
                int sx = gx * grid + grid / 2;
                while (rows[rowIndex].Runs[runIndex].End <= sx) runIndex++;
                sampled[gy * outputWidth + gx] = rows[rowIndex].Runs[runIndex].Color;
            }
        }
        return new(outputWidth, outputHeight, grid, width, height, sampled);
    }
    private static uint Color(byte[] row, int x, int type, int channels, byte[]? palette, byte[]? alpha)
    {
        int p = x * channels; byte r, g, b, a = 255;
        switch (type)
        {
            case 6: r = row[p]; g = row[p + 1]; b = row[p + 2]; a = row[p + 3]; break;
            case 2:
                r = row[p]; g = row[p + 1]; b = row[p + 2];
                if (alpha?.Length == 6 && r == BinaryPrimitives.ReadUInt16BigEndian(alpha) && g == BinaryPrimitives.ReadUInt16BigEndian(alpha.AsSpan(2)) && b == BinaryPrimitives.ReadUInt16BigEndian(alpha.AsSpan(4))) a = 0;
                break;
            case 3:
                int index = row[p]; if (index * 3 + 2 >= palette!.Length) throw new InvalidDataException("Неверный индекс палитры.");
                r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2]; if (alpha is not null && index < alpha.Length) a = alpha[index]; break;
            default:
                r = g = b = row[p]; if (type == 4) a = row[p + 1];
                else if (alpha?.Length == 2 && r == BinaryPrimitives.ReadUInt16BigEndian(alpha)) a = 0; break;
        }
        return a == 0 ? 0 : ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }
    private static void Unfilter(byte[] row, byte[] previous, int bpp, int filter)
    {
        if (filter == 0) return;
        if (filter is < 0 or > 4) throw new InvalidDataException("Неверный фильтр PNG.");
        if (filter == 2 && row.AsSpan().IndexOfAnyExcept((byte)0) < 0) { previous.CopyTo(row, 0); return; }
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
            int predict = filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
            row[i] = unchecked((byte)(row[i] + predict));
        }
    }
    private static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i => { uint c = (uint)i; for (int n = 0; n < 8; n++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
    private static uint Crc(ReadOnlySpan<byte> bytes, uint crc) { foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8); return crc; }
}
