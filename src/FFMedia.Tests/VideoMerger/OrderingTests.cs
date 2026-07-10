using System;
using System.Collections.Generic;
using System.Linq;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class OrderingTests
{
    private sealed record Clip(string Name, int? Locked);

    private static IReadOnlyList<Clip> Clips(params (string Name, int? Locked)[] items)
        => items.Select(i => new Clip(i.Name, i.Locked)).ToList();

    [Fact]
    public void Shuffle_KeepsEveryItemExactlyOnce()
    {
        var input = Clips(("a", null), ("b", null), ("c", null), ("d", null), ("e", null));

        var result = Ordering.Shuffle(input, c => c.Locked, seed: 1234);

        Assert.Equal(input.Count, result.Count);
        Assert.Equal(input.OrderBy(c => c.Name), result.OrderBy(c => c.Name));
    }

    [Fact]
    public void Shuffle_HonorsLockedIndices()
    {
        var input = Clips(("a", null), ("b", 0), ("c", null), ("d", 3), ("e", null));

        var result = Ordering.Shuffle(input, c => c.Locked, seed: 99);

        Assert.Equal("b", result[0].Name);
        Assert.Equal("d", result[3].Name);
    }

    [Fact]
    public void Shuffle_IsDeterministicForASeed()
    {
        var input = Clips(("a", null), ("b", null), ("c", null), ("d", null));

        var first = Ordering.Shuffle(input, c => c.Locked, seed: 7);
        var second = Ordering.Shuffle(input, c => c.Locked, seed: 7);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Shuffle_SameSeedTwice_ProducesIdenticalPermutationEvenWithLocks()
    {
        var input = Clips(("a", null), ("b", 1), ("c", null), ("d", null), ("e", 4));

        var first = Ordering.Shuffle(input, c => c.Locked, seed: 42);
        var second = Ordering.Shuffle(input, c => c.Locked, seed: 42);

        Assert.Equal(first.Select(c => c.Name), second.Select(c => c.Name));
    }

    [Fact]
    public void Shuffle_DiffersAcrossSeeds()
    {
        var input = Clips(("a", null), ("b", null), ("c", null), ("d", null), ("e", null), ("f", null));

        var first = Ordering.Shuffle(input, c => c.Locked, seed: 1);
        var second = Ordering.Shuffle(input, c => c.Locked, seed: 2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Shuffle_AllLocked_ReturnsExactOrder()
    {
        var input = Clips(("a", 2), ("b", 0), ("c", 1));

        var result = Ordering.Shuffle(input, c => c.Locked, seed: 5);

        Assert.Equal(["b", "c", "a"], result.Select(c => c.Name));
    }

    [Fact]
    public void Shuffle_AllLockedToDistinctIndices_IsNoOpRegardlessOfSeed()
    {
        var input = Clips(("a", 0), ("b", 1), ("c", 2), ("d", 3));

        for (var seed = 0; seed < 20; seed++)
        {
            var result = Ordering.Shuffle(input, c => c.Locked, seed);
            Assert.Equal(["a", "b", "c", "d"], result.Select(c => c.Name));
        }
    }

    [Fact]
    public void Shuffle_NoItemsLocked_IsPlainShuffle()
    {
        var input = Clips(("a", null), ("b", null), ("c", null));

        var result = Ordering.Shuffle(input, c => c.Locked, seed: 11);

        Assert.Equal(input.Count, result.Count);
        Assert.Equal(input.OrderBy(c => c.Name), result.OrderBy(c => c.Name));
    }

    [Fact]
    public void Shuffle_SingleItem_IsIdentity()
    {
        var input = Clips(("a", null));

        Assert.Equal("a", Ordering.Shuffle(input, c => c.Locked, seed: 3)[0].Name);
    }

    [Fact]
    public void Shuffle_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(Ordering.Shuffle(new List<Clip>(), c => c.Locked, seed: 3));
    }

    [Fact]
    public void Shuffle_LockAtIndexZeroAndLastIndex_BothHeld()
    {
        var input = Clips(("a", null), ("b", null), ("c", 0), ("d", 3), ("e", null));

        for (var seed = 0; seed < 30; seed++)
        {
            var result = Ordering.Shuffle(input, c => c.Locked, seed);
            Assert.Equal("c", result[0].Name);
            Assert.Equal("d", result[3].Name);
        }
    }

    [Fact]
    public void Shuffle_RejectsOutOfRangeLock()
    {
        var input = Clips(("a", 5), ("b", null));

        Assert.Throws<ArgumentOutOfRangeException>(() => Ordering.Shuffle(input, c => c.Locked, seed: 1));
    }

    [Fact]
    public void Shuffle_RejectsNegativeLock()
    {
        var input = Clips(("a", -1), ("b", null));

        Assert.Throws<ArgumentOutOfRangeException>(() => Ordering.Shuffle(input, c => c.Locked, seed: 1));
    }

    [Fact]
    public void Shuffle_RejectsDuplicateLocks()
    {
        var input = Clips(("a", 1), ("b", 1), ("c", null));

        Assert.Throws<ArgumentException>(() => Ordering.Shuffle(input, c => c.Locked, seed: 1));
    }

    [Fact]
    public void Shuffle_OutOfRangeAndDuplicate_BothIndependentlyDetected()
    {
        // Out-of-range only: should not be mistaken for a duplicate-lock error.
        var outOfRangeOnly = Clips(("a", 10), ("b", null));
        var outOfRangeEx = Record.Exception(() => Ordering.Shuffle(outOfRangeOnly, c => c.Locked, seed: 1));
        Assert.IsType<ArgumentOutOfRangeException>(outOfRangeEx);

        // Duplicate only, all indices otherwise in range: should not be mistaken for out-of-range.
        var duplicateOnly = Clips(("a", 0), ("b", 0));
        var duplicateEx = Record.Exception(() => Ordering.Shuffle(duplicateOnly, c => c.Locked, seed: 1));
        Assert.IsType<ArgumentException>(duplicateEx);
        Assert.IsNotType<ArgumentOutOfRangeException>(duplicateEx);
    }

    [Fact]
    public void Shuffle_UnlockedItemsNeverLandOnLockedSlots()
    {
        var input = Clips(("a", null), ("b", 1), ("c", null), ("d", null));

        for (var seed = 0; seed < 50; seed++)
        {
            var result = Ordering.Shuffle(input, c => c.Locked, seed);
            Assert.Equal("b", result[1].Name);
        }
    }

    [Fact]
    public void Shuffle_NullItems_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Ordering.Shuffle<Clip>(null!, c => c.Locked, seed: 1));
    }

    [Fact]
    public void Shuffle_NullSelector_Throws()
    {
        var input = Clips(("a", null));

        Assert.Throws<ArgumentNullException>(() => Ordering.Shuffle(input, null!, seed: 1));
    }
}
