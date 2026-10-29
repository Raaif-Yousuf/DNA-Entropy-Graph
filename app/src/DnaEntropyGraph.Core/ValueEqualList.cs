using System.Collections;

namespace DnaEntropyGraph.Core;

/// <summary>
/// A read-only list that compares by its items, in order (issue #552). A record's generated equality compares each member with
/// <c>EqualityComparer&lt;T&gt;.Default</c>, which for a plain list is reference equality: two options read from the same JSON would differ.
/// Holding a list member in this type makes the record's own equality cover it, with no hand-written member list to drift.
/// </summary>
public sealed class ValueEqualList<T> : IReadOnlyList<T>, IEquatable<ValueEqualList<T>>
    where T : IEquatable<T>
{
    private readonly T[] _items;

    public ValueEqualList(IEnumerable<T>? items) => _items = items is null ? [] : [.. items];

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(ValueEqualList<T>? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);

    public override bool Equals(object? obj) => obj is ValueEqualList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
