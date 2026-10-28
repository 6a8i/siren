#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Persistence;

/// <summary>
/// Provides methods for accessing MediaStreams.
/// </summary>
public interface IMediaStreamRepository
{
    /// <summary>
    /// Gets the media streams.
    /// </summary>
    /// <param name="filter">The query.</param>
    /// <returns>IEnumerable{MediaStream}.</returns>
    IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery filter);

    /// <summary>
    /// Gets all language codes of the provided stream type.
    /// </summary>
    /// <param name="mediaStreamType">The type of the media stream.</param>
    /// <returns>IEnumerable{string}.</returns>
    IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType);

    /// <summary>
    /// Gets the subset of <paramref name="itemIds"/> that have at least one stream of the given type.
    /// </summary>
    /// <param name="itemIds">The item ids to check.</param>
    /// <param name="mediaStreamType">The type of the media stream.</param>
    /// <returns>The ids of the items that have such a stream.</returns>
    IReadOnlySet<Guid> GetItemIdsWithStreamType(IReadOnlyList<Guid> itemIds, MediaStreamType mediaStreamType);

    /// <summary>
    /// Saves the media streams.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <param name="streams">The streams.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    void SaveMediaStreams(Guid id, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken);
}
