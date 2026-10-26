using System.Buffers.Binary;
using System.Text;

namespace Luna.Ops;

/// <summary>
/// Removes EXIF/XMP/IPTC/text metadata (GPS, camera, author...) by rewriting the container only,
/// so pixels are untouched. JPEG orientation is preserved so photos don't flip.
/// </summary>
internal static class MetadataOps
{
    public static bool TryStripLossless(string input, string output)
    {
        var ext = ImageOps.Norm(Paths.Ext(input));
        if (ext is not ("jpg" or "png" or "webp")) return false;
        var data = File.ReadAllBytes(input);
        byte[]? result = ext switch
        {
            "jpg" => StripJpeg(data),
            "png" => StripPng(data),
            _ => StripWebp(data),
        };
        if (result == null) return false;
        File.WriteAllBytes(output, result);
        return true;
    }

    /// <summary>EXIF orientation (1-8) of a JPEG, or 1 when absent.</summary>
    public static int JpegOrientation(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[Math.Min(f.Length, 256 * 1024)];
            f.ReadExactly(head);
            int orientation = 1;
            WalkJpeg(head, (marker, start, len) =>
            {
                if (marker == 0xE1 && IsExif(head, start, len)) orientation = ReadOrientation(head, start + 6, len - 6);
                return marker != 0xDA;
            });
            return orientation;
        }
        catch { return 1; }
    }

    static byte[]? StripJpeg(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        using var o = new MemoryStream(d.Length);
        o.Write(d, 0, 2);
        int orientation = 1;
        bool orientationWritten = false;
        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) return null;
            byte m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m is >= 0xD0 and <= 0xD7 or 0x01) { o.Write(d, i, 2); i += 2; continue; }
            if (m is 0xDA or 0xD9)
            {
                if (!orientationWritten) WriteOrientation(o, orientation);
                o.Write(d, i, d.Length - i); // entropy-coded data and anything after it, verbatim
                return o.ToArray();
            }
            int len = (d[i + 2] << 8) | d[i + 3];
            if (len < 2 || i + 2 + len > d.Length) return null;
            int payload = i + 4, payloadLen = len - 2;

            bool drop = m switch
            {
                0xE1 => true,                       // EXIF / XMP
                0xED or 0xFE => true,               // IPTC / Photoshop, comments
                >= 0xE3 and <= 0xEF => m != 0xEE,   // other APPn except Adobe (needed for colors)
                _ => false,                         // APP0 JFIF, APP2 ICC profile, tables, frames
            };
            if (m == 0xE1 && IsExif(d, payload, payloadLen))
                orientation = ReadOrientation(d, payload + 6, payloadLen - 6);

            if (!drop)
            {
                bool isApp = m is >= 0xE0 and <= 0xEF;
                if (!isApp && !orientationWritten) { WriteOrientation(o, orientation); orientationWritten = true; }
                o.Write(d, i, 2 + len);
            }
            i += 2 + len;
        }
        return null;
    }

    static void WalkJpeg(byte[] d, Func<byte, int, int, bool> visit)
    {
        int i = 2;
        while (i + 4 <= d.Length && d[i] == 0xFF)
        {
            byte m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m is >= 0xD0 and <= 0xD7 or 0x01) { i += 2; continue; }
            int len = (d[i + 2] << 8) | d[i + 3];
            if (!visit(m, i + 4, len - 2)) return;
            i += 2 + len;
        }
    }

    static bool IsExif(byte[] d, int start, int len) =>
        len > 14 && d[start] == 'E' && d[start + 1] == 'x' && d[start + 2] == 'i' && d[start + 3] == 'f' && d[start + 4] == 0;

    static int ReadOrientation(byte[] d, int tiff, int len)
    {
        try
        {
            if (len < 8) return 1;
            bool le = d[tiff] == 'I';
            ushort U16(int at) => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at));
            uint U32(int at) => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
            int ifd = tiff + (int)U32(tiff + 4);
            int count = U16(ifd);
            for (int e = 0; e < count; e++)
            {
                int entry = ifd + 2 + e * 12;
                if (entry + 12 > tiff + len) break;
                if (U16(entry) == 0x0112) { int v = U16(entry + 8); return v is >= 1 and <= 8 ? v : 1; }
            }
        }
        catch { }
        return 1;
    }

    /// <summary>A minimal EXIF block holding only the orientation tag.</summary>
    static void WriteOrientation(Stream o, int orientation)
    {
        if (orientation is < 2 or > 8) return;
        byte[] seg =
        {
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,   // TIFF header, IFD0 at 8
            0x00, 0x01,                                                 // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,             // Orientation, SHORT, count 1
            0x00, (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                                     // no next IFD
        };
        o.Write(seg);
    }

    static readonly string[] PngDrop = { "tEXt", "zTXt", "iTXt", "eXIf", "tIME" };

    static byte[]? StripPng(byte[] d)
    {
        if (d.Length < 8 || d[0] != 0x89 || d[1] != 'P') return null;
        using var o = new MemoryStream(d.Length);
        o.Write(d, 0, 8);
        int i = 8;
        while (i + 12 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(i));
            if (len < 0 || i + 12 + len > d.Length) return null;
            var type = Encoding.ASCII.GetString(d, i + 4, 4);
            if (!PngDrop.Contains(type)) o.Write(d, i, 12 + len);
            i += 12 + len;
            if (type == "IEND") return o.ToArray();
        }
        return null;
    }

    static byte[]? StripWebp(byte[] d)
    {
        if (d.Length < 12 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WEBP") return null;
        using var o = new MemoryStream(d.Length);
        o.Write(d, 0, 12);
        int i = 12;
        while (i + 8 <= d.Length)
        {
            var fourcc = Encoding.ASCII.GetString(d, i, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i + 4));
            int padded = size + (size & 1);
            if (size < 0 || i + 8 + size > d.Length) return null;
            int chunkLen = Math.Min(8 + padded, d.Length - i);
            if (fourcc is "EXIF" or "XMP ") { i += chunkLen; continue; }
            var chunk = d.AsSpan(i, chunkLen).ToArray();
            if (fourcc == "VP8X") chunk[8] &= unchecked((byte)~0x0C); // clear EXIF + XMP flags
            o.Write(chunk);
            i += chunkLen;
        }
        var result = o.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        return result;
    }
}
