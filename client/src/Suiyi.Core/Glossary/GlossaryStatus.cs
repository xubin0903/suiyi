namespace Suiyi.Core.Glossary;

/// <summary>服务报告的术语表加载状态（<c>/health</c> 或 <c>POST /glossary/reload</c> 的 <c>glossary_*</c> 字段）。</summary>
/// <param name="ServerEnabled">服务端默认是否开启（<c>glossary_enabled</c>）。</param>
/// <param name="BuiltinEntries">内置条数（<c>glossary_builtin_entries</c>，按方向展开）。</param>
/// <param name="UserEntries">当前生效的用户条目数（<c>glossary_user_entries</c>）。</param>
/// <param name="UserPath">实际使用的用户术语表路径（<c>glossary_user_path</c>）。</param>
/// <param name="Error">文件级错误（<c>glossary_error</c>），没有时为 <see langword="null"/>。</param>
/// <param name="Warnings">行级问题（<c>glossary_warnings</c>，最多 20 条）。</param>
public sealed record GlossaryStatus(
    bool? ServerEnabled,
    int BuiltinEntries,
    int UserEntries,
    string? UserPath,
    string? Error,
    IReadOnlyList<string> Warnings)
{
    /// <summary>是否有文件级错误。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>值相等（含警告列表逐项比较），用于判断状态是否变化。</summary>
    public bool Equals(GlossaryStatus? other) =>
        other is not null
        && ServerEnabled == other.ServerEnabled
        && BuiltinEntries == other.BuiltinEntries
        && UserEntries == other.UserEntries
        && UserPath == other.UserPath
        && Error == other.Error
        && Warnings.SequenceEqual(other.Warnings);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ServerEnabled, BuiltinEntries, UserEntries, UserPath, Error, Warnings.Count);
}
