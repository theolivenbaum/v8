// Port of the parsing half of d8's TryExecuteBundle (src/d8/d8.cc): the
// --bundle file format that puts scripts, modules and module entry points in
// one file. The execution half is the shell's (d8sharp's Shell and the
// TestRunner's D8Shell).
namespace V8Sharp.D8;

/// <summary>A parsed d8 bundle: its modules by normalized name, and what runs in which order.</summary>
public sealed class D8Bundle
{
    /// <summary>bundle_module_files: normalized module name to source.</summary>
    public Dictionary<string, string> ModuleFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>The execution order: a script's source, or a module entry point's normalized name.</summary>
    public List<(bool IsScript, string ContentOrName)> ExecutionOrder { get; } = [];

    /// <summary>Warnings d8 prints while parsing (unknown markers).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// TryExecuteBundle's first pass. A file whose first non-comment text is a
    /// "// JS_BUNDLE_" marker is a bundle of scripts ("// JS_BUNDLE_SCRIPT"),
    /// modules ("// JS_BUNDLE_MODULE:name") and module entry points
    /// ("// JS_BUNDLE_MODULE_ENTRYPOINT[:name]"); anything else is not a
    /// bundle (null).
    /// </summary>
    public static D8Bundle? TryParse(string content, string workingDirectory)
    {
        // Find the first // JS_BUNDLE_ comment. It's either
        // "// JS_BUNDLE_SCRIPT", "// JS_BUNDLE_MODULE:filename.mjs", or "//
        // JS_BUNDLE_MODULE_ENTRYPOINT". Then find either the end of the file, or the
        // next // JS_BUNDLE_ comment. Between those locations, we have our file (or
        // module).
        const string scriptMarker = "// JS_BUNDLE_SCRIPT";
        const string moduleMarkerPrefix = "// JS_BUNDLE_MODULE:";
        const string entrypointMarkerPrefix = "// JS_BUNDLE_MODULE_ENTRYPOINT";
        const string markerPrefix = "// JS_BUNDLE_";

        int pos = 0;
        // Skip whitespace and comments before the first marker.
        while (pos < content.Length)
        {
            if (char.IsWhiteSpace(content[pos]))
            {
                pos++;
                continue;
            }
            if (pos + 2 <= content.Length && content[pos] == '/')
            {
                if (content[pos + 1] == '/')
                {
                    // Check if it's a marker.
                    if (string.CompareOrdinal(content, pos, markerPrefix, 0, markerPrefix.Length) == 0) break;
                    // Skip single line comment.
                    pos = content.IndexOf('\n', pos);
                    if (pos < 0) return null;
                    pos++;
                    continue;
                }
                if (content[pos + 1] == '*')
                {
                    // Skip multi-line comment.
                    pos = content.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                    if (pos < 0) return null;
                    pos += 2;
                    continue;
                }
            }
            // Not a bundle if we encounter anything else.
            return null;
        }
        if (pos >= content.Length) return null;

        var bundle = new D8Bundle();
        int anonModuleCounter = 0;
        while (pos < content.Length)
        {
            // We expect a marker at pos.
            if (string.CompareOrdinal(content, pos, markerPrefix, 0, markerPrefix.Length) != 0) break;

            int nlPos = content.IndexOf('\n', pos);
            string header;
            if (nlPos < 0)
            {
                header = content[pos..];
                pos = content.Length;
            }
            else
            {
                header = content[pos..nlPos];
                if (header.EndsWith('\r')) header = header[..^1];
                pos = nlPos + 1;
            }

            // Find the next marker.
            int nextMarker = content.IndexOf(markerPrefix, pos, StringComparison.Ordinal);
            int partEnd = nextMarker < 0 ? content.Length : nextMarker;
            string partContent = content[pos..partEnd];
            pos = partEnd;

            if (header == scriptMarker)
            {
                bundle.ExecutionOrder.Add((true, partContent));
            }
            else if (header.StartsWith(moduleMarkerPrefix, StringComparison.Ordinal))
            {
                string mName = header[moduleMarkerPrefix.Length..];
                bundle.ModuleFiles[D8ModuleSourceProvider.NormalizeModuleSpecifier(mName, workingDirectory)] = partContent;
            }
            else if (header.StartsWith(entrypointMarkerPrefix, StringComparison.Ordinal))
            {
                string mName = header.Length > entrypointMarkerPrefix.Length && header[entrypointMarkerPrefix.Length] == ':'
                    ? header[(entrypointMarkerPrefix.Length + 1)..]
                    : "entrypoint_" + anonModuleCounter++ + ".mjs";
                string normalizedName = D8ModuleSourceProvider.NormalizeModuleSpecifier(mName, workingDirectory);
                bundle.ModuleFiles[normalizedName] = partContent;
                bundle.ExecutionOrder.Add((false, normalizedName));
            }
            else
            {
                bundle.Warnings.Add("Warning: Unknown bundle marker: " + header + "\n");
            }
        }
        return bundle;
    }
}
