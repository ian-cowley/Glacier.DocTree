namespace Glacier.DocTree.Binary;

using System;
using System.IO;
using System.Text.Json;
using Glacier.DocTree.Core;
using Glacier.DocTree.GDoc;

/// <summary>
/// First-party binary AST serialization extensions connecting Glacier.DocTree with Glacier.Storage.Doc (.gdoc).
/// Preserves exact NodeType (including Header4–Header6) and per-node Metadata dictionaries.
/// </summary>
public static class GlacierStorageDocExtensions
{
    private const ushort ExactNodeTypeTag = 0x4000;
    private const ushort ExactNodeTypeMask = 0x00FF;
    private const ushort MetadataBlobFlag = 0x8000;

    public static GDocNodeType ToGDocType(NodeType type) => type switch
    {
        NodeType.Root => GDocNodeType.Document,
        NodeType.Header1 => GDocNodeType.Heading1,
        NodeType.Header2 => GDocNodeType.Heading2,
        NodeType.Header3 => GDocNodeType.Heading3,
        NodeType.Header4 => GDocNodeType.Section,
        NodeType.Header5 => GDocNodeType.Section,
        NodeType.Header6 => GDocNodeType.Section,
        NodeType.Paragraph => GDocNodeType.Paragraph,
        NodeType.CodeBlock => GDocNodeType.CodeBlock,
        NodeType.List => GDocNodeType.List,
        _ => GDocNodeType.Text
    };

    public static NodeType FromGDocType(GDocNodeType type, ushort flags = 0)
    {
        if ((flags & ExactNodeTypeTag) != 0)
        {
            byte raw = (byte)(flags & ExactNodeTypeMask);
            if (Enum.IsDefined((NodeType)raw))
            {
                return (NodeType)raw;
            }
        }

        return type switch
        {
            GDocNodeType.Document => NodeType.Root,
            GDocNodeType.Heading1 => NodeType.Header1,
            GDocNodeType.Heading2 => NodeType.Header2,
            GDocNodeType.Heading3 => NodeType.Header3,
            GDocNodeType.Section => NodeType.Header4,
            GDocNodeType.Paragraph => NodeType.Paragraph,
            GDocNodeType.CodeBlock => NodeType.CodeBlock,
            GDocNodeType.List => NodeType.List,
            _ => NodeType.Paragraph
        };
    }

    /// <summary>
    /// Serializes a DocNode hierarchy into a stream in Glacier.Storage .gdoc format.
    /// </summary>
    public static void SaveGdoc(this DocNode root, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(stream);
        var writer = new GDocWriter(vectorDim: 768);
        WriteRecursive(writer, root, GDocAstNode.NoNode);
        writer.WriteToStream(stream);
    }

    private static uint WriteRecursive(GDocWriter writer, DocNode node, uint parentId)
    {
        var gType = ToGDocType(node.Type);
        ushort flags = (ushort)(ExactNodeTypeTag | ((ushort)node.Type & ExactNodeTypeMask));
        uint nodeId = writer.AddNode(gType, node.Content, parentId: parentId, flags: flags);

        if (node.Metadata is { Count: > 0 })
        {
            string metaJson = JsonSerializer.Serialize(node.Metadata, DocTreeJsonContext.Default.DictionaryStringString);
            uint metaNodeId = writer.AddNode(GDocNodeType.Text, metaJson, parentId: nodeId, flags: MetadataBlobFlag);
            writer.LinkChild(nodeId, metaNodeId);
        }

        foreach (var child in node.Children)
        {
            uint childId = WriteRecursive(writer, child, nodeId);
            writer.LinkChild(nodeId, childId);
        }

        return nodeId;
    }

    /// <summary>
    /// Serializes a DocNode hierarchy to a .gdoc byte array.
    /// </summary>
    public static byte[] ToGdocBytes(this DocNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var writer = new GDocWriter(vectorDim: 768);
        WriteRecursive(writer, root, GDocAstNode.NoNode);
        return writer.ToByteArray();
    }

    /// <summary>
    /// Serializes and writes a DocNode hierarchy directly to a .gdoc file.
    /// </summary>
    public static void SaveGdoc(this DocNode root, string filePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var bytes = ToGdocBytes(root);
        File.WriteAllBytes(filePath, bytes);
    }

    /// <summary>
    /// Reads a .gdoc byte array or memory and constructs a DocNode hierarchy.
    /// </summary>
    public static DocNode LoadGdoc(ReadOnlyMemory<byte> memory)
    {
        using var reader = GDocReader.FromMemory(memory);
        if (reader.NodeCount == 0)
        {
            return new DocNode { Type = NodeType.Root };
        }

        ref readonly var ast = ref reader.GetNode(0);
        string text = reader.GetText(in ast);
        var root = new DocNode
        {
            Type = FromGDocType((GDocNodeType)ast.NodeType, ast.Flags),
            Content = text
        };

        ReconstructChildren(reader, 0, root);
        return root;
    }

    private static void ReconstructChildren(GDocReader reader, uint parentIdx, DocNode parentNode)
    {
        var childIndices = reader.GetChildren(parentIdx);
        foreach (var childIdx in childIndices)
        {
            ref readonly var childAst = ref reader.GetNode(childIdx);
            string childText = reader.GetText(in childAst);

            if ((childAst.Flags & MetadataBlobFlag) != 0)
            {
                if (!string.IsNullOrEmpty(childText))
                {
                    var decoded = JsonSerializer.Deserialize(childText, DocTreeJsonContext.Default.DictionaryStringString);
                    if (decoded != null)
                    {
                        foreach (var kvp in decoded)
                        {
                            parentNode.Metadata[kvp.Key] = kvp.Value;
                        }
                    }
                }
                continue;
            }

            var childNode = new DocNode
            {
                Type = FromGDocType((GDocNodeType)childAst.NodeType, childAst.Flags),
                Content = childText
            };
            parentNode.AddChild(childNode);

            if (childAst.FirstChildId != GDocAstNode.NoNode)
            {
                ReconstructChildren(reader, childIdx, childNode);
            }
        }
    }
}
