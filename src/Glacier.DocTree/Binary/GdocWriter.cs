using System;
using Glacier.DocTree.Core;

namespace Glacier.DocTree.Binary
{
    public static class GdocWriter
    {
        public const uint GdocMagic = 0x47444F43; // Legacy v1 magic; unified v2 writes GDocHeader.MagicValue ("GDOC")
        public const ushort CurrentVersion = 1;
        public const int HeaderSize = 64;

        /// <summary>
        /// Iteratively serializes a <see cref="DocNode"/> tree into the unified .gdoc v2 binary format
        /// with native metadata entries, IEEE 802.3 CRC-32 payload integrity, and atomic file replacement.
        /// </summary>
        public static void Write(DocNode root, string filePath)
        {
            ArgumentNullException.ThrowIfNull(root);
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            }

            GlacierStorageDocExtensions.BuildGDocWriter(root, vectorDim: 0).WriteToFile(filePath);
        }
    }
}
