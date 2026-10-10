// The subset of V8's CHECK/EXPECT macros (src/base/logging.h, gtest) that the
// ported unit tests use, so the converted test bodies stay close to the C++.

using System.Numerics;

namespace V8Sharp.Base.Tests;

public static class V8Check
{
    public static void CHECK(bool condition) => Assert.True(condition);
    public static void EXPECT_TRUE(bool condition) => Assert.True(condition);
    public static void EXPECT_FALSE(bool condition) => Assert.False(condition);
    public static void CHECK_IMPLIES(bool a, bool b) => Assert.True(!a || b);

    public static void CHECK_EQ(string? expected, string? actual) => Assert.Equal(expected, actual);
    public static void EXPECT_EQ(string? expected, string? actual) => Assert.Equal(expected, actual);

    // Doubles compare with ==, as CHECK_EQ does in C++ (so -0.0 equals 0.0).
    public static void CHECK_EQ(double expected, double actual) =>
        Assert.True(expected == actual, $"Expected {Show(expected)}, actual {Show(actual)}");
    public static void EXPECT_EQ(double expected, double actual) => CHECK_EQ(expected, actual);
    public static void CHECK_NE(double expected, double actual) =>
        Assert.True(expected != actual, $"Expected != {Show(expected)}");

    public static void CHECK_EQ<T>(T expected, T actual) where T : IEquatable<T> => Assert.Equal(expected, actual);
    public static void EXPECT_EQ<T>(T expected, T actual) where T : IEquatable<T> => Assert.Equal(expected, actual);
    public static void CHECK_NE<T>(T expected, T actual) where T : IEquatable<T> => Assert.NotEqual(expected, actual);
    public static void EXPECT_NE<T>(T expected, T actual) where T : IEquatable<T> => Assert.NotEqual(expected, actual);

    public static void CHECK_EQ(long expected, long actual) => Assert.Equal(expected, actual);
    public static void CHECK_EQ(ulong expected, ulong actual) => Assert.Equal(expected, actual);
    public static void EXPECT_EQ(long expected, long actual) => Assert.Equal(expected, actual);
    public static void EXPECT_EQ(ulong expected, ulong actual) => Assert.Equal(expected, actual);
    public static void CHECK_NE(long expected, long actual) => Assert.NotEqual(expected, actual);

    public static void CHECK_LT<T>(T a, T b) where T : IComparable<T> => Assert.True(a.CompareTo(b) < 0, $"{a} < {b}");
    public static void CHECK_LE<T>(T a, T b) where T : IComparable<T> => Assert.True(a.CompareTo(b) <= 0, $"{a} <= {b}");
    public static void CHECK_GT<T>(T a, T b) where T : IComparable<T> => Assert.True(a.CompareTo(b) > 0, $"{a} > {b}");
    public static void CHECK_GE<T>(T a, T b) where T : IComparable<T> => Assert.True(a.CompareTo(b) >= 0, $"{a} >= {b}");
    public static void EXPECT_LT<T>(T a, T b) where T : IComparable<T> => CHECK_LT(a, b);
    public static void EXPECT_LE<T>(T a, T b) where T : IComparable<T> => CHECK_LE(a, b);
    public static void EXPECT_GT<T>(T a, T b) where T : IComparable<T> => CHECK_GT(a, b);
    public static void EXPECT_GE<T>(T a, T b) where T : IComparable<T> => CHECK_GE(a, b);

    static string Show(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                                    (d == 0 && double.IsNegative(d) ? " (-0)" : "");

    /// <summary>The characters of a null-terminated buffer, like a C string.</summary>
    public static string CStr(ReadOnlySpan<char> buffer)
    {
        int n = buffer.IndexOf('\0');
        return new string(n < 0 ? buffer : buffer[..n]);
    }

    public static string CStrVector(string s) => s;

    /// <summary>Removes trailing '0' digits of a null-terminated buffer.
    /// Can create an empty string if all digits are 0.</summary>
    public static void TrimRepresentation(Span<char> representation)
    {
        int len = representation.IndexOf('\0');
        if (len < 0) len = representation.Length;
        while (len > 0 && representation[len - 1] == '0') --len;
        representation[len] = '\0';
    }
}
