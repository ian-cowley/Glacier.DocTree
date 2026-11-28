namespace Glacier.DocTree.GDoc;

using System.Runtime.InteropServices;

public enum GDocNodeType : ushort
{
    Document = 0,
    Section = 1,
    Heading1 = 2,
    Heading2 = 3,
    Heading3 = 4,
    Paragraph = 5,
    CodeBlock = 6,
    Table = 7,
    List = 8,
    ListItem = 9,
    Bold = 10,
    Italic = 11,
    Link = 12,
    Text = 13
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct GDocHeader
{
    public const uint MagicValue = 0x434F4447; // "GDOC" (little-endian)
    public const uint CurrentVersion = 2;
    public const uint LegacyVersion1 = 1;
    public const int HeaderSize = 64;

    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public uint Version;
    [FieldOffset(8)] public uint StringPoolOffset;
    [FieldOffset(12)] public uint StringPoolLength;
    [FieldOffset(16)] public uint AstTableOffset;
    [FieldOffset(20)] public uint AstNodeCount;
    [FieldOffset(24)] public uint AstNodeSize;
    [FieldOffset(28)] public uint TokenSpanOffset;
    [FieldOffset(32)] public uint TokenSpanCount;
    [FieldOffset(36)] public uint TokenSpanSize;
    [FieldOffset(40)] public uint VectorOffset;
    [FieldOffset(44)] public uint VectorCount;
    [FieldOffset(48)] public uint VectorDim;

    // Unified v2 fields mapped onto bytes 52..64 (preserving Reserved0..2 source aliases)
    [FieldOffset(52)] public uint MetadataTableOffset;
    [FieldOffset(56)] public uint MetadataCount;
    [FieldOffset(60)] public uint PayloadCrc32;

    [FieldOffset(52)] public uint Reserved0;
    [FieldOffset(56)] public uint Reserved1;
    [FieldOffset(60)] public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 48)]
public struct GDocAstNode
{
    public const uint NoNode = 0xFFFFFFFF;
    public const uint NoVector = 0xFFFFFFFF;
    public const int NoMetadata = -1;
    public const int NodeSize = 48;

    public uint Id;
    public ushort NodeType;
    public ushort Flags;
    public uint ParentId;
    public uint FirstChildId;
    public uint NextSiblingId;
    public uint TextOffset;
    public uint TextLength;
    public uint VectorIndex;
    public uint TokenSpanIndex;
    public int MetadataIndex;
    public ushort MetadataCount;
    public byte CoreNodeType;
    public byte Reserved;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
internal struct GDocAstNodeV1
{
    public uint Id;
    public ushort NodeType;
    public ushort Flags;
    public uint ParentId;
    public uint FirstChildId;
    public uint NextSiblingId;
    public uint TextOffset;
    public ushort TextLength;
    public ushort TokenSpanIndex;
    public uint VectorIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
public struct GDocTokenSpan
{
    public const int SpanSize = 16;

    public uint StartOffset;
    public uint EndOffset;
    public ushort TokenType;
    public ushort Flags;
    public uint Metadata;
}
