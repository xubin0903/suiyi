using System.Text;
using Suiyi.Core.Settings;

namespace Suiyi.Core.Tests.Settings;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static Action<Stream> Text(string text) => s => s.Write(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void CreatesNewFileAndDirectory()
    {
        var path = Path.Combine(_dir.Path, "a", "b", "settings.json");

        AtomicFile.Write(path, Text("new"));

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void ReplacesExisting()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, "old");

        AtomicFile.Write(path, Text("new content"));

        Assert.Equal("new content", File.ReadAllText(path));
        Assert.Equal(["settings.json"], _dir.Entries());
    }

    [Fact]
    public void FailureMidWrite_KeepsOldFileAndCleansTemp()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, "old");

        var ex = Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, s =>
        {
            s.Write(Encoding.UTF8.GetBytes("{ \"half"));
            throw new InvalidOperationException("断电");
        }));

        Assert.Equal("断电", ex.Message);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal(["settings.json"], _dir.Entries());
    }

    [Fact]
    public void FailureMidWrite_NoFileCreatedWhenAbsent()
    {
        var path = _dir.File("settings.json");

        Assert.Throws<IOException>(() => AtomicFile.Write(path, _ => throw new IOException("磁盘满")));

        Assert.Empty(_dir.Entries());
    }

    [Fact]
    public void Arguments_Validated()
    {
        Assert.Throws<ArgumentException>(() => AtomicFile.Write(" ", Text("x")));
        Assert.Throws<ArgumentNullException>(() => AtomicFile.Write(_dir.File("x"), null!));
    }
}
