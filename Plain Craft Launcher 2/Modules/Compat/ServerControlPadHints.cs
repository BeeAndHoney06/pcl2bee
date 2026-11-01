using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PCL.Core.UI.Hints;

/// <summary>
///     待显示的提示请求。
/// </summary>
/// <param name="Text">提示正文。</param>
/// <param name="Kind">提示类型。</param>
public sealed record HintRequest(string Text, HintType Kind);

/// <summary>
///     提示请求的构造入口。
/// </summary>
public static class Hints
{
    public static HintRequest Info(string text) => new(text, HintType.Info);

    public static HintRequest Success(string text) => new(text, HintType.Success);

    public static HintRequest Error(string text) => new(text, HintType.Error);

    public static HintRequest Warning(string text) => new(text, HintType.Warning);
}

/// <summary>
///     提示显示入口：转发到启动器既有的 HintService。
/// </summary>
public static class HintService
{
    public static void Show(HintRequest request)
    {
        if (request is null) return;
        PCL.HintService.Hint(request.Text, request.Kind);
    }
}
