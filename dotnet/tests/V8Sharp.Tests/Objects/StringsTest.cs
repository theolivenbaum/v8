// Port of test/cctest/test-strings.cc: the tests that do not run JavaScript.
//
// V8Sharp has no external strings and no one-byte representation, so the
// building blocks V8 makes external or one-byte are ordinary UTF-16 strings
// here (drawn from the same random sequence). Not ported: the tests that run
// JavaScript or use the embedder API (Utf8Conversion*, ExternalShortStringAdd,
// ReplaceInvalidUtf8, JSONStringifyWellFormed, CachedHashOverflow,
// InternalizeExternal*, Regress*, SliceFromExternal, ExternalizeDuringJsonStringify,
// TrivialSlice, SliceFromSlice, OneByteArrayJoin, CountBreakIterator,
// StringReplaceAtomTwoByteResult, InvalidExternalString, FormatMessage,
// ExternalStringIndexOf, CheckCachedData*, CheckIntlSegmentIterator*), and
// IsAscii / Latin1IgnoreCase, which test V8Sharp.Base helpers.
using V8Sharp.Strings;

namespace V8Sharp.Tests.Objects;

public class StringsTest : TestWithContext
{
    // Adapted from http://en.wikipedia.org/wiki/Multiply-with-carry
    sealed class MyRandomNumberGenerator
    {
        const uint kQSize = 4096;
        readonly uint[] _q = new uint[kQSize];
        uint _c;
        uint _i;

        public MyRandomNumberGenerator() => Init();

        public void Init(uint seed = 0x5688C73E)
        {
            const uint phi = 0x9E3779B9;
            _c = 362436;
            _i = kQSize - 1;
            _q[0] = seed;
            _q[1] = seed + phi;
            _q[2] = seed + phi + phi;
            for (uint j = 3; j < kQSize; j++)
            {
                _q[j] = _q[j - 3] ^ _q[j - 2] ^ phi ^ j;
            }
        }

        public uint Next()
        {
            const ulong a = 18782;
            const uint r = 0xFFFFFFFE;
            _i = (_i + 1) & (kQSize - 1);
            ulong t = a * _q[_i] + _c;
            _c = (uint)(t >> 32);
            uint x = (uint)(t + _c);
            if (x < _c)
            {
                x++;
                _c++;
            }
            return _q[_i] = r - x;
        }

        public uint Next(int max) => Next() % (uint)max;

        public bool Next(double threshold)
        {
            Assert.True(threshold >= 0.0 && threshold <= 1.0);
            if (threshold == 1.0) return true;
            if (threshold == 0.0) return false;
            uint value = Next() % 100000;
            return threshold > value / 100000.0;
        }
    }

    const int DEEP_DEPTH = 8 * 1024;
    const int SUPER_DEEP_DEPTH = 80 * 1024;

    void InitializeBuildingBlocks(JSString[] buildingBlocks, bool longBlocks, MyRandomNumberGenerator rng)
    {
        for (int i = 0; i < buildingBlocks.Length; i++)
        {
            uint len = rng.Next(16);
            uint sliceHeadChars = 0;
            uint sliceTailChars = 0;
            uint sliceDepth = 0;
            for (int j = 0; j < 3; j++)
            {
                if (rng.Next(0.35)) sliceDepth++;
            }
            // Must truncate something for a slice string. Loop until
            // at least one end will be sliced.
            while (sliceHeadChars == 0 && sliceTailChars == 0)
            {
                sliceHeadChars = rng.Next(15);
                sliceTailChars = rng.Next(12);
            }
            if (longBlocks)
            {
                // Generate building blocks which will never be merged
                len += ConsString.kMinLength + 1;
            }
            else if (len > 14)
            {
                len += 1234;
            }
            // Don't slice 0 length strings.
            if (len == 0) sliceDepth = 0;
            uint sliceLength = sliceDepth * (sliceHeadChars + sliceTailChars);
            len += sliceLength;
            // Cases 0 and 2 are two-byte (sequential and external) and 1 and 3
            // one-byte (sequential and external) in V8.
            uint kind = rng.Next(4);
            int maxChar = kind is 0 or 2 ? 0x10000 : 0x80;
            var buf = new char[len];
            for (int j = 0; j < len; j++)
            {
                buf[j] = (char)rng.Next(maxChar);
            }
            buildingBlocks[i] = factory.NewStringFromUtf16(new string(buf));
            for (int j = 0; j < len; j++)
            {
                Assert.Equal(buf[j], buildingBlocks[i].Get(j));
            }
            for (uint j = sliceDepth; j > 0; j--)
            {
                buildingBlocks[i] = factory.NewSubString(buildingBlocks[i], (int)sliceHeadChars,
                    buildingBlocks[i].Length - (int)sliceTailChars);
            }
            Assert.Equal((int)len, buildingBlocks[i].Length + (int)sliceLength);
        }
    }

    sealed class ConsStringStats
    {
        public int leaves_;
        public int empty_leaves_;
        public int chars_;
        public int left_traversals_;
        public int right_traversals_;

        public void Reset()
        {
            leaves_ = 0;
            empty_leaves_ = 0;
            chars_ = 0;
            left_traversals_ = 0;
            right_traversals_ = 0;
        }

        public void VerifyEqual(ConsStringStats that)
        {
            Assert.Equal(leaves_, that.leaves_);
            Assert.Equal(empty_leaves_, that.empty_leaves_);
            Assert.Equal(chars_, that.chars_);
            Assert.Equal(left_traversals_, that.left_traversals_);
            Assert.Equal(right_traversals_, that.right_traversals_);
        }
    }

    sealed class ConsStringGenerationData
    {
        public const int kNumberOfBuildingBlocks = 256;
        // Input variables.
        public double early_termination_threshold_;
        public double leftness_;
        public double rightness_;
        public double empty_leaf_threshold_;
        public int max_leaves_;
        // Cached data.
        public readonly JSString[] building_blocks_ = new JSString[kNumberOfBuildingBlocks];
        public readonly MyRandomNumberGenerator rng_ = new();
        // Stats.
        public readonly ConsStringStats stats_ = new();
        public int early_terminations_;

        public ConsStringGenerationData(StringsTest test, bool longBlocks)
        {
            rng_.Init();
            test.InitializeBuildingBlocks(building_blocks_, longBlocks, rng_);
            Reset();
        }

        public JSString Block(uint offset) => building_blocks_[offset % kNumberOfBuildingBlocks];

        public JSString Block(int offset)
        {
            Assert.True(offset >= 0);
            return building_blocks_[offset % kNumberOfBuildingBlocks];
        }

        public void Reset()
        {
            early_termination_threshold_ = 0.01;
            leftness_ = 0.75;
            rightness_ = 0.75;
            empty_leaf_threshold_ = 0.02;
            max_leaves_ = 1000;
            stats_.Reset();
            early_terminations_ = 0;
            rng_.Init();
        }
    }

    static JSString SecondOf(ConsString s) => s.Second ?? ReadOnlyRoots.empty_string;

    static void AccumulateStats(ConsString consString, ConsStringStats stats)
    {
        int leftLength = consString.First.Length;
        int rightLength = SecondOf(consString).Length;
        Assert.Equal(consString.Length, leftLength + rightLength);
        // Check left side.
        bool leftIsCons = consString.First is ConsString;
        if (consString.First is ConsString leftCons)
        {
            stats.left_traversals_++;
            AccumulateStats(leftCons, stats);
        }
        else
        {
            Assert.NotEqual(0, leftLength);
            stats.leaves_++;
            stats.chars_ += leftLength;
        }
        // Check right side.
        if (SecondOf(consString) is ConsString rightCons)
        {
            stats.right_traversals_++;
            AccumulateStats(rightCons, stats);
        }
        else
        {
            if (rightLength == 0)
            {
                stats.empty_leaves_++;
                Assert.False(leftIsCons);
            }
            stats.leaves_++;
            stats.chars_ += rightLength;
        }
    }

    static void AccumulateStats(JSString consString, ConsStringStats stats)
    {
        if (consString is ConsString cons)
        {
            AccumulateStats(cons, stats);
            return;
        }
        // This string got flattened by gc.
        stats.chars_ += consString.Length;
    }

    static void AccumulateStatsWithOperator(ConsString consString, ConsStringStats stats)
    {
        var iter = new ConsStringIterator(consString);
        for (JSString? str = iter.Next(out int offset); str is not null; str = iter.Next(out offset))
        {
            // Accumulate stats.
            Assert.Equal(0, offset);
            stats.leaves_++;
            stats.chars_ += str.Length;
        }
    }

    static void VerifyConsString(JSString root, ConsStringGenerationData data)
    {
        // Verify basic data.
        ConsString rootCons = Assert.IsType<ConsString>(root);
        Assert.Equal(root.Length, data.stats_.chars_);
        // Recursive verify.
        var stats = new ConsStringStats();
        AccumulateStats(rootCons, stats);
        stats.VerifyEqual(data.stats_);
        // Iteratively verify.
        stats.Reset();
        AccumulateStatsWithOperator(rootCons, stats);
        // Don't see these. Must copy over.
        stats.empty_leaves_ = data.stats_.empty_leaves_;
        stats.left_traversals_ = data.stats_.left_traversals_;
        stats.right_traversals_ = data.stats_.right_traversals_;
        // Adjust total leaves to compensate.
        stats.leaves_ += stats.empty_leaves_;
        stats.VerifyEqual(data.stats_);
    }

    JSString ConstructRandomString(ConsStringGenerationData data, uint maxRecursion)
    {
        // Compute termination characteristics.
        bool terminate = false;
        bool flat = data.rng_.Next(data.empty_leaf_threshold_);
        bool terminateEarly = data.rng_.Next(data.early_termination_threshold_);
        if (terminateEarly) data.early_terminations_++;
        // The obvious condition.
        terminate |= maxRecursion == 0;
        // Flat cons string terminate by definition.
        terminate |= flat;
        // Cap for max leaves.
        terminate |= data.stats_.leaves_ >= data.max_leaves_;
        // Roll the dice.
        terminate |= terminateEarly;
        // Compute termination characteristics for each side.
        bool terminateLeft = terminate || !data.rng_.Next(data.leftness_);
        bool terminateRight = terminate || !data.rng_.Next(data.rightness_);
        // Generate left string.
        JSString? left = null;
        if (terminateLeft)
        {
            left = data.Block(data.rng_.Next());
            data.stats_.leaves_++;
            data.stats_.chars_ += left.Length;
        }
        else
        {
            data.stats_.left_traversals_++;
        }
        // Generate right string.
        JSString? right = null;
        if (terminateRight)
        {
            right = data.Block(data.rng_.Next());
            data.stats_.leaves_++;
            data.stats_.chars_ += right.Length;
        }
        else
        {
            data.stats_.right_traversals_++;
        }
        // Generate the necessary sub-nodes recursively.
        if (!terminateRight)
        {
            // Need to balance generation fairly.
            if (!terminateLeft && data.rng_.Next(0.5))
            {
                left = ConstructRandomString(data, maxRecursion - 1);
            }
            right = ConstructRandomString(data, maxRecursion - 1);
        }
        if (!terminateLeft && left is null)
        {
            left = ConstructRandomString(data, maxRecursion - 1);
        }
        // Build the cons string.
        JSString root = factory.NewConsString(left!, right!);
        Assert.True(root is ConsString && !root.IsFlat);
        // Special work needed for flat string.
        if (flat)
        {
            data.stats_.empty_leaves_++;
            JSString.Flatten(i_isolate, root);
            Assert.True(root is ConsString && root.IsFlat);
        }
        return root;
    }

    JSString ConstructLeft(ConsStringGenerationData data, int depth)
    {
        JSString answer = factory.NewStringFromAsciiChecked("");
        data.stats_.leaves_++;
        for (int i = 0; i < depth; i++)
        {
            JSString block = data.Block(i);
            JSString next = factory.NewConsString(answer, block);
            if (next is ConsString) data.stats_.leaves_++;
            data.stats_.chars_ += block.Length;
            answer = next;
        }
        data.stats_.left_traversals_ = data.stats_.leaves_ - 2;
        return answer;
    }

    JSString ConstructRight(ConsStringGenerationData data, int depth)
    {
        JSString answer = factory.NewStringFromAsciiChecked("");
        data.stats_.leaves_++;
        for (int i = depth - 1; i >= 0; i--)
        {
            JSString block = data.Block(i);
            JSString next = factory.NewConsString(block, answer);
            if (next is ConsString) data.stats_.leaves_++;
            data.stats_.chars_ += block.Length;
            answer = next;
        }
        data.stats_.right_traversals_ = data.stats_.leaves_ - 2;
        return answer;
    }

    JSString ConstructBalancedHelper(ConsStringGenerationData data, int from, int to)
    {
        Assert.True(to > from);
        if (to - from == 1)
        {
            data.stats_.chars_ += data.Block(from).Length;
            return data.Block(from);
        }
        if (to - from == 2)
        {
            data.stats_.chars_ += data.Block(from).Length;
            data.stats_.chars_ += data.Block(from + 1).Length;
            return factory.NewConsString(data.Block(from), data.Block(from + 1));
        }
        JSString part1 = ConstructBalancedHelper(data, from, from + ((to - from) / 2));
        JSString part2 = ConstructBalancedHelper(data, from + ((to - from) / 2), to);
        if (part1 is ConsString) data.stats_.left_traversals_++;
        if (part2 is ConsString) data.stats_.right_traversals_++;
        return factory.NewConsString(part1, part2);
    }

    JSString ConstructBalanced(ConsStringGenerationData data, int depth = DEEP_DEPTH)
    {
        JSString str = ConstructBalancedHelper(data, 0, depth);
        data.stats_.leaves_ = data.stats_.left_traversals_ + data.stats_.right_traversals_ + 2;
        return str;
    }

    // V8's static Traverse(s1, s2); renamed because xUnit forbids overloading the
    // Traverse test method.
    static void TraverseAll(JSString s1, JSString s2)
    {
        int i = 0;
        var characterStream1 = new StringCharacterStream(s1);
        var characterStream2 = new StringCharacterStream(s2);
        while (characterStream1.HasMore())
        {
            Assert.True(characterStream2.HasMore());
            ushort c = characterStream1.GetNext();
            Assert.Equal(c, characterStream2.GetNext());
            i++;
        }
        Assert.False(characterStream1.HasMore());
        Assert.False(characterStream2.HasMore());
        Assert.Equal(s1.Length, i);
        Assert.Equal(s2.Length, i);
    }

    static void TraverseFirst(JSString s1, JSString s2, int chars)
    {
        int i = 0;
        var characterStream1 = new StringCharacterStream(s1);
        var characterStream2 = new StringCharacterStream(s2);
        while (characterStream1.HasMore() && i < chars)
        {
            Assert.True(characterStream2.HasMore());
            ushort c = characterStream1.GetNext();
            Assert.Equal(c, characterStream2.GetNext());
            i++;
        }
        s1.Get(s1.Length - 1);
        s2.Get(s2.Length - 1);
    }

    [Fact]
    public void Traverse()
    {
        Isolate isolate = i_isolate;
        var data = new ConsStringGenerationData(this, false);
        JSString flat = ConstructBalanced(data);
        JSString.Flatten(isolate, flat);
        JSString leftAsymmetric = ConstructLeft(data, DEEP_DEPTH);
        JSString rightAsymmetric = ConstructRight(data, DEEP_DEPTH);
        JSString symmetric = ConstructBalanced(data);
        TraverseAll(flat,symmetric);
        TraverseAll(flat,leftAsymmetric);
        TraverseAll(flat,rightAsymmetric);
        JSString leftDeepAsymmetric = ConstructLeft(data, SUPER_DEEP_DEPTH);
        JSString rightDeepAsymmetric = ConstructRight(data, SUPER_DEEP_DEPTH);
        TraverseFirst(leftAsymmetric, leftDeepAsymmetric, 1050);
        TraverseFirst(leftAsymmetric, rightDeepAsymmetric, 65536);
        JSString.Flatten(isolate, leftAsymmetric);
        TraverseAll(flat,leftAsymmetric);
        JSString.Flatten(isolate, rightAsymmetric);
        TraverseAll(flat,rightAsymmetric);
        JSString.Flatten(isolate, symmetric);
        TraverseAll(flat,symmetric);
        JSString.Flatten(isolate, leftDeepAsymmetric);
    }

    [Fact]
    public void ConsStringWithEmptyFirstFlatten()
    {
        JSString initialFst = factory.NewStringFromAsciiChecked("fst012345");
        JSString initialSnd = factory.NewStringFromAsciiChecked("snd012345");
        JSString str = factory.NewConsString(initialFst, initialSnd);
        ConsString cons = Assert.IsType<ConsString>(str);

        int initialLength = cons.Length;

        // set_first / set_second does not update the length (which the heap verifier
        // checks), so we need to ensure the length stays the same.

        JSString newFst = factory.EmptyString;
        JSString newSnd = factory.NewStringFromAsciiChecked("snd012345012345678");
        cons.SetFirst(newFst);
        cons.SetSecond(newSnd);
        Assert.False(cons.IsFlat);
        Assert.Equal(initialLength, newFst.Length + newSnd.Length);
        Assert.Equal(initialLength, cons.Length);

        // Make sure Flatten doesn't alloc a new string.
        JSString flat = JSString.Flatten(i_isolate, cons);
        Assert.True(flat.IsFlat);
        Assert.Equal(initialLength, flat.Length);
        Assert.Same(newSnd, flat);
    }

    static void VerifyCharacterStream(JSString flatString, JSString consString)
    {
        // Do not want to test ConString traversal on flat string.
        Assert.True(flatString.IsFlat && flatString is not ConsString);
        Assert.IsType<ConsString>(consString);
        // TODO(dcarney) Test stream reset as well.
        int length = flatString.Length;
        // Iterate start search in multiple places in the string.
        int outerIterations = length > 20 ? 20 : length;
        for (int j = 0; j <= outerIterations; j++)
        {
            int offset = length * j / outerIterations;
            if (offset < 0) offset = 0;
            // Want to test the offset == length case.
            if (offset > length) offset = length;
            var flatStream = new StringCharacterStream(flatString, offset);
            var consStream = new StringCharacterStream(consString, offset);
            for (int i = offset; i < length; i++)
            {
                ushort c = flatString.Get(i);
                Assert.True(flatStream.HasMore());
                Assert.True(consStream.HasMore());
                Assert.Equal(c, flatStream.GetNext());
                Assert.Equal(c, consStream.GetNext());
            }
            Assert.False(flatStream.HasMore());
            Assert.False(consStream.HasMore());
        }
    }

    delegate JSString BuildString(int testCase, ConsStringGenerationData data);

    void TestStringCharacterStream(BuildString build, int testCases)
    {
        var data = new ConsStringGenerationData(this, true);
        for (int i = 0; i < testCases; i++)
        {
            // Build flat version of cons string.
            JSString flatString = build(i, data);
            var flatStringStats = new ConsStringStats();
            AccumulateStats(flatString, flatStringStats);
            // Flatten string.
            JSString.Flatten(i_isolate, flatString);
            // Build unflattened version of cons string to test.
            JSString consString = build(i, data);
            var consStringStats = new ConsStringStats();
            AccumulateStats(consString, consStringStats);
            // Full verify of cons string.
            consStringStats.VerifyEqual(flatStringStats);
            consStringStats.VerifyEqual(data.stats_);
            VerifyConsString(consString, data);
            JSString flatStringPtr = flatString is ConsString flatCons ? flatCons.First : flatString;
            VerifyCharacterStream(flatStringPtr, consString);
        }
    }

    const int kCharacterStreamNonRandomCases = 8;

    JSString BuildEdgeCaseConsString(int testCase, ConsStringGenerationData data)
    {
        data.Reset();
        switch (testCase)
        {
            case 0:
                return ConstructBalanced(data, 71);
            case 1:
                return ConstructLeft(data, 71);
            case 2:
                return ConstructRight(data, 71);
            case 3:
                return ConstructLeft(data, 10);
            case 4:
                return ConstructRight(data, 10);
            case 5:
                // 2 element balanced tree.
                data.stats_.chars_ += data.Block(0).Length;
                data.stats_.chars_ += data.Block(1).Length;
                data.stats_.leaves_ += 2;
                return factory.NewConsString(data.Block(0), data.Block(1));
            case 6:
            {
                // Simple flattened tree.
                data.stats_.chars_ += data.Block(0).Length;
                data.stats_.chars_ += data.Block(1).Length;
                data.stats_.leaves_ += 2;
                data.stats_.empty_leaves_ += 1;
                JSString str = factory.NewConsString(data.Block(0), data.Block(1));
                JSString.Flatten(i_isolate, str);
                return str;
            }
            case 7:
            {
                // Left node flattened.
                data.stats_.chars_ += data.Block(0).Length;
                data.stats_.chars_ += data.Block(1).Length;
                data.stats_.chars_ += data.Block(2).Length;
                data.stats_.leaves_ += 3;
                data.stats_.empty_leaves_ += 1;
                data.stats_.left_traversals_ += 1;
                JSString left = factory.NewConsString(data.Block(0), data.Block(1));
                JSString.Flatten(i_isolate, left);
                return factory.NewConsString(left, data.Block(2));
            }
            case 8:
            {
                // Left node and right node flattened.
                data.stats_.chars_ += data.Block(0).Length;
                data.stats_.chars_ += data.Block(1).Length;
                data.stats_.chars_ += data.Block(2).Length;
                data.stats_.chars_ += data.Block(3).Length;
                data.stats_.leaves_ += 4;
                data.stats_.empty_leaves_ += 2;
                data.stats_.left_traversals_ += 1;
                data.stats_.right_traversals_ += 1;
                JSString left = factory.NewConsString(data.Block(0), data.Block(1));
                JSString.Flatten(i_isolate, left);
                JSString right = factory.NewConsString(data.Block(2), data.Block(2));
                JSString.Flatten(i_isolate, right);
                return factory.NewConsString(left, right);
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public void StringCharacterStreamEdgeCases() =>
        TestStringCharacterStream(BuildEdgeCaseConsString, kCharacterStreamNonRandomCases);

    const int kBalances = 3;
    const int kTreeLengths = 4;
    const int kEmptyLeaves = 4;
    const int kUniqueRandomParameters = kBalances * kTreeLengths * kEmptyLeaves;

    static void InitializeGenerationData(int testCase, ConsStringGenerationData data)
    {
        // Clear the settings and reinit the rng.
        data.Reset();
        // Spin up the rng to a known location that is unique per test.
        const int kPerTestJump = 501;
        for (int j = 0; j < testCase * kPerTestJump; j++)
        {
            data.rng_.Next();
        }
        // Choose balanced, left or right heavy trees.
        switch (testCase % kBalances)
        {
            case 0:
                // Nothing to do.  Already balanced.
                break;
            case 1:
                // Left balanced.
                data.leftness_ = 0.90;
                data.rightness_ = 0.15;
                break;
            case 2:
                // Right balanced.
                data.leftness_ = 0.15;
                data.rightness_ = 0.90;
                break;
        }
        // Must remove the influence of the above decision.
        testCase /= kBalances;
        // Choose tree length.
        switch (testCase % kTreeLengths)
        {
            case 0:
                data.max_leaves_ = 16;
                data.early_termination_threshold_ = 0.2;
                break;
            case 1:
                data.max_leaves_ = 50;
                data.early_termination_threshold_ = 0.05;
                break;
            case 2:
                data.max_leaves_ = 500;
                data.early_termination_threshold_ = 0.03;
                break;
            case 3:
                data.max_leaves_ = 5000;
                data.early_termination_threshold_ = 0.001;
                break;
        }
        // Must remove the influence of the above decision.
        testCase /= kTreeLengths;
        // Choose how much we allow empty nodes, including not at all.
        data.empty_leaf_threshold_ = 0.03 * (testCase % kEmptyLeaves);
    }

    JSString BuildRandomConsString(int testCase, ConsStringGenerationData data)
    {
        InitializeGenerationData(testCase, data);
        return ConstructRandomString(data, 200);
    }

    [Fact]
    public void StringCharacterStreamRandom() =>
        TestStringCharacterStream(BuildRandomConsString, kUniqueRandomParameters * 7);

    const int kDeepOneByteDepth = 100000;

    [Fact]
    public void DeepOneByte()
    {
        var foo = new char[kDeepOneByteDepth];
        for (int i = 0; i < kDeepOneByteDepth; i++)
        {
            foo[i] = "foo "[i % 4];
        }
        JSString str = factory.NewStringFromUtf16(new string(foo));
        JSString fooString = factory.NewStringFromAsciiChecked("foo");
        for (int i = 0; i < kDeepOneByteDepth; i += 10)
        {
            str = factory.NewConsString(str, fooString);
        }
        JSString flatString = factory.NewConsString(str, fooString);
        JSString.Flatten(i_isolate, flatString);

        for (int i = 0; i < 500; i++)
        {
            TraverseFirst(flatString, str, kDeepOneByteDepth);
        }
    }

    [Fact]
    public void SliceFromCons()
    {
        JSString str = factory.NewStringFromAsciiChecked("parentparentparent");
        JSString parent = factory.NewConsString(str, str);
        Assert.IsType<ConsString>(parent);
        Assert.False(parent.IsFlat);
        JSString slice = factory.NewSubString(parent, 1, 25);
        // After slicing, the original string becomes a flat cons.
        Assert.True(parent.IsFlat);
        SlicedString sliced = Assert.IsType<SlicedString>(slice);
        // Parent could have been short-circuited.
        Assert.Same(parent is ConsString parentCons ? parentCons.First : parent, sliced.Parent);
        Assert.IsType<SeqString>(sliced.Parent);
        Assert.True(slice.IsFlat);
    }

    readonly record struct IndexData(string str, bool is_array_index, uint array_index, bool is_integer_index,
        ulong integer_index);

    void TestString(IndexData data)
    {
        JSString s = factory.NewStringFromAsciiChecked(data.str);
        if (data.is_array_index)
        {
            Assert.True(s.AsArrayIndex(out uint index));
            Assert.Equal(data.array_index, index);
        }
        if (data.is_integer_index)
        {
            Assert.True(s.AsIntegerIndex(out ulong index));
            Assert.Equal(data.integer_index, index);
            Assert.True(Name.IsIntegerIndex(s.EnsureRawHash()));
            Assert.True(s.HasHashCode);
        }
        if (!s.HasHashCode) s.EnsureHash();
        Assert.True(s.HasHashCode);
        if (!data.is_integer_index)
        {
            Assert.True(Name.IsHash(s.RawHashField));
        }
    }

    // Name::HashBits::decode.
    static uint DecodeHashBits(uint rawHashField) => (rawHashField >> Name.HashShift) & Name.HashBitsMax;

    [Fact]
    public void HashArrayIndexStrings()
    {
        Assert.Equal(DecodeHashBits(StringHasher.MakeArrayIndexHash(0 /* value */, 1 /* length */)),
            ReadOnlyRoots.zero_string.EnsureHash());
        Assert.Equal(DecodeHashBits(StringHasher.MakeArrayIndexHash(1 /* value */, 1 /* length */)),
            ReadOnlyRoots.one_string.EnsureHash());

        Assert.Equal(0u, StringHasher.DecodeArrayIndexFromHashField(ReadOnlyRoots.zero_string.RawHashField));
        Assert.Equal(1u, StringHasher.DecodeArrayIndexFromHashField(ReadOnlyRoots.one_string.RawHashField));

        IndexData[] tests =
        [
            new("", false, 0, false, 0),
            new("123no", false, 0, false, 0),
            new("12345", true, 12345, true, 12345),
            new("12345678", true, 12345678, true, 12345678),
            new("1000000", true, 1000000, true, 1000000),
            new("9999999", true, 9999999, true, 9999999),
            new("10000000", true, 10000000, true, 10000000),
            new("16777215", true, 16777215, true, 16777215),  // max cached index
            new("99999999", true, 99999999, true, 99999999),
            new("4294967294", true, 4294967294u, true, 4294967294u),
            new("4294967295", false, 0, true, 4294967295u),
            new("4294967296", false, 0, true, 4294967296ul),
            new("9007199254740991", false, 0, true, 9007199254740991ul),
            new("9007199254740992", false, 0, false, 0),
            new("18446744073709551615", false, 0, false, 0),
            new("18446744073709551616", false, 0, false, 0),
        ];
        foreach (IndexData test in tests)
        {
            TestString(test);
        }
    }

    [Fact]
    public void ArrayIndexHashRoundTrip()
    {
        const uint maxValue = (1u << Name.kArrayIndexValueBits) - 1;
        for (uint value = 0; value <= maxValue; value++)
        {
            uint length = value == 0 ? 1 : (uint)Math.Log10(value) + 1;
            uint rawHashField = StringHasher.MakeArrayIndexHash(value, length);
            uint decoded = StringHasher.DecodeArrayIndexFromHashField(rawHashField);
            Assert.Equal(value, decoded);
        }
    }

    [Fact]
    public void StringEquals()
    {
        JSString fooStr = factory.NewStringFromUtf16("foo");
        JSString barStr = factory.NewStringFromUtf16("bar");
        JSString fooStr2 = factory.NewStringFromUtf16("foo");
        JSString fooTwoByteStr = factory.NewStringFromUtf16(new string(['f', 'o', 'o']));

        Assert.True(JSString.Equals(fooStr, fooStr));
        Assert.False(JSString.Equals(fooStr, barStr));
        Assert.True(JSString.Equals(fooStr, fooStr2));
        Assert.True(JSString.Equals(fooStr, fooTwoByteStr));
        Assert.False(JSString.Equals(barStr, fooStr2));
    }
}
