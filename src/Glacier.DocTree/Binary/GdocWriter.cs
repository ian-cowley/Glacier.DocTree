using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Glacier.DocTree.Core;

namespace Glacier.DocTree.Binary
{
    public static class GdocWriter
    {
        public const uint GdocMagic = 0x47444F43; // "GDOC"
        public const ushort CurrentVersion = 1;
        public const int HeaderSize = 64;

        public static void Write(DocNode root, string filePath)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 1. Flatten tree and build string arena & metadata table
            var flatNodes = new List<DocNode>();
            var parentIndices = new List<int>();
            FlattenTree(root, -1, flatNodes, parentIndices);

            uint nodeCount = (uint)flatNodes.Count;
            var nodeEntries = new GdocNodeEntry[nodeCount];
            var metadataEntries = new List<GdocMetadataEntry>();
            using var stringArena = new MemoryStream();

            // First pass: populate string offsets and metadata
            for (int i = 0; i < flatNodes.Count; i++)
            {
                var node = flatNodes[i];
                uint contentOffset = (uint)stringArena.Position;
                uint contentLength = 0;

                if (!string.IsNullOrEmpty(node.Content))
                {
                    byte[] contentBytes = Encoding.UTF8.GetBytes(node.Content);
                    stringArena.Write(contentBytes, 0, contentBytes.Length);
                    contentLength = (uint)contentBytes.Length;
                }

                // Process metadata
                ushort metaCount = 0;
                int firstMetaIndex = -1;
                int lastMetaIndex = -1;

                if (node.Metadata != null && node.Metadata.Count > 0)
                {
                    metaCount = (ushort)node.Metadata.Count;
                    foreach (var kvp in node.Metadata)
                    {
                        uint kOffset = (uint)stringArena.Position;
                        byte[] kBytes = Encoding.UTF8.GetBytes(kvp.Key);
                        stringArena.Write(kBytes, 0, kBytes.Length);

                        uint vOffset = (uint)stringArena.Position;
                        byte[] vBytes = Encoding.UTF8.GetBytes(kvp.Value);
                        stringArena.Write(vBytes, 0, vBytes.Length);

                        int newMetaIndex = metadataEntries.Count;
                        if (firstMetaIndex == -1) firstMetaIndex = newMetaIndex;
                        if (lastMetaIndex != -1)
                        {
                            var prev = metadataEntries[lastMetaIndex];
                            metadataEntries[lastMetaIndex] = new GdocMetadataEntry(
                                prev.KeyOffset, prev.KeyLength, prev.ValOffset, prev.ValLength, newMetaIndex);
                        }

                        metadataEntries.Add(new GdocMetadataEntry(
                            kOffset, (ushort)kBytes.Length, vOffset, (ushort)vBytes.Length, -1));
                        lastMetaIndex = newMetaIndex;
                    }
                }

                byte flags = 0;
                if (metaCount > 0) flags |= 0x01;

                nodeEntries[i] = new GdocNodeEntry(
                    (byte)node.Type,
                    flags,
                    contentOffset,
                    contentLength,
                    parentIndices[i],
                    -1, // FirstChildIndex - resolved in second pass
                    -1, // NextSiblingIndex - resolved in second pass
                    (ushort)node.Children.Count,
                    metaCount,
                    firstMetaIndex
                );
            }

            // Second pass: link first-child and next-sibling indices
            var nodeToIndex = new Dictionary<DocNode, int>();
            for (int i = 0; i < flatNodes.Count; i++)
            {
                nodeToIndex[flatNodes[i]] = i;
            }

            for (int i = 0; i < flatNodes.Count; i++)
            {
                var node = flatNodes[i];
                int firstChildIdx = -1;
                if (node.Children.Count > 0 && nodeToIndex.TryGetValue(node.Children[0], out int fc))
                {
                    firstChildIdx = fc;
                }

                // Sibling linking: if this node has a parent, find this node's sibling
                int nextSiblingIdx = -1;
                if (node.Parent != null)
                {
                    int siblingPos = node.Parent.Children.IndexOf(node);
                    if (siblingPos >= 0 && siblingPos + 1 < node.Parent.Children.Count)
                    {
                        if (nodeToIndex.TryGetValue(node.Parent.Children[siblingPos + 1], out int ns))
                        {
                            nextSiblingIdx = ns;
                        }
                    }
                }

                var old = nodeEntries[i];
                nodeEntries[i] = new GdocNodeEntry(
                    old.NodeType,
                    old.Flags,
                    old.ContentOffset,
                    old.ContentLength,
                    old.ParentIndex,
                    firstChildIdx,
                    nextSiblingIdx,
                    old.ChildCount,
                    old.MetadataCount,
                    old.MetadataIndex
                );
            }

            // Calculate offsets
            ulong nodeTableOffset = (ulong)HeaderSize;
            ulong nodeTableLength = (ulong)(nodeCount * 32);

            ulong stringArenaOffset = nodeTableOffset + nodeTableLength;
            byte[] stringArenaBytes = stringArena.ToArray();
            ulong stringArenaLength = (ulong)stringArenaBytes.Length;

            ulong metadataTableOffset = stringArenaOffset + stringArenaLength;
            ulong metadataTableLength = (ulong)(metadataEntries.Count * 16);

            // Compute checksum over payload
            using var payloadStream = new MemoryStream();
            using (var bw = new BinaryWriter(payloadStream))
            {
                // Write Node Table
                for (int i = 0; i < nodeCount; i++)
                {
                    var entry = nodeEntries[i];
                    bw.Write(entry.NodeType);
                    bw.Write(entry.Flags);
                    bw.Write(entry.Reserved);
                    bw.Write(entry.ContentOffset);
                    bw.Write(entry.ContentLength);
                    bw.Write(entry.ParentIndex);
                    bw.Write(entry.FirstChildIndex);
                    bw.Write(entry.NextSiblingIndex);
                    bw.Write(entry.ChildCount);
                    bw.Write(entry.MetadataCount);
                    bw.Write(entry.MetadataIndex);
                }

                // Write String Arena
                bw.Write(stringArenaBytes);

                // Write Metadata Table
                for (int i = 0; i < metadataEntries.Count; i++)
                {
                    var m = metadataEntries[i];
                    bw.Write(m.KeyOffset);
                    bw.Write(m.KeyLength);
                    bw.Write(m.ValOffset);
                    bw.Write(m.ValLength);
                    bw.Write(m.NextMetadataIndex);
                }
            }

            byte[] payloadBytes = payloadStream.ToArray();
            uint checksum = GdocCrc32.Compute(payloadBytes);

            // Build Header (64 bytes)
            byte[] header = new byte[HeaderSize];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), GdocMagic);
            BitConverter.TryWriteBytes(header.AsSpan(4, 2), CurrentVersion);
            BitConverter.TryWriteBytes(header.AsSpan(6, 4), nodeCount);
            BitConverter.TryWriteBytes(header.AsSpan(10, 8), stringArenaOffset);
            BitConverter.TryWriteBytes(header.AsSpan(18, 8), stringArenaLength);
            BitConverter.TryWriteBytes(header.AsSpan(26, 8), nodeTableOffset);
            BitConverter.TryWriteBytes(header.AsSpan(34, 8), nodeTableLength);
            BitConverter.TryWriteBytes(header.AsSpan(42, 8), metadataTableOffset);
            BitConverter.TryWriteBytes(header.AsSpan(50, 8), metadataTableLength);
            BitConverter.TryWriteBytes(header.AsSpan(58, 4), checksum);

            // Write atomic output via temporary file
            string tmpPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(header, 0, header.Length);
                    fs.Write(payloadBytes, 0, payloadBytes.Length);
                    fs.Flush(true);
                }

                File.Move(tmpPath, filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        private static void FlattenTree(DocNode node, int parentIdx, List<DocNode> flatNodes, List<int> parentIndices)
        {
            int currentIdx = flatNodes.Count;
            flatNodes.Add(node);
            parentIndices.Add(parentIdx);

            for (int i = 0; i < node.Children.Count; i++)
            {
                FlattenTree(node.Children[i], currentIdx, flatNodes, parentIndices);
            }
        }
    }
}
