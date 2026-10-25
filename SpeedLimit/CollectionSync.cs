using System.Collections.ObjectModel;

namespace SpeedLimit;

public static class CollectionSync
{
    /// <summary>Bringt target per Remove/Insert/Move auf den Stand von desired, damit WPF Container und Fokus behält.</summary>
    public static void SyncTo<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : notnull
    {
        var keep = desired.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var cur = target.IndexOf(desired[i]);
            if (cur < 0) target.Insert(i, desired[i]);
            else if (cur != i) target.Move(cur, i);
        }
    }
}
