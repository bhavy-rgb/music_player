using Resonance.Core;
using Xunit;

namespace Resonance.Tests;

public sealed class LibraryScannerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedFileUsesCachedRecordWithoutReadingTags(bool explicitFiles)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("cached.mp3");
        // Deliberately invalid audio: reading tags would fail, proving the cache branch is used.
        await File.WriteAllTextAsync(path, "not an audio stream");
        var info = new FileInfo(path);
        var cached = new Track { Id = 3, FilePath = path, Title = "Cached title", FileSize = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks };
        var scanner = new LibraryScanner(temp.FilePath("art"));
        var result = explicitFiles
            ? await scanner.ScanFilesAsync([path], [cached], default)
            : await scanner.ScanAsync([temp.Root], [cached], null, default);
        Assert.Empty(result.Errors);
        Assert.Same(cached, Assert.Single(result.Tracks));
    }

    [Fact]
    public async Task ChangedFileIsRereadAndFailureDoesNotDeclareItMissing()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("changed.mp3"); await File.WriteAllTextAsync(path, "invalid audio");
        var cached = new Track { Id = 4, FilePath = path, FileSize = 0, ModifiedTicks = 0 };
        var result = await new LibraryScanner(temp.FilePath("art")).ScanAsync([temp.Root], [cached], null, default);
        Assert.Empty(result.Tracks); Assert.Single(result.Errors);
        Assert.Contains(path, result.SeenFiles); Assert.Contains(temp.Root, result.EnumeratedFolders);
    }

    [Fact]
    public async Task ReadsWaveDurationAndDeduplicatesOverlappingRoots()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.FilePath("tone.wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            const int dataSize = 16000;
            writer.Write("RIFF"u8); writer.Write(36 + dataSize); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000);
            writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(dataSize); writer.Write(new byte[dataSize]);
        }
        var result = await new LibraryScanner(temp.FilePath("art")).ScanAsync([temp.Root, temp.Root], [], null, default);
        var track = Assert.Single(result.Tracks);
        Assert.Empty(result.Errors); Assert.Equal("tone", track.Title);
        Assert.InRange(track.DurationMs, 999, 1001); Assert.Equal(16044, track.FileSize);
    }

    [Fact]
    public async Task MissingRootIsNotAuthoritativeForPruning()
    {
        using var temp = new TemporaryDirectory();
        var result = await new LibraryScanner(temp.FilePath("art")).ScanAsync([temp.FilePath("missing")], [], null, default);
        Assert.Empty(result.Tracks); Assert.Empty(result.EnumeratedFolders);
    }

    [Fact]
    public async Task HonorsPrecancelledScan()
    {
        using var temp = new TemporaryDirectory(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LibraryScanner(temp.FilePath("art")).ScanAsync([temp.Root], [], null, cancellation.Token));
    }
}
