using System;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Naming.Common;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;
using ServerLibraryManager = Emby.Server.Implementations.Library.LibraryManager;

namespace Jellyfin.Server.Implementations.Tests.Library;

/// <summary>
/// The by-name ids are memoized: the folder path (which goes through the file system's name
/// cleaning) and the hash are only computed the first time a name is asked for. The static
/// BaseItem services the path builders use are swapped in for the test and restored after it.
/// </summary>
public class LibraryManagerItemByNameIdTests
{
    [Fact]
    public void GetStudioId_SameName_ComputesThePathOnce()
    {
        var previousConfiguration = BaseItem.ConfigurationManager;
        var previousFileSystem = BaseItem.FileSystem;
        try
        {
            var (manager, configuration, fileSystem) = CreateManager();

            var first = manager.GetStudioId("Warner Bros.");
            var second = manager.GetStudioId("Warner Bros.");
            var other = manager.GetStudioId("Universal");

            Assert.Equal(first, second);
            Assert.NotEqual(first, other);

            // Two names, two path builds: the repeat came from the cache.
            fileSystem.Verify(f => f.GetValidFilename(It.IsAny<string>()), Times.Exactly(2));

            // The configuration takes part in the id, so a change forgets what was remembered.
            configuration.Raise(c => c.ConfigurationUpdated += null, EventArgs.Empty);
            Assert.Equal(first, manager.GetStudioId("Warner Bros."));
            fileSystem.Verify(f => f.GetValidFilename(It.IsAny<string>()), Times.Exactly(3));
        }
        finally
        {
            BaseItem.ConfigurationManager = previousConfiguration;
            BaseItem.FileSystem = previousFileSystem;
        }
    }

    [Fact]
    public void GetPersonId_GetGenreId_SameNameDifferentType_AreDifferentIds()
    {
        var previousConfiguration = BaseItem.ConfigurationManager;
        var previousFileSystem = BaseItem.FileSystem;
        try
        {
            var (manager, _, fileSystem) = CreateManager();

            var person = manager.GetPersonId("Drama");
            var genre = manager.GetGenreId("Drama");
            var musicGenre = manager.GetMusicGenreId("Drama");

            Assert.NotEqual(person, genre);
            Assert.NotEqual(genre, musicGenre);
            Assert.Equal(person, manager.GetPersonId("Drama"));
            Assert.Equal(genre, manager.GetGenreId("Drama"));
            Assert.Equal(musicGenre, manager.GetMusicGenreId("Drama"));

            fileSystem.Verify(f => f.GetValidFilename(It.IsAny<string>()), Times.Exactly(3));
        }
        finally
        {
            BaseItem.ConfigurationManager = previousConfiguration;
            BaseItem.FileSystem = previousFileSystem;
        }
    }

    private static (ServerLibraryManager Manager, Mock<IServerConfigurationManager> Configuration, Mock<IFileSystem> FileSystem) CreateManager()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());

        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        configuration.Setup(c => c.ApplicationPaths.PeoplePath).Returns("/data/metadata/People");
        configuration.Setup(c => c.ApplicationPaths.StudioPath).Returns("/data/metadata/Studio");
        configuration.Setup(c => c.ApplicationPaths.GenrePath).Returns("/data/metadata/Genre");
        configuration.Setup(c => c.ApplicationPaths.MusicGenrePath).Returns("/data/metadata/MusicGenre");

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns<string>(name => name);

        BaseItem.ConfigurationManager = configuration.Object;
        BaseItem.FileSystem = fileSystem.Object;

        return (fixture.Create<ServerLibraryManager>(), configuration, fileSystem);
    }
}
