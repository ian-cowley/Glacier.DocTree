using System;
using System.IO.Hashing;

namespace Glacier.DocTree.Binary
{
    /// <summary>
    /// Hardware-accelerated IEEE 802.3 CRC-32 helper backed by <see cref="Crc32"/>.
    /// </summary>
    internal static class GdocCrc32
    {
        public static uint Compute(ReadOnlySpan<byte> data) => Crc32.HashToUInt32(data);

        public static unsafe uint ComputeLarge(byte* ptr, long length)
        {
            if (length <= 0) return 0;
            if (length <= int.MaxValue)
            {
                return Crc32.HashToUInt32(new ReadOnlySpan<byte>(ptr, (int)length));
            }

            var crc = new Crc32();
            long remaining = length;
            byte* current = ptr;
            const int chunkSize = 1 << 26; // 64 MiB chunks
            while (remaining > 0)
            {
                int slice = (int)Math.Min(remaining, chunkSize);
                crc.Append(new ReadOnlySpan<byte>(current, slice));
                current += slice;
                remaining -= slice;
            }
            return crc.GetCurrentHashAsUInt32();
        }
    }
}
