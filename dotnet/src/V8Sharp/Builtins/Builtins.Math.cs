// Port of src/builtins/math.tq (every Math function, Math.hypot's fast and
// slow paths, Math.random's xorshift128+ over the native context state),
// src/builtins/builtins-math.cc (Math.sumPrecise) and
// src/numbers/math-random.cc (MathRandom).
//
// The Float64* machine operations map to V8Sharp.Base.Ieee754 (base::ieee754)
// and InternalMath.pow (math::pow), as V8's ExternalReferences do.
using System.Runtime.CompilerServices;
using V8Sharp.Base;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterMath()
    {
        Register(Builtin.MathAbs, BuiltinsMath.MathAbs);
        Register(Builtin.MathCeil, BuiltinsMath.MathCeil);
        Register(Builtin.MathFloor, BuiltinsMath.MathFloor);
        Register(Builtin.MathRound, BuiltinsMath.MathRound);
        Register(Builtin.MathTrunc, BuiltinsMath.MathTrunc);
        Register(Builtin.MathPow, BuiltinsMath.MathPow);
        Register(Builtin.MathMax, BuiltinsMath.MathMax);
        Register(Builtin.MathMin, BuiltinsMath.MathMin);
        Register(Builtin.MathAcos, BuiltinsMath.MathAcos);
        Register(Builtin.MathAcosh, BuiltinsMath.MathAcosh);
        Register(Builtin.MathAsin, BuiltinsMath.MathAsin);
        Register(Builtin.MathAsinh, BuiltinsMath.MathAsinh);
        Register(Builtin.MathAtan, BuiltinsMath.MathAtan);
        Register(Builtin.MathAtan2, BuiltinsMath.MathAtan2);
        Register(Builtin.MathAtanh, BuiltinsMath.MathAtanh);
        Register(Builtin.MathCbrt, BuiltinsMath.MathCbrt);
        Register(Builtin.MathClz32, BuiltinsMath.MathClz32);
        Register(Builtin.MathCos, BuiltinsMath.MathCos);
        Register(Builtin.MathCosh, BuiltinsMath.MathCosh);
        Register(Builtin.MathExp, BuiltinsMath.MathExp);
        Register(Builtin.MathExpm1, BuiltinsMath.MathExpm1);
        Register(Builtin.MathFround, BuiltinsMath.MathFround);
        Register(Builtin.MathF16round, BuiltinsMath.MathF16round);
        Register(Builtin.MathImul, BuiltinsMath.MathImul);
        Register(Builtin.MathLog, BuiltinsMath.MathLog);
        Register(Builtin.MathLog1p, BuiltinsMath.MathLog1p);
        Register(Builtin.MathLog10, BuiltinsMath.MathLog10);
        Register(Builtin.MathLog2, BuiltinsMath.MathLog2);
        Register(Builtin.MathSin, BuiltinsMath.MathSin);
        Register(Builtin.MathSign, BuiltinsMath.MathSign);
        Register(Builtin.MathSinh, BuiltinsMath.MathSinh);
        Register(Builtin.MathSqrt, BuiltinsMath.MathSqrt);
        Register(Builtin.MathTan, BuiltinsMath.MathTan);
        Register(Builtin.MathTanh, BuiltinsMath.MathTanh);
        Register(Builtin.MathHypot, BuiltinsMath.MathHypot);
        Register(Builtin.MathRandom, BuiltinsMath.MathRandom);
        Register(Builtin.MathSumPrecise, BuiltinsMath.MathSumPrecise);
    }
}

/// <summary>The Math builtins.</summary>
public static class BuiltinsMath
{
    /// <summary>ToNumber_Inline followed by Convert&lt;float64&gt;.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double ToFloat64(Isolate isolate, in BuiltinArguments args, int index)
    {
        JSValue x = args.AtOrUndefined(index);
        return x.IsNumber ? x.Number : ObjectOps.ConvertToNumber(isolate, x).Number;
    }

    /// <summary>ReduceToSmiOrFloat64: the argument as a Number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSValue ReduceToNumber(Isolate isolate, in BuiltinArguments args)
    {
        JSValue x = args.AtOrUndefined(1);
        return x.IsNumber ? x : ObjectOps.ConvertToNumber(isolate, x);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSValue N(double d) => JSValue.FromNumber(d);

    /// <summary>https://tc39.es/ecma262/#sec-math.abs</summary>
    public static JSValue MathAbs(Isolate isolate, in BuiltinArguments args)
    {
        JSValue x = ReduceToNumber(isolate, args);
        if (x.IsSmi) return x.Number >= 0 ? x : N(-x.Number);
        return N(Math.Abs(x.Number));
    }

    /// <summary>https://tc39.es/ecma262/#sec-math.ceil</summary>
    public static JSValue MathCeil(Isolate isolate, in BuiltinArguments args)
    {
        JSValue x = ReduceToNumber(isolate, args);
        return x.IsSmi ? x : N(Math.Ceiling(x.Number));
    }

    /// <summary>https://tc39.es/ecma262/#sec-math.floor</summary>
    public static JSValue MathFloor(Isolate isolate, in BuiltinArguments args)
    {
        JSValue x = ReduceToNumber(isolate, args);
        return x.IsSmi ? x : N(Math.Floor(x.Number));
    }

    /// <summary>https://tc39.es/ecma262/#sec-math.round</summary>
    public static JSValue MathRound(Isolate isolate, in BuiltinArguments args)
    {
        JSValue x = ReduceToNumber(isolate, args);
        return x.IsSmi ? x : N(Float64Round(x.Number));
    }

    /// <summary>
    /// CodeStubAssembler::Float64Round: round half up, keeping -0 for
    /// arguments in [-0.5, -0].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Float64Round(double x)
    {
        // Round up {x} towards Infinity.
        double result = Math.Ceiling(x);
        if (result - 0.5 <= x) return result;
        return result - 1.0;
    }

    /// <summary>https://tc39.es/ecma262/#sec-math.trunc</summary>
    public static JSValue MathTrunc(Isolate isolate, in BuiltinArguments args)
    {
        JSValue x = ReduceToNumber(isolate, args);
        return x.IsSmi ? x : N(Math.Truncate(x.Number));
    }

    /// <summary>MathPowImpl: TruncateTaggedToFloat64 of both, then Float64Pow (math::pow).</summary>
    public static double MathPowImpl(Isolate isolate, JSValue @base, JSValue exponent)
    {
        double baseValue = ObjectOps.ToNumber(isolate, @base).Number;
        double exponentValue = ObjectOps.ToNumber(isolate, exponent).Number;
        return InternalMath.pow(baseValue, exponentValue);
    }

    /// <summary>https://tc39.es/ecma262/#sec-math.pow</summary>
    public static JSValue MathPow(Isolate isolate, in BuiltinArguments args) =>
        N(MathPowImpl(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2)));

    /// <summary>https://tc39.es/ecma262/#sec-math.max</summary>
    public static JSValue MathMax(Isolate isolate, in BuiltinArguments args)
    {
        double result = double.NegativeInfinity;
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        for (int i = 0; i < arguments.Length; i++)
        {
            JSValue a = arguments[i];
            double doubleValue = a.IsNumber ? a.Number : ObjectOps.ConvertToNumber(isolate, a).Number;
            result = Math.Max(result, doubleValue);
        }
        return N(result);
    }

    /// <summary>https://tc39.es/ecma262/#sec-math.min</summary>
    public static JSValue MathMin(Isolate isolate, in BuiltinArguments args)
    {
        double result = double.PositiveInfinity;
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        for (int i = 0; i < arguments.Length; i++)
        {
            JSValue a = arguments[i];
            double doubleValue = a.IsNumber ? a.Number : ObjectOps.ConvertToNumber(isolate, a).Number;
            result = Math.Min(result, doubleValue);
        }
        return N(result);
    }

    public static JSValue MathAcos(Isolate isolate, in BuiltinArguments args) => N(Ieee754.acos(ToFloat64(isolate, args, 1)));
    public static JSValue MathAcosh(Isolate isolate, in BuiltinArguments args) => N(Ieee754.acosh(ToFloat64(isolate, args, 1)));
    public static JSValue MathAsin(Isolate isolate, in BuiltinArguments args) => N(Ieee754.asin(ToFloat64(isolate, args, 1)));
    public static JSValue MathAsinh(Isolate isolate, in BuiltinArguments args) => N(Ieee754.asinh(ToFloat64(isolate, args, 1)));
    public static JSValue MathAtan(Isolate isolate, in BuiltinArguments args) => N(Ieee754.atan(ToFloat64(isolate, args, 1)));

    /// <summary>https://tc39.es/ecma262/#sec-math.atan2</summary>
    public static JSValue MathAtan2(Isolate isolate, in BuiltinArguments args)
    {
        double yValue = ToFloat64(isolate, args, 1);
        double xValue = ToFloat64(isolate, args, 2);
        return N(Ieee754.atan2(yValue, xValue));
    }

    public static JSValue MathAtanh(Isolate isolate, in BuiltinArguments args) => N(Ieee754.atanh(ToFloat64(isolate, args, 1)));
    public static JSValue MathCbrt(Isolate isolate, in BuiltinArguments args) => N(Ieee754.cbrt(ToFloat64(isolate, args, 1)));

    /// <summary>https://tc39.es/ecma262/#sec-math.clz32</summary>
    public static JSValue MathClz32(Isolate isolate, in BuiltinArguments args)
    {
        int value = Conversions.DoubleToInt32(ToFloat64(isolate, args, 1));
        return JSValue.FromInt(System.Numerics.BitOperations.LeadingZeroCount((uint)value));
    }

    public static JSValue MathCos(Isolate isolate, in BuiltinArguments args) => N(Ieee754.cos(ToFloat64(isolate, args, 1)));
    public static JSValue MathCosh(Isolate isolate, in BuiltinArguments args) => N(Ieee754.cosh(ToFloat64(isolate, args, 1)));
    public static JSValue MathExp(Isolate isolate, in BuiltinArguments args) => N(Ieee754.exp(ToFloat64(isolate, args, 1)));
    public static JSValue MathExpm1(Isolate isolate, in BuiltinArguments args) => N(Ieee754.expm1(ToFloat64(isolate, args, 1)));

    /// <summary>https://tc39.es/ecma262/#sec-math.fround</summary>
    public static JSValue MathFround(Isolate isolate, in BuiltinArguments args) =>
        N((float)ToFloat64(isolate, args, 1));

    /// <summary>https://tc39.es/ecma262/#sec-math.f16round</summary>
    public static JSValue MathF16round(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kFloat16Array");
        ushort x16 = Conversions.DoubleToFloat16(ToFloat64(isolate, args, 1));
        return N((double)BitConverter.UInt16BitsToHalf(x16));
    }

    /// <summary>https://tc39.es/ecma262/#sec-math.imul</summary>
    public static JSValue MathImul(Isolate isolate, in BuiltinArguments args)
    {
        int x = Conversions.DoubleToInt32(ToFloat64(isolate, args, 1));
        int y = Conversions.DoubleToInt32(ToFloat64(isolate, args, 2));
        return JSValue.FromInt(unchecked(x * y));
    }

    public static JSValue MathLog(Isolate isolate, in BuiltinArguments args) => N(Ieee754.log(ToFloat64(isolate, args, 1)));
    public static JSValue MathLog1p(Isolate isolate, in BuiltinArguments args) => N(Ieee754.log1p(ToFloat64(isolate, args, 1)));
    public static JSValue MathLog10(Isolate isolate, in BuiltinArguments args) => N(Ieee754.log10(ToFloat64(isolate, args, 1)));
    public static JSValue MathLog2(Isolate isolate, in BuiltinArguments args) => N(Ieee754.log2(ToFloat64(isolate, args, 1)));
    public static JSValue MathSin(Isolate isolate, in BuiltinArguments args) => N(Ieee754.sin(ToFloat64(isolate, args, 1)));

    /// <summary>https://tc39.es/ecma262/#sec-math.sign</summary>
    public static JSValue MathSign(Isolate isolate, in BuiltinArguments args)
    {
        JSValue num = ReduceToNumber(isolate, args);
        double value = num.Number;
        if (value < 0) return JSValue.FromInt(-1);
        if (value > 0) return JSValue.FromInt(1);
        return num;
    }

    public static JSValue MathSinh(Isolate isolate, in BuiltinArguments args) => N(Ieee754.sinh(ToFloat64(isolate, args, 1)));
    public static JSValue MathSqrt(Isolate isolate, in BuiltinArguments args) => N(Math.Sqrt(ToFloat64(isolate, args, 1)));
    public static JSValue MathTan(Isolate isolate, in BuiltinArguments args) => N(Ieee754.tan(ToFloat64(isolate, args, 1)));
    public static JSValue MathTanh(Isolate isolate, in BuiltinArguments args) => N(Ieee754.tanh(ToFloat64(isolate, args, 1)));

    /// <summary>https://tc39.es/ecma262/#sec-math.hypot</summary>
    public static JSValue MathHypot(Isolate isolate, in BuiltinArguments args)
    {
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        int length = arguments.Length;

        // FastMathHypot: few arguments, avoiding the loop.
        if (length <= 3)
        {
            if (length == 0) return JSValue.Zero;
            double a = Math.Abs(ToFloat64(isolate, args, 1));
            if (length == 1) return N(a);
            double b = Math.Abs(ToFloat64(isolate, args, 2));
            double max;
            if (length == 2)
            {
                if (a == double.PositiveInfinity || b == double.PositiveInfinity) return N(double.PositiveInfinity);
                max = Math.Max(a, b);
                if (double.IsNaN(max)) return JSValue.NaN;
                if (max == 0) return JSValue.Zero;
                return N(Math.Sqrt((a / max) * (a / max) + (b / max) * (b / max)) * max);
            }
            double c = Math.Abs(ToFloat64(isolate, args, 3));
            if (a == double.PositiveInfinity || b == double.PositiveInfinity || c == double.PositiveInfinity)
            {
                return N(double.PositiveInfinity);
            }
            max = Math.Max(Math.Max(a, b), c);
            if (double.IsNaN(max)) return JSValue.NaN;
            if (max == 0) return JSValue.Zero;
            double powerA = (a / max) * (a / max);
            double powerB = (b / max) * (b / max);
            double compensation = (powerA + powerB) - powerA - powerB;
            double powerC = (c / max) * (c / max) - compensation;
            return N(Math.Sqrt(powerA + powerB + powerC) * max);
        }

        // Slow path.
        double[] absValues = new double[length];
        bool oneArgIsNaN = false;
        double maxValue = 0;
        for (int i = 0; i < length; ++i)
        {
            double value = ToFloat64(isolate, args, i + 1);
            if (double.IsNaN(value))
            {
                oneArgIsNaN = true;
            }
            else
            {
                double absValue = Math.Abs(value);
                absValues[i] = absValue;
                if (absValue > maxValue) maxValue = absValue;
            }
        }
        if (maxValue == double.PositiveInfinity) return N(double.PositiveInfinity);
        if (oneArgIsNaN) return JSValue.NaN;
        if (maxValue == 0) return JSValue.Zero;

        // Kahan summation to avoid rounding errors.
        // Normalize the numbers to the largest one to avoid overflow.
        double sum = 0;
        double comp = 0;
        for (int i = 0; i < length; ++i)
        {
            double n = absValues[i] / maxValue;
            double summand = n * n - comp;
            double preliminary = sum + summand;
            comp = (preliminary - sum) - summand;
            sum = preliminary;
        }
        return N(Math.Sqrt(sum) * maxValue);
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-math.random, the 64-bit variant of
    /// math.tq: xorshift128+ over the state in the native context, no cache.
    /// </summary>
    public static JSValue MathRandom(Isolate isolate, in BuiltinArguments args)
    {
        NativeContext context = isolate.NativeContext;
        V8Sharp.Builtins.MathRandom.State state = V8Sharp.Builtins.MathRandom.GetState(isolate, context);
        if (state.S0 == 0 && state.S1 == 0)
        {
            V8Sharp.Builtins.MathRandom.InitializeAndMaybeRefillCache(isolate, context);
        }
        ulong random = V8Sharp.Base.Utils.RandomNumberGenerator.XorShift128(ref state.S0, ref state.S1);
        return N(RandomToDouble(random));
    }

    /// <summary>RandomToDouble: a [0,2**53) integer mapped to [0,1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double RandomToDouble(ulong random) => (random >> 11) / 9007199254740992.0;

    // ---------------------------------------------------------------------
    // builtins-math.cc

    /// <summary>Math.sumPrecise ( items ).</summary>
    public static JSValue MathSumPrecise(Isolate isolate, in BuiltinArguments args)
    {
        JSValue items = args.AtOrUndefined(1);

        // 1. Perform ? RequireObjectCoercible(items).
        if (items.IsNullOrUndefined)
        {
            isolate.ThrowTypeError(MessageTemplate.CalledOnNullOrUndefined, isolate.Factory.NewStringFromAsciiChecked("Math.sumPrecise"));
        }

        const uint kMaxIteration = uint.MaxValue;
        var visitor = new SumPreciseVisitor(isolate);
        IterableForEach.Run(isolate, items, ref visitor, allowJSExecution: false, out ulong maxCount, (ulong)EngineGlobals.kMaxSafeInteger);

        if (visitor.OnlyInts)
        {
            if (maxCount > kMaxIteration)
            {
                // This should be basically impossible. Externally mapped array
                // buffers could theoretically be that large.
                isolate.ThrowTypeError(MessageTemplate.IterableTooLargeToSum);
            }
            return N(visitor.IntSum);
        }

        (Xsum.Result kind, double value) = visitor.Xsum.GetSumPrecise();
        return kind switch
        {
            Xsum.Result.kMinusZero => JSValue.MinusZero,
            Xsum.Result.kPlusInfinity => N(double.PositiveInfinity),
            Xsum.Result.kMinusInfinity => N(double.NegativeInfinity),
            Xsum.Result.kNaN => JSValue.NaN,
            _ => N(value),
        };
    }

    struct SumPreciseVisitor(Isolate isolate) : IterableForEach.IVisitor
    {
        public readonly Xsum Xsum = new();
        public bool OnlyInts;
        public long IntSum;

        public bool VisitInt(int value)
        {
            OnlyInts = true;
            IntSum += value;
            return true;
        }

        public bool VisitDouble(double value)
        {
            Debug.Assert(!OnlyInts);
            Xsum.AddForSumPrecise(value);
            return true;
        }

        public bool VisitGeneric(JSValue value)
        {
            if (!value.IsNumber)
            {
                OnlyInts = false;
                isolate.ThrowTypeError(MessageTemplate.IsNotNumber, isolate.Factory.NewStringFromAsciiChecked("Iterator value"),
                    ObjectOps.TypeOf(isolate, value));
            }
            Debug.Assert(!OnlyInts);
            Xsum.AddForSumPrecise(value.Number);
            return true;
        }
    }
}

/// <summary>
/// Port of src/numbers/math-random.{h,cc}: the per-native-context state of
/// Math.random. On 64-bit targets V8 keeps only the xorshift128+ state
/// (kUseRefillCache is false); the cache slot stays undefined.
/// </summary>
public static class MathRandom
{
    public const int kCacheSize = 64;
    public const int kStateSize = 2 * sizeof(long);

    /// <summary>MathRandom::State, held in the MATH_RANDOM_STATE_INDEX slot (V8: a PodArray&lt;State&gt;).</summary>
    public sealed class State() : HeapObject(InstanceType.ByteArrayType)
    {
        public ulong S0;
        public ulong S1;
    }

    /// <summary>MathRandom::InitializeContext.</summary>
    public static void InitializeContext(Isolate isolate, NativeContext nativeContext)
    {
        nativeContext.MathRandomState = new State();
        ResetContext(nativeContext);
        nativeContext.MathRandomCache = JSValue.Undefined;
    }

    /// <summary>MathRandom::ResetContext.</summary>
    public static void ResetContext(NativeContext nativeContext)
    {
        nativeContext.MathRandomIndex = JSValue.Zero;
        if (nativeContext.MathRandomState.HeapObjectOrNull is State state)
        {
            state.S0 = 0;
            state.S1 = 0;
        }
    }

    // Deviation: V8's Genesis calls MathRandom::InitializeContext for every
    // native context; the bootstrapper here leaves the slot empty, so the
    // state is created on first use. Nothing observable depends on when.
    internal static State GetState(Isolate isolate, NativeContext nativeContext)
    {
        if (nativeContext.MathRandomState.HeapObjectOrNull is State state) return state;
        InitializeContext(isolate, nativeContext);
        return (State)nativeContext.MathRandomState.Object;
    }

    /// <summary>MathRandom::InitializeAndMaybeRefillCache.</summary>
    public static int InitializeAndMaybeRefillCache(Isolate isolate, NativeContext nativeContext)
    {
        State state = GetState(isolate, nativeContext);
        // Initialize state if not yet initialized. If a fixed random seed was
        // requested, use it to reset our state the first time a script asks for
        // random numbers in this context. This ensures the script sees a consistent
        // sequence.
        if (state.S0 == 0 && state.S1 == 0)
        {
            ulong seed;
            if (isolate.Flags.random_seed != 0)
            {
                seed = unchecked((ulong)(long)isolate.Flags.random_seed);
            }
            else
            {
                Span<byte> bytes = stackalloc byte[sizeof(ulong)];
                IsolateRandomNumberGenerator(isolate).NextBytes(bytes);
                seed = BitConverter.ToUInt64(bytes);
            }
            state.S0 = V8Sharp.Base.Utils.RandomNumberGenerator.MurmurHash3(seed);
            state.S1 = V8Sharp.Base.Utils.RandomNumberGenerator.MurmurHash3(~seed);
            if (state.S0 == 0 && state.S1 == 0) throw new InvalidOperationException("Check failed: state.s0 != 0 || state.s1 != 0");
        }
        return 0;
    }

    static readonly ConditionalWeakTable<Isolate, V8Sharp.Base.Utils.RandomNumberGenerator> s_generators = new();

    /// <summary>
    /// Isolate::random_number_generator: seeded from --random-seed when it is
    /// set, from the OS otherwise.
    /// </summary>
    public static V8Sharp.Base.Utils.RandomNumberGenerator IsolateRandomNumberGenerator(Isolate isolate) =>
        s_generators.GetValue(isolate, static i =>
            i.Flags.random_seed != 0
                ? new V8Sharp.Base.Utils.RandomNumberGenerator(i.Flags.random_seed)
                : new V8Sharp.Base.Utils.RandomNumberGenerator());
}

/// <summary>
/// Port of src/builtins/builtins-math-xsum.{h,cc}: Radford Neal's exact
/// summation (small superaccumulator), as used by Math.sumPrecise.
/// </summary>
public sealed class Xsum
{
    const int kMantissaBits = 52;
    const int kExpBits = 11;
    const long kMantissaMask = (1L << kMantissaBits) - 1;
    const long kExpMask = (1 << kExpBits) - 1;
    const long kExpBias = (1 << (kExpBits - 1)) - 1;
    const int kSignBit = kMantissaBits + kExpBits;
    const ulong kSignMask = 1UL << kSignBit;
    const int kLowExpBits = 5;
    const int kLowExpMask = (1 << kLowExpBits) - 1;
    const int kHighExpBits = kExpBits - kLowExpBits;
    const int kSchunks = (1 << kHighExpBits) + 3;  // 67
    const int kLowMantissaBits = 1 << kLowExpBits;  // 32
    const long kLowMantissaMask = (1L << kLowMantissaBits) - 1;
    const int kSchunkBits = 64;
    const int kSmallCarryBits = (kSchunkBits - 1) - kMantissaBits;  // 11
    const int kSmallCarryTerms = (1 << kSmallCarryBits) - 1;  // 2047

    readonly long[] _chunk = new long[kSchunks];
    long _inf;
    long _nan;
    int _addsUntilPropagate = kSmallCarryTerms;
    bool _minusZero = true;
    bool _infSignChange;

    public enum Result { kMinusZero, kFinite, kPlusInfinity, kMinusInfinity, kNaN }

    /// <summary>The default xsum algorithm.</summary>
    public void Add(double value)
    {
        if (_addsUntilPropagate == 0) CarryPropagate();
        Add1NoCarry(value);
        _addsUntilPropagate--;
    }

    /// <summary>Variant used in Math.sumPrecise.</summary>
    public void AddForSumPrecise(double value)
    {
        // Math.sumPrecise treats all NaN the same.
        if (_nan != 0) return;
        // Empty lists or lists with only -0 result in -0.
        if (!Conversions.IsMinusZero(value)) _minusZero = false;
        Add(value);
    }

    public void AddForSumPrecise(int value)
    {
        _minusZero = false;
        Add(value);
    }

    public (Result, double) GetSumPrecise()
    {
        if (_minusZero) return (Result.kMinusZero, 0);
        if (_nan != 0 || _infSignChange) return (Result.kNaN, 0);
        if (_inf != 0)
        {
            return (BitConverter.Int64BitsToDouble(_inf) > 0 ? Result.kPlusInfinity : Result.kMinusInfinity, 0);
        }
        return (Result.kFinite, Round());
    }

    void Add1NoCarry(double value)
    {
        long ivalue = BitConverter.DoubleToInt64Bits(value);
        long exp = (ivalue >> kMantissaBits) & kExpMask;
        long mantissa = ivalue & kMantissaMask;
        long highExp = exp >> kLowExpBits;
        long lowExp = exp & kLowExpMask;

        if (exp == 0)
        {
            // Zero or denormalized.
            if (mantissa == 0) return;
            exp = lowExp = 1;
        }
        else if (exp == kExpMask)
        {
            // Inf or NaN.
            AddInfNan(ivalue);
            return;
        }
        else
        {
            // Normalized.
            mantissa |= 1L << kMantissaBits;
        }

        int chunkIt = (int)highExp;
        long split0 = (long)(((ulong)mantissa << (int)lowExp) & (ulong)kLowMantissaMask);
        long split1 = mantissa >> (kLowMantissaBits - (int)lowExp);
        if (ivalue < 0)
        {
            _chunk[chunkIt] -= split0;
            _chunk[chunkIt + 1] -= split1;
        }
        else
        {
            _chunk[chunkIt] += split0;
            _chunk[chunkIt + 1] += split1;
        }
    }

    void AddInfNan(long ivalue)
    {
        long mantissa = ivalue & kMantissaMask;
        if (mantissa == 0)
        {  // Inf
            if (_inf == 0)
            {
                // no previous Inf
                _inf = ivalue;
            }
            else if (_inf != ivalue)
            {
                // previous Inf was opposite sign
                double fltv = BitConverter.Int64BitsToDouble(ivalue);
                fltv -= fltv;  // result will be a NaN
                _inf = BitConverter.DoubleToInt64Bits(fltv);
                _infSignChange = true;
            }
        }
        else
        {  // NaN
            // Choose the NaN with the bigger payload and clear its sign.  Using <=
            // ensures that we will choose the first NaN over the previous zero.
            if ((_nan & kMantissaMask) <= mantissa)
            {
                _nan = ivalue & long.MaxValue;
            }
        }
    }

    /// <summary>Returns the index of the uppermost non-zero chunk.</summary>
    int CarryPropagate()
    {
        long[] chunk = _chunk;
        int u = kSchunks - 1;

        // Search for the uppermost non-zero chunk.
        bool found = false;
        for (int k = 0; k < 3; k++)
        {
            if (chunk[u] != 0) { found = true; break; }
            u--;
        }
        if (!found)
        {
            // Now u is 63. Search downwards in groups of 4.
            while (u >= 0)
            {
                if ((chunk[u] | chunk[u - 1] | chunk[u - 2] | chunk[u - 3]) != 0) break;
                u -= 4;
            }

            // Number is zero.
            if (u < 0)
            {
                _addsUntilPropagate = kSmallCarryTerms - 1;
                return 0;
            }

            for (int k = 0; k < 3; k++)
            {
                if (chunk[u] != 0) break;
                u--;
            }
        }

        Debug.Assert(chunk[u] != 0);

        // Propagate carries.
        // i is the index of the next non-zero chunk from the bottom.
        int i = 0;

        // Quickly skip over unused low-order chunks.
        int limit = u - 3;
        while (i <= limit)
        {
            if ((chunk[i] | chunk[i + 1] | chunk[i + 2] | chunk[i + 3]) != 0) break;
            i += 4;
        }

        int uix = -1;  // Index of uppermost non-zero chunk found so far.

        do
        {
            long c = chunk[i];
            if (c == 0)
            {
                i++;
                // Find the next non-zero chunk.
                while (i <= u && chunk[i] == 0) i++;
                if (i > u) break;
                c = chunk[i];
            }

            long chigh = c >> kLowMantissaBits;
            if (chigh == 0)
            {
                uix = i;
                i++;
                continue;
            }

            if (u == i)
            {
                if (chigh == -1)
                {
                    uix = i;
                    break;  // Don't propagate -1 into the region of all zeros above.
                }
                u = i + 1;
            }

            long clow = c & kLowMantissaMask;
            if (clow != 0) uix = i;

            chunk[i] = clow;
            if (i + 1 >= kSchunks)
            {
                AddInfNan((kExpMask << kMantissaBits) | kMantissaMask);
                u = i;
            }
            else
            {
                chunk[i + 1] += chigh;
            }

            i++;
        } while (i <= u);

        if (uix < 0)
        {
            _addsUntilPropagate = kSmallCarryTerms - 1;
            return 0;
        }

        // While the uppermost chunk is negative, with value -1, combine it with
        // the chunk below.
        while (chunk[uix] == -1 && uix > 0)
        {
            chunk[uix - 1] += -1L * (1L << kLowMantissaBits);
            chunk[uix] = 0;
            uix--;
        }

        _addsUntilPropagate = kSmallCarryTerms - 1;
        return uix;
    }

    /// <summary>Xsum::Round: the correctly rounded sum.</summary>
    public double Round()
    {
        if (_nan != 0) return BitConverter.Int64BitsToDouble(_nan);
        if (_inf != 0) return BitConverter.Int64BitsToDouble(_inf);

        long[] chunk = _chunk;
        int i = CarryPropagate();
        long ivalue = chunk[i];
        long intv;

        // Handle a possible denormalized number, including zero.
        if (i <= 1)
        {
            if (ivalue == 0) return 0.0;

            if (i == 0)
            {
                intv = ivalue >= 0 ? ivalue : -ivalue;
                intv >>= 1;
                if (ivalue < 0) intv |= long.MinValue;
                return BitConverter.Int64BitsToDouble(intv);
            }
            intv = ivalue * (1L << (kLowMantissaBits - 1)) + (chunk[0] >> 1);
            if (intv < 0)
            {
                if (intv > -(1L << kMantissaBits))
                {
                    intv = (-intv) | long.MinValue;
                    return BitConverter.Int64BitsToDouble(intv);
                }
            }
            else
            {
                if ((ulong)intv < (1UL << kMantissaBits)) return BitConverter.Int64BitsToDouble(intv);
            }
        }

        double fltv = ivalue;
        intv = BitConverter.DoubleToInt64Bits(fltv);
        int e = (int)((intv >> kMantissaBits) & kExpMask);
        int more = 2 + kMantissaBits + (int)kExpBias - e;

        ivalue = unchecked(ivalue * (1L << more));
        int j = i - 1;
        long lower = chunk[j];
        if (more >= kLowMantissaBits)
        {
            more -= kLowMantissaBits;
            ivalue += lower << more;
            j--;
            lower = j < 0 ? 0 : chunk[j];
        }
        ivalue += lower >> (kLowMantissaBits - more);
        lower &= (1L << (kLowMantissaBits - more)) - 1;

        bool roundAway = false;
        if (ivalue >= 0)
        {
            intv = 0;
            if ((ivalue & 2) != 0)
            {
                if ((ivalue & 1) != 0)
                {
                    roundAway = true;
                }
                else if ((ivalue & 4) != 0)
                {
                    roundAway = true;
                }
                else
                {
                    if (lower == 0)
                    {
                        while (j > 0)
                        {
                            j--;
                            if (chunk[j] != 0)
                            {
                                lower = 1;
                                break;
                            }
                        }
                    }
                    if (lower != 0) roundAway = true;
                }
            }
        }
        else
        {
            if (((-ivalue) & (1L << (kMantissaBits + 2))) == 0)
            {
                long pos = 1L << (kLowMantissaBits - 1 - more);
                ivalue *= 2;
                if ((lower & pos) != 0)
                {
                    ivalue += 1;
                    lower &= ~pos;
                }
                e -= 1;
            }

            intv = long.MinValue;
            ivalue = -ivalue;

            if ((ivalue & 3) == 3)
            {
                roundAway = true;
            }
            else if ((ivalue & 3) > 1 && (ivalue & 4) != 0)
            {
                if (lower == 0)
                {
                    while (j > 0)
                    {
                        j--;
                        if (chunk[j] != 0)
                        {
                            lower = 1;
                            break;
                        }
                    }
                }
                if (lower == 0) roundAway = true;
            }
        }

        if (roundAway)
        {
            ivalue += 4;
            if ((ivalue & (1L << (kMantissaBits + 3))) != 0)
            {
                ivalue >>= 1;
                e += 1;
            }
        }

        ivalue >>= 2;
        e += (i << kLowExpBits) - (int)kExpBias - kMantissaBits;

        if (e >= kExpMask)
        {
            intv |= kExpMask << kMantissaBits;
            return BitConverter.Int64BitsToDouble(intv);
        }

        intv += (long)e << kMantissaBits;
        intv += ivalue & kMantissaMask;

        return BitConverter.Int64BitsToDouble(intv);
    }
}
