#!/usr/bin/env python3
# Generates dotnet/src/V8Sharp.RegExp/Unicode/UnicodeTables.g.cs from the
# Unicode Character Database.
#
# V8 answers \p{...}, case folding and case closure through ICU. V8Sharp has
# no ICU, so the same data comes from these tables, generated from the UCD at
# the Unicode version test262 expects (see UNICODE_VERSION).
#
# Usage:
#   python3 gen_regexp_unicode_tables.py [--ucd DIR] [--download] OUTPUT.cs
#
# With --download the UCD files are fetched from the unicode-org/unicodetools
# data mirror into DIR (default: ./ucd).

import argparse
import base64
import os
import sys
import urllib.request

UNICODE_VERSION = "17.0.0"
EMOJI_VERSION = "17.0"
MIRROR = "https://raw.githubusercontent.com/unicode-org/unicodetools/main/unicodetools/data"

UCD_FILES = [
    "UnicodeData.txt", "Scripts.txt", "ScriptExtensions.txt", "PropertyAliases.txt",
    "PropertyValueAliases.txt", "PropList.txt", "DerivedCoreProperties.txt",
    "CaseFolding.txt", "SpecialCasing.txt", "extracted/DerivedBinaryProperties.txt",
    "DerivedNormalizationProps.txt", "emoji/emoji-data.txt",
]
EMOJI_FILES = ["emoji-sequences.txt", "emoji-zwj-sequences.txt"]

MAX_CP = 0x10FFFF


def download(ucd):
    os.makedirs(ucd, exist_ok=True)
    for f in UCD_FILES:
        data = urllib.request.urlopen(f"{MIRROR}/ucd/{UNICODE_VERSION}/{f}").read()
        open(os.path.join(ucd, os.path.basename(f)), "wb").write(data)
    for f in EMOJI_FILES:
        data = urllib.request.urlopen(f"{MIRROR}/emoji/{EMOJI_VERSION}/{f}").read()
        open(os.path.join(ucd, f), "wb").write(data)


def lines(ucd, name):
    with open(os.path.join(ucd, name), encoding="utf-8") as f:
        for line in f:
            line = line.split("#", 1)[0].strip()
            if line:
                yield [x.strip() for x in line.split(";")]


def parse_cps(s):
    if ".." in s:
        a, b = s.split("..")
        return int(a, 16), int(b, 16)
    v = int(s, 16)
    return v, v


def to_ranges(cps):
    cps = sorted(set(cps))
    out = []
    for c in cps:
        if out and out[-1][1] + 1 == c:
            out[-1][1] = c
        else:
            out.append([c, c])
    return [tuple(r) for r in out]


def ranges_from_set_of_ranges(rs):
    rs = sorted(rs)
    out = []
    for a, b in rs:
        if out and a <= out[-1][1] + 1:
            out[-1][1] = max(out[-1][1], b)
        else:
            out.append([a, b])
    return [tuple(r) for r in out]


def complement(rs):
    out = []
    nxt = 0
    for a, b in rs:
        if a > nxt:
            out.append((nxt, a - 1))
        nxt = b + 1
    if nxt <= MAX_CP:
        out.append((nxt, MAX_CP))
    return out


def binary_props(ucd, fname, wanted=None):
    props = {}
    for f in lines(ucd, fname):
        if len(f) < 2:
            continue
        name = f[1]
        if wanted is not None and name not in wanted:
            continue
        props.setdefault(name, []).append(parse_cps(f[0]))
    return props


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ucd", default="ucd")
    ap.add_argument("--download", action="store_true")
    ap.add_argument("output")
    args = ap.parse_args()
    ucd = args.ucd
    if args.download:
        download(ucd)

    # ---------------------------------------------------------------- aliases
    prop_aliases = {}  # long name -> [aliases]
    for f in lines(ucd, "PropertyAliases.txt"):
        prop_aliases[f[1]] = f
    value_aliases = {"gc": [], "sc": []}
    for f in lines(ucd, "PropertyValueAliases.txt"):
        if f[0] in value_aliases:
            value_aliases[f[0]].append(f[1:])

    # ---------------------------------------------------- general category
    gc_of = {}
    first = None
    upper_simple = {}
    for f in lines(ucd, "UnicodeData.txt"):
        cp = int(f[0], 16)
        name = f[1]
        gc = f[2]
        if f[12]:
            upper_simple[cp] = int(f[12], 16)
        if name.endswith(", First>"):
            first = cp
            continue
        if name.endswith(", Last>"):
            for c in range(first, cp + 1):
                gc_of[c] = gc
            first = None
            continue
        gc_of[cp] = gc
    gc_cps = {}
    for c, g in gc_of.items():
        gc_cps.setdefault(g, []).append(c)
    gc_ranges = {g: to_ranges(v) for g, v in gc_cps.items()}
    assigned = ranges_from_set_of_ranges([r for v in gc_ranges.values() for r in v])
    gc_ranges["Cn"] = complement(assigned)
    groups = {
        "C": ["Cc", "Cf", "Cn", "Co", "Cs"], "L": ["Ll", "Lm", "Lo", "Lt", "Lu"],
        "LC": ["Ll", "Lt", "Lu"], "M": ["Mc", "Me", "Mn"], "N": ["Nd", "Nl", "No"],
        "P": ["Pc", "Pd", "Pe", "Pf", "Pi", "Po", "Ps"], "S": ["Sc", "Sk", "Sm", "So"],
        "Z": ["Zl", "Zp", "Zs"],
    }
    for g, members in groups.items():
        gc_ranges[g] = ranges_from_set_of_ranges([r for m in members for r in gc_ranges[m]])

    # ------------------------------------------------------------- scripts
    sc_long_to_short = {}
    for f in value_aliases["sc"]:
        sc_long_to_short[f[1]] = f[0]
    sc_of_ranges = {}
    sc_cp = {}
    for f in lines(ucd, "Scripts.txt"):
        a, b = parse_cps(f[0])
        short = sc_long_to_short[f[1]]
        sc_of_ranges.setdefault(short, []).append((a, b))
    # Unknown (Zzzz): everything not listed.
    listed = ranges_from_set_of_ranges([r for v in sc_of_ranges.values() for r in v])
    sc_of_ranges["Zzzz"] = complement(listed)
    for k in sc_of_ranges:
        sc_of_ranges[k] = ranges_from_set_of_ranges(sc_of_ranges[k])
    # Script extensions: default scx = {sc}; ScriptExtensions.txt overrides.
    scx_override = {}
    for f in lines(ucd, "ScriptExtensions.txt"):
        a, b = parse_cps(f[0])
        for c in range(a, b + 1):
            scx_override[c] = f[1].split()
    # Start from sc sets minus overridden code points.
    scx_ranges = {k: list(rs) for k, rs in sc_of_ranges.items()}
    over_set = set(scx_override.keys())
    for k in list(scx_ranges.keys()):
        out = []
        for a, b in scx_ranges[k]:
            start = a
            for c in range(a, b + 1):
                if c in over_set:
                    if start < c:
                        out.append((start, c - 1))
                    start = c + 1
            if start <= b:
                out.append((start, b))
        scx_ranges[k] = out
    for c, scripts in scx_override.items():
        for s in scripts:
            scx_ranges.setdefault(s, []).append((c, c))
    for k in scx_ranges:
        scx_ranges[k] = ranges_from_set_of_ranges(scx_ranges[k])

    # ------------------------------------------------------ binary properties
    supported = [
        "Alphabetic", "ASCII_Hex_Digit", "Bidi_Control", "Bidi_Mirrored", "Case_Ignorable",
        "Cased", "Changes_When_Casefolded", "Changes_When_Casemapped",
        "Changes_When_Lowercased", "Changes_When_NFKC_Casefolded", "Changes_When_Titlecased",
        "Changes_When_Uppercased", "Dash", "Default_Ignorable_Code_Point", "Deprecated",
        "Diacritic", "Emoji", "Emoji_Component", "Emoji_Modifier_Base", "Emoji_Modifier",
        "Emoji_Presentation", "Extended_Pictographic", "Extender", "Grapheme_Base",
        "Grapheme_Extend", "Hex_Digit", "ID_Continue", "ID_Start", "Ideographic",
        "IDS_Binary_Operator", "IDS_Trinary_Operator", "Join_Control",
        "Logical_Order_Exception", "Lowercase", "Math", "Noncharacter_Code_Point",
        "Pattern_Syntax", "Pattern_White_Space", "Quotation_Mark", "Radical",
        "Regional_Indicator", "Sentence_Terminal", "Soft_Dotted", "Terminal_Punctuation",
        "Unified_Ideograph", "Uppercase", "Variation_Selector", "White_Space",
        "XID_Continue", "XID_Start",
    ]
    wanted = set(supported)
    bprops = {}
    for fname in ["PropList.txt", "DerivedCoreProperties.txt", "DerivedBinaryProperties.txt",
                  "DerivedNormalizationProps.txt", "emoji-data.txt"]:
        for k, v in binary_props(ucd, fname, wanted).items():
            bprops.setdefault(k, []).extend(v)
    for k in supported:
        if k not in bprops:
            print("missing binary property", k, file=sys.stderr)
            sys.exit(1)
        bprops[k] = ranges_from_set_of_ranges(bprops[k])

    # ----------------------------------------------------- properties of strings
    string_props = ["Basic_Emoji", "Emoji_Keycap_Sequence", "RGI_Emoji_Modifier_Sequence",
                    "RGI_Emoji_Flag_Sequence", "RGI_Emoji_Tag_Sequence",
                    "RGI_Emoji_ZWJ_Sequence", "RGI_Emoji"]
    sp_ranges = {k: [] for k in string_props}
    sp_strings = {k: [] for k in string_props}
    for fname in EMOJI_FILES:
        for f in lines(ucd, fname):
            prop = f[1]
            if prop not in sp_ranges:
                continue
            seq = f[0]
            if " " in seq:
                s = [int(x, 16) for x in seq.split()]
                sp_strings[prop].append(s)
                sp_strings["RGI_Emoji"].append(s)
            else:
                r = parse_cps(seq)
                sp_ranges[prop].append(r)
                sp_ranges["RGI_Emoji"].append(r)
    for k in string_props:
        sp_ranges[k] = ranges_from_set_of_ranges(sp_ranges[k])
        sp_strings[k] = sorted(set(tuple(s) for s in sp_strings[k]))

    # ----------------------------------------------------------- case folding
    fold = {}
    for f in lines(ucd, "CaseFolding.txt"):
        if f[1] in ("C", "S"):
            fold[int(f[0], 16)] = int(f[2], 16)

    # Full uppercase (root locale): unconditional SpecialCasing, else simple.
    full_upper = {}
    for f in lines(ucd, "SpecialCasing.txt"):
        # code; lower; title; upper; (condition;)?
        if len(f) >= 5 and f[4]:
            continue  # conditional
        full_upper[int(f[0], 16)] = [int(x, 16) for x in f[3].split()]

    def upper(c):
        if c in full_upper:
            return full_upper[c]
        return [upper_simple.get(c, c)]

    # Simple case closure classes: connected components of c -> fold(c).
    members = {}
    for c, t in fold.items():
        members.setdefault(t, {t}).add(c)

    def closure(c):
        t = fold.get(c, c)
        return members.get(t, {c}) | {c}

    # ECMA-262 Canonicalize (non-unicode): toUpperCase with the single-code-unit
    # and ASCII rules. This is gen-regexp-special-case.cc's Canonicalize.
    def canonicalize(ch):
        u = upper(ch)
        # length in UTF-16 code units
        if len(u) != 1 or u[0] > 0xFFFF:
            return ch
        cu = u[0]
        if ch >= 128 and cu < 128:
            return ch
        return cu

    ignore = []
    special_add = []
    for i in range(0x10000):
        if 0xD800 <= i <= 0xDFFF:
            continue
        cur = closure(i)
        canon = canonicalize(i)
        has_match = False
        has_non_match = False
        for c in cur:
            if c == i:
                continue
            if canonicalize(c) == canon:
                has_match = True
            else:
                has_non_match = True
        if has_non_match:
            if has_match:
                special_add.append(i)
            else:
                ignore.append(i)
    ignore_set = set(ignore)
    for c in special_add:
        canon = canonicalize(c)
        for c2 in closure(c) - ignore_set:
            assert canonicalize(c2) == canon, (hex(c), hex(c2))

    # ---------------------------------------------------------------- output
    blob = bytearray()

    def put_varint(v):
        assert v >= 0
        while True:
            b = v & 0x7F
            v >>= 7
            if v:
                blob.append(b | 0x80)
            else:
                blob.append(b)
                break

    def put_ranges(rs):
        off = len(blob)
        put_varint(len(rs))
        prev = 0
        for a, b in rs:
            put_varint(a - prev)
            put_varint(b - a)
            prev = b
        return off

    def put_strings(ss):
        off = len(blob)
        put_varint(len(ss))
        for s in ss:
            put_varint(len(s))
            for c in s:
                put_varint(c)
        return off

    out = []
    w = out.append
    w("// Copyright 2026 the V8Sharp authors. Generated file, do not edit.")
    w(f"// Generated by dotnet/tools/unicode/gen_regexp_unicode_tables.py from the")
    w(f"// Unicode Character Database {UNICODE_VERSION} (emoji {EMOJI_VERSION}).")
    w("")
    w("namespace V8Sharp.RegExp.Unicode;")
    w("")
    w("internal static partial class UnicodeTables")
    w("{")
    w(f"    public const string UnicodeVersion = \"{UNICODE_VERSION}\";")
    w("")

    # General category values.
    gc_entries = []
    for f in value_aliases["gc"]:
        short = f[0]
        names = [x for x in f if x]
        off = put_ranges(gc_ranges[short])
        gc_entries.append((names, off))
    w("    // General_Category values: (aliases, offset of the ranges in Blob).")
    w("    public static readonly (string[] Names, int Offset)[] GeneralCategoryValues =")
    w("    [")
    for names, off in gc_entries:
        w("        ([" + ", ".join(f'"{n}"' for n in names) + f"], {off}),")
    w("    ];")
    w("")

    sc_entries = []
    for f in value_aliases["sc"]:
        short = f[0]
        names = [x for x in f if x]
        sc_off = put_ranges(sc_of_ranges.get(short, []))
        scx_off = put_ranges(scx_ranges.get(short, []))
        sc_entries.append((names, sc_off, scx_off))
    w("    // Script values: (aliases, Script ranges offset, Script_Extensions ranges offset).")
    w("    public static readonly (string[] Names, int ScriptOffset, int ScriptExtensionsOffset)[] ScriptValues =")
    w("    [")
    for names, a, b in sc_entries:
        w("        ([" + ", ".join(f'"{n}"' for n in names) + f"], {a}, {b}),")
    w("    ];")
    w("")

    w("    // Supported binary properties (IsSupportedBinaryProperty): (aliases, offset).")
    w("    public static readonly (string[] Names, int Offset)[] BinaryProperties =")
    w("    [")
    for k in supported:
        names = [x for x in prop_aliases[k] if x]
        off = put_ranges(bprops[k])
        w("        ([" + ", ".join(f'"{n}"' for n in names) + f"], {off}),")
    w("    ];")
    w("")

    w("    // Binary properties of strings (only with /v): (name, ranges offset, strings offset).")
    w("    public static readonly (string Name, int RangesOffset, int StringsOffset)[] StringProperties =")
    w("    [")
    for k in string_props:
        a = put_ranges(sp_ranges[k])
        b = put_strings(sp_strings[k])
        w(f"        (\"{k}\", {a}, {b}),")
    w("    ];")
    w("")

    fold_items = sorted(fold.items())
    w("    // Simple case folding (CaseFolding.txt, status C and S): sorted (code point, folded) pairs.")
    off = len(blob)
    w(f"    public const int SimpleCaseFoldingOffset = {off};")
    put_varint(len(fold_items))
    prev = 0
    for a, b in fold_items:
        put_varint(a - prev)
        put_varint(b)
        prev = a
    w("")
    w("    // IgnoreSet of src/regexp/special-case.h: characters that must match only")
    w("    // themselves under non-unicode /i (see gen-regexp-special-case.cc).")
    ign_off = put_ranges(to_ranges(ignore))
    w(f"    public const int IgnoreSetOffset = {ign_off};")
    w("")
    b64 = base64.b64encode(bytes(blob)).decode("ascii")
    w(f"    // {len(blob)} bytes of varint-encoded data.")
    w("    const string BlobBase64 =")
    for i in range(0, len(b64), 100):
        chunk = b64[i:i + 100]
        sep = ";" if i + 100 >= len(b64) else " +"
        w(f"        \"{chunk}\"{sep}")
    w("}")
    w("")
    with open(args.output, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out))
    print(f"wrote {args.output}: {len(blob)} bytes of data, {len(ignore)} ignore-set chars",
          file=sys.stderr)


if __name__ == "__main__":
    main()
