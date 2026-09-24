using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Runtime.CompilerServices.Unsafe;
using static nietras.SeparatedValues.SepDefaults;
using static nietras.SeparatedValues.SepParseMask;

namespace nietras.SeparatedValues.Test;

// Software mask adapter parser for differential testing. Computes
// separator/line ending/quote masks with a simple per-char loop for a
// configurable lane/vector width (8/16/32/64 chars corresponding to
// 64/128/256/512-bit paths with 16-bit chars), then consumes those masks via
// the exact same core `SepParseMask` helpers as the hardware accelerated
// parsers. This allows exercising wide-vector mask semantics on platforms
// where the corresponding intrinsics are not available, and allows injecting
// mutations to prove the differential test suite catches them.
public sealed class SepParserSoftwareMaskAdapter : ISepParser
{
    public enum Mutation
    {
        None = 0,
        // Drops the most significant bit of every computed mask i.e. a
        // special char in the last lane of a vector is missed.
        DropHighestMaskBit = 1,
        // Does not skip the line feed of a "\r\n" line ending, so the line
        // feed is parsed as another row.
        IgnoreLineEndingOffset = 2,
        // Counts a quote in the last lane of a vector twice.
        DoubleCountQuoteAtLastLane = 3,
    }

    readonly char _separator;
    readonly char _quotesOrSeparatorIfDisabled;
    readonly int _laneCount;
    readonly Mutation _mutation;
    nuint _quoteCount = 0;

    internal SepParserSoftwareMaskAdapter(SepParserOptions options, int laneCount,
        Mutation mutation = Mutation.None)
    {
        if (laneCount <= 0 || laneCount > SizeOf<nuint>() * 8)
        { throw new ArgumentOutOfRangeException(nameof(laneCount)); }
        _separator = options.Separator;
        _quotesOrSeparatorIfDisabled = options.QuotesOrSeparatorIfDisabled;
        _laneCount = laneCount;
        _mutation = mutation;
    }

    internal int LaneCount => _laneCount;

    public int PaddingLength => _laneCount;
    public int QuoteCount => (int)_quoteCount;

    public void ParseColEnds(SepReaderState s) => Parse<int, SepColEndMethods>(s);
    public void ParseColInfos(SepReaderState s) => Parse<SepColInfo, SepColInfoMethods>(s);

    void Parse<TColInfo, TColInfoMethods>(SepReaderState s)
        where TColInfo : unmanaged
        where TColInfoMethods : ISepColInfoMethods<TColInfo>
    {
        // Unpack instance fields
        var separator = _separator;
        var quotesOrSeparatorIfDisabled = _quotesOrSeparatorIfDisabled;
        var quoteCount = _quoteCount;
        var laneCount = _laneCount;
        var mutation = _mutation;

        // Unpack state fields
        var chars = s._chars;
        var charsIndex = s._charsParseStart;
        var charsEnd = s._charsDataEnd;
        var lineNumber = s._parsingLineNumber;
        var colInfos = s._colEndsOrColInfos;

        var colInfosLength = TColInfoMethods.IntsLengthToColInfosLength(colInfos.Length);

        chars.CheckPaddingAndIsZero(charsEnd, PaddingLength);
        SepArrayExtensions.CheckPadding(colInfosLength, s._parsingRowColCount + s._parsingRowColEndsOrInfosStartIndex, PaddingLength);
        SepAssert.Assert(charsIndex <= charsEnd);
        SepAssert.Assert(charsEnd <= (chars.Length - PaddingLength));

        ref var charsOriginRef = ref MemoryMarshal.GetArrayDataReference(chars);

        ref var colInfosRefOrigin = ref As<int, TColInfo>(ref MemoryMarshal.GetArrayDataReference(colInfos));
        ref var colInfosRef = ref Add(ref colInfosRefOrigin, s._parsingRowColEndsOrInfosStartIndex);
        ref var colInfosRefCurrent = ref Add(ref colInfosRefOrigin, s._parsingRowColCount + s._parsingRowColEndsOrInfosStartIndex);
        ref var colInfosRefEnd = ref Add(ref colInfosRefOrigin, colInfosLength);
        var colInfosStopLength = colInfosLength - laneCount - SepReaderState.ColEndsOrInfosExtraEndCount;
        ref var colInfosRefStop = ref Add(ref colInfosRefOrigin, colInfosStopLength);

        charsIndex -= laneCount;
    LOOPSTEP:
        charsIndex += laneCount;
    LOOPNOSTEP:
        if (charsIndex < charsEnd &&
            !IsAddressLessThan(ref colInfosRefStop, ref colInfosRefCurrent))
        {
            ref var charsRef = ref Add(ref charsOriginRef, (uint)charsIndex);
            // Software mask computation over a vector of lane count chars
            var separatorsMask = (nuint)0;
            var lineEndingsMask = (nuint)0;
            var quotesMask = (nuint)0;
            for (var i = 0; i < laneCount; i++)
            {
                var c = Add(ref charsRef, i);
                var bit = (nuint)1 << i;
                if (c == separator) { separatorsMask |= bit; }
                if (c == CarriageReturn || c == LineFeed) { lineEndingsMask |= bit; }
                if (c == quotesOrSeparatorIfDisabled) { quotesMask |= bit; }
            }
            if (mutation == Mutation.DropHighestMaskBit)
            {
                var highestBit = (nuint)1 << (laneCount - 1);
                separatorsMask &= ~highestBit;
                lineEndingsMask &= ~highestBit;
                quotesMask &= ~highestBit;
            }

            var separatorsLineEndingsMask = separatorsMask | lineEndingsMask;
            var specialCharMask = separatorsLineEndingsMask | quotesMask;
            // Optimize for the case of no special character
            if (specialCharMask != 0)
            {
                // Add quote count to mask as hack to skip if quoting
                var testMask = specialCharMask + quoteCount;
                // Optimize for case of only separators i.e. no endings or quotes
                var onlySeparators = separatorsMask == testMask;
                // Mutation always uses the full path to inject line ending bug
                var useFastPaths = mutation != Mutation.IgnoreLineEndingOffset;
                if (useFastPaths && onlySeparators)
                {
                    colInfosRefCurrent = ref ParseSeparatorsMask<TColInfo, TColInfoMethods>(
                        separatorsMask, charsIndex, ref colInfosRefCurrent);
                }
                else if (useFastPaths && separatorsLineEndingsMask == testMask)
                {
                    colInfosRefCurrent = ref ParseSeparatorsLineEndingsMasks<TColInfo, TColInfoMethods>(
                        separatorsMask, separatorsLineEndingsMask,
                        ref charsRef, ref charsIndex, separator,
                        ref colInfosRefCurrent, ref lineNumber);
                    goto NEWROW;
                }
                else
                {
                    var rowLineEndingOffset = 0;
                    colInfosRefCurrent = ref ParseAnyCharsMask<TColInfo, TColInfoMethods>(specialCharMask,
                        separator, ref charsRef, charsIndex,
                        ref rowLineEndingOffset, ref quoteCount,
                        ref colInfosRefCurrent, ref lineNumber);
                    // Used both to indicate row ended and if need to step +2 due to '\r\n'
                    if (rowLineEndingOffset != 0)
                    {
                        // Must be a col end and last is then dataIndex
                        charsIndex = TColInfoMethods.GetColEnd(colInfosRefCurrent) +
                            (mutation == Mutation.IgnoreLineEndingOffset ? 1 : rowLineEndingOffset);
                        goto NEWROW;
                    }
                }
            }
            if (mutation == Mutation.DoubleCountQuoteAtLastLane &&
                quotesOrSeparatorIfDisabled != separator &&
                Add(ref charsRef, laneCount - 1) == quotesOrSeparatorIfDisabled)
            {
                // Mutation: quote at vector end counted again
                ++quoteCount;
            }
            goto LOOPSTEP;
        NEWROW:
            var colCount = TColInfoMethods.CountOffset(ref colInfosRef, ref colInfosRefCurrent);
            // Add new parsed row
            ref var parsedRowRef = ref MemoryMarshal.GetArrayDataReference(s._parsedRows);
            Add(ref parsedRowRef, s._parsedRowsCount) = new(lineNumber, colCount);
            ++s._parsedRowsCount;
            // Next row start (one before)
            colInfosRefCurrent = ref Add(ref colInfosRefCurrent, 1);
            SepAssert.Assert(IsAddressLessThan(ref colInfosRefCurrent, ref colInfosRefEnd));
            colInfosRefCurrent = TColInfoMethods.Create(charsIndex - 1, 0);
            // Update for next row
            colInfosRef = ref colInfosRefCurrent;
            s._parsingRowColEndsOrInfosStartIndex += colCount + 1;
            s._parsingRowCharsStartIndex = charsIndex;
            // Space for more rows?
            if (s._parsedRowsCount < s._parsedRows.Length)
            {
                goto LOOPNOSTEP;
            }
        }
        // Update instance state from enregistered
        _quoteCount = quoteCount;
        s._parsingRowColCount = TColInfoMethods.CountOffset(ref colInfosRef, ref colInfosRefCurrent);
        s._parsingLineNumber = lineNumber;
        // Step is lane count so may go past end, ensure limited
        s._charsParseStart = Math.Min(charsEnd, charsIndex);
    }
}
