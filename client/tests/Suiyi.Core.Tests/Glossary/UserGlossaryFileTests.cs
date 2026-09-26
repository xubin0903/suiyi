using System.Text;
using Suiyi.Core.Glossary;

namespace Suiyi.Core.Tests.Glossary;

public sealed class UserGlossaryFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "suiyi-glossary-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void ResolvePath_IsGlossaryTsvInSettingsDirectory()
    {
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "glossary.tsv")), UserGlossaryFile.ResolvePath(_dir));
    }

    [Fact]
    public void EnsureExists_CreatesTemplateUtf8WithoutBomAndLf()
    {
        var path = UserGlossaryFile.ResolvePath(_dir);

        Assert.True(UserGlossaryFile.EnsureExists(path));

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal(UserGlossaryFile.Template, text);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_IsAllCommentsSoItAddsNoEntries()
    {
        var lines = UserGlossaryFile.Template.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines, l => Assert.StartsWith("#", l, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains('\t', StringComparison.Ordinal)); // 示例用真正的 Tab
        Assert.Contains("en-zh", UserGlossaryFile.Template, StringComparison.Ordinal);
        Assert.Contains("zh-en", UserGlossaryFile.Template, StringComparison.Ordinal);
        Assert.Contains("# k8s\tKubernetes\n", UserGlossaryFile.Template, StringComparison.Ordinal); // 两列都不含中文的行有效（#83 更新）
        Assert.Contains("两列都不含中文", UserGlossaryFile.Template, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureExists_DoesNotOverwrite()
    {
        Directory.CreateDirectory(_dir);
        var path = UserGlossaryFile.ResolvePath(_dir);
        File.WriteAllText(path, "Kubernetes\tK8s\n");

        Assert.False(UserGlossaryFile.EnsureExists(path));
        Assert.Equal("Kubernetes\tK8s\n", File.ReadAllText(path));
    }

    [Fact]
    public void Editor_IsAlwaysNotepad()
    {
        var path = Path.Combine(_dir, "带 空格", "glossary.tsv");

        var info = UserGlossaryFile.EditorStartInfo(path);

        Assert.Equal("notepad.exe", info.FileName);
        Assert.False(info.UseShellExecute); // 不走 .tsv 的默认程序（Excel 会改写 Tab 和编码）
        Assert.Equal([path], info.ArgumentList);
        Assert.Contains("记事本", UserGlossaryFile.Template, StringComparison.Ordinal);
        Assert.Contains("Excel", UserGlossaryFile.Template, StringComparison.Ordinal);
    }

    [Fact]
    public void IsMismatch()
    {
        var path = UserGlossaryFile.ResolvePath(_dir);

        Assert.False(UserGlossaryFile.IsMismatch(path, null));
        Assert.False(UserGlossaryFile.IsMismatch(path, " "));
        Assert.False(UserGlossaryFile.IsMismatch(path, path));
        Assert.True(UserGlossaryFile.IsMismatch(path, Path.Combine(_dir, "other.tsv")));
        Assert.Equal(!OperatingSystem.IsWindows(), UserGlossaryFile.IsMismatch(path, path.ToUpperInvariant()));
    }
}
