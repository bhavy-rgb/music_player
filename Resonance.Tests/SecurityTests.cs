using Resonance.Core;
using Xunit;

namespace Resonance.Tests;

public class SecurityTests
{
    [Fact] public void RejectsTraversal() => Assert.Throws<InvalidDataException>(() => GenericImporter.Parse("../escape.mp3", Path.GetTempPath()));
    [Fact] public void RejectsOversizedText() => Assert.Throws<InvalidDataException>(() => GenericImporter.Parse(new string('a', InputValidation.MaxBytes + 1)));
    [Fact] public void RejectsExecutableEntry() => Assert.Throws<InvalidDataException>(() => GenericImporter.Parse("#EXTM3U\nrun.exe"));
    [Fact] public void RejectsUnexpectedSnapshotValues() => Assert.Throws<InvalidDataException>(() => InputValidation.Item(new() { Confidence = double.NaN }));
    [Theory]
    [InlineData("http://api.spotify.com/v1/tracks/1")]
    [InlineData("https://api.spotify.com.evil.test/v1/tracks/1")]
    [InlineData("https://api.spotify.com:444/v1/tracks/1")]
    public async Task RejectsUnsafeApiAddress(string url)
    {
        using var http = new HttpClient();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProviderApi(http).GetAsync(url, null, default));
    }
    [Fact] public async Task BoundsStreamWithoutContentLength()
    {
        using var stream = new MemoryStream(new byte[InputValidation.MaxBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => InputValidation.ReadBoundedAsync(stream, default));
    }
}
