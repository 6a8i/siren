#pragma warning disable CA1002

using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.Dto
{
    /// <summary>
    /// Interface IDtoService.
    /// </summary>
    public interface IDtoService
    {
        /// <summary>
        /// Gets the primary image aspect ratio.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>System.Nullable&lt;System.Double&gt;.</returns>
        double? GetPrimaryImageAspectRatio(BaseItem item);

        /// <summary>
        /// Gets the base item dto.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="options">The options.</param>
        /// <param name="user">The user.</param>
        /// <param name="owner">The owner.</param>
        /// <returns>BaseItemDto.</returns>
        BaseItemDto GetBaseItemDto(BaseItem item, DtoOptions options, User? user = null, BaseItem? owner = null);

        /// <summary>
        /// Gets the base item dtos.
        /// </summary>
        /// <param name="items">The items.</param>
        /// <param name="options">The options.</param>
        /// <param name="user">The user.</param>
        /// <param name="owner">The owner.</param>
        /// <param name="skipVisibilityCheck">Skip redundant visibility check if items are already filtered.</param>
        /// <returns>The <see cref="IReadOnlyList{T}"/> of <see cref="BaseItemDto"/>.</returns>
        IReadOnlyList<BaseItemDto> GetBaseItemDtos(
            IReadOnlyList<BaseItem> items,
            DtoOptions options,
            User? user = null,
            BaseItem? owner = null,
            bool skipVisibilityCheck = false);

        /// <summary>
        /// Gets the item by name dto.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="options">The dto options.</param>
        /// <param name="taggedItems">The list of tagged items.</param>
        /// <param name="user">The user.</param>
        /// <returns>The item dto.</returns>
        BaseItemDto GetItemByNameDto(BaseItem item, DtoOptions options, List<BaseItem>? taggedItems, User? user = null);

        /// <summary>
        /// Converts a page of by-name items (artists, genres, studios, people, years) to DTOs in one pass,
        /// batching the per-item lookups the way <see cref="GetBaseItemDtos"/> does. Item counts are left
        /// to the caller, which fetched them alongside the items.
        /// </summary>
        /// <param name="items">The items, already filtered for the user.</param>
        /// <param name="options">The options.</param>
        /// <param name="user">The user.</param>
        /// <returns>The DTOs, in the order of <paramref name="items"/>.</returns>
        IReadOnlyList<BaseItemDto> GetItemByNameDtos(IReadOnlyList<BaseItem> items, DtoOptions options, User? user = null);
    }
}
