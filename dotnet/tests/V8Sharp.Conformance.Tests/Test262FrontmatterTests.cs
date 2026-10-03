// Tests for the port of test262's parseTestRecord.py (the frontmatter YAML).
using V8Sharp.TestRunner.Suites;

namespace V8Sharp.Conformance.Tests;

public class Test262FrontmatterTests
{
    [Fact]
    public void FlowSequencesAndScalars()
    {
        var fm = Test262Frontmatter.Parse("""
            // Copyright (C) 2016 the V8 project authors. All rights reserved.
            // This code is governed by the BSD license found in the LICENSE file.
            /*---
            esid: sec-array.prototype.map
            description: >
              Array.prototype.map applied to boolean primitive
            includes: [compareArray.js, propertyHelper.js]
            flags: [onlyStrict, async]
            features: [Symbol.species, Reflect.construct]
            ---*/
            assert(true);
            """)!;
        Assert.Equal("sec-array.prototype.map", fm.Esid);
        Assert.Equal("Array.prototype.map applied to boolean primitive", fm.Description);
        Assert.Equal(["compareArray.js", "propertyHelper.js"], fm.Includes);
        Assert.Equal(["onlyStrict", "async"], fm.Flags);
        Assert.True(fm.HasFlag("async"));
        Assert.Equal(["Symbol.species", "Reflect.construct"], fm.Features);
        Assert.False(fm.IsNegative);
    }

    [Fact]
    public void NegativeBlockAndBlockSequence()
    {
        var fm = Test262Frontmatter.Parse("""
            /*---
            info: |
              The production
                IdentifierReference : yield
              is a syntax error: with a colon inside.
            negative:
              phase: parse
              type: SyntaxError
            includes:
              - asyncHelpers.js
              - compareArray.js
            flags:
              - module
            ---*/
            $DONOTEVALUATE();
            """)!;
        Assert.True(fm.IsNegative);
        Assert.Equal("parse", fm.NegativePhase);
        Assert.Equal("SyntaxError", fm.NegativeType);
        Assert.Equal(["asyncHelpers.js", "compareArray.js"], fm.Includes);
        Assert.True(fm.HasFlag("module"));
        Assert.Contains("IdentifierReference : yield", (string)fm.Raw["info"]);
    }

    [Fact]
    public void FlowMappingNegative()
    {
        var fm = Test262Frontmatter.FromYaml("negative: {phase: runtime, type: TypeError}\nflags: [raw]");
        Assert.Equal("runtime", fm.NegativePhase);
        Assert.Equal("TypeError", fm.NegativeType);
        Assert.True(fm.HasFlag("raw"));
    }

    [Fact]
    public void QuotedValuesAndComments()
    {
        var fm = Test262Frontmatter.FromYaml("""
            description: "quoted: value" # trailing comment
            features: [ 'a', "b" ]  # comment
            locale: [en-US]
            """);
        Assert.Equal("quoted: value", fm.Description);
        Assert.Equal(["a", "b"], fm.Features);
        Assert.Equal(["en-US"], fm.Locale);
    }

    [Fact]
    public void NoFrontmatter()
    {
        Assert.Null(Test262Frontmatter.Parse("// just a harness file\nfunction f() {}"));
    }

    /// <summary>Every test262 test in the checkout parses, and the metadata the
    /// runner uses is well-formed.</summary>
    [Fact]
    public void AllTest262TestsParse()
    {
        string root = Path.Combine(TestPaths.Test262Root, "test");
        Assert.SkipUnless(Directory.Exists(root), "test262 data not available");
        int count = 0, negative = 0, modules = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
        {
            if (file.EndsWith("_FIXTURE.js", StringComparison.Ordinal)) continue;
            var fm = Test262Frontmatter.Parse(File.ReadAllText(file));
            Assert.True(fm is not null, file);
            if (fm!.IsNegative)
            {
                negative++;
                Assert.True(fm.NegativeType is { Length: > 0 } && fm.NegativePhase is "parse" or "resolution" or "runtime", file);
            }
            if (fm.HasFlag("module")) modules++;
            foreach (var inc in fm.Includes) Assert.EndsWith(".js", inc);
            count++;
        }
        Assert.True(count > 40000, $"only {count} tests");
        Assert.True(negative > 3000, $"only {negative} negative tests");
        Assert.True(modules > 500, $"only {modules} module tests");
    }
}
