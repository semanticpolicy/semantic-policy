using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SemanticPolicy.Core.Tests;

// The expected values are RFC 8785's own (https://www.rfc-editor.org/rfc/rfc8785): the §3.2.2 sample with
// the bytes §3.2.4 gives for its canonical form, the §3.2.3 sorting sample, and Appendix B's numbers, typed
// as their IEEE 754 bits so no parser stands between the table and the test.
public sealed class CanonicalJsonTests
{
    private const string _primitives = """
        {
          "numbers": [333333333.33333329, 1E30, 4.50,
                      2e-3, 0.000000000000000000000000001],
          "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
          "literals": [null, true, false]
        }
        """;

    // As §3.2.4 prints it.
    private const string _primitivesUtf8 = """
        7b 22 6c 69 74 65 72 61 6c 73 22 3a 5b 6e 75 6c 6c 2c 74 72
        75 65 2c 66 61 6c 73 65 5d 2c 22 6e 75 6d 62 65 72 73 22 3a
        5b 33 33 33 33 33 33 33 33 33 2e 33 33 33 33 33 33 33 2c 31
        65 2b 33 30 2c 34 2e 35 2c 30 2e 30 30 32 2c 31 65 2d 32 37
        5d 2c 22 73 74 72 69 6e 67 22 3a 22 e2 82 ac 24 5c 75 30 30
        30 66 5c 6e 41 27 42 5c 22 5c 5c 5c 5c 5c 22 2f 22 7d
        """;

    private const string _sorting = """
        {
          "\u20ac": "Euro Sign",
          "\r": "Carriage Return",
          "\ufb33": "Hebrew Letter Dalet With Dagesh",
          "1": "One",
          "\ud83d\ude00": "Emoji: Grinning Face",
          "\u0080": "Control",
          "\u00f6": "Latin Small Letter O With Diaeresis"
        }
        """;

    // Every character JSON must escape, the ones JCS writes as is although a serializer might escape them,
    // and text outside ASCII, one character of it outside the Basic Multilingual Plane.
    private const string _escaping = """
        { "s": "\u0000\u0001\b\t\n\u000b\f\r\u001f \"\\\/ <&'+> \u007f \u2028 \u20ac \ud83d\ude00" }
        """;

    public static TheoryData<string, string, byte[]> Samples => new()
    {
        {
            "RFC 8785 §3.2.2, with §3.2.4's bytes",
            _primitives,
            Convert.FromHexString(_primitivesUtf8.Replace(" ", string.Empty).ReplaceLineEndings(string.Empty))
        },
        {
            "RFC 8785 §3.2.3, sorted by UTF-16 code units",
            _sorting,
            Encoding.UTF8.GetBytes(
                "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\","
                + "\"\u00f6\":\"Latin Small Letter O With Diaeresis\",\"\u20ac\":\"Euro Sign\","
                + "\"\ud83d\ude00\":\"Emoji: Grinning Face\",\"\ufb33\":\"Hebrew Letter Dalet With Dagesh\"}")
        },
        {
            "escaping",
            _escaping,
            Encoding.UTF8.GetBytes(
                "{\"s\":\"\\u0000\\u0001\\b\\t\\n\\u000b\\f\\r\\u001f \\\"\\\\/ <&'+> "
                + "\u007f \u2028 \u20ac \ud83d\ude00\"}")
        },
        {
            "nested objects sorted at every depth, arrays kept in order",
            """{ "b": [ { "z": 1, "a": [ 3, 1, 2 ] }, [] ], "a": { "d": {}, "c": null } }""",
            Encoding.UTF8.GetBytes("""{"a":{"c":null,"d":{}},"b":[{"a":[3,1,2],"z":1},[]]}""")
        },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Canonical_Form_Matches_The_Rfc_8785_Samples(string label, string json, byte[] expected)
    {
        byte[] canonical = CanonicalJson.ToUtf8Bytes(JsonNode.Parse(json));

        canonical.Should().Equal(expected, label);
    }

    public static TheoryData<string, string> Numbers => new()
    {
        // RFC 8785 Appendix B: every finite sample, by its IEEE 754 bits.
        { "0000000000000000", "0" },
        { "8000000000000000", "0" },
        { "0000000000000001", "5e-324" },
        { "8000000000000001", "-5e-324" },
        { "7fefffffffffffff", "1.7976931348623157e+308" },
        { "ffefffffffffffff", "-1.7976931348623157e+308" },
        { "4340000000000000", "9007199254740992" },
        { "c340000000000000", "-9007199254740992" },
        { "4430000000000000", "295147905179352830000" },
        { "44b52d02c7e14af5", "9.999999999999997e+22" },
        { "44b52d02c7e14af6", "1e+23" },
        { "44b52d02c7e14af7", "1.0000000000000001e+23" },
        { "444b1ae4d6e2ef4e", "999999999999999700000" },
        { "444b1ae4d6e2ef4f", "999999999999999900000" },
        { "444b1ae4d6e2ef50", "1e+21" },
        { "3eb0c6f7a0b5ed8c", "9.999999999999997e-7" },
        { "3eb0c6f7a0b5ed8d", "0.000001" },
        { "41b3de4355555553", "333333333.3333332" },
        { "41b3de4355555554", "333333333.33333325" },
        { "41b3de4355555555", "333333333.3333333" },
        { "41b3de4355555556", "333333333.3333334" },
        { "41b3de4355555557", "333333333.33333343" },
        { "becbf647612f3696", "-0.0000033333333333333333" },
        { "43143ff3c1cb0959", "1424953923781206.2" },

        // Values the library's serializer writes in a form of its own: 1E-05, 1E-07 and 1E+20 (its 1E+21 and
        // -0 are rows above).
        { Bits(1e-5), "0.00001" },
        { Bits(1e-7), "1e-7" },
        { Bits(1e20), "100000000000000000000" },
        { Bits(0.5), "0.5" },
        { Bits(-0.25), "-0.25" },
    };

    [Theory]
    [MemberData(nameof(Numbers))]
    public void Canonical_Number_Matches_Rfc_8785_Appendix_B(string bits, string expected)
    {
        double value = BitConverter.Int64BitsToDouble(Convert.ToInt64(bits, 16));

        string canonical = Encoding.UTF8.GetString(CanonicalJson.ToUtf8Bytes(JsonValue.Create(value)));

        canonical.Should().Be(expected, "the bits are {0}", bits);
    }

    // The layout moves digits and inserts zeros, so it is checked on numbers no table lists: random bit
    // patterns, which mostly land far from 1, and short decimals near it, where the fixed forms switch.
    [Fact]
    public void Canonical_Number_Reads_Back_As_The_Same_Double()
    {
        Random random = new(8785);
        List<double> values = [];
        while (values.Count < 50_000)
        {
            double value = BitConverter.Int64BitsToDouble(random.NextInt64(long.MinValue, long.MaxValue));
            if (double.IsFinite(value))
            {
                values.Add(value);
            }
        }

        for (int i = 0; i < 50_000; i++)
        {
            double digits = Math.Round(random.NextDouble(), random.Next(1, 16));
            values.Add(digits * Math.Pow(10, random.Next(-12, 26)) * (random.Next(2) == 0 ? -1 : 1));
        }

        List<string> misread = [];
        foreach (double value in values)
        {
            string canonical = Encoding.UTF8.GetString(CanonicalJson.ToUtf8Bytes(JsonValue.Create(value)));
            if (double.Parse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture) != value)
            {
                misread.Add($"{Bits(value)} written {canonical}");
            }
        }

        misread.Should().BeEmpty();
    }

    public static TheoryData<string> Unrepresentable => new()
    {
        "a lone high surrogate",
        "a lone low surrogate",
        "a lone surrogate in a member name",
        "a high surrogate at the end of a string",
        "NaN",
        "Infinity",
        "-Infinity",
    };

    [Theory]
    [MemberData(nameof(Unrepresentable))]
    public void Values_Rfc_8785_Cannot_Hold_Are_Rejected(string label)
    {
        JsonNode node = label switch
        {
            "a lone high surrogate" => new JsonObject { ["s"] = "a\ud83db" },
            "a lone low surrogate" => new JsonObject { ["s"] = "a\ude00b" },
            "a lone surrogate in a member name" => new JsonObject { ["a\ud83d"] = 1 },
            "a high surrogate at the end of a string" => new JsonArray("ok", "a\ud83d"),
            "NaN" => new JsonArray(JsonValue.Create(BitConverter.Int64BitsToDouble(0x7fffffffffffffff))),
            "Infinity" => new JsonObject { ["n"] = BitConverter.Int64BitsToDouble(0x7ff0000000000000) },
            "-Infinity" => JsonValue.Create(double.NegativeInfinity),
            _ => throw new ArgumentOutOfRangeException(nameof(label), label, null),
        };

        Action canonicalize = () => CanonicalJson.ToUtf8Bytes(node);

        canonicalize.Should().Throw<ArgumentException>(label);
    }

    private static string Bits(double value) =>
        BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);
}
