using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace NoesisMcp.Engine;

/// <summary>Minimal RGBA PNG encoder (zlib from the BCL), so no imaging dependency is needed.</summary>
public static class Png
{
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (stride + 1);
            raw[dst++] = 0; // filter: none
            int src = y * stride;
            for (int x = 0; x < width; x++, src += 4, dst += 4)
            {
                raw[dst] = bgra[src + 2];
                raw[dst + 1] = bgra[src + 1];
                raw[dst + 2] = bgra[src];
                raw[dst + 3] = bgra[src + 3];
            }
        }

        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> be = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(be, data.Length);
        s.Write(be);
        var td = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, td);
        data.CopyTo(td, 4);
        s.Write(td);
        BinaryPrimitives.WriteUInt32BigEndian(be, Crc32.HashToUInt32(td));
        s.Write(be);
    }
}
