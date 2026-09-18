using System;

namespace Glacier.DocTree.Parser
{
    /// <summary>
    /// High-performance ref struct enumerating lines from a ReadOnlySpan without any heap allocation.
    /// Supports Windows (\r\n), Unix (\n), and legacy Mac (\r) line endings.
    /// </summary>
    public ref struct SpanLineEnumerator
    {
        private ReadOnlySpan<char> _remaining;
        private ReadOnlySpan<char> _current;

        public SpanLineEnumerator(ReadOnlySpan<char> text)
        {
            _remaining = text;
            _current = default;
        }

        public readonly ReadOnlySpan<char> Current => _current;

        public readonly SpanLineEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_remaining.IsEmpty)
            {
                return false;
            }

            int idx = _remaining.IndexOfAny('\r', '\n');
            if (idx < 0)
            {
                _current = _remaining;
                _remaining = ReadOnlySpan<char>.Empty;
                return true;
            }

            _current = _remaining[..idx];

            int skip = 1;
            if (_remaining[idx] == '\r' && idx + 1 < _remaining.Length && _remaining[idx + 1] == '\n')
            {
                skip = 2;
            }

            _remaining = _remaining[(idx + skip)..];
            return true;
        }
    }
}
