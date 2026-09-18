using System.Runtime.InteropServices;

namespace Glacier.DocTree.Binary
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
    public readonly struct GdocMetadataEntry
    {
        public readonly uint KeyOffset;
        public readonly ushort KeyLength;
        public readonly uint ValOffset;
        public readonly ushort ValLength;
        public readonly int NextMetadataIndex;

        public GdocMetadataEntry(uint keyOffset, ushort keyLength, uint valOffset, ushort valLength, int nextMetadataIndex)
        {
            KeyOffset = keyOffset;
            KeyLength = keyLength;
            ValOffset = valOffset;
            ValLength = valLength;
            NextMetadataIndex = nextMetadataIndex;
        }
    }
}
