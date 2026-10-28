using System;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class ItemByNameIdCacheTests
{
    [Fact]
    public void TryGet_AfterSet_ReturnsTheSameIdForTheSameTypeAndName()
    {
        var cache = new ItemByNameIdCache(16);
        var id = Guid.NewGuid();

        Assert.False(cache.TryGet(typeof(Person), "Tom Hanks", out _));

        cache.Set(typeof(Person), "Tom Hanks", id);

        Assert.True(cache.TryGet(typeof(Person), "Tom Hanks", out var found));
        Assert.Equal(id, found);
    }

    [Fact]
    public void TryGet_DifferentTypeOrSpelling_IsAnotherEntry()
    {
        var cache = new ItemByNameIdCache(16);
        cache.Set(typeof(Person), "Tom Hanks", Guid.NewGuid());

        // A studio called the same thing has its own id, and the name is taken exactly as asked for.
        Assert.False(cache.TryGet(typeof(Studio), "Tom Hanks", out _));
        Assert.False(cache.TryGet(typeof(Person), "tom hanks", out _));
    }

    [Fact]
    public void Set_PastCapacity_StaysBounded()
    {
        const int Capacity = 32;
        var cache = new ItemByNameIdCache(Capacity);

        for (var i = 0; i < Capacity * 10; i++)
        {
            cache.Set(typeof(MusicGenre), "Genre " + i, Guid.NewGuid());
        }

        Assert.InRange(cache.Count, 1, Capacity);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var cache = new ItemByNameIdCache(16);
        cache.Set(typeof(Genre), "Drama", Guid.NewGuid());

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(typeof(Genre), "Drama", out _));
    }
}
