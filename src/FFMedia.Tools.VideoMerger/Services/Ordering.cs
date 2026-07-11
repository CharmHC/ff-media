namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure clip ordering. Locked items are pinned to their index; the rest are
/// Fisher–Yates-shuffled into the remaining slots. Seeded, so tests are deterministic.</summary>
public static class Ordering
{
    /// <summary>Reorders <paramref name="items"/>: any item whose <paramref name="lockedIndexSelector"/>
    /// returns a non-null index is pinned there; the rest are seeded Fisher–Yates-shuffled and
    /// dropped into the remaining slots, left to right.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A locked index falls outside <c>[0, items.Count)</c>.
    /// </exception>
    /// <exception cref="ArgumentException">Two items lock to the same index.</exception>
    public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> items, Func<T, int?> lockedIndexSelector, int seed)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(lockedIndexSelector);

        if (items.Count == 0)
        {
            return [];
        }

        var slots = new T?[items.Count];
        var taken = new bool[items.Count];
        var unlocked = new List<T>();

        foreach (var item in items)
        {
            var locked = lockedIndexSelector(item);
            if (locked is null)
            {
                unlocked.Add(item);
                continue;
            }

            var index = locked.Value;
            if (index < 0 || index >= items.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lockedIndexSelector), index, $"Locked index must be within [0, {items.Count - 1}].");
            }

            if (taken[index])
            {
                throw new ArgumentException($"Two clips are locked to index {index}.", nameof(items));
            }

            slots[index] = item;
            taken[index] = true;
        }

        // Unbiased Fisher–Yates over the unlocked items only: rand.Next(i + 1) is inclusive of i,
        // so every element (including the current one) is an equally likely swap partner and can
        // legitimately stay in place.
        var random = new Random(seed);
        for (var i = unlocked.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (unlocked[i], unlocked[j]) = (unlocked[j], unlocked[i]);
        }

        var next = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            if (!taken[i])
            {
                slots[i] = unlocked[next++];
            }
        }

        return slots.Select(s => s!).ToList();
    }
}
