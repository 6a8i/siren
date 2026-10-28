using System;
using BitFaster.Caching.Lru;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Remembers the id a by-name item (person, studio, genre, music genre, artist, year) gets for a name.
/// The id is a pure function of the item type, the name and two configuration flags: the folder path
/// is built from the name, lower-cased when the ids are normalized and hashed with MD5. That is a path
/// combine, a copy and a hash for every lookup, and building a page of DTOs asks for the same few
/// thousand names again and again. A bounded, thread-safe LRU keeps the answer; it is cleared when the
/// configuration changes, the only input that can change while the server runs, since renaming an
/// item does not change which id a given name maps to.
/// </summary>
public sealed class ItemByNameIdCache
{
    private readonly FastConcurrentLru<Key, Guid> _ids;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemByNameIdCache"/> class.
    /// </summary>
    /// <param name="capacity">How many names to keep; the least recently used are dropped past it.</param>
    public ItemByNameIdCache(int capacity)
    {
        _ids = new FastConcurrentLru<Key, Guid>(capacity);
    }

    /// <summary>
    /// Gets the number of names currently kept.
    /// </summary>
    public int Count => _ids.Count;

    /// <summary>
    /// Looks a name up.
    /// </summary>
    /// <param name="type">The item type the name belongs to.</param>
    /// <param name="name">The name, exactly as asked for.</param>
    /// <param name="id">The id remembered for it.</param>
    /// <returns>Whether the name was known.</returns>
    public bool TryGet(Type type, string name, out Guid id)
        => _ids.TryGet(new Key(type, name), out id);

    /// <summary>
    /// Remembers the id of a name.
    /// </summary>
    /// <param name="type">The item type the name belongs to.</param>
    /// <param name="name">The name, exactly as asked for.</param>
    /// <param name="id">Its id.</param>
    public void Set(Type type, string name, Guid id)
        => _ids.AddOrUpdate(new Key(type, name), id);

    /// <summary>
    /// Forgets every name.
    /// </summary>
    public void Clear()
        => _ids.Clear();

    private readonly record struct Key(Type Type, string Name);
}
