// Port of regexp::CompileData (src/regexp/regexp.h).

namespace V8Sharp.RegExp;

public enum CompilationTarget { kBytecode, kNative }

public sealed class RegExpCompileData
{
    /// <summary>The parsed AST as produced by the parser.</summary>
    public RegExpTree? Tree;

    /// <summary>The compiled node graph as produced by RegExpTree.ToNode.</summary>
    public RegExpNode? Node;

    /// <summary>The generated code (bytecode) as produced by the compiler.</summary>
    public byte[]? Code;

    /// <summary>True, iff the pattern is a 'simple' atom with zero captures. In
    /// other words, the pattern consists of a string with no metacharacters and
    /// special regexp features, and can be implemented as a standard string
    /// search.</summary>
    public bool Simple = true;

    /// <summary>True, iff the pattern is anchored at the start of the string with '^'.</summary>
    public bool ContainsAnchor;

    /// <summary>Only set if the pattern contains named captures.</summary>
    public List<RegExpCapture>? NamedCaptures;

    /// <summary>The error. Only used if an error occurred during parsing or compilation.</summary>
    public RegExpError Error = RegExpError.None;

    /// <summary>The position at which the error was detected.</summary>
    public int ErrorPos;

    /// <summary>The number of capture groups, without the global capture \0.</summary>
    public int CaptureCount;

    /// <summary>The number of registers used by the generated code.</summary>
    public int RegisterCount;

    public CompilationTarget CompilationTarget = CompilationTarget.kBytecode;
}
