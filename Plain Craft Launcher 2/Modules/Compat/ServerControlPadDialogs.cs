using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PCL;

namespace PCL.Core.UI.Dialogs;

/// <summary>
///     对话框按钮定义。
/// </summary>
public sealed class DialogButton
{
    /// <summary>按钮显示文本。</summary>
    public string Text { get; init; } = "";

    public static DialogButton Create(string text) => new() { Text = text };
}

/// <summary>
///     按钮组合的构造入口。
/// </summary>
public static class DialogButtons
{
    public static List<DialogButton> OkCancel { get; } =
    [
        DialogButton.Create("OK"),
        DialogButton.Create("Cancel")
    ];

    public static List<DialogButton> From(params DialogButton[] buttons) => [..buttons];
}

/// <summary>
///     下拉/单选选项。
/// </summary>
public sealed class DialogOption
{
    public string Text { get; init; } = "";
    public object? Tag { get; init; }
    public bool IsChecked { get; init; }
}

/// <summary>
///     对话框请求基类。
/// </summary>
public abstract class DialogRequest
{
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public IReadOnlyList<DialogButton> Buttons { get; init; } = DialogButtons.OkCancel;
    public bool IsWarn { get; init; }
    public bool ForceWait { get; init; } = true;

    internal string Button1Text => Buttons.Count > 0 ? Buttons[0].Text : "OK";
    internal string Button2Text => Buttons.Count > 1 ? Buttons[1].Text : "";
    internal string Button3Text => Buttons.Count > 2 ? Buttons[2].Text : "";
}

/// <summary>纯文本提示对话框。</summary>
public sealed class TextDialogRequest : DialogRequest;

/// <summary>文本输入对话框。</summary>
public sealed class InputDialogRequest : DialogRequest
{
    /// <summary>输入框默认值。</summary>
    public string DefaultValue { get; init; } = "";

    /// <summary>输入框占位提示。</summary>
    public string HintText { get; init; } = "";
}

/// <summary>单选对话框。</summary>
public sealed class SelectDialogRequest : DialogRequest
{
    public IReadOnlyList<DialogOption> Options { get; init; } = [];
}

/// <summary>
///     对话框结果。Button 为 1 起始的按钮序号（1 = 第一个按钮）。
/// </summary>
public sealed class DialogResult
{
    public int Button { get; init; }
    public int SelectedIndex { get; init; } = -1;
    public string? InputText { get; init; }
}

/// <summary>
///     对话框服务：把通用的对话框请求映射到启动器既有的消息框实现。
/// </summary>
public sealed class DialogService
{
    private static readonly DialogService InstanceInternal = new();

    public static DialogService Instance => InstanceInternal;

    /// <summary>
    ///     显示对话框。必须在 UI 线程调用（启动器消息框是模态阻塞的）。
    /// </summary>
    public Task<DialogResult> ShowAsync(DialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 保证在 UI 线程执行
        if (!Application.Current.Dispatcher.CheckAccess())
            return Application.Current.Dispatcher.Invoke(() => ShowAsync(request));

        return Task.FromResult(ShowCore(request));
    }

    private static DialogResult ShowCore(DialogRequest request)
    {
        switch (request)
        {
            case InputDialogRequest input:
            {
                var value = ModMain.MyMsgBoxInput(input.Title, input.Content, input.DefaultValue,
                    hintText: input.HintText, button1: input.Button1Text, button2: input.Button2Text,
                    isWarn: input.IsWarn);
                var cancelled = string.IsNullOrEmpty(value);
                return new DialogResult { Button = cancelled ? 2 : 1, InputText = cancelled ? null : value };
            }
            case SelectDialogRequest select:
            {
                var radios = select.Options.Select(option =>
                {
                    var radio = new MyRadioBox { Text = option.Text, Tag = option.Tag };
                    if (option.IsChecked) radio.Checked = true;
                    return (IMyRadio)radio;
                }).ToList();
                var index = ModMain.MyMsgBoxSelect(radios, select.Title, select.Button1Text, select.Button2Text,
                    select.IsWarn);
                return index is null
                    ? new DialogResult { Button = 2, SelectedIndex = -1 }
                    : new DialogResult { Button = 1, SelectedIndex = index.Value };
            }
            default:
            {
                var choice = ModMain.MyMsgBox(request.Content, request.Title, request.Button1Text,
                    request.Button2Text, request.Button3Text, isWarn: request.IsWarn);
                return new DialogResult { Button = choice };
            }
        }
    }
}
