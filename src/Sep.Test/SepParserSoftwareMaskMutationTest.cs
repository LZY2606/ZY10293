using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static nietras.SeparatedValues.Test.SepParserSoftwareMaskAdapter;

namespace nietras.SeparatedValues.Test;

// Proves the differential test suite catches representative mask/vector bugs
// by injecting mutations into the software mask adapter and verifying the
// differential comparison fails. Also verifies the unmutated software mask
// adapters pass on the same corpus.
[TestClass]
public class SepParserSoftwareMaskMutationTest
{
    static IEnumerable<SepReaderDifferentialTest.Case> MutationCorpus()
    {
        // Position sweep placing specials at/around every lane boundary
        foreach (var c in SepReaderDifferentialTest.BoundarySweepCases(positionMax: 2 * 64 + 1))
        {
            yield return c;
        }
        // Bounded random rows
        foreach (var c in SepReaderDifferentialTest.RandomCases(SepReaderDifferentialTest.Seed + 1, 32))
        {
            yield return c;
        }
    }

    [TestMethod]
    public void SepParserSoftwareMaskMutationTest_Unmutated_MatchesReference()
    {
        // Sanity: unmutated adapters must have zero mismatches on the corpus
        foreach (var c in MutationCorpus())
        foreach (var laneCount in SepReaderDifferentialTest.s_laneCounts)
        {
            var mismatch = FindMismatch(c, laneCount, Mutation.None);
            Assert.IsNull(mismatch, mismatch);
        }
    }

    [TestMethod]
    [DataRow(Mutation.DropHighestMaskBit)]
    [DataRow(Mutation.IgnoreLineEndingOffset)]
    [DataRow(Mutation.DoubleCountQuoteAtLastLane)]
    public void SepParserSoftwareMaskMutationTest_Mutation_IsDetected(Mutation mutation)
    {
        var mismatchCount = 0;
        var firstMismatch = (string?)null;
        foreach (var c in MutationCorpus())
        foreach (var laneCount in SepReaderDifferentialTest.s_laneCounts)
        {
            var mismatch = FindMismatch(c, laneCount, mutation);
            if (mismatch != null)
            {
                ++mismatchCount;
                firstMismatch ??= mismatch;
            }
        }
        Trace.WriteLine($"Mutation {mutation}: {mismatchCount} mismatches, first: {firstMismatch}");
        Assert.IsGreaterThan(0, mismatchCount,
            $"Mutation {mutation} was not detected by the differential suite");
    }

    static string? FindMismatch(SepReaderDifferentialTest.Case c, int laneCount, Mutation mutation)
    {
        var name = $"SoftwareMask{laneCount * 16}bit+{mutation}";
        return SepReaderDifferentialTest.FindMismatch(c, name,
            o => new SepParserSoftwareMaskAdapter(o, laneCount, mutation),
            chunkSize: 64);
    }
}
