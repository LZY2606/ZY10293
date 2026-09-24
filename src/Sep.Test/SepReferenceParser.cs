using System;
using System.Collections.Generic;
using System.Text;

namespace nietras.SeparatedValues.Test;

// Simple, independent, char-by-char reference parser for differential
// testing. Implements only the well-defined subset used by the differential
// test suite: a single separator char, '"' quotes toggling quoting state
// anywhere in a column, CR/LF/CRLF line endings, escaped quotes ("") and
// optional space trimming and unescaping. Does NOT use any of the Sep mask
// helpers or parsers so it can serve as an independent oracle. Expected
// errors (column count mismatches) are derived directly from the repository
// policy (first row defines expected column count), not from any other CSV
// library.
static class SepReferenceParser
{
    internal const char Quote = SepDefaults.Quote;
    internal const char CarriageReturn = SepDefaults.CarriageReturn;
    internal const char LineFeed = SepDefaults.LineFeed;
    internal const char Space = SepDefaults.Space;

    internal readonly record struct Col(string Raw, string Value);
    internal readonly record struct Row(string Raw, Col[] Cols, int LineNumberFrom, int LineNumberTo);
    internal readonly record struct Error(int RowIndex, int LineNumberFrom, int LineNumberTo,
        int ColCount, int ExpectedColCount, string Row, string FirstRow);

    internal readonly record struct Result(Row[] Rows, Error? Error)
    {
        internal string? ExpectedErrorMessage => Error is { } e
            ? $"Found {e.ColCount} column(s) on row {e.RowIndex}/lines [{e.LineNumberFrom}..{e.LineNumberTo}]:'{e.Row}'" +
              Environment.NewLine +
              $"Expected {e.ExpectedColCount} column(s) matching header/first row '{e.FirstRow}'"
            : null;
    }

    internal static Result Parse(string text, char separator, bool unescape, SepTrim trim)
    {
        var rows = new List<Row>();
        var cols = new List<Col>();
        var lineNumber = 1;
        var rowLineFrom = 1;
        var rowStart = 0;
        var colStart = 0;
        var quoteCount = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if ((quoteCount & 1) != 0)
            {
                // Inside quotes
                if (c == CarriageReturn)
                {
                    // If next char is a line feed, don't count it in line
                    // number, that happens on the line feed when handled next.
                    var oneCharAhead = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (oneCharAhead != LineFeed) { ++lineNumber; }
                    ++i;
                    continue;
                }
                if (c == LineFeed) { ++lineNumber; ++i; continue; }
                if (c != Quote) { ++i; continue; }
            }
            if (c == separator)
            {
                EndCol(text, colStart, i, quoteCount, unescape, trim, cols);
                colStart = i + 1;
                quoteCount = 0;
                ++i;
                continue;
            }
            if (c == CarriageReturn || c == LineFeed)
            {
                EndCol(text, colStart, i, quoteCount, unescape, trim, cols);
                quoteCount = 0;
                var rowEnd = i;
                ++lineNumber;
                if (c == CarriageReturn && i + 1 < text.Length && text[i + 1] == LineFeed) { ++i; }
                ++i;
                rows.Add(new(text[rowStart..rowEnd], cols.ToArray(), rowLineFrom, lineNumber));
                cols.Clear();
                rowLineFrom = lineNumber;
                rowStart = i;
                colStart = i;
                continue;
            }
            if (c == Quote) { ++quoteCount; ++i; continue; }
            ++i;
        }
        // Final row if any chars or cols pending. The reader counts the
        // final row without line ending as a line too (increments line
        // number at end of file).
        if (colStart < text.Length || cols.Count > 0)
        {
            EndCol(text, colStart, text.Length, quoteCount, unescape, trim, cols);
            ++lineNumber;
            rows.Add(new(text[rowStart..], cols.ToArray(), rowLineFrom, lineNumber));
        }
        // The reader parses the first row once during initialization and then
        // "moves back" without resetting the current line number, so the
        // first row is reported with LineNumberFrom equal to its LineNumberTo.
        if (rows.Count > 0)
        {
            rows[0] = rows[0] with { LineNumberFrom = rows[0].LineNumberTo };
        }

        // Repository policy: column count of first row defines expected count
        Error? error = null;
        if (rows.Count > 0)
        {
            var expectedColCount = rows[0].Cols.Length;
            for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                if (row.Cols.Length != expectedColCount)
                {
                    // With no header the reader reports an empty "first row"
                    error = new(rowIndex, row.LineNumberFrom, row.LineNumberTo,
                        row.Cols.Length, expectedColCount, row.Raw, string.Empty);
                    rows.RemoveRange(rowIndex, rows.Count - rowIndex);
                    break;
                }
            }
        }
        return new(rows.ToArray(), error);
    }

    static void EndCol(string text, int start, int end, int quoteCount,
        bool unescape, SepTrim trim, List<Col> cols)
    {
        var raw = text[start..end];
        var value = ComputeValue(raw, quoteCount, unescape, trim);
        cols.Add(new(raw, value));
    }

    // Mirrors SepReaderState.GetColSpan/GetColSpanTrimmed and
    // SepUnescape.UnescapeInPlace/TrimUnescapeInPlace semantics with simple
    // per-char loops.
    static string ComputeValue(string raw, int quoteCount, bool unescape, SepTrim trim)
    {
        var trimOuter = (trim & SepTrim.Outer) != 0;
        var trimAfterUnescape = unescape && (trim & SepTrim.AfterUnescape) != 0;
        if (!unescape)
        {
            return trimOuter ? TrimSpaces(raw) : raw;
        }
        if (trimOuter || trimAfterUnescape)
        {
            var span = raw.AsSpan();
            if (trimOuter) { span = TrimSpaces(span); }
            if (quoteCount == 2 && span.Length >= 2 &&
                span[0] == Quote && span[^1] == Quote)
            {
                span = span[1..^1];
            }
            else if (span.Length > 0 && span[0] == Quote)
            {
                // TrimUnescapeInPlace returns directly, no trim after
                return TrimUnescape(span);
            }
            if (trimAfterUnescape) { span = TrimSpaces(span); }
            return span.ToString();
        }
        else
        {
            if (quoteCount == 0 || raw.Length == 0 || raw[0] != Quote) { return raw; }
            if (quoteCount == 2 && raw[^1] == Quote) { return raw[1..^1]; }
            return Unescape(raw);
        }
    }

    // Mirrors SepUnescape.UnescapeInPlace
    static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        var quoteCount = 1; // Start just past first quote
        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (c == Quote) { ++quoteCount; }
            var keep = (quoteCount & 1) == 1 || c != Quote;
            if (keep) { sb.Append(c); }
        }
        return sb.ToString();
    }

    // Mirrors SepUnescape.TrimUnescapeInPlace
    static string TrimUnescape(ReadOnlySpan<char> s)
    {
        var sb = new StringBuilder(s.Length);
        var quoteCount = 1; // Start just past first quote
        var i = 1;
        for (; i < s.Length && s[i] == Space; i++) { }
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c == Quote) { ++quoteCount; }
            var keep = (quoteCount & 1) == 1 || c != Quote;
            if (keep) { sb.Append(c); }
        }
        while (sb.Length > 0 && sb[^1] == Space) { --sb.Length; }
        return sb.ToString();
    }

    static string TrimSpaces(string s) => TrimSpaces(s.AsSpan()).ToString();

    static ReadOnlySpan<char> TrimSpaces(ReadOnlySpan<char> s)
    {
        while (s.Length > 0 && s[0] == Space) { s = s[1..]; }
        while (s.Length > 0 && s[^1] == Space) { s = s[..^1]; }
        return s;
    }
}
