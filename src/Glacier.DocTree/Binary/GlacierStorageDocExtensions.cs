namespace Glacier.DocTree.Binary;

using System;
using System.IO;
using Glacier.DocTree.Core;
using Glacier.Storage.Doc;

/// <summary>
/// First-party binary AST serialization extensions connecting Glacier.DocTree with Glacier.Storage.Doc (.gdoc).
/// Enables ultra-fast memory-mapped traversal and LLM context extraction.
/// </summary>
public static class GlacierStorageDocExtensions
{
    public static GDocNodeType ToGDocType(NodeType type) => type switch
    {
        NodeType.Root => GDocNodeType.Document,
        NodeType.Header1 => GDocNodeType.Heading1,
        NodeType.Header2 => GDocNodeType.Heading2,
        NodeType.Header3 => GDocNodeType.Heading3,
        NodeType.Paragraph => GDocNodeType.Paragraph,
        NodeType.CodeBlock => GDocNodeType.CodeBlock,
        NodeType.List => GDocNodeType.List,
        _ => GDocNodeType.Text
    };

    public static NodeType FromGDocType(GDocNodeType type) => type switch
    {
        GDocNodeType.Document => NodeType.Root,
        GDocNodeType.Heading1 => NodeType.Header1,
        GDocNodeType.Heading2 => NodeType.Header2,
        GDocNodeType.Heading3 => NodeType.Header3,
        GDocNodeType.Paragraph => NodeType.Paragraph,
        GDocNodeType.CodeBlock => NodeType.CodeBlock,
        GDocNodeType.List => NodeType.List,
        _ => NodeType.Paragraph
    };

    /// <summary>
    /// Serializes a DocNode hierarchy into a stream in Glacier.Storage .gdoc format.
    /// </summary>
    public static void SaveGdoc(this DocNode root, Stream stream)
    {
        var writer = new GDocWriter(vectorDim: 768);
        WriteRecursive(writer, root, GDocAstNode.NoNode);
        writer.WriteToStream(stream);
    }

    private static uint WriteRecursive(GDocWriter writer, DocNode node, uint parentId)
    {
        var gType = ToGDocType(node.Type);
        uint nodeId = writer.AddNode(gType, node.Content, parentId: parentId);

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
        var writer = new GDocWriter(vectorDim: 768);
        WriteRecursive(writer, root, GDocAstNode.NoNode);
        return writer.ToByteArray();
    }

    /// <summary>
    /// Serializes and writes a DocNode hierarchy directly to a .gdoc file.
    /// </summary>
    public static void SaveGdoc(this DocNode root, string filePath)
    {
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
            Type = FromGDocType((GDocNodeType)ast.NodeType),
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
            var childNode = new DocNode
            {
                Type = FromGDocType((GDocNodeType)childAst.NodeType),
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
