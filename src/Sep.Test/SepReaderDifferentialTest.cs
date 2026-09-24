using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace nietras.SeparatedValues.Test;

// Differential test suite comparing field raw/value, row boundaries (line
// numbers) and error offsets for the exact same bytes across:
//  * all available parser backends (real intrinsics when supported)
//  * software mask adapters for 64/128/256/512-bit vector widths
//  * different chunk sizes (initial buffer lengths forcing buffer refills)
//  * a simple independent char-by-char reference parser (SepReferenceParser)
// Generation uses a fixed seed and both random bounded rows and targeted
// cases placing separator/quote/CR/LF at and around every vector lane and
// chunk boundary.
[TestClass]
public class SepReaderDifferentialTest
{
    internal const int Seed = 23768213;

    static readonly char[] s_separators = [';', ',', '|', '\t', '~', '^', ':'];
    static readonly string[] s_normalChars = ["a", "b", "1", "_", ".", "#", "¼", "ˉ"];
    static readonly string[] s_unicodeChars = ["é", "界", "🙂", "¼", "ˉ", "Å"];
    // Lane counts in chars for 64/128/256/512-bit vector paths
    internal static readonly int[] s_laneCounts = [8, 16, 32, 64];
    internal static readonly int[] s_chunkSizes = [64, 256, 8192];

    internal static IReadOnlyList<(string Name, Func<SepParserOptions, ISepParser> Create)> Backends { get; } = CreateBackends();

    static IReadOnlyList<(string Name, Func<SepParserOptions, ISepParser> Create)> CreateBackends()
    {
        var backends = new List<(string, Func<SepParserOptions, ISepParser>)>();
        foreach (var (name, create) in SepParserFactory.AvailableFactories)
        {
            backends.Add((name, create));
        }
        foreach (var laneCount in s_laneCounts)
        {
            var lanes = laneCount;
            backends.Add(($"SoftwareMask{lanes * 16}bit", o => new SepParserSoftwareMaskAdapter(o, lanes)));
        }
        return backends;
    }

    internal readonly record struct Case(string Name, string Text, char Separator, bool Unescape, SepTrim Trim);

    [TestMethod]
    public void SepReaderDifferentialTest_Random()
    {
        foreach (var c in RandomCases(Seed, 128))
        {
            AssertCaseAllBackends(c);
        }
    }

    [TestMethod]
    public void SepReaderDifferentialTest_LaneBoundaries()
    {
        // Sweep special patterns over every position around vector lane ends
        // for 64/128/256/512-bit widths (8/16/32/64 chars)
        foreach (var c in BoundarySweepCases(positionMax: 2 * 64 + 1))
        {
            AssertCaseAllBackends(c);
        }
    }

    [TestMethod]
    public void SepReaderDifferentialTest_ChunkBoundaries()
    {
        // Place special patterns right before/at/after positions where buffer
        // refills occur for the chunk sizes used e.g. "\r\n" split with '\r'
        // as last char in one chunk and '\n' first in the next
        foreach (var c in ChunkBoundaryCases())
        {
            AssertCaseAllBackends(c);
        }
    }

    internal static IEnumerable<Case> ChunkBoundaryCases()
    {
        foreach (var boundary in new[] { 64, 128, 256, 512 })
        {
            foreach (var offset in new[] { -2, -1, 0, 1 })
            {
                var position = boundary + offset;
                foreach (var (patternName, pattern) in s_boundaryPatterns)
                {
                    const char separator = ';';
                    var sb = new StringBuilder();
                    sb.Append('a', position);
                    sb.Append(pattern.Replace("{S}", separator.ToString()));
                    sb.Append($"y{separator}z\nnext{separator}row\n");
                    yield return new Case($"Chunk{boundary}:{patternName}@{position}",
                        sb.ToString(), separator, position % 2 == 0, (SepTrim)(position % 4));
                }
            }
        }
    }

    [TestMethod]
    public void SepReaderDifferentialTest_Errors()
    {
        foreach (var c in ErrorCases())
        {
            AssertCaseAllBackends(c);
        }
    }

    internal static void AssertCaseAllBackends(Case c)
    {
        foreach (var (backendName, create) in Backends)
        {
            foreach (var chunkSize in s_chunkSizes)
            {
                var mismatch = FindMismatch(c, backendName, create, chunkSize);
                Assert.IsNull(mismatch, mismatch);
            }
        }
    }

    // Returns null if reader output matches the reference parser, otherwise a
    // description of the first mismatch found.
    internal static string? FindMismatch(Case c, string backendName,
        Func<SepParserOptions, ISepParser> create, int chunkSize)
    {
        // Compare both with the case options (field values) and with raw
        // options (field raws) to cover raw and unescaped/trimmed access
        var configs = new[] { (c.Unescape, c.Trim), (false, SepTrim.None) };
        for (var configIndex = 0; configIndex < configs.Length; configIndex++)
        {
            var (unescape, trim) = configs[configIndex];
            if (configIndex == 1 && unescape == c.Unescape && trim == c.Trim) { continue; }
            var expected = SepReferenceParser.Parse(c.Text, c.Separator, unescape, trim);
            var actual = RunReader(c, create, chunkSize, unescape, trim);
            var context = $"Case '{c.Name}' [{backendName}] chunk {chunkSize} unescape {unescape} trim {trim} sep '{c.Separator}'" +
                          $"{Environment.NewLine}text: '{Escape(c.Text)}'";
            var mismatch = Compare(expected, actual, context);
            if (mismatch != null) { return mismatch; }
        }
        return null;
    }

    static string? Compare(SepReferenceParser.Result expected, Actual actual, string context)
    {
        var expectedError = expected.ExpectedErrorMessage is { } m
            ? typeof(InvalidDataException).FullName + "|" + m
            : null;
        if (!Equals(expectedError, actual.Error))
        {
            return $"{context}: error mismatch{Environment.NewLine}expected: {expectedError ?? "<none>"}{Environment.NewLine}actual:   {actual.Error ?? "<none>"}";
        }
        if (expected.Rows.Length != actual.Rows.Length)
        {
            return $"{context}: row count {expected.Rows.Length} != {actual.Rows.Length}";
        }
        for (var rowIndex = 0; rowIndex < expected.Rows.Length; rowIndex++)
        {
            var e = expected.Rows[rowIndex];
            var a = actual.Rows[rowIndex];
            if (e.LineNumberFrom != a.LineNumberFrom || e.LineNumberTo != a.LineNumberTo)
            {
                return $"{context}: row {rowIndex} lines [{e.LineNumberFrom}..{e.LineNumberTo}] != [{a.LineNumberFrom}..{a.LineNumberTo}]";
            }
            if (e.Raw != a.Raw)
            {
                return $"{context}: row {rowIndex} raw '{e.Raw}' != '{a.Raw}'";
            }
            if (e.Cols.Length != a.Cols.Length)
            {
                return $"{context}: row {rowIndex} col count {e.Cols.Length} != {a.Cols.Length}";
            }
            for (var colIndex = 0; colIndex < e.Cols.Length; colIndex++)
            {
                if (e.Cols[colIndex].Value != a.Cols[colIndex])
                {
                    return $"{context}: row {rowIndex} col {colIndex} value '{e.Cols[colIndex].Value}' != '{a.Cols[colIndex]}'";
                }
            }
        }
        return null;
    }

    internal static string Escape(string text) => text
        .Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n")
        .Replace("\t", "\\t").Replace("\"", "\\\"");

    readonly record struct ActualRow(string Raw, string[] Cols, int LineNumberFrom, int LineNumberTo);
    readonly record struct Actual(ActualRow[] Rows, string? Error);

    static Actual RunReader(Case c, Func<SepParserOptions, ISepParser> create,
        int chunkSize, bool unescape, SepTrim trim)
    {
        var options = Sep.New(c.Separator).Reader(o => o with
        {
            HasHeader = false,
            Unescape = unescape,
            Trim = trim,
            InitialBufferLength = chunkSize,
            CreateParser = create,
        });
        using var reader = options.FromText(c.Text);
        var rows = new List<ActualRow>();
        string? error = null;
        try
        {
            while (reader.MoveNext())
            {
                var row = reader.Current;
                // Capture row span before any col access as unescape/trim may
                // modify the row span in-place
                var rowRaw = row.Span.ToString();
                var cols = new string[row.ColCount];
                for (var colIndex = 0; colIndex < cols.Length; colIndex++)
                {
                    cols[colIndex] = row[colIndex].ToString();
                }
                rows.Add(new(rowRaw, cols, row.LineNumberFrom, row.LineNumberToExcl));
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().FullName + "|" + ex.Message;
        }
        return new(rows.ToArray(), error);
    }

    internal static IEnumerable<Case> RandomCases(int seed, int count)
    {
        var random = new Random(seed);
        for (var n = 0; n < count; n++)
        {
            var separator = s_separators[random.Next(s_separators.Length)];
            var sb = new StringBuilder();
            var rowCount = random.Next(1, 24);
            var sameColCount = random.Next(4) != 0;
            var colCount = random.Next(1, 8);
            for (var row = 0; row < rowCount; row++)
            {
                var cols = sameColCount ? colCount : random.Next(1, 8);
                for (var col = 0; col < cols; col++)
                {
                    AppendRandomCol(sb, random, separator);
                    if (col < cols - 1) { sb.Append(separator); }
                }
                sb.Append(random.Next(4) switch { 0 => "\r\n", 1 => "\r", _ => "\n" });
            }
            var text = sb.ToString();
            // Sometimes remove final line ending
            if (random.Next(3) == 0 && text.Length > 0)
            {
                text = text.TrimEnd('\n').TrimEnd('\r');
            }
            var unescape = random.Next(2) == 0;
            var trim = (SepTrim)random.Next(4);
            yield return new Case($"Random{n}", text, separator, unescape, trim);
        }
    }

    static void AppendRandomCol(StringBuilder sb, Random random, char separator)
    {
        if (random.Next(4) == 0) { sb.Append(' ', random.Next(1, 3)); }
        var quoted = random.Next(3) == 0;
        if (quoted) { sb.Append('"'); }
        var length = random.Next(0, 20);
        for (var i = 0; i < length; i++)
        {
            var r = random.Next(100);
            if (r < 55) { sb.Append(s_normalChars[random.Next(s_normalChars.Length)]); }
            else if (r < 65) { sb.Append(' '); }
            else if (r < 75) { sb.Append('"'); }
            else if (r < 85) { sb.Append(separator); }
            else if (r < 90) { sb.Append("\r\n"); }
            else if (r < 95) { sb.Append('\n'); }
            else { sb.Append(s_unicodeChars[random.Next(s_unicodeChars.Length)]); }
        }
        if (quoted) { sb.Append('"'); }
        if (random.Next(4) == 0) { sb.Append(' ', random.Next(1, 3)); }
    }

    // Special patterns placed at every position of a sweep. `{S}` is replaced
    // by the case separator. The tail ensures following rows exist so any
    // state error (quoting, line numbers) propagates to observable output.
    internal static readonly (string Name, string Pattern)[] s_boundaryPatterns =
    [
        ("Separator", "{S}x"),
        ("Quote", "\"x\"y"),
        ("QuotePair", "\"\""),
        ("QuotedSeparator", "\"{S}\""),
        ("QuotedLineFeed", "\"\n\""),
        ("QuotedCarriageReturnLineFeed", "\"\r\n\""),
        ("EscapedQuote", "\"a\"\"b\""),
        ("CarriageReturn", "\r"),
        ("LineFeed", "\n"),
        ("CarriageReturnLineFeed", "\r\n"),
    ];

    internal static IEnumerable<Case> BoundarySweepCases(int positionMax)
    {
        foreach (var separator in new[] { ';', '|' })
        {
            for (var position = 0; position <= positionMax; position++)
            {
                foreach (var (patternName, pattern) in s_boundaryPatterns)
                {
                    var sb = new StringBuilder();
                    sb.Append('a', position);
                    sb.Append(pattern.Replace("{S}", separator.ToString()));
                    sb.Append($"y{separator}z\nnext{separator}row\n");
                    var text = sb.ToString();
                    // Cycle options deterministically based on position
                    var unescape = position % 2 == 0;
                    var trim = (SepTrim)(position % 4);
                    yield return new Case($"{patternName}@{position}{separator}", text, separator, unescape, trim);
                }
            }
        }
    }

    internal static IEnumerable<Case> ErrorCases()
    {
        var cases = new (string Text, char Separator)[]
        {
            ("a;b\nc\n", ';'),
            ("a\nb;c\nd\n", ';'),
            ("a;\"x\ny\"\nb\n", ';'),
            ("a;\"x\r\ny\"\r\nb\r\n", ';'),
            ("a;b\r\nc;d\ne\n", ';'),
            ("a;b;c\nd\ne;f;g\n", ';'),
            ("x\ny\nz;w\n", ';'),
            ("a|b|c\nd|e\n", '|'),
            ("a\tb\nc\n", '\t'),
            ("a;\"\"\"\"\nb;c\n", ';'),
            ("a;\"u\"\"v\"\nb\n", ';'),
            ("one;two;three\n1;2\n1;2;3\n1\n", ';'),
        };
        foreach (var (text, separator) in cases)
        {
            foreach (var unescape in new[] { false, true })
            {
                foreach (var trim in new[] { SepTrim.None, SepTrim.All })
                {
                    yield return new Case($"Error:{text.Length}:{separator}:{unescape}:{trim}",
                        text, separator, unescape, trim);
                }
            }
        }
    }
}
