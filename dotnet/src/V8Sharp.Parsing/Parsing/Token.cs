// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/token.h and src/parsing/token.cc.
//
// V8's Token::Value enum becomes the enum `Token` (members without the `k`
// prefix, same order); the static Token:: predicates are C# 14 static
// extension members on it, so call sites read `Token.IsKeyword(tok)` as in V8.
// Token::String() is `Token.StringOf()`, since `Token.String` is the kString value.

using System.Runtime.CompilerServices;
using V8Sharp.Common;

namespace V8Sharp.Parsing;

public enum Token : byte
{
    // BEGIN PropertyOrCall
    // BEGIN Member
    // BEGIN Template
    // ES6 Template Literals
    TemplateSpan,
    TemplateTail,
    // END Template

    // Punctuators (ECMA-262, section 7.7, page 15).
    // BEGIN Property
    Period,
    LeftBracket,
    // END Property
    // END Member
    QuestionPeriod,
    LeftParen,
    // END PropertyOrCall
    RightParen,
    RightBracket,
    LeftBrace,
    Colon,
    Ellipsis,
    Conditional,
    // BEGIN AutoSemicolon
    Semicolon,
    RightBrace,
    // End of source indicator.
    Eos,
    // END AutoSemicolon

    // BEGIN ArrowOrAssignmentOp
    Arrow,
    // BEGIN AssignmentOp
    // IsAssignmentOp() relies on this block of enum values being
    // contiguous and sorted in the same order!
    Init, // AST-use only.
    Assign,
    AssignNullish,
    AssignOr,
    AssignAnd,
    AssignBitOr,
    AssignBitXor,
    AssignBitAnd,
    AssignShl,
    AssignSar,
    AssignShr,
    AssignMul,
    AssignDiv,
    AssignMod,
    AssignExp,
    AssignAdd,
    AssignSub,
    // END AssignmentOp
    // END ArrowOrAssignmentOp

    // Binary operators sorted by precedence.
    // IsBinaryOp() relies on this block of enum values
    // being contiguous and sorted in the same order!
    Comma,

    // Unary operators, starting at Add in BINARY_OP_TOKEN_LIST
    Nullish,
    Or,
    And,
    BitOr,
    BitXor,
    BitAnd,
    Shl,
    Sar,
    Shr,
    Mul,
    Div,
    Mod,
    Exp,
    Add,
    Sub,

    Not,
    BitNot,
    Delete,
    TypeOf,
    Void,

    // BEGIN IsCountOp
    Inc,
    Dec,
    // END IsCountOp
    // END IsUnaryOrCountOp

    // Compare operators sorted by precedence.
    Eq,
    EqStrict,
    NotEq,
    NotEqStrict,
    LessThan,
    GreaterThan,
    LessThanEq,
    GreaterThanEq,
    InstanceOf,
    In,

    // Keywords (ECMA-262, section 7.5.2, page 13).
    Break,
    Case,
    Catch,
    Continue,
    Debugger,
    Default,
    Do,
    Else,
    Finally,
    For,
    Function,
    If,
    New,
    Return,
    Switch,
    Throw,
    Try,
    Var,
    While,
    With,
    This,

    // Literals (ECMA-262, section 7.8, page 16).
    NullLiteral,
    TrueLiteral,
    FalseLiteral,
    Number,
    Smi,
    BigInt,
    String,

    // BEGIN Callable
    Super,
    // BEGIN AnyIdentifier
    // Identifiers (not keywords or future reserved words).
    Identifier,
    Get,
    Set,
    Using,
    Of,
    Accessor,
    Async,
    // `await` is a reserved word in module code only
    Await,
    Yield,
    Let,
    Static,
    // Future reserved words (ECMA-262, section 7.6.1.2).
    FutureStrictReservedWord,
    EscapedStrictReservedWord,
    // END AnyIdentifier
    // END Callable
    Enum,
    Class,
    Const,
    Export,
    Extends,
    Import,
    PrivateName,

    // Illegal token - not able to scan.
    Illegal,
    EscapedKeyword,

    // Scanner-internal use only.
    Whitespace,
    Uninitialized,
    RegExpLiteral,

    NumTokens,
}

public static class TokenInfo
{
    // name, string, precedence, is keyword (K) - in enum order.
    internal static readonly (string Name, string? Str, sbyte Precedence, bool Keyword)[] s_table =
    [
        ("kTemplateSpan", null, 0, false),
        ("kTemplateTail", null, 0, false),
        ("kPeriod", ".", 0, false),
        ("kLeftBracket", "[", 0, false),
        ("kQuestionPeriod", "?.", 0, false),
        ("kLeftParen", "(", 0, false),
        ("kRightParen", ")", 0, false),
        ("kRightBracket", "]", 0, false),
        ("kLeftBrace", "{", 0, false),
        ("kColon", ":", 0, false),
        ("kEllipsis", "...", 0, false),
        ("kConditional", "?", 3, false),
        ("kSemicolon", ";", 0, false),
        ("kRightBrace", "}", 0, false),
        ("kEos", "EOS", 0, false),
        ("kArrow", "=>", 0, false),
        ("kInit", "=init", 2, false),
        ("kAssign", "=", 2, false),
        ("kAssignNullish", "??=", 2, false),
        ("kAssignOr", "||=", 2, false),
        ("kAssignAnd", "&&=", 2, false),
        ("kAssignBitOr", "|=", 2, false),
        ("kAssignBitXor", "^=", 2, false),
        ("kAssignBitAnd", "&=", 2, false),
        ("kAssignShl", "<<=", 2, false),
        ("kAssignSar", ">>=", 2, false),
        ("kAssignShr", ">>>=", 2, false),
        ("kAssignMul", "*=", 2, false),
        ("kAssignDiv", "/=", 2, false),
        ("kAssignMod", "%=", 2, false),
        ("kAssignExp", "**=", 2, false),
        ("kAssignAdd", "+=", 2, false),
        ("kAssignSub", "-=", 2, false),
        ("kComma", ",", 1, false),
        ("kNullish", "??", 3, false),
        ("kOr", "||", 4, false),
        ("kAnd", "&&", 5, false),
        ("kBitOr", "|", 6, false),
        ("kBitXor", "^", 7, false),
        ("kBitAnd", "&", 8, false),
        ("kShl", "<<", 11, false),
        ("kSar", ">>", 11, false),
        ("kShr", ">>>", 11, false),
        ("kMul", "*", 13, false),
        ("kDiv", "/", 13, false),
        ("kMod", "%", 13, false),
        ("kExp", "**", 14, false),
        ("kAdd", "+", 12, false),
        ("kSub", "-", 12, false),
        ("kNot", "!", 0, false),
        ("kBitNot", "~", 0, false),
        ("kDelete", "delete", 0, true),
        ("kTypeOf", "typeof", 0, true),
        ("kVoid", "void", 0, true),
        ("kInc", "++", 0, false),
        ("kDec", "--", 0, false),
        ("kEq", "==", 9, false),
        ("kEqStrict", "===", 9, false),
        ("kNotEq", "!=", 9, false),
        ("kNotEqStrict", "!==", 9, false),
        ("kLessThan", "<", 10, false),
        ("kGreaterThan", ">", 10, false),
        ("kLessThanEq", "<=", 10, false),
        ("kGreaterThanEq", ">=", 10, false),
        ("kInstanceOf", "instanceof", 10, true),
        ("kIn", "in", 10, true),
        ("kBreak", "break", 0, true),
        ("kCase", "case", 0, true),
        ("kCatch", "catch", 0, true),
        ("kContinue", "continue", 0, true),
        ("kDebugger", "debugger", 0, true),
        ("kDefault", "default", 0, true),
        ("kDo", "do", 0, true),
        ("kElse", "else", 0, true),
        ("kFinally", "finally", 0, true),
        ("kFor", "for", 0, true),
        ("kFunction", "function", 0, true),
        ("kIf", "if", 0, true),
        ("kNew", "new", 0, true),
        ("kReturn", "return", 0, true),
        ("kSwitch", "switch", 0, true),
        ("kThrow", "throw", 0, true),
        ("kTry", "try", 0, true),
        ("kVar", "var", 0, true),
        ("kWhile", "while", 0, true),
        ("kWith", "with", 0, true),
        ("kThis", "this", 0, true),
        ("kNullLiteral", "null", 0, true),
        ("kTrueLiteral", "true", 0, true),
        ("kFalseLiteral", "false", 0, true),
        ("kNumber", null, 0, false),
        ("kSmi", null, 0, false),
        ("kBigInt", null, 0, false),
        ("kString", null, 0, false),
        ("kSuper", "super", 0, true),
        ("kIdentifier", null, 0, false),
        ("kGet", "get", 0, true),
        ("kSet", "set", 0, true),
        ("kUsing", "using", 0, true),
        ("kOf", "of", 0, true),
        ("kAccessor", "accessor", 0, true),
        ("kAsync", "async", 0, true),
        ("kAwait", "await", 0, true),
        ("kYield", "yield", 0, true),
        ("kLet", "let", 0, true),
        ("kStatic", "static", 0, true),
        ("kFutureStrictReservedWord", null, 0, false),
        ("kEscapedStrictReservedWord", null, 0, false),
        ("kEnum", "enum", 0, true),
        ("kClass", "class", 0, true),
        ("kConst", "const", 0, true),
        ("kExport", "export", 0, true),
        ("kExtends", "extends", 0, true),
        ("kImport", "import", 0, true),
        ("kPrivateName", null, 0, false),
        ("kIllegal", "ILLEGAL", 0, false),
        ("kEscapedKeyword", null, 0, false),
        ("kWhitespace", null, 0, false),
        ("kUninitialized", null, 0, false),
        ("kRegExpLiteral", null, 0, false),
    ];

    private const byte IsKeywordBit = 1;
    private const byte IsPropertyNameBit = 2;

    internal static readonly byte[] s_tokenFlags = BuildFlags();
    // precedence_[0] for accept_IN == false, precedence_[1] for accept_IN = true.
    internal static readonly sbyte[] s_precedenceNoIn = BuildPrecedence(false);
    internal static readonly sbyte[] s_precedenceIn = BuildPrecedence(true);
    internal static readonly byte[] s_stringLength = BuildStringLength();

    private static byte[] BuildFlags()
    {
        var flags = new byte[(int)Token.NumTokens];
        for (int i = 0; i < flags.Length; i++)
        {
            var t = (Token)i;
            if (s_table[i].Keyword)
                flags[i] = IsKeywordBit | IsPropertyNameBit;
            else if (Token.IsAnyIdentifier(t) || t == Token.EscapedKeyword)
                flags[i] = IsPropertyNameBit;
        }
        return flags;
    }

    private static sbyte[] BuildPrecedence(bool acceptIn)
    {
        var p = new sbyte[(int)Token.NumTokens];
        for (int i = 0; i < p.Length; i++)
            p[i] = (!acceptIn && (Token)i == Token.In) ? (sbyte)0 : s_table[i].Precedence;
        return p;
    }

    private static byte[] BuildStringLength()
    {
        var p = new byte[(int)Token.NumTokens];
        for (int i = 0; i < p.Length; i++)
            p[i] = (byte)(s_table[i].Str?.Length ?? 0);
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool InRange(Token t, Token lo, Token hi) => (uint)(t - lo) <= (uint)(hi - lo);

    extension(Token)
    {
        // Returns a string corresponding to the C++ token name
        // (e.g. "kLessThan" for the token kLessThan).
        public static string Name(Token token) => s_table[(int)token].Name;

        public static bool IsKeyword(Token token) => (s_tokenFlags[(int)token] & IsKeywordBit) != 0;

        public static bool IsPropertyName(Token token) => (s_tokenFlags[(int)token] & IsPropertyNameBit) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValidIdentifier(Token token, LanguageMode language_mode, bool is_generator, bool disallow_await)
        {
            if (InRange(token, Token.Identifier, Token.Async)) return true;
            if (token == Token.Await) return !disallow_await;
            if (token == Token.Yield) return !is_generator && Globals.is_sloppy(language_mode);
            return Token.IsStrictReservedWord(token) && Globals.is_sloppy(language_mode);
        }

        public static bool IsCallable(Token token) => InRange(token, Token.Super, Token.EscapedStrictReservedWord);
        public static bool IsAutoSemicolon(Token token) => InRange(token, Token.Semicolon, Token.Eos);
        public static bool IsAnyIdentifier(Token token) => InRange(token, Token.Identifier, Token.EscapedStrictReservedWord);
        public static bool IsStrictReservedWord(Token token) => InRange(token, Token.Yield, Token.EscapedStrictReservedWord);
        public static bool IsLiteral(Token token) => InRange(token, Token.NullLiteral, Token.String);
        public static bool IsTemplate(Token token) => InRange(token, Token.TemplateSpan, Token.TemplateTail);
        public static bool IsMember(Token token) => InRange(token, Token.TemplateSpan, Token.LeftBracket);
        public static bool IsProperty(Token token) => InRange(token, Token.Period, Token.LeftBracket);
        public static bool IsPropertyOrCall(Token token) => InRange(token, Token.TemplateSpan, Token.LeftParen);
        public static bool IsArrowOrAssignmentOp(Token token) => InRange(token, Token.Arrow, Token.AssignSub);
        public static bool IsAssignmentOp(Token token) => InRange(token, Token.Init, Token.AssignSub);
        public static bool IsLogicalAssignmentOp(Token token) => InRange(token, Token.AssignNullish, Token.AssignAnd);
        public static bool IsBinaryOp(Token op) => InRange(op, Token.Comma, Token.Sub);
        public static bool IsCompareOp(Token op) => InRange(op, Token.Eq, Token.In);
        public static bool IsCompareOpWithEmbeddedFeedback(Token op) => InRange(op, Token.Eq, Token.GreaterThanEq);
        public static bool IsBinaryOpWithEmbeddedFeedback(Token op) => InRange(op, Token.BitOr, Token.Sub);
        public static bool IsUnaryOpWithEmbeddedFeedback(Token op) => op == Token.Sub || op == Token.BitNot || op == Token.Inc || op == Token.Dec;
        public static bool IsOrderedRelationalCompareOp(Token op) => InRange(op, Token.LessThan, Token.GreaterThanEq);
        public static bool IsEqualityOp(Token op) => InRange(op, Token.Eq, Token.EqStrict);

        public static Token BinaryOpForAssignment(Token op)
        {
            System.Diagnostics.Debug.Assert(InRange(op, Token.AssignNullish, Token.AssignSub));
            return (Token)(op - Token.AssignNullish + (int)Token.Nullish);
        }

        public static bool IsBitOp(Token op) => InRange(op, Token.BitOr, Token.Shr) || op == Token.BitNot;
        public static bool IsUnaryOp(Token op) => InRange(op, Token.Add, Token.Void);
        public static bool IsCountOp(Token op) => InRange(op, Token.Inc, Token.Dec);
        public static bool IsUnaryOrCountOp(Token op) => InRange(op, Token.Add, Token.Dec);
        public static bool IsShiftOp(Token op) => InRange(op, Token.Shl, Token.Shr);

        // Returns a string corresponding to the JS token string
        // (.e., "<" for the token kLessThan) or null if the token doesn't
        // have a (unique) string (e.g. a kIdentifier).
        public static string? StringOf(Token token) => s_table[(int)token].Str;

        public static byte StringLength(Token token) => s_stringLength[(int)token];

        // Returns the precedence > 0 for binary and compare
        // operators; returns 0 otherwise.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Precedence(Token token, bool accept_IN)
            => accept_IN ? s_precedenceIn[(int)token] : s_precedenceNoIn[(int)token];
    }
}
