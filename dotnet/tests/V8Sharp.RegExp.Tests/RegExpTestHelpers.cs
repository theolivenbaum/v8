using System.Text;

namespace V8Sharp.RegExp.Tests;

internal static class RegExpTestHelpers
{
    /// <summary>
    /// Runs pattern/flags on subject from lastIndex and returns a JS-like
    /// rendering of the match: null, or [index,"m0","m1"|undefined,...].
    /// </summary>
    public static string Exec(string pattern, string flags, string subject, int lastIndex = 0)
    {
        RegExpFlags f = RegExpFlagsExtensions.FromString(flags) ?? throw new ArgumentException("bad flags");
        RegExpCompileResult r = RegExpEngine.Compile(pattern, f);
        if (!r.Succeeded) return "SyntaxError: " + r.ErrorMessage;
        CompiledRegExp re = r.RegExp!;
        int[] regs = new int[re.RegistersPerMatch];
        int n = re.Exec(subject, lastIndex, regs);
        if (n < 0) return "Exception";
        if (n == 0) return "null";
        var sb = new StringBuilder();
        sb.Append('[').Append(regs[0]);
        for (int i = 0; i <= re.CaptureCount; i++)
        {
            sb.Append(',');
            if (regs[2 * i] < 0) sb.Append("undefined");
            else sb.Append('"').Append(subject, regs[2 * i], regs[2 * i + 1] - regs[2 * i]).Append('"');
        }
        sb.Append(']');
        return sb.ToString();
    }
}
