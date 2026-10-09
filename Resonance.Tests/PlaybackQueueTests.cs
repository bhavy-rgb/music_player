using Resonance.Core;
using Xunit;

namespace Resonance.Tests;

public sealed class PlaybackQueueTests
{
    private static Track[] Tracks(int count = 4) => Enumerable.Range(1, count)
        .Select(i => new Track { Id = i, Title = $"Track {i}" }).ToArray();

    [Fact]
    public void SequentialStopsAtEndWithoutLosingCurrent()
    {
        var tracks = Tracks(); var queue = new PlaybackQueue(); queue.Replace(tracks);
        Assert.Equal(tracks[0], queue.Current);
        foreach (var track in tracks.Skip(1)) Assert.Equal(track, queue.Next());
        Assert.Null(queue.Next(true));
        Assert.Equal(tracks[^1], queue.Current);
        Assert.Equal(tracks[^2], queue.Previous());
    }

    [Fact]
    public void RepeatAllWrapsAndPreviousReturnsLastTrack()
    {
        var tracks = Tracks(); var queue = new PlaybackQueue(); queue.Replace(tracks, 3);
        queue.SetMode(PlaybackMode.RepeatAll);
        Assert.Equal(tracks[0], queue.Next(true));
        Assert.Equal(tracks[3], queue.Previous());
    }

    [Fact]
    public void RepeatOneOnlyRepeatsAutomaticAdvance()
    {
        var tracks = Tracks(); var queue = new PlaybackQueue(); queue.Replace(tracks, 1);
        queue.SetMode(PlaybackMode.RepeatOne);
        Assert.Equal(tracks[1], queue.Next(true));
        Assert.Equal(tracks[2], queue.Next(false));
        Assert.Equal(tracks[1], queue.Previous());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(1234)]
    public void ShuffleVisitsEveryTrackAndAvoidsImmediateRepeatAcrossCycles(int seed)
    {
        var tracks = Tracks(12); var queue = new PlaybackQueue(new Random(seed)); queue.Replace(tracks);
        queue.SetMode(PlaybackMode.Shuffle);
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var visited = new HashSet<long> { queue.Current!.Id };
            for (var i = 1; i < tracks.Length; i++)
            {
                var previous = queue.Current;
                var next = queue.Next(true)!;
                Assert.NotEqual(previous, next);
                Assert.True(visited.Add(next.Id));
            }
            Assert.Equal(tracks.Length, visited.Count);
        }
    }

    [Fact]
    public void PreviousRetracesShuffleHistory()
    {
        var queue = new PlaybackQueue(new Random(42)); queue.Replace(Tracks(8)); queue.SetMode(PlaybackMode.Shuffle);
        var history = new List<Track> { queue.Current! };
        for (var i = 0; i < 5; i++) history.Add(queue.Next()!);
        foreach (var track in history.SkipLast(1).Reverse()) Assert.Equal(track, queue.Previous());
    }

    [Theory]
    [InlineData(1, 3, 1, 3)]
    [InlineData(0, 3, 2, 1)]
    [InlineData(3, 0, 2, 3)]
    [InlineData(0, 1, 3, 3)]
    [InlineData(2, 2, 2, 2)]
    [InlineData(-1, 2, 2, 2)]
    [InlineData(1, 4, 2, 2)]
    public void MovePreservesCurrentTrackAndCorrectIndex(int from, int to, int current, int expected)
    {
        var tracks = Tracks(); var queue = new PlaybackQueue(); queue.Replace(tracks, current);
        queue.Move(from, to);
        Assert.Equal(expected, queue.Index);
        Assert.Equal(tracks[current], queue.Current);
        Assert.Equal(4, queue.Items.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void SelectRecordsHistoryAndIgnoresInvalidIndices()
    {
        var tracks = Tracks(); var queue = new PlaybackQueue(); queue.Replace(tracks);
        queue.Select(3); queue.Select(-1); queue.Select(4);
        Assert.Equal(tracks[3], queue.Current);
        Assert.Equal(tracks[0], queue.Previous());
    }

    [Fact]
    public void ReplaceClearsOldHistoryAndClampsIndex()
    {
        var queue = new PlaybackQueue(); queue.Replace(Tracks()); queue.Next(); queue.Next();
        var replacement = Tracks(1); queue.Replace(replacement, 999);
        Assert.Equal(0, queue.Index);
        Assert.Equal(replacement[0], queue.Previous());
        queue.SetMode(PlaybackMode.Shuffle);
        Assert.Equal(replacement[0], queue.Next());
        queue.Replace([]);
        Assert.Equal(-1, queue.Index);
        Assert.Null(queue.Current); Assert.Null(queue.Next()); Assert.Null(queue.Previous());
    }

    [Fact]
    public void AddNextDoesNotChangeCurrent()
    {
        var queue = new PlaybackQueue(); queue.Replace(Tracks());
        var current = queue.Current; var added = new Track { Id = 99 };
        queue.Add(added, next: true);
        Assert.Equal(current, queue.Current);
        Assert.Equal(added, queue.Next());
    }
}
