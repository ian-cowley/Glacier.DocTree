using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Glacier.DocTree.Core
{
    public class LazyDocNode : DocNode
    {
        private readonly string _directoryPath;
        private readonly string _nodeId;
        private readonly object _loadLock = new();
        private bool _isLoaded;
        private NodeType _type;
        private string _content = string.Empty;
        private Dictionary<string, string> _metadata = new();
        private List<DocNode> _children = new();
        private DocNode? _parent;

        public LazyDocNode(string directoryPath, string nodeId)
        {
            _directoryPath = directoryPath ?? throw new ArgumentNullException(nameof(directoryPath));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        }

        public string NodeId => _nodeId;

        private void EnsureLoaded()
        {
            if (_isLoaded) return;

            lock (_loadLock)
            {
                if (_isLoaded) return;

                string filePath = Path.Combine(_directoryPath, $"{_nodeId}.json");
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Serialized node file not found: {filePath}");
                }

                string json = File.ReadAllText(filePath);
                var dto = JsonSerializer.Deserialize<SerializedDocNodeDto>(json);
                if (dto != null)
                {
                    _type = dto.Type;
                    _content = dto.Content ?? string.Empty;
                    _metadata = dto.Metadata ?? new Dictionary<string, string>();
                    _children = new List<DocNode>();

                    if (dto.ChildrenIds != null)
                    {
                        foreach (var childId in dto.ChildrenIds)
                        {
                            var child = new LazyDocNode(_directoryPath, childId)
                            {
                                Parent = this
                            };
                            _children.Add(child);
                        }
                    }
                }

                _isLoaded = true;
            }
        }

        public override NodeType Type
        {
            get { EnsureLoaded(); return _type; }
            set { EnsureLoaded(); _type = value; }
        }

        public override string Content
        {
            get { EnsureLoaded(); return _content; }
            set { EnsureLoaded(); _content = value; }
        }

        public override Dictionary<string, string> Metadata
        {
            get { EnsureLoaded(); return _metadata; }
            set { EnsureLoaded(); _metadata = value; }
        }

        public override DocNode? Parent
        {
            get => _parent;
            set => _parent = value;
        }

        public override List<DocNode> Children
        {
            get { EnsureLoaded(); return _children; }
            set { EnsureLoaded(); _children = value; }
        }

        public override void AddChild(DocNode child)
        {
            EnsureLoaded();
            child.Parent = this;
            _children.Add(child);
        }
    }

    public class SerializedDocNodeDto
    {
        public string Id { get; set; } = string.Empty;
        public NodeType Type { get; set; }
        public string Content { get; set; } = string.Empty;
        public Dictionary<string, string> Metadata { get; set; } = new();
        public List<string> ChildrenIds { get; set; } = new();
    }
}
