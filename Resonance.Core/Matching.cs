using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Resonance.Core;

public static partial class LibraryMatcher
{
    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
    public static string Normalize(string input)
    {
        var decomposed = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return NonWord().Replace(plain, " ").Trim();
    }
    public static double Similarity(string left, string right)
    {
        left = Normalize(left); right = Normalize(right);
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        var sortedLeft = string.Join(' ', left.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Order());
        var sortedRight = string.Join(' ', right.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Order());
        return Math.Max(Ratio(left, right), Ratio(sortedLeft, sortedRight));
    }
    private static double Ratio(string a, string b)
    {
        // Limit untrusted metadata lengths to bound matching work.
        a = a[..Math.Min(256, a.Length)]; b = b[..Math.Min(256, b.Length)];
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (current, previous) = (previous, current);
        }
        return 1d - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
    public static PlaylistItem Match(PlaylistItem item, IReadOnlyList<Track> library, double threshold = .85)
    {
        if (item.LocalPath is not null)
        {
            var direct = library.FirstOrDefault(t => string.Equals(t.FilePath, item.LocalPath, StringComparison.OrdinalIgnoreCase));
            // A path is not marked Local until it is indexed, exists, and can be played.
            if (direct is not null) return item with { TrackId = direct.Id, MatchStatus = MatchStatus.Local, Confidence = 1 };
        }
        Track? best = null; double score = 0;
        foreach (var track in library)
        {
            var title = Similarity(item.ExternalTitle, track.Title);
            if (title < .72) continue;
            var artist = Similarity(item.ExternalArtist, track.Artist);
            var candidate = string.IsNullOrWhiteSpace(item.ExternalArtist) ? title * .88 : .65 * title + .35 * artist;
            if (artist < .6 && !string.IsNullOrWhiteSpace(item.ExternalArtist)) continue;
            if (item.DurationMs > 0 && Math.Abs(track.DurationMs - item.DurationMs) > 15000) candidate *= .9;
            if (candidate > score) { score = candidate; best = track; }
        }
        var matched = score >= threshold && best is not null;
        return item with { TrackId = matched ? best!.Id : null, MatchStatus = matched ? MatchStatus.Local : item.PreviewUrl is null ? MatchStatus.Link : MatchStatus.Preview, Confidence = score };
    }
}

public sealed class PlaybackQueue
{
    private readonly Random random;
    private readonly List<Track> items = [];
    private readonly Stack<int> history = new();
    private readonly List<int> remaining = [];
    public PlaybackQueue(Random? random = null) => this.random = random ?? Random.Shared;
    public IReadOnlyList<Track> Items => items;
    public int Index { get; private set; } = -1;
    public Track? Current => Index >= 0 && Index < items.Count ? items[Index] : null;
    public PlaybackMode Mode { get; private set; }
    public void SetMode(PlaybackMode mode) { Mode = mode; RebuildShuffle(); }
    public void Replace(IEnumerable<Track> tracks, int index = 0)
    {
        items.Clear(); items.AddRange(tracks); Index = items.Count == 0 ? -1 : Math.Clamp(index, 0, items.Count - 1); history.Clear(); RebuildShuffle();
    }
    public void Select(int index)
    {
        if (index < 0 || index >= items.Count) return;
        if (Index >= 0) history.Push(Index);
        Index = index; remaining.Remove(index);
    }
    public void Add(Track track, bool next = false)
    {
        var current = Index;
        items.Insert(next && Index >= 0 ? Index + 1 : items.Count, track);
        Index = current; history.Clear(); RebuildShuffle();
    }
    public void Move(int from, int to)
    {
        if (from < 0 || to < 0 || from >= items.Count || to >= items.Count || from == to) return;
        var track = items[from]; items.RemoveAt(from); items.Insert(to, track);
        if (Index == from) Index = to;
        else if (from < Index && to >= Index) Index--;
        else if (from > Index && to <= Index) Index++;
        history.Clear(); RebuildShuffle();
    }
    public Track? Next(bool automatic = false)
    {
        if (items.Count == 0) return null;
        if (automatic && Mode == PlaybackMode.RepeatOne && Current is not null) return Current;
        var next = Index + 1;
        if (Mode == PlaybackMode.Shuffle)
        {
            if (remaining.Count == 0) RebuildShuffle();
            if (remaining.Count == 0) return Current;
            next = remaining[^1]; remaining.RemoveAt(remaining.Count - 1);
        }
        else if (next >= items.Count)
        {
            if (Mode != PlaybackMode.RepeatAll) return null;
            next = 0;
        }
        if (Index >= 0) history.Push(Index);
        Index = next; return Current;
    }
    public Track? Previous()
    {
        if (items.Count == 0) return null;
        Index = history.TryPop(out var previous) ? previous : Math.Max(0, Index - 1);
        return Current;
    }
    private void RebuildShuffle()
    {
        remaining.Clear(); remaining.AddRange(Enumerable.Range(0, items.Count).Where(i => i != Index));
        for (var i = remaining.Count - 1; i > 0; i--) { var j = random.Next(i + 1); (remaining[i], remaining[j]) = (remaining[j], remaining[i]); }
    }
}
