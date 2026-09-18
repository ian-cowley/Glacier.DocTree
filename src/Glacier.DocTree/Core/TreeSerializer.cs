using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Glacier.DocTree.Core
{
    public static class TreeSerializer
    {
        /// <summary>
        /// Serializes a DocNode tree to a directory on disk.
        /// Each node is serialized to a separate JSON file, permitting lazy loading.
        /// </summary>
        public static void Serialize(DocNode root, string directoryPath)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrEmpty(directoryPath)) throw new ArgumentException("Directory path cannot be null or empty.", nameof(directoryPath));

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            else
            {
                // Clear existing files in the directory
                foreach (var file in Directory.GetFiles(directoryPath, "*.json"))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            SerializeNode(root, "root", directoryPath);
        }

        /// <summary>
        /// Deserializes a DocNode tree lazily. Only the root proxy is returned, and sub-nodes
        /// are loaded on-demand.
        /// </summary>
        public static DocNode DeserializeLazy(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) throw new ArgumentException("Directory path cannot be null or empty.", nameof(directoryPath));

            string rootPath = Path.Combine(directoryPath, "root.json");
            if (!File.Exists(rootPath))
            {
                throw new FileNotFoundException($"Serialized root node file not found: {rootPath}");
            }

            return new LazyDocNode(directoryPath, "root");
        }

        private static void SerializeNode(DocNode node, string nodeId, string directoryPath)
        {
            var childrenIds = new List<string>();

            // If the node is a LazyDocNode and hasn't been loaded, we could preserve its existing children IDs.
            // But normally we'll just traverse whatever is loaded or load it as we go.
            for (int i = 0; i < node.Children.Count; i++)
            {
                string childId = Guid.NewGuid().ToString("N");
                childrenIds.Add(childId);
                SerializeNode(node.Children[i], childId, directoryPath);
            }

            var dto = new SerializedDocNodeDto
            {
                Id = nodeId,
                Type = node.Type,
                Content = node.Content ?? string.Empty,
                Metadata = node.Metadata ?? new Dictionary<string, string>(),
                ChildrenIds = childrenIds
            };

            string filePath = Path.Combine(directoryPath, $"{nodeId}.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(dto, options);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Serializes a DocNode tree into a single contiguous memory-mapped binary (.gdoc) format.
        /// </summary>
        public static void SerializeBinary(DocNode root, string filePath)
        {
            Binary.GdocWriter.Write(root, filePath);
        }

        /// <summary>
        /// Opens a memory-mapped .gdoc file for zero-copy binary reading and navigation.
        /// </summary>
        public static Binary.GdocReader OpenBinary(string filePath)
        {
            return new Binary.GdocReader(filePath);
        }

        /// <summary>
        /// Deserializes a DocNode tree from a .gdoc binary file into an in-memory DocNode AST.
        /// </summary>
        public static DocNode DeserializeBinary(string filePath)
        {
            using var reader = new Binary.GdocReader(filePath);
            return reader.ToDocNodeTree();
        }
    }
}
