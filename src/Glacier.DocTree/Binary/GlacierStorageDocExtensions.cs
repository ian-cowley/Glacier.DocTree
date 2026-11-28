namespace Glacier.DocTree.Binary;

using System;
using System.IO;
using System.Text.Json;
using Glacier.DocTree.Core;
using Glacier.DocTree.GDoc;

/// <summary>
/// First-party binary AST serialization extensions connecting Glacier.DocTree with the unified .gdoc v2 format.
/// Preserves exact <see cref="NodeType"/> (including Header4–Header6) and native per-node metadata entries.
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

    internal static GDocWriter BuildGDocWriter(DocNode root, int vectorDim = 768)
    {
        var writer = new GDocWriter(vectorDim: vectorDim);
        using var stack = new ValueDocNodeStack<(DocNode Node, uint ParentId)>();
        stack.Push((root, GDocAstNode.NoNode));

        while (stack.Count > 0)
        {
            var (node, parentId) = stack.Pop();
            var gType = ToGDocType(node.Type);
            ushort flags = (ushort)(ExactNodeTypeTag | ((ushort)node.Type & ExactNodeTypeMask));
            uint nodeId = writer.AddNode(gType, node.Content, parentId: parentId, flags: flags);
            writer.SetCoreNodeType(nodeId, node.Type);

            if (parentId != GDocAstNode.NoNode)
            {
                writer.LinkChild(parentId, nodeId);
            }

            if (node.Metadata is { Count: > 0 })
            {
                writer.AddMetadata(nodeId, node.Metadata);
            }

            var children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], nodeId));
            }
        }

        return writer;
    }

    /// <summary>
    /// Serializes a DocNode hierarchy into a stream in unified .gdoc v2 format.
    /// </summary>
    public static void SaveGdoc(this DocNode root, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(stream);
        BuildGDocWriter(root, vectorDim: 768).WriteToStream(stream);
    }

    /// <summary>
    /// Serializes a DocNode hierarchy to a .gdoc byte array.
    /// </summary>
    public static byte[] ToGdocBytes(this DocNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return BuildGDocWriter(root, vectorDim: 768).ToByteArray();
    }

    /// <summary>
    /// Serializes and atomically writes a DocNode hierarchy directly to a .gdoc file.
    /// </summary>
    public static void SaveGdoc(this DocNode root, string filePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        BuildGDocWriter(root, vectorDim: 768).WriteToFile(filePath);
    }

    /// <summary>
    /// Reads a .gdoc byte array or memory (v1 or unified v2) and constructs a DocNode hierarchy.
    /// </summary>
    public static DocNode LoadGdoc(ReadOnlyMemory<byte> memory)
    {
        using var reader = GDocReader.FromMemory(memory);
        if (reader.NodeCount == 0)
        {
            return new DocNode { Type = NodeType.Root };
        }

        bool isLegacyV1 = reader.Header.Version == GDocHeader.LegacyVersion1;
        ref readonly var rootAst = ref reader.GetNode(0);
        var root = new DocNode
        {
            Type = ResolveNodeType(in rootAst),
            Content = reader.GetText(in rootAst),
            Metadata = reader.GetMetadata(0)
        };

        using var stack = new ValueDocNodeStack<(uint NodeId, DocNode DocNode)>();
        var visited = new bool[reader.NodeCount];
        visited[0] = true;
        stack.Push((0u, root));

        while (stack.Count > 0)
        {
            var (parentIdx, parentNode) = stack.Pop();
            var childIndices = reader.GetChildren(parentIdx);

            // First pass in forward order to preserve child ordering and handle legacy v1 metadata blobs
            int firstAddedChildOffset = parentNode.Children.Count;
            for (int i = 0; i < childIndices.Count; i++)
            {
                uint childIdx = childIndices[i];
                if (childIdx >= reader.NodeCount)
                {
                    throw new InvalidDataException($"Invalid .gdoc AST child node index {childIdx} (node count {reader.NodeCount}).");
                }
                if (visited[childIdx])
                {
                    throw new InvalidDataException($"Cycle detected in .gdoc AST at node {childIdx}.");
                }
                visited[childIdx] = true;

                ref readonly var childAst = ref reader.GetNode(childIdx);
                string childText = reader.GetText(in childAst);

                if (isLegacyV1 && (childAst.Flags & MetadataBlobFlag) != 0)
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
                    Type = ResolveNodeType(in childAst),
                    Content = childText,
                    Metadata = reader.GetMetadata(childIdx)
                };
                parentNode.AddChild(childNode);
            }

            // Push newly added non-metadata children in reverse order for iterative pre-order traversal
            int addedIdx = parentNode.Children.Count - 1;
            for (int i = childIndices.Count - 1; i >= 0 && addedIdx >= firstAddedChildOffset; i--)
            {
                uint childIdx = childIndices[i];
                ref readonly var childAst = ref reader.GetNode(childIdx);
                if (isLegacyV1 && (childAst.Flags & MetadataBlobFlag) != 0)
                {
                    continue;
                }

                var childNode = parentNode.Children[addedIdx--];
                if (childAst.FirstChildId != GDocAstNode.NoNode)
                {
                    stack.Push((childIdx, childNode));
                }
            }
        }

        return root;
    }

    internal static NodeType ResolveNodeType(in GDocAstNode ast)
    {
        if (ast.CoreNodeType != 0 && Enum.IsDefined((NodeType)ast.CoreNodeType))
        {
            return (NodeType)ast.CoreNodeType;
        }
        return FromGDocType((GDocNodeType)ast.NodeType, ast.Flags);
    }
}
