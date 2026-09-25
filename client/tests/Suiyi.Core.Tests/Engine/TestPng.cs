using System.Buffers.Binary;

namespace Suiyi.Core.Tests.Engine;

/// <summary>测试用的 PNG 字节：只有签名 + IHDR 头（客户端不解码，足够测预检与上传）。</summary>
internal static class TestPng
{
    public static byte[] Header(int width, int height, int totalLength = 33)
    {
        var bytes = new byte[Math.Max(totalLength, 33)];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
        bytes[24] = 8; // bit depth
        bytes[25] = 6; // RGBA
        return bytes;
    }

    public static byte[] Small { get; } = Header(320, 80);
}
