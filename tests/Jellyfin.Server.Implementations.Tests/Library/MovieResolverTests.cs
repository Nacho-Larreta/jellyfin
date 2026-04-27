using System;
using System.Collections.Generic;
using Emby.Naming.Common;
using Emby.Server.Implementations.Library.Resolvers.Movies;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class MovieResolverTests
{
    private static readonly NamingOptions _namingOptions = new();

    [Fact]
    public void Resolve_GivenLocalAlternateVersion_ResolvesToVideo()
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>());
        var itemResolveArgs = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            null)
        {
            Parent = null,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/movies/Black Panther (2018)/Black Panther (2018) - 1080p 3D.mk3d"
            }
        };

        Assert.NotNull(movieResolver.Resolve(itemResolveArgs));
    }

    [Theory]
    [InlineData(CollectionType.courses)]
    [InlineData(CollectionType.adultvideos)]
    public void Resolve_GivenJellyflixVideoCollectionType_ResolvesToPlainVideo(CollectionType collectionType)
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>());
        var itemResolveArgs = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            null)
        {
            Parent = new Folder { Id = Guid.NewGuid(), Path = "/library" },
            CollectionType = collectionType,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/library/training-video.mp4"
            }
        };

        var item = movieResolver.Resolve(itemResolveArgs);

        Assert.NotNull(item);
        Assert.False(item is Movie);
    }

    [Fact]
    public void IsVisible_GivenAdultLibraryAndEnableAllFolders_RequiresExplicitFolderAccess()
    {
        var previousLibraryManager = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = Mock.Of<ILibraryManager>(manager =>
                manager.GetCollectionFolders(It.IsAny<BaseItem>()) == new List<Folder>());

            var libraryId = Guid.NewGuid();
            var user = new User("test", "default", "default");
            var library = new TestCollectionFolder
            {
                Id = libraryId,
                CollectionType = CollectionType.adultvideos
            };

            user.SetPermission(PermissionKind.EnableAllFolders, true);

            Assert.False(library.IsVisible(user));

            user.SetPreference(PreferenceKind.EnabledFolders, [libraryId]);

            Assert.True(library.IsVisible(user));
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }
    }

    private sealed class TestCollectionFolder : Folder, ICollectionFolder
    {
        public CollectionType? CollectionType { get; init; }
    }
}
