using System.Collections.ObjectModel;

namespace DevDriveStorage;

/// <summary>
/// Merges a freshly computed, ordered sequence into an existing observable collection by identity,
/// reusing the view models that are still wanted instead of replacing them.
/// </summary>
/// <remarks>
/// A streaming scan republishes the whole tree ten times a second. Clearing and refilling is the
/// obvious implementation and the wrong one: a reset tells a list control that everything it knows
/// is gone, so it drops every container, and a <c>TreeView</c> additionally drops every
/// <c>TreeViewNode</c> — which collapses the tree, loses the selection, and jumps the scroll. The
/// user sees the room rebuild itself on every tick.
/// <para>
/// So the collection is edited rather than replaced: survivors keep their object identity and are
/// re-pointed at the newer node, arrivals are inserted at their slot, departures are removed, and
/// re-ranking moves an item rather than rebuilding it. Only the rows that genuinely changed raise
/// anything.
/// </para>
/// <para>
/// This works only because node identifiers are derived from paths and are therefore stable across
/// snapshots. With freshly minted identifiers every item would look new on every tick and this would
/// degrade into the clear-and-refill it exists to avoid.
/// </para>
/// </remarks>
internal static class CollectionReconciler
{
    /// <summary>
    /// Brings <paramref name="target"/> into line with <paramref name="desired"/>, in that order.
    /// </summary>
    /// <param name="target">The live collection a control is bound to.</param>
    /// <param name="desired">The wanted contents, already in display order.</param>
    /// <param name="keyOfItem">Identity of an existing item.</param>
    /// <param name="keyOfSource">Identity of a wanted item.</param>
    /// <param name="create">Builds an item for a source that has no counterpart yet.</param>
    /// <param name="adopt">Re-points a surviving item at its newer source.</param>
    /// <param name="reorder">How the control bound to <paramref name="target"/> wants re-ranking expressed.</param>
    public static void Reconcile<TItem, TSource>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TSource> desired,
        Func<TItem, Guid> keyOfItem,
        Func<TSource, Guid> keyOfSource,
        Func<TSource, TItem> create,
        Action<TItem, TSource> adopt,
        ReorderStrategy reorder = ReorderStrategy.Move)
    {
        var wanted = new HashSet<Guid>(desired.Count);
        foreach (TSource source in desired)
        {
            wanted.Add(keyOfSource(source));
        }

        // Back to front, so the indices of the items still to be examined never shift.
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(keyOfItem(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        var survivors = new Dictionary<Guid, TItem>(target.Count);
        foreach (TItem item in target)
        {
            survivors[keyOfItem(item)] = item;
        }

        for (int slot = 0; slot < desired.Count; slot++)
        {
            TSource source = desired[slot];
            Guid key = keyOfSource(source);
            if (!survivors.TryGetValue(key, out TItem? existing))
            {
                target.Insert(slot, create(source));
                continue;
            }

            adopt(existing, source);

            // Everything below `slot` is already final and holds a different key, so the survivor
            // cannot be behind us. Scanning from `slot` keeps this linear in the common case where
            // nothing has re-ranked.
            for (int current = slot; current < target.Count; current++)
            {
                if (keyOfItem(target[current]) != key)
                {
                    continue;
                }

                if (current != slot)
                {
                    if (reorder == ReorderStrategy.Move)
                    {
                        target.Move(current, slot);
                    }
                    else
                    {
                        target.RemoveAt(current);
                        target.Insert(slot, existing);
                    }
                }

                break;
            }
        }
    }
}

/// <summary>
/// How a re-ranked item is repositioned, which is a property of the control bound to the collection
/// rather than of the data in it.
/// </summary>
internal enum ReorderStrategy
{
    /// <summary>
    /// Emit a single move and let the control reposition the container it has already built. Correct
    /// for <c>ListView</c>, and the cheaper of the two.
    /// </summary>
    Move,

    /// <summary>
    /// Remove the item and insert it at its new slot.
    /// </summary>
    /// <remarks>
    /// Required by <c>TreeView</c>, which mirrors the bound collection into its own
    /// <c>TreeViewNode</c> tree and <b>silently ignores</b> a move: the model re-ranks and the tree
    /// keeps rendering the order the items were first inserted in. Verified live against a real
    /// scan, where the table showed folders by size while the tree stayed in the alphabetical order
    /// the first tick happened to produce. This costs the moved item its container, so callers that
    /// keep state on the container have to restore it.
    /// </remarks>
    Reinsert,
}
