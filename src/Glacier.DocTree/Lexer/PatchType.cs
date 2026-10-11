namespace Glacier.DocTree.Lexer;

/// <summary>
/// Categorizes the syntactic or structural nature of a compressed byte patch.
/// </summary>
public enum PatchType : ushort
{
    /// <summary>Raw unformatted or continuous text span.</summary>
    VerbatimText = 0,

    /// <summary>Scope initiation delimiter ('{', '[', '(').</summary>
    ScopeOpen = 1,

    /// <summary>Scope termination delimiter ('}', ']', ')').</summary>
    ScopeClose = 2,

    /// <summary>Type, function, or symbol declaration span.</summary>
    Declaration = 3,

    /// <summary>Terminated statement or line ending in semicolon or newline.</summary>
    Statement = 4,

    /// <summary>Key-value pair delimiter or association (e.g. JSON or YAML attribute).</summary>
    KeyValuePair = 5,

    /// <summary>Markdown or documentation section header (e.g. '#', '##').</summary>
    DocHeader = 6,

    /// <summary>Syntactic indentation cluster (runs of spaces or tabs).</summary>
    IndentationCluster = 7
}
