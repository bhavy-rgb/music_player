using System.Net;
using System.Text;
using Resonance.Core;
using Xunit;

namespace Resonance.Tests;

public sealed class GenericImporterTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void M3uExtinfLabelsAreAppliedOnlyToTheNextEntry(string newline)
    {
        using var temp = new TemporaryDirectory();
        var text = string.Join(newline, "#EXTM3U", "#EXTINF:180,Beyoncé - Déjà Vu", "first.mp3", "second.flac");
        var items = GenericImporter.Parse(text, temp.Root);
        Assert.Equal(2, items.Count);
        Assert.Equal("Beyoncé", items[0].ExternalArtist);
        Assert.Equal("Déjà Vu", items[0].ExternalTitle);
        Assert.Equal(temp.FilePath("first.mp3"), items[0].LocalPath);
        Assert.Equal("second", items[1].ExternalTitle);
        Assert.Equal("", items[1].ExternalArtist);
        Assert.Equal(new[] { 0, 1 }, items.Select(i => i.Position));
        Assert.All(items, item => { Assert.Null(item.TrackId); Assert.Equal(MatchStatus.Link, item.MatchStatus); });
        // EXTINF duration is currently not imported; do not promise unsupported metadata.
        Assert.Equal(0, items[0].DurationMs);
    }

    [Fact]
    public void PlsOrdersEntriesNumericallyAndReadsCaseInsensitiveLabels()
    {
        using var temp = new TemporaryDirectory();
        var items = GenericImporter.Parse("[playlist]\nFile10=ten.mp3\nTitle10=Artist - Ten\nfile2=two.mp3\ntitle2=Band - Two\nNumberOfEntries=2\nVersion=2", temp.Root);
        Assert.Equal(new[] { "Two", "Ten" }, items.Select(i => i.ExternalTitle));
        Assert.Equal(new[] { "Band", "Artist" }, items.Select(i => i.ExternalArtist));
        Assert.Equal(temp.FilePath("two.mp3"), items[0].LocalPath);
    }

    [Fact]
    public void PlainLinesKeepArtistAndTitleWithoutInventingLocalPaths()
    {
        var items = GenericImporter.Parse(" Artist - Title - Live \n#comment\n;comment\nInstrumental");
        Assert.Equal(2, items.Count);
        Assert.Equal("Artist", items[0].ExternalArtist);
        Assert.Equal("Title - Live", items[0].ExternalTitle);
        Assert.Equal("Instrumental", items[1].ExternalTitle);
        Assert.All(items, item => Assert.Null(item.LocalPath));
    }

    [Fact]
    public void BomIsRemovedBeforeM3uHeaderAndPlainText()
    {
        Assert.Equal("Artist", Assert.Single(GenericImporter.Parse("\uFEFFArtist - Song")).ExternalArtist);
        using var temp = new TemporaryDirectory();
        Assert.Equal("Song", Assert.Single(GenericImporter.Parse("\uFEFF#EXTM3U\n#EXTINF:3,Artist - Song\nsong.wav", temp.Root)).ExternalTitle);
    }

    [Fact]
    public void ResolvesRelativeAbsoluteAndFileUriPaths()
    {
        using var temp = new TemporaryDirectory();
        var absolute = temp.FilePath("absolute.mp3");
        var items = GenericImporter.Parse($"sub/song.mp3\n{absolute}\n{new Uri(absolute).AbsoluteUri}", temp.Root);
        Assert.Equal(temp.FilePath(Path.Combine("sub", "song.mp3")), items[0].LocalPath);
        Assert.Equal(absolute, items[1].LocalPath);
        Assert.Equal(absolute, items[2].LocalPath);
    }

    [Fact]
    public void RemoteRelativeEntriesRemainLinks()
    {
        var item = Assert.Single(GenericImporter.Parse("#EXTM3U\nsub/song.mp3", baseUri: new Uri("https://example.test/list.m3u")));
        Assert.Null(item.LocalPath);
        Assert.Equal("https://example.test/sub/song.mp3", item.ExternalUrl);
        Assert.Equal(MatchStatus.Link, item.MatchStatus);
    }

    [Theory]
    [InlineData("../escape.mp3")]
    [InlineData("sub/../../escape.mp3")]
    public void RejectsEscapingRelativePaths(string path)
    {
        using var temp = new TemporaryDirectory();
        Assert.Throws<InvalidDataException>(() => GenericImporter.Parse(path, temp.Root));
    }

    [Fact]
    public void ByteLimitUsesUtf8BytesAndIncludesBoundary()
    {
        // A comment avoids the separate 4096-character metadata limit.
        Assert.Empty(GenericImporter.Parse("#" + new string('a', InputValidation.MaxBytes - 1)));
        Assert.Throws<InvalidDataException>(() => GenericImporter.Parse("#" + new string('a', InputValidation.MaxBytes)));
        Assert.Throws<InvalidDataException>(() => GenericImporter.Parse("#" + new string('é', InputValidation.MaxBytes / 2)));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void LineLimitIncludesTwentyThousandLogicalLines(string newline)
    {
        var input = string.Join(newline, Enumerable.Repeat("Artist - Song", 20000));
        Assert.Equal(20000, GenericImporter.Parse(input).Count);
        Assert.Throws<InvalidDataException>(() => GenericImporter.Parse(input + newline + "one too many"));
        Assert.Throws<InvalidDataException>(() => GenericImporter.Parse(new string('\n', 20000)));
    }

    [Fact]
    public async Task ResolveReadsBomFileAndPreservesSource()
    {
        using var temp = new TemporaryDirectory(); using var http = new HttpClient(new NoNetworkHandler());
        var path = temp.FilePath("mix.m3u8");
        await File.WriteAllTextAsync(path, "#EXTM3U\n#EXTINF:1,Artist - Song\nsong.mp3", new UTF8Encoding(true));
        var result = await new GenericImporter(http).ResolveAsync(path, default);
        Assert.Equal("mix", result.Name); Assert.Equal("Generic", result.Provider); Assert.Equal(path, result.SourceUrl);
        Assert.Equal(temp.FilePath("song.mp3"), Assert.Single(result.Items).LocalPath);
    }

    [Fact]
    public async Task ResolveRejectsOversizedFilesAndWrongExtensions()
    {
        using var temp = new TemporaryDirectory(); using var http = new HttpClient(new NoNetworkHandler());
        var importer = new GenericImporter(http);
        var oversized = temp.FilePath("large.m3u");
        using (var file = File.Create(oversized)) file.SetLength(InputValidation.MaxBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ResolveAsync(oversized, default));
        var wrong = temp.FilePath("playlist.exe"); await File.WriteAllTextAsync(wrong, "Artist - Song");
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ResolveAsync(wrong, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ResolveAsync("#comments only", default));
    }

    [Theory]
    [InlineData("http://example.test/a.m3u")]
    [InlineData("https://127.0.0.1/a.m3u")]
    [InlineData("https://example.test:444/a.m3u")]
    [InlineData("https://user@example.test/a.m3u")]
    [InlineData("https://example.test/a.exe")]
    public async Task RejectsUnsafeRemotePlaylistRequestsBeforeNetwork(string url)
    {
        using var http = new HttpClient(new NoNetworkHandler());
        await Assert.ThrowsAsync<InvalidDataException>(() => new GenericImporter(http).ResolveAsync(url, default));
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tests must not contact the network.");
    }
}
