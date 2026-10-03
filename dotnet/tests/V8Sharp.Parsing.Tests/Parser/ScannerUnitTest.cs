// Port of test/unittests/parser/scanner-unittest.cc.

namespace V8Sharp.Parsing.Tests.Parser;

public class ScannerTest
{
    private static Scanner make_scanner(string src)
    {
        var scanner = new Scanner(ScannerStream.ForTesting(src), UnoptimizedCompileFlags.ForTest());
        scanner.Initialize();
        return scanner;
    }

    private const string src_simple = "function foo() { var x = 2 * a() + b; }";

    // CHECK_TOK checks token equality, but by checking for equality of the
    // token names.
    private static void CHECK_TOK(Token a, Token b) => Assert.Equal(Token.Name(a), Token.Name(b));

    private static List<Token> ScanAll(string src)
    {
        var tokens = new List<Token>();
        Scanner scanner = make_scanner(src);
        do
        {
            tokens.Add(scanner.Next());
        } while (scanner.current_token() != Token.Eos);
        return tokens;
    }

    [Fact]
    public void Bookmarks()
    {
        // Scan through the given source and record the tokens for use as
        // reference below.
        List<Token> tokens = ScanAll(src_simple);

        // For each position:
        // - Scan through file,
        // - set a bookmark once the position is reached,
        // - scan a bit more,
        // - reset to the bookmark, and
        // - scan until the end.
        // At each step, compare to the reference token sequence generated above.
        for (int bookmark_pos = 0; bookmark_pos < tokens.Count; bookmark_pos++)
        {
            Scanner scanner = make_scanner(src_simple);
            var bookmark = new Scanner.BookmarkScope(scanner);

            for (int i = 0; i < Math.Min(bookmark_pos + 10, tokens.Count); i++)
            {
                if (i == bookmark_pos)
                {
                    bookmark.Set(scanner.peek_location().beg_pos);
                }
                CHECK_TOK(tokens[i], scanner.Next());
            }

            bookmark.Apply();
            for (int i = bookmark_pos; i < tokens.Count; i++)
            {
                CHECK_TOK(tokens[i], scanner.Next());
            }
        }
    }

    [Fact]
    public void AllThePushbacks()
    {
        (string src, Token[] tokens)[] test_cases =
        [
            ("<-x", [Token.LessThan, Token.Sub, Token.Identifier, Token.Eos]),
            ("<!x", [Token.LessThan, Token.Not, Token.Identifier, Token.Eos]),
            ("<!-x", [Token.LessThan, Token.Not, Token.Sub, Token.Identifier, Token.Eos]),
            ("<!-- xx -->\nx", [Token.Identifier, Token.Eos]),
        ];

        foreach (var test_case in test_cases)
        {
            Scanner scanner = make_scanner(test_case.src);
            for (int i = 0; test_case.tokens[i] != Token.Eos; i++)
            {
                CHECK_TOK(test_case.tokens[i], scanner.Next());
            }
            CHECK_TOK(Token.Eos, scanner.Next());
        }
    }

    private static void CheckPeekAheadAhead(string src)
    {
        List<Token> tokens = ScanAll(src);

        Scanner scanner = make_scanner(src);
        var bookmark = new Scanner.BookmarkScope(scanner);
        bookmark.Set(scanner.peek_location().beg_pos);
        bookmark.Apply();

        CHECK_TOK(tokens[0], scanner.Next());
        CHECK_TOK(tokens[1], scanner.peek());
        CHECK_TOK(tokens[2], scanner.PeekAhead());
        CHECK_TOK(tokens[3], scanner.PeekAheadAhead());
    }

    [Fact]
    public void PeekAheadAheadAwaitUsingDeclaration() => CheckPeekAheadAhead("await using a = 2;");

    [Fact]
    public void PeekAheadAheadAwaitExpression() => CheckPeekAheadAhead("await using + 5;");
}
