#nullable disable

#pragma warning disable CA1819, CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace MediaBrowser.Controller.Entities
{
    /// <summary>
    /// Specialized folder that can have items added to it's children by external entities.
    /// Used for our RootFolder so plugins can add items.
    /// </summary>
    public class AggregateFolder : Folder
    {
        private readonly Lock _childIdsLock = new();

        /// <summary>
        /// The _virtual children.
        /// </summary>
        private readonly ConcurrentBag<BaseItem> _virtualChildren = new ConcurrentBag<BaseItem>();

        /// <summary>
        /// Stopwatch ticks each top-level folder spent in the scan that is running, keyed by its id.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, long> _libraryScanTicks = new();
        private bool _requiresRefresh;
        private Guid[] _childrenIds = null;

        public AggregateFolder()
        {
            PhysicalLocationsList = Array.Empty<string>();
        }

        /// <summary>
        /// Gets the virtual children.
        /// </summary>
        /// <value>The virtual children.</value>
        public ConcurrentBag<BaseItem> VirtualChildren => _virtualChildren;

        [JsonIgnore]
        public override bool IsPhysicalRoot => true;

        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        [JsonIgnore]
        public override string[] PhysicalLocations => PhysicalLocationsList;

        public string[] PhysicalLocationsList { get; set; }

        // Children caches the resolved items, _childrenIds the ids they were loaded from. Clearing
        // only the former sends the next read back through LoadChildren, which replays the stale
        // id list, so a caller invalidating this folder has to drop both.
        [JsonIgnore]
        public override IEnumerable<BaseItem> Children
        {
            get => base.Children;
            set
            {
                if (value is null)
                {
                    ClearCache();
                }

                base.Children = value;
            }
        }

        public override bool CanDelete()
        {
            return false;
        }

        protected override FileSystemMetadata[] GetFileSystemChildren(IDirectoryService directoryService)
        {
            return CreateResolveArgs(directoryService, true).FileSystemChildren;
        }

        protected override IReadOnlyList<BaseItem> LoadChildren()
        {
            lock (_childIdsLock)
            {
                if (_childrenIds is null || _childrenIds.Length == 0)
                {
                    var list = base.LoadChildren();
                    _childrenIds = list.Select(i => i.Id).ToArray();
                    return list;
                }

                return _childrenIds.Select(LibraryManager.GetItemById).Where(i => i is not null).ToList();
            }
        }

        private void ClearCache()
        {
            lock (_childIdsLock)
            {
                _childrenIds = null;
            }
        }

        public override bool RequiresRefresh()
        {
            var changed = base.RequiresRefresh() || _requiresRefresh;

            if (!changed)
            {
                var locations = PhysicalLocations;

                var newLocations = CreateResolveArgs(new DirectoryService(FileSystem), false).PhysicalLocations;

                if (!locations.SequenceEqual(newLocations))
                {
                    changed = true;
                }
            }

            return changed;
        }

        public override bool BeforeMetadataRefresh(bool replaceAllMetadata)
        {
            ClearCache();

            var changed = base.BeforeMetadataRefresh(replaceAllMetadata) || _requiresRefresh;
            _requiresRefresh = false;
            return changed;
        }

        private ItemResolveArgs CreateResolveArgs(IDirectoryService directoryService, bool setPhysicalLocations)
        {
            ClearCache();

            var path = ContainingFolderPath;

            var args = new ItemResolveArgs(ConfigurationManager.ApplicationPaths, LibraryManager)
            {
                FileInfo = FileSystem.GetDirectoryInfo(path)
            };

            // Gather child folder and files
            if (args.IsDirectory)
            {
                // When resolving the root, we need it's grandchildren (children of user views)
                var flattenFolderDepth = 2;

                var files = FileData.GetFilteredFileSystemEntries(directoryService, args.Path, FileSystem, CollectionFolder.ApplicationHost, Logger, args, flattenFolderDepth: flattenFolderDepth, resolveShortcuts: true);

                // Need to remove subpaths that may have been resolved from shortcuts
                // Example: if \\server\movies exists, then strip out \\server\movies\action
                files = LibraryManager.NormalizeRootPathList(files).ToArray();

                args.FileSystemChildren = files;
            }

            _requiresRefresh = _requiresRefresh || !args.PhysicalLocations.SequenceEqual(PhysicalLocations);
            if (setPhysicalLocations)
            {
                PhysicalLocationsList = args.PhysicalLocations;
            }

            return args;
        }

        protected override IEnumerable<BaseItem> GetNonCachedChildren(IDirectoryService directoryService)
        {
            return base.GetNonCachedChildren(directoryService).Concat(_virtualChildren);
        }

        protected override async Task ValidateChildrenInternal(IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            ClearCache();

            await base.ValidateChildrenInternal(progress, recursive, refreshChildMetadata, allowRemoveRoot, refreshOptions, directoryService, cancellationToken)
                .ConfigureAwait(false);

            ClearCache();
        }

        /// <summary>
        /// Runs one phase of the scan of a top-level folder (a library path) and adds the time it took to that
        /// folder's total, so the library manager can log one line per library once the scan is over.
        /// </summary>
        /// <param name="child">The top-level folder being scanned.</param>
        /// <param name="work">The phase to run.</param>
        /// <returns>A task that completes when the phase does.</returns>
        internal async Task TimeLibraryScan(BaseItem child, Func<Task> work)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                await work().ConfigureAwait(false);
            }
            finally
            {
                _libraryScanTicks.AddOrUpdate(
                    child.Id,
                    static (_, elapsed) => elapsed,
                    static (_, total, elapsed) => total + elapsed,
                    Stopwatch.GetTimestamp() - start);
            }
        }

        /// <summary>
        /// Returns the time each top-level folder spent in the scan that just ran and clears the counters.
        /// </summary>
        /// <returns>The elapsed time per top-level folder id.</returns>
        public Dictionary<Guid, TimeSpan> TakeLibraryScanTimings()
        {
            var timings = new Dictionary<Guid, TimeSpan>(_libraryScanTicks.Count);
            foreach (var (id, ticks) in _libraryScanTicks)
            {
                timings[id] = Stopwatch.GetElapsedTime(0, ticks);
            }

            _libraryScanTicks.Clear();
            return timings;
        }

        /// <summary>
        /// Adds the virtual child.
        /// </summary>
        /// <param name="child">The child.</param>
        /// <exception cref="ArgumentNullException">Throws if child is null.</exception>
        public void AddVirtualChild(BaseItem child)
        {
            ArgumentNullException.ThrowIfNull(child);

            _virtualChildren.Add(child);
        }

        /// <summary>
        /// Finds the virtual child.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>BaseItem.</returns>
        /// <exception cref="ArgumentNullException">The id is empty.</exception>
        public BaseItem FindVirtualChild(Guid id)
        {
            if (id.IsEmpty())
            {
                throw new ArgumentNullException(nameof(id));
            }

            foreach (var child in _virtualChildren)
            {
                if (child.Id.Equals(id))
                {
                    return child;
                }
            }

            return null;
        }
    }
}
