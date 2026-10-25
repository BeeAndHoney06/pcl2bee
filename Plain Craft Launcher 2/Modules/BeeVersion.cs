using PCL.Core.App;

namespace PCL;

/// <summary>
///     PCL2 蜜蜂版的版本信息。
///     数值全部取自 metadata.json（与更新检查、启动参数共用同一来源），
///     发版时只需修改 metadata.json 的 base / code 字段，无需再改这里。
/// </summary>
public static class BeeVersion
{
    /// <summary>展示版本名，如「Alpha 1.0.2」（"Alpha" 前缀 + metadata 的 base/suffix）。</summary>
    public static string Name => $"Alpha {Basics.VersionName}";

    /// <summary>内部版本号（build 值），与更新检查使用的 <see cref="Basics.VersionCode"/> 完全一致。</summary>
    public static int Code => Basics.VersionCode;
}
