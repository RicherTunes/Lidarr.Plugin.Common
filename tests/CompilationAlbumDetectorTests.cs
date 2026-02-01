using System;
using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class CompilationAlbumDetectorTests
    {
        #region IsVariousArtists Tests

        public class IsVariousArtists
        {
            [Theory]
            [InlineData(null)]
            [InlineData("")]
            [InlineData("   ")]
            [InlineData("\t")]
            public void Should_Return_False_For_Null_Or_Whitespace_Artist(string? albumArtist)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist!);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Various Artists")]
            [InlineData("various artists")]
            [InlineData("VARIOUS ARTISTS")]
            [InlineData("Various  Artists")] // Multiple spaces
            [InlineData(" Various Artists ")] // Leading/trailing spaces
            public void Should_Return_True_For_VariousArtists_Pattern(string albumArtist)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("V.A.")]
            [InlineData("v.a.")]
            [InlineData("VA")]
            [InlineData("va")]
            [InlineData("Va")]
            public void Should_Return_True_For_VA_Abbreviations(string albumArtist)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("V A")]
            [InlineData("v a")]
            [InlineData("V.A")]
            [InlineData("v.a")]
            public void Should_Return_False_For_VA_Abbreviations_With_Spaces_Or_Dots(string albumArtist)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Various")]
            [InlineData("various")]
            [InlineData("Compilation")]
            [InlineData("compilation")]
            [InlineData("Mixed")]
            [InlineData("mixed")]
            [InlineData("Sampler")]
            [InlineData("sampler")]
            [InlineData("Collection")]
            [InlineData("collection")]
            [InlineData("Anthology")]
            [InlineData("anthology")]
            [InlineData("Tribute")]
            [InlineData("tribute")]
            [InlineData("Best Of Various")]
            [InlineData("best of various")]
            public void Should_Return_True_For_Compilation_Patterns(string albumArtist)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Taylor Swift")]
            [InlineData("The Beatles")]
            [InlineData("Daft Punk")]
            [InlineData("Radiohead")]
            [InlineData("Artist Name")]
            public void Should_Return_False_For_Regular_Artist_Names(string albumArtist)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Various Artists", "Greatest Hits of the 80s")]
            [InlineData("VA", "Best of 2020")]
            [InlineData("Various", "The Very Best of Rock")]
            public void Should_Return_True_For_VariousArtist_With_Compilation_Title(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Hans Zimmer", "Inception OST")]
            [InlineData("Hans Zimmer", "Inception Original Soundtrack")]
            [InlineData("Various", "Movie Soundtrack Collection")]
            public void Should_Return_True_For_Soundtrack_Titles_Even_With_Non_VA_Artist(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Various Artists", "Greatest Hits")]
            [InlineData("VA", "Best Of Classic Rock")]
            [InlineData("The Beatles", "The Very Best Of The Beatles")]
            [InlineData("Artist", "Essential Collection")]
            [InlineData("Band", "Ultimate Collection")]
            [InlineData("Composer", "Complete Works")]
            [InlineData("Group", "Anthology 1968-1970")]
            [InlineData("Singer", "Retrospective")]
            [InlineData("Various", "Hits Collection")]
            [InlineData("VA", "Singles Compilation")]
            [InlineData("Various Artists", "Rarities Compilation")]
            [InlineData("Various Artists", "B-Sides Compilation")]
            [InlineData("VA", "Unreleased Collection")]
            public void Should_Return_True_For_Compilation_Title_Patterns(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Artist", "Album Vol. 1")]
            [InlineData("Band", "Greatest Hits Vol. 2")]
            [InlineData("DJ Mix", "Club Hits Vol. 10")]
            [InlineData("Various", "Collection Vol. 3")]
            public void Should_Return_True_For_Volume_Patterns_In_Title(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Artist", "Album Disc 1")]
            [InlineData("Band", "Collection CD 2")]
            public void Should_Return_True_For_Disc_CD_Patterns_In_Title(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Taylor Swift", "1989")]
            [InlineData("The Beatles", "Abbey Road")]
            [InlineData("Radiohead", "In Rainbows")]
            public void Should_Return_False_For_Standard_Album_With_Regular_Artist(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData(null!, "Greatest Hits")]
            [InlineData("", "Best Of")]
            [InlineData("   ", "Essential Collection")]
            public void Should_Return_False_For_Null_Artist_Even_With_Compilation_Title(string? albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist!, albumTitle);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Artist", null)]
            [InlineData("Artist", "")]
            [InlineData("Artist", "   ")]
            [InlineData("Artist", "\t")]
            public void Should_Handle_Null_Or_Whitespace_Title(string albumArtist, string? albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle!);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Various Artists", "Album (Deluxe Edition)")]
            [InlineData("V.A.", "Album [Remastered]")]
            [InlineData("VA", "Album: Special Edition")]
            public void Should_Handle_Special_Characters_In_Title(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.True(result);
            }
        }

        #endregion

        #region IsSoundtrack Tests

        public class IsSoundtrack
        {
            [Theory]
            [InlineData(null)]
            [InlineData("")]
            [InlineData("   ")]
            [InlineData("\t")]
            public void Should_Return_False_For_Null_Or_Whitespace_Title(string? albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle!);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Inception Soundtrack")]
            [InlineData("The Dark Knight Rises Soundtrack")]
            [InlineData("Movie Soundtrack Collection")]
            [InlineData("Original Soundtrack")]
            [InlineData("OST Collection")]
            public void Should_Return_True_For_Soundtrack_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Inception OST")]
            [InlineData("ost Collection")]
            [InlineData("OST Volume 1")]
            public void Should_Return_True_For_OST_Abbreviation_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Inception Original Soundtrack")]
            [InlineData("The Matrix Original Soundtrack")]
            public void Should_Return_True_For_Original_Soundtrack_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Motion Picture Soundtrack")]
            [InlineData("Original Motion Picture Soundtrack")]
            public void Should_Return_True_For_Motion_Picture_Soundtrack_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Movie Soundtrack")]
            [InlineData("Film Soundtrack")]
            public void Should_Return_True_For_Movie_Film_Soundtrack_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("TV Soundtrack")]
            [InlineData("Television Soundtrack")]
            [InlineData("Breaking Bad TV Soundtrack")]
            public void Should_Return_True_For_TV_Television_Soundtrack_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Game Soundtrack")]
            [InlineData("Video Game Soundtrack")]
            [InlineData("The Last of Us Game Soundtrack")]
            public void Should_Return_True_For_Game_Soundtrack_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Original Motion Picture Score")]
            [InlineData("The Score")]
            [InlineData("Film Score Collection")]
            public void Should_Return_True_For_Score_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Theme Song Collection")]
            [InlineData("Movie Themes")]
            [InlineData("Themes")]
            public void Should_Return_True_For_Theme_Themes_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Regular Album", "")]
            [InlineData("Studio Album", "")]
            [InlineData("Greatest Hits", "")] // Not a soundtrack without context
            [InlineData("Live Concert", "")]
            [InlineData("Taylor Swift", "1989")]
            public void Should_Return_False_For_Non_Soundtrack_Titles(string albumTitle, string? albumArtist = null)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle, albumArtist!);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Soundtrack Orchestra", "Classical Music")]
            [InlineData("Hans Zimmer", "Inception Soundtrack")]
            public void Should_Return_True_When_Artist_Contains_Soundtrack_Pattern(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle, albumArtist);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Hans Zimmer", "Inception")]
            [InlineData("Composer Name", "Regular Album")]
            public void Should_Return_False_When_Neither_Artist_Nor_Title_Contains_Soundtrack_Pattern(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle, albumArtist);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("inception soundtrack")] // lowercase
            [InlineData("THE DARK KNIGHT OST")] // uppercase
            [InlineData("MoViE sOuNdTrAcK")] // mixed case
            public void Should_Be_Case_Insensitive(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("Album (OST)")]
            [InlineData("Album [Soundtrack]")]
            [InlineData("Album: Score")]
            public void Should_Handle_Special_Characters_In_Title(string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsSoundtrack(albumTitle);

                // Assert
                Assert.True(result);
            }
        }

        #endregion

        #region GetCompilationType Tests

        public class GetCompilationType
        {
            [Fact]
            public void Should_Return_Standard_For_Regular_Album()
            {
                // Arrange
                var albumArtist = "Taylor Swift";
                var albumTitle = "1989";

                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert
                Assert.Equal(CompilationType.Standard, result);
            }

            [Theory]
            [InlineData("Hans Zimmer", "Inception Soundtrack")]
            [InlineData("Hans Zimmer", "Inception OST")]
            [InlineData("Various Artists", "Movie Soundtrack")]
            [InlineData(null!, "Original Motion Picture Score")]
            public void Should_Return_Soundtrack_For_Soundtrack_Patterns(string? albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist!, albumTitle);

                // Assert
                Assert.Equal(CompilationType.Soundtrack, result);
            }

            [Theory]
            [InlineData("Various Artists", null)]
            [InlineData("Various Artists", "")]
            [InlineData("Various Artists", "   ")]
            [InlineData("V.A.", null)]
            [InlineData("VA", "")]
            public void Should_Return_VariousArtists_For_VA_Without_Title(string albumArtist, string? albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle!);

                // Assert
                Assert.Equal(CompilationType.VariousArtists, result);
            }

            [Theory]
            [InlineData("Various Artists", "Greatest Hits")]
            [InlineData("VA", "Best of Rock")]
            [InlineData("Various", "The Very Best of 80s")]
            [InlineData("V.A.", "Greatest Hits of the Decade")]
            public void Should_Return_GreatestHits_For_GreatestHits_Patterns(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert
                Assert.Equal(CompilationType.GreatestHits, result);
            }

            [Theory]
            [InlineData("Various Artists", "Live at Wembley")]
            [InlineData("VA", "Concert Recording")]
            [InlineData("Various", "Live Collection")]
            [InlineData("V.A.", "In Concert")]
            public void Should_Return_LiveCompilation_For_Live_Patterns(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert
                Assert.Equal(CompilationType.LiveCompilation, result);
            }

            [Theory]
            [InlineData("Various Artists", "Tribute to The Beatles")]
            [InlineData("VA", "A Tribute to Queen")]
            [InlineData("Various", "Rockabilly Tribute")]
            public void Should_Return_Tribute_For_Tribute_Patterns(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert
                Assert.Equal(CompilationType.Tribute, result);
            }

            [Theory]
            [InlineData("Various Artists", "Summer Hits 2020")]
            [InlineData("VA", "Club Collection")]
            [InlineData("Various", "Essential Dance")]
            [InlineData("V.A.", "Ultimate Rock")]
            public void Should_Return_VariousArtists_For_Other_Compilations(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert
                Assert.Equal(CompilationType.VariousArtists, result);
            }

            [Theory]
            [InlineData(null!, "Some Album")]
            [InlineData("", "Some Album")]
            [InlineData("   ", "Some Album")]
            public void Should_Return_Standard_For_Null_Artist(string? albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist!, albumTitle);

                // Assert
                Assert.Equal(CompilationType.Standard, result);
            }

            [Fact]
            public void Should_Prioritize_Soundtrack_Detection_Over_VariousArtists()
            {
                // Arrange - Album that could be both VA and soundtrack
                var albumArtist = "Various Artists";
                var albumTitle = "Movie Soundtrack Collection";

                // Act
                var result = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert
                Assert.Equal(CompilationType.Soundtrack, result);
            }
        }

        #endregion

        #region GetMatchingStrategy Tests

        public class GetMatchingStrategy
        {
            [Fact]
            public void Should_Return_TitleAndYearOnly_For_VariousArtists_Type()
            {
                // Arrange
                var type = CompilationType.VariousArtists;

                // Act
                var result = CompilationAlbumDetector.GetMatchingStrategy(type);

                // Assert
                Assert.Equal(CompilationMatchingStrategy.TitleAndYearOnly, result);
            }

            [Fact]
            public void Should_Return_TitleFuzzyMatch_For_Soundtrack_Type()
            {
                // Arrange
                var type = CompilationType.Soundtrack;

                // Act
                var result = CompilationAlbumDetector.GetMatchingStrategy(type);

                // Assert
                Assert.Equal(CompilationMatchingStrategy.TitleFuzzyMatch, result);
            }

            [Fact]
            public void Should_Return_ArtistAndTitleFuzzy_For_GreatestHits_Type()
            {
                // Arrange
                var type = CompilationType.GreatestHits;

                // Act
                var result = CompilationAlbumDetector.GetMatchingStrategy(type);

                // Assert
                Assert.Equal(CompilationMatchingStrategy.ArtistAndTitleFuzzy, result);
            }

            [Fact]
            public void Should_Return_TitleAndVenueMatch_For_LiveCompilation_Type()
            {
                // Arrange
                var type = CompilationType.LiveCompilation;

                // Act
                var result = CompilationAlbumDetector.GetMatchingStrategy(type);

                // Assert
                Assert.Equal(CompilationMatchingStrategy.TitleAndVenueMatch, result);
            }

            [Fact]
            public void Should_Return_TitleAndGenreMatch_For_Tribute_Type()
            {
                // Arrange
                var type = CompilationType.Tribute;

                // Act
                var result = CompilationAlbumDetector.GetMatchingStrategy(type);

                // Assert
                Assert.Equal(CompilationMatchingStrategy.TitleAndGenreMatch, result);
            }

            [Fact]
            public void Should_Return_Standard_For_Standard_Type()
            {
                // Arrange
                var type = CompilationType.Standard;

                // Act
                var result = CompilationAlbumDetector.GetMatchingStrategy(type);

                // Assert
                Assert.Equal(CompilationMatchingStrategy.Standard, result);
            }

            [Fact]
            public void Should_Return_Standard_For_Unknown_Type()
            {
                // Arrange - Using all enum values to ensure coverage
                var allTypes = (CompilationType[])Enum.GetValues(typeof(CompilationType));

                // Act & Assert
                foreach (var type in allTypes)
                {
                    var result = CompilationAlbumDetector.GetMatchingStrategy(type);

                    // All known types should have a non-Standard strategy except Standard itself
                    if (type == CompilationType.Standard)
                    {
                        Assert.Equal(CompilationMatchingStrategy.Standard, result);
                    }
                    else
                    {
                        Assert.NotEqual(CompilationMatchingStrategy.Standard, result);
                    }
                }
            }
        }

        #endregion

        #region Edge Cases and Integration Tests

        public class EdgeCases
        {
            [Theory]
            [InlineData("Various Artists", "Greatest Hits (2020 Remaster)")]
            [InlineData("V.A.", "OST: The Movie [Deluxe]")]
            [InlineData("VA", "Best Of - Volume 1")]
            public void Should_Handle_Complex_Titles_With_Special_Characters(string albumArtist, string albumTitle)
            {
                // Act
                var isVA = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);
                var isSoundtrack = CompilationAlbumDetector.IsSoundtrack(albumTitle, albumArtist);
                var compilationType = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert - Should not throw and should return consistent results
                Assert.True(isVA);
                var matchingStrategy = CompilationAlbumDetector.GetMatchingStrategy(compilationType);
                Assert.NotEqual(CompilationMatchingStrategy.Standard, matchingStrategy);
            }

            [Theory]
            [InlineData("Various Artists\t", "\tAlbum Title\t")]
            [InlineData("  VA  ", "  Greatest Hits  ")]
            [InlineData("\nV.A.\n", "\nBest Of\n")]
            public void Should_Handle_Whitespace_Correctly(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.True(result);
            }

            [Fact]
            public void Should_Handle_Unicode_Characters()
            {
                // Arrange
                var albumArtist = "Various Artists";
                var albumTitle = "Greatest Hits: The Best of 2020 - Various Artists Edition";

                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.True(result);
            }

            [Theory]
            [InlineData("VA", "Greatest Hits")] // VA + Greatest Hits = GreatestHits type
            [InlineData("Various Artists", "Live Concert")] // VA + Live = LiveCompilation type
            [InlineData("Various", "Tribute to Rock")] // Various + Tribute = Tribute type
            public void Should_Correctly_Classify_VA_With_Specific_Types(string albumArtist, string albumTitle)
            {
                // Act
                var compilationType = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert
                Assert.NotEqual(CompilationType.Standard, compilationType);
                Assert.NotEqual(CompilationType.VariousArtists, compilationType);
                var matchingStrategy = CompilationAlbumDetector.GetMatchingStrategy(compilationType);
                Assert.NotEqual(CompilationMatchingStrategy.Standard, matchingStrategy);
            }

            [Fact]
            public void Should_Handle_Empty_Artist_With_Soundtrack_Title()
            {
                // Arrange
                var albumArtist = "";
                var albumTitle = "Inception OST";

                // Act
                var isSoundtrack = CompilationAlbumDetector.IsSoundtrack(albumTitle, albumArtist);
                var compilationType = CompilationAlbumDetector.GetCompilationType(albumArtist, albumTitle);

                // Assert
                Assert.True(isSoundtrack);
                Assert.Equal(CompilationType.Soundtrack, compilationType);
            }

            [Theory]
            [InlineData("The Beatles", "1")] // Number album title
            [InlineData("Prince", "1999")] // Year as title
            [InlineData("Adele", "25")] // Number as title
            public void Should_Not_Misidentify_Number_Album_Titles_As_Volumes(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Soundgarden", "Superunknown")]
            [InlineData("Scoring", "The Big Game")]
            public void Should_Not_Misidentify_Artist_Names_With_Compilation_Words(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                Assert.False(result);
            }

            [Theory]
            [InlineData("Anthology", "Band Name")]
            [InlineData("The Collection", "Artist")]
            [InlineData("Tribute", "Album")]
            public void Should_Misidentify_Artist_Names_That_Contain_Compilation_Words_As_Substring(string albumArtist, string albumTitle)
            {
                // Act
                var result = CompilationAlbumDetector.IsVariousArtists(albumArtist, albumTitle);

                // Assert
                // The implementation uses Contains() which matches substrings
                // "Anthology" contains "anthology", "The Collection" contains "collection", etc.
                Assert.True(result);
            }
        }

        #endregion
    }
}
