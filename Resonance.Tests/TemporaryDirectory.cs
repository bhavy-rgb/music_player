namespace Resonance.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Resonance.Tests", Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Root);
    public string FilePath(string name) => Path.Combine(Root, name);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
