using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glacier.DocTree.Core
{
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SerializedDocNodeDto))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class DocTreeJsonContext : JsonSerializerContext
    {
    }

    public static class TreeSerializer
    {
        /// <summary>
        /// Serializes a DocNode tree to a directory on disk.
        /// Each node is serialized to a separate JSON file, permitting lazy loading,
        /// while preserving unrelated *.json files in the target directory.
        /// </summary>
        public static void Serialize(DocNode root, string directoryPath)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrEmpty(directoryPath)) throw new ArgumentException("Directory path cannot be null or empty.", nameof(directoryPath));

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var previousChildIds = CollectPreviousChildIds(directoryPath);
            var writtenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "root" };

            using var stack = new ValueDocNodeStack<(DocNode Node, string NodeId)>();
            var childFilesToWrite = new List<(DocNode Node, string NodeId, List<string> ChildrenIds)>();

            var rootChildrenIds = AllocateChildrenIds(root);
            for (int i = root.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((root.Children[i], rootChildrenIds[i]));
            }

            while (stack.Count > 0)
            {
                var (node, nodeId) = stack.Pop();
                writtenIds.Add(nodeId);
                var childrenIds = AllocateChildrenIds(node);
                childFilesToWrite.Add((node, nodeId, childrenIds));

                for (int i = node.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push((node.Children[i], childrenIds[i]));
                }
            }

            // Write all new child JSON files first
            for (int i = 0; i < childFilesToWrite.Count; i++)
            {
                var (node, nodeId, childrenIds) = childFilesToWrite[i];
                WriteNodeJson(directoryPath, nodeId, node, childrenIds);
            }

            // Atomically write/replace root.json
            WriteRootJsonAtomic(directoryPath, root, rootChildrenIds);

            // Clean up only stale child JSON files belonging to the previous serialized tree
            foreach (string oldId in previousChildIds)
            {
                if (writtenIds.Contains(oldId)) continue;
                string stalePath = Path.Combine(directoryPath, $"{oldId}.json");
                try
                {
                    File.Delete(stalePath);
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
            }
        }

        private static List<string> AllocateChildrenIds(DocNode node)
        {
            int count = node.Children.Count;
            if (count == 0) return new List<string>();
            var ids = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                ids.Add(Guid.NewGuid().ToString("N"));
            }
            return ids;
        }

        private static void WriteNodeJson(string directoryPath, string nodeId, DocNode node, List<string> childrenIds)
        {
            var dto = new SerializedDocNodeDto
            {
                Id = nodeId,
                Type = node.Type,
                Content = node.Content ?? string.Empty,
                Metadata = node.Metadata ?? new Dictionary<string, string>(),
                ChildrenIds = childrenIds
            };

            string filePath = Path.Combine(directoryPath, $"{nodeId}.json");
            string json = JsonSerializer.Serialize(dto, DocTreeJsonContext.Default.SerializedDocNodeDto);
            File.WriteAllText(filePath, json);
        }

        private static void WriteRootJsonAtomic(string directoryPath, DocNode root, List<string> rootChildrenIds)
        {
            var dto = new SerializedDocNodeDto
            {
                Id = "root",
                Type = root.Type,
                Content = root.Content ?? string.Empty,
                Metadata = root.Metadata ?? new Dictionary<string, string>(),
                ChildrenIds = rootChildrenIds
            };

            string rootPath = Path.Combine(directoryPath, "root.json");
            string tmpPath = Path.Combine(directoryPath, $"root.json.tmp.{Guid.NewGuid():N}");
            try
            {
                string json = JsonSerializer.Serialize(dto, DocTreeJsonContext.Default.SerializedDocNodeDto);
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, rootPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        private static HashSet<string> CollectPreviousChildIds(string directoryPath)
        {
            var previousChildIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string rootPath = Path.Combine(directoryPath, "root.json");
            if (!File.Exists(rootPath))
            {
                return previousChildIds;
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "root" };
            using var stack = new ValueDocNodeStack<string>();
            stack.Push("root");

            while (stack.Count > 0)
            {
                string currentId = stack.Pop();
                string filePath = Path.Combine(directoryPath, $"{currentId}.json");
                if (!File.Exists(filePath))
                {
                    continue;
                }

                SerializedDocNodeDto? dto;
                try
                {
                    string json = File.ReadAllText(filePath);
                    dto = JsonSerializer.Deserialize(json, DocTreeJsonContext.Default.SerializedDocNodeDto);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (dto?.ChildrenIds == null) continue;
                for (int i = 0; i < dto.ChildrenIds.Count; i++)
                {
                    string childId = dto.ChildrenIds[i];
                    if (IsSafeNodeId(childId) && visited.Add(childId))
                    {
                        previousChildIds.Add(childId);
                        stack.Push(childId);
                    }
                }
            }

            return previousChildIds;
        }

        private static bool IsSafeNodeId(string? nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId)) return false;
            if (string.Equals(nodeId, "root", StringComparison.OrdinalIgnoreCase)) return false;
            return nodeId.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && !nodeId.Contains("..", StringComparison.Ordinal);
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
