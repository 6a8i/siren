using System;
using Jellyfin.Data.Enums;

namespace MediaBrowser.Model.Dto;

/// <summary>
/// One item a family profile played, newest first: what a family administrator sees of a profile's
/// watch history.
/// </summary>
public class FamilyHistoryEntryDto
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name (the episode title for an episode).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the series name, for an episode.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the season name, for an episode.
    /// </summary>
    public string? SeasonName { get; set; }

    /// <summary>
    /// Gets or sets the item type.
    /// </summary>
    public BaseItemKind Type { get; set; }

    /// <summary>
    /// Gets or sets when the item was last played, in UTC.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Gets or sets how much of the item was played, in percent, when playback stopped part way.
    /// </summary>
    public double? PlayedPercentage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item was played to the end.
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// Gets or sets the primary image tag of the item, or of its series for an episode.
    /// </summary>
    public string? ImageTag { get; set; }
}
