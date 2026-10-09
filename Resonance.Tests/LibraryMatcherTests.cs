using Resonance.Core;
using Xunit;

namespace Resonance.Tests;

public sealed class LibraryMatcherTests
{
    private static readonly Track Song = new() { Id = 7, Title = "Song", Artist = "Artist", DurationMs = 180000 };
    private static readonly PlaylistItem Item = new() { ExternalTitle = "Song", ExternalArtist = "Artist", DurationMs = 180000 };

    [Fact]
    public void ExactMatchHasFullConfidence()
    {
        var result = LibraryMatcher.Match(Item, [Song]);
        Assert.Equal(1, LibraryMatcher.Similarity("Song", "Song"));
        Assert.Equal(1, result.Confidence);
        Assert.Equal(Song.Id, result.TrackId);
        Assert.Equal(MatchStatus.Local, result.MatchStatus);
    }

    [Theory]
    [InlineData("Beyoncé — Déjà Vu!", "beyonce deja vu")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData("Cafe\u0301", "cafe")]
    public void NormalizesAccentsCaseAndPunctuation(string input, string expected)
    {
        Assert.Equal(expected, LibraryMatcher.Normalize(input));
        Assert.Equal(1, LibraryMatcher.Similarity(input, expected));
    }

    [Theory]
    [InlineData("", "song")]
    [InlineData("song", "")]
    [InlineData("!!!", "...")]
    public void EmptyNormalizedTextDoesNotMatch(string left, string right) =>
        Assert.Equal(0, LibraryMatcher.Similarity(left, right));

    [Fact]
    public void TokenOrderDoesNotPreventMatch() =>
        Assert.Equal(1, LibraryMatcher.Similarity("Live Song", "Song Live"));

    [Fact]
    public void ThresholdIsInclusive()
    {
        var unknownArtist = Item with { ExternalArtist = "" };
        var at = LibraryMatcher.Match(unknownArtist, [Song], .88);
        var above = LibraryMatcher.Match(unknownArtist, [Song], Math.BitIncrement(.88));
        Assert.Equal(.88, at.Confidence);
        Assert.Equal(Song.Id, at.TrackId);
        Assert.Null(above.TrackId);
        Assert.Equal(MatchStatus.Link, above.MatchStatus);
    }

    [Theory]
    [InlineData(165000, 1)]
    [InlineData(195000, 1)]
    [InlineData(164999, .9)]
    [InlineData(195001, .9)]
    [InlineData(0, 1)]
    public void DurationPenaltyStartsBeyondFifteenSeconds(long duration, double confidence)
    {
        var result = LibraryMatcher.Match(Item with { DurationMs = duration }, [Song], .95);
        Assert.Equal(confidence, result.Confidence, 12);
        Assert.Equal(confidence >= .95, result.TrackId.HasValue);
    }

    [Fact]
    public void ChoosesBestCandidateNotFirst() =>
        Assert.Equal(Song.Id, LibraryMatcher.Match(Item, [Song with { Id = 8, Title = "Songs" }, Song]).TrackId);

    [Fact]
    public void RejectsWrongArtistEvenForExactTitle() =>
        Assert.Null(LibraryMatcher.Match(Item, [Song with { Artist = "ZZZZZZ" }]).TrackId);

    [Theory]
    [InlineData(null, MatchStatus.Link)]
    [InlineData("https://example.test/preview", MatchStatus.Preview)]
    public void EmptyLibraryClearsStaleMatchButPreservesReference(string? preview, MatchStatus status)
    {
        var result = LibraryMatcher.Match(Item with { TrackId = 99, MatchStatus = MatchStatus.Local, PreviewUrl = preview }, []);
        Assert.Null(result.TrackId);
        Assert.Equal(0, result.Confidence);
        Assert.Equal(status, result.MatchStatus);
    }

    [Fact]
    public void DirectPathMustBeIndexed()
    {
        var path = Path.GetFullPath("track.mp3");
        var item = new PlaylistItem { LocalPath = path };
        Assert.Null(LibraryMatcher.Match(item, []).TrackId);
        var result = LibraryMatcher.Match(item, [Song with { FilePath = path }]);
        Assert.Equal(Song.Id, result.TrackId);
        Assert.Equal(1, result.Confidence);
    }
}
