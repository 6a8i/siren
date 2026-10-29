using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// One item of a user's play history, straight from the stored user data: the item is resolved and
/// access-checked by the caller.
/// </summary>
/// <param name="ItemId">The item id.</param>
/// <param name="LastPlayedDate">When the item was last played, in UTC.</param>
/// <param name="Played">Whether the item was played to the end.</param>
/// <param name="PlaybackPositionTicks">Where playback stopped, in ticks, or zero.</param>
public record PlayHistoryEntry(Guid ItemId, DateTime LastPlayedDate, bool Played, long PlaybackPositionTicks);
