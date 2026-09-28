// Port of test/unittests/interpreter/bytecode-expectations-parser.h/.cc: the
// reader of the golden files in test/unittests/interpreter/bytecode_expectations.
using System.Text;

namespace V8Sharp.Tests.Interpreter;

public sealed class BytecodeExpectationsHeaderOptions
{
    public bool wrap = true;
    public bool module;
    public bool top_level;
    public bool print_callee;
    public string test_function_name = "";
    public string extra_flags = "";
}

public sealed class BytecodeExpectationsParser(TextReader reader)
{
    int _currentLine;

    public int CurrentLine => _currentLine;

    static bool ParseBoolean(string value) => value switch
    {
        "yes" => true,
        "no" => false,
        _ => throw new FormatException("Unrecognised boolean: " + value + " (must be 'yes' or 'no')"),
    };

    static string? GetHeaderParam(string line, string key)
    {
        if (!line.StartsWith(key, StringComparison.Ordinal)) return null;
        string post_key = line[key.Length..];
        if (!post_key.StartsWith(": ", StringComparison.Ordinal)) return null;
        return post_key[2..];
    }

    static string UnescapeString(string escaped_string)
    {
        var unescaped_string = new StringBuilder();
        bool previous_was_backslash = false;
        foreach (char c in escaped_string)
        {
            if (previous_was_backslash)
            {
                // If it was not an escape sequence, emit the previous backslash.
                if (c != '\\' && c != '"') unescaped_string.Append('\\');
                unescaped_string.Append(c);
                previous_was_backslash = false;
            }
            else if (c == '\\')
            {
                previous_was_backslash = true;
                // Defer emission to the point where we can check if it was an escape.
            }
            else
            {
                unescaped_string.Append(c);
            }
        }
        // Emit the previous backslash if it wasn't emitted.
        if (previous_was_backslash) unescaped_string.Append('\\');
        return unescaped_string.ToString();
    }

    bool GetLine(out string line)
    {
        _currentLine++;
        string? l = reader.ReadLine();
        line = l ?? "";
        return l is not null;
    }

    public BytecodeExpectationsHeaderOptions ParseHeader()
    {
        var options = new BytecodeExpectationsHeaderOptions();
        string line;

        // Skip to the beginning of the options header.
        while (GetLine(out line))
        {
            if (line == "---") break;
        }

        while (GetLine(out line))
        {
            string? v;
            if ((v = GetHeaderParam(line, "module")) is not null) options.module = ParseBoolean(v);
            else if ((v = GetHeaderParam(line, "wrap")) is not null) options.wrap = ParseBoolean(v);
            else if ((v = GetHeaderParam(line, "test function name")) is not null) options.test_function_name = v;
            else if ((v = GetHeaderParam(line, "top level")) is not null) options.top_level = ParseBoolean(v);
            else if ((v = GetHeaderParam(line, "print callee")) is not null) options.print_callee = ParseBoolean(v);
            else if ((v = GetHeaderParam(line, "extra flags")) is not null) options.extra_flags = v;
            else if (line.Length == 0) continue;
            else if (line == "---") break;
            else throw new FormatException("Unrecognised option: " + line);
        }
        return options;
    }

    public bool ReadNextSnippet(out string snippet, out int lineOut)
    {
        var string_out = new StringBuilder();
        bool found_begin_snippet = false;
        lineOut = 0;
        while (GetLine(out string line))
        {
            if (line == "snippet: \"")
            {
                found_begin_snippet = true;
                lineOut = _currentLine;
                continue;
            }
            if (!found_begin_snippet) continue;
            if (line == "\"")
            {
                snippet = string_out.ToString();
                return true;
            }
            if (line.Length == 0)
            {
                string_out.Append('\n'); // consume empty line.
                continue;
            }
            if (line.Length < 2) throw new FormatException("snippet line without indent"); // We should have the indent.
            line = UnescapeString(line);
            string_out.Append(line, 2, line.Length - 2);
            string_out.Append('\n');
        }
        snippet = string_out.ToString();
        return false;
    }

    public string ReadToNextSeparator()
    {
        var @out = new StringBuilder();
        while (GetLine(out string line))
        {
            if (line == "---") break;
            @out.Append(line).Append('\n');
        }
        return @out.ToString();
    }

    /// <summary>The golden files (*.golden) in |directoryPath|.</summary>
    public static List<string> CollectGoldenFiles(string directoryPath)
    {
        var ret = new List<string>();
        if (!Directory.Exists(directoryPath)) return ret;
        foreach (string file in Directory.EnumerateFiles(directoryPath))
        {
            if (Path.GetExtension(file) == ".golden") ret.Add(file);
        }
        ret.Sort(StringComparer.Ordinal);
        return ret;
    }

    /// <summary>test/unittests/interpreter/bytecode_expectations, found by
    /// walking up from the test binary to the V8 checkout.</summary>
    public static string GoldenFileDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "test", "unittests", "interpreter", "bytecode_expectations");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return "test/unittests/interpreter/bytecode_expectations";
    }
}
