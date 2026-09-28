// Checks the bigint library against an independent implementation
// (System.Numerics.BigInteger) on random operands of many sizes, so that every
// algorithm (schoolbook, Karatsuba, Toom-Cook, FFT, Burnikel-Ziegler, Barrett,
// the fast and classic to/from-string paths) is exercised through the public
// entry points the engine uses.

using System.Globalization;
using System.Numerics;
using V8Sharp.Base.BigInts;
using static V8Sharp.Base.BigInts.Bigint;

namespace V8Sharp.Base.Tests.BigInts;

public class BigintReferenceTest
{
    readonly Random _rng = new(20260928);
    readonly Processor _processor = new();

    static BigInteger ToBig(ReadOnlySpan<ulong> x)
    {
        byte[] bytes = new byte[x.Length * 8 + 1];
        for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), x[i]);
        return new BigInteger(bytes, isUnsigned: true);
    }

    static ulong[] FromBig(BigInteger b)
    {
        byte[] bytes = b.ToByteArray(isUnsigned: true);
        ulong[] r = new ulong[(bytes.Length + 7) / 8];
        for (int i = 0; i < bytes.Length; i++) r[i / 8] |= (ulong)bytes[i] << (8 * (i % 8));
        return (ulong[])Normalize(r).ToArray();
    }

    ulong[] Random(int len)
    {
        ulong[] r = new ulong[len];
        for (int i = 0; i < len; i++)
        {
            r[i] = _rng.Next(4) switch
            {
                0 => ~0UL,
                1 => 0,
                _ => (ulong)_rng.NextInt64() ^ ((ulong)_rng.Next(2) << 63),
            };
        }
        if (len > 0) while (r[^1] == 0) r[^1] = (ulong)_rng.NextInt64();
        return r;
    }

    static readonly int[] s_sizes = [1, 2, 3, 5, 10, 33, 34, 35, 56, 57, 58, 70, 100, 150, 209, 210, 211, 300, 500, 719, 720, 721, 1000, 1500, 2600];

    [Fact]
    public void Multiply()
    {
        foreach (int xl in s_sizes)
        {
            foreach (int yl in s_sizes)
            {
                if (yl > xl || (long)xl * yl > 3_000_000) continue;
                ulong[] X = Random(xl);
                ulong[] Y = Random(yl);
                ulong[] Z = new ulong[xl + yl];
                var (done, _) = MultiplySmall(Z, X, Y);
                if (!done) _processor.MultiplyLarge(Z, X, Y);
                Assert.Equal(ToBig(X) * ToBig(Y), ToBig(Z));
            }
            // Squaring (the FFT has a special path for X == Y).
            ulong[] S = Random(xl);
            ulong[] Z2 = new ulong[2 * xl];
            _processor.Multiply(Z2, S, S);
            Assert.Equal(ToBig(S) * ToBig(S), ToBig(Z2));
        }
    }

    [Fact]
    public void DivideAndModulo()
    {
        foreach (int al in s_sizes)
        {
            foreach (int bl in s_sizes)
            {
                if (bl > al) continue;
                ulong[] A = Random(al);
                ulong[] B = Random(bl);
                BigInteger a = ToBig(A), b = ToBig(B);
                ulong[] Q = new ulong[DivideResultLength(A, B)];
                var (done, _) = DivideSmall(Q, A, B);
                if (!done) _processor.DivideLarge(Q, A, B);
                Assert.Equal(a / b, ToBig(Q));
                ulong[] R = new ulong[B.Length];
                (done, _) = ModuloSmall(R, A, B);
                if (!done) _processor.ModuloLarge(R, A, B);
                Assert.Equal(a % b, ToBig(R));
            }
        }
        // Barrett division proper (divisor above kBarrettThreshold).
        {
            ulong[] B = Random(BigintConfig.kBarrettThreshold + 17);
            ulong[] A = Random(BigintConfig.kBarrettThreshold * 3 + 5);
            ulong[] Q = new ulong[DivideResultLength(A, B)];
            _processor.DivideLarge(Q, A, B);
            Assert.Equal(ToBig(A) / ToBig(B), ToBig(Q));
            ulong[] R = new ulong[B.Length];
            _processor.ModuloLarge(R, A, B);
            Assert.Equal(ToBig(A) % ToBig(B), ToBig(R));
        }
    }

    [Fact]
    public void ToStringAllRadixes()
    {
        foreach (int len in s_sizes)
        {
            if (len > 1500) continue;
            ulong[] X = Random(len);
            BigInteger x = ToBig(X);
            string dec = _processor.ToString(X, 10, false);
            Assert.Equal(x.ToString(CultureInfo.InvariantCulture), dec);
            Assert.Equal("-" + dec, _processor.ToString(X, 10, true));
            string hex = _processor.ToString(X, 16, false);
            Assert.Equal(x.ToString("x", CultureInfo.InvariantCulture).TrimStart('0'), hex);
            for (int radix = 2; radix <= 36; radix++)
            {
                string s = _processor.ToString(X, radix, false);
                Assert.Equal(x, ParseReference(s, radix));
            }
        }
    }

    static BigInteger ParseReference(string s, int radix)
    {
        BigInteger r = BigInteger.Zero;
        foreach (char c in s)
        {
            int d = c <= '9' ? c - '0' : c - 'a' + 10;
            Assert.InRange(d, 0, radix - 1);
            r = r * radix + d;
        }
        return r;
    }

    [Fact]
    public void FromStringAllRadixes()
    {
        foreach (int len in s_sizes)
        {
            if (len > 1500) continue;
            ulong[] X = Random(len);
            for (int radix = 2; radix <= 36; radix++)
            {
                string s = _processor.ToString(X, radix, false);
                FromStringAccumulator acc = new(1 << 24);
                Assert.Equal(s.Length, acc.Parse(s, (ulong)radix));
                ulong[] Z = new ulong[acc.ResultLength()];
                _processor.FromString(Z, acc);
                Assert.Equal(ToBig(X), ToBig(Z));
            }
        }
    }

    [Fact]
    public void AddSubtractSigned()
    {
        for (int iter = 0; iter < 2000; iter++)
        {
            ulong[] X = Random(_rng.Next(1, 20));
            ulong[] Y = Random(_rng.Next(1, 20));
            bool xn = _rng.Next(2) == 0, yn = _rng.Next(2) == 0;
            BigInteger x = xn ? -ToBig(X) : ToBig(X), y = yn ? -ToBig(Y) : ToBig(Y);
            ulong[] Z = new ulong[AddSignedResultLength(X.Length, Y.Length, xn == yn)];
            bool zn = AddSigned(Z, X, xn, Y, yn);
            Assert.Equal(x + y, zn ? -ToBig(Z) : ToBig(Z));
            Z = new ulong[SubtractSignedResultLength(X.Length, Y.Length, xn == yn)];
            zn = SubtractSigned(Z, X, xn, Y, yn);
            Assert.Equal(x - y, zn ? -ToBig(Z) : ToBig(Z));
        }
    }

    [Fact]
    public void Bitwise()
    {
        for (int iter = 0; iter < 2000; iter++)
        {
            ulong[] X = Random(_rng.Next(1, 8));
            ulong[] Y = Random(_rng.Next(1, 8));
            BigInteger x = ToBig(X), y = ToBig(Y);
            int len = Math.Max(X.Length, Y.Length) + 1;
            ulong[] Z = new ulong[len];

            BitwiseAnd_PosPos(Z, X, Y);
            Assert.Equal(x & y, ToBig(Z));
            BitwiseAnd_NegNeg(Z, X, Y);
            Assert.Equal((-x) & (-y), -ToBig(Z));
            BitwiseAnd_PosNeg(Z, X, Y);
            Assert.Equal(x & (-y), ToBig(Z));
            BitwiseOr_PosPos(Z, X, Y);
            Assert.Equal(x | y, ToBig(Z));
            BitwiseOr_NegNeg(Z, X, Y);
            Assert.Equal((-x) | (-y), -ToBig(Z));
            BitwiseOr_PosNeg(Z, X, Y);
            Assert.Equal(x | (-y), -ToBig(Z));
            BitwiseXor_PosPos(Z, X, Y);
            Assert.Equal(x ^ y, ToBig(Z));
            BitwiseXor_NegNeg(Z, X, Y);
            Assert.Equal((-x) ^ (-y), ToBig(Z));
            BitwiseXor_PosNeg(Z, X, Y);
            Assert.Equal(x ^ (-y), -ToBig(Z));
        }
    }

    [Fact]
    public void Shifts()
    {
        for (int iter = 0; iter < 2000; iter++)
        {
            ulong[] X = Random(_rng.Next(1, 8));
            BigInteger x = ToBig(X);
            int shift = _rng.Next(0, 600);
            ulong[] Z = new ulong[X.Length + shift / 64 + 1];
            LeftShift(Z, X, (ulong)shift);
            Assert.Equal(x << shift, ToBig(Z));
            bool neg = _rng.Next(2) == 0;
            int rl = RightShift_ResultLength(X, neg, (ulong)shift, out RightShiftState state);
            BigInteger expected = neg ? -((-x) >> shift) : x >> shift;  // BigInteger >> floors.
            if (rl == 0)
            {
                Assert.True(expected.IsZero || expected == BigInteger.One && neg);
                continue;
            }
            Z = new ulong[rl];
            RightShift(Z, X, (ulong)shift, state);
            Assert.Equal(neg ? ((-x) >> shift) : x >> shift, neg ? -ToBig(Z) : ToBig(Z));
        }
    }

    [Fact]
    public void AsIntNAndAsUintN()
    {
        for (int iter = 0; iter < 3000; iter++)
        {
            ulong[] X = Random(_rng.Next(1, 6));
            bool neg = _rng.Next(2) == 0;
            BigInteger x = neg ? -ToBig(X) : ToBig(X);
            int n = _rng.Next(1, 400);
            BigInteger mod = BigInteger.One << n;
            BigInteger u = ((x % mod) + mod) % mod;
            BigInteger s = u >= (mod >> 1) ? u - mod : u;

            int len = AsIntNResultLength(X, neg, n);
            if (len < 0)
            {
                Assert.Equal(s, x);
            }
            else
            {
                ulong[] Z = new ulong[len];
                bool zn = AsIntN(Z, X, neg, n);
                Assert.Equal(s, zn ? -ToBig(Z) : ToBig(Z));
            }

            if (!neg)
            {
                len = AsUintN_Pos_ResultLength(X, n);
                if (len < 0)
                {
                    Assert.Equal(u, x);
                }
                else
                {
                    ulong[] Z = new ulong[len];
                    AsUintN_Pos(Z, X, n);
                    Assert.Equal(u, ToBig(Z));
                }
            }
            else
            {
                ulong[] Z = new ulong[AsUintN_Neg_ResultLength(n)];
                AsUintN_Neg(Z, X, n);
                Assert.Equal(u, ToBig(Z));
            }
        }
    }

    [Fact]
    public void DigitDiv()
    {
        for (int iter = 0; iter < 100000; iter++)
        {
            ulong divisor = (ulong)_rng.NextInt64() >> _rng.Next(64);
            if (divisor == 0) divisor = 1;
            ulong high = (ulong)_rng.NextInt64() % divisor;
            ulong low = (ulong)_rng.NextInt64();
            ulong q = digit_div(high, low, divisor, out ulong r);
            UInt128 n = ((UInt128)high << 64) | low;
            Assert.Equal((ulong)(n / divisor), q);
            Assert.Equal((ulong)(n % divisor), r);
        }
    }
}
