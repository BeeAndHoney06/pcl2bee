// 兼容辅助：服务器管理模块（移植自 ErieneNeko/PCL-CE-ServerControlPad，Apache-2.0）
// 该分支提供 AniBeginControl / AniEndControl 作为 ModAnimation.AniControlEnabled 的封装。
namespace PCL;

public static partial class ModAnimation
{
    /// <summary>进入一段不希望被控件动画干扰的区间。</summary>
    public static void AniBeginControl() => AniControlEnabled += 1;

    /// <summary>结束该区间。</summary>
    public static void AniEndControl() => AniControlEnabled -= 1;
}
