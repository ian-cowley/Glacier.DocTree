using System.Runtime.InteropServices;

namespace Glacier.DocTree.Binary
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
    public readonly struct GdocNodeEntry
    {
        public readonly byte NodeType;
        public readonly byte Flags;
        public readonly ushort Reserved;
        public readonly uint ContentOffset;
        public readonly uint ContentLength;
        public readonly int ParentIndex;
        public readonly int FirstChildIndex;
        public readonly int NextSiblingIndex;
        public readonly ushort ChildCount;
        public readonly ushort MetadataCount;
        public readonly int MetadataIndex;

        public GdocNodeEntry(
            byte nodeType,
            byte flags,
            uint contentOffset,
            uint contentLength,
            int parentIndex,
            int firstChildIndex,
            int nextSiblingIndex,
            ushort childCount,
            ushort metadataCount,
            int metadataIndex)
        {
            NodeType = nodeType;
            Flags = flags;
            Reserved = 0;
            ContentOffset = contentOffset;
            ContentLength = contentLength;
            ParentIndex = parentIndex;
            FirstChildIndex = firstChildIndex;
            NextSiblingIndex = nextSiblingIndex;
            ChildCount = childCount;
            MetadataCount = metadataCount;
            MetadataIndex = metadataIndex;
        }
    }
}
