// ─────────────────────────────────────────────────────────────────────────────
// 服务器管理模块：移植自 ErieneNeko/PCL-CE-ServerControlPad
//   https://github.com/ErieneNeko/PCL-CE-ServerControlPad
//   原作者：ErieneNeko    许可：Apache License 2.0
//   本文件在原作基础上做了适配修改（命名空间/本地化/对话框 API 对齐本项目）。
// ─────────────────────────────────────────────────────────────────────────────
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Server;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;

namespace PCL;

public partial class PageServerManageLocal
{
    private readonly ServerStore _store = ServerStore.Shared;
    private readonly ServerFileService _files = new();
    private readonly ServerBackupService _backups = new();

    private ServerInstance? _selected;
    private string _filePath = "";
    private string? _editingPath;
    private TextBox? _logBox;
    private MyTextBox? _commandBox;
    private TextBlock? _statusLabel;
    private MyButton? _btnStart;
    private MyButton? _btnStop;
    private MyButton? _btnRestart;
    private StackPanel? _filePanel;
    private StackPanel? _backupPanel;
    private TextBlock? _filePathLabel;
    private MyCard? _fileEditorCard;
    private TextBox? _fileEditorBox;
    private TextBlock? _fileEditorLabel;
    private MyHint? _eulaHint;
    private MyTextBox? _textMemory;
    private MyTextBox? _textMinMemory;
    private MyTextBox? _textPort;
    private MyTextBox? _textJava;
    private MyTextBox? _textArgs;

    public PageServerManageLocal()
    {
        InitializeComponent();
        PageEnter += () =>
        {
            if (_selected is null)
                ShowList();
            else
                RefreshActionState();
        };
        ServerProcessManager.Instance.StateChanged += OnServerStateChanged;
        ServerProcessManager.Instance.LogReceived += OnServerLog;
    }

    private void ShowList()
    {
        _selected = null;
        PanMain.Children.Clear();

        var headerContent = new StackPanel();
        headerContent.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Local.Description"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("ColorBrushGray2"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button(Lang.Text("Server.Local.Create"), (_, _) =>
        {
            Launcher.frmMain?.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadServer);
        }, true));
        actions.Children.Add(Button(Lang.Text("Server.Local.Refresh"), (_, _) => ShowList(), false, 10));
        // 把现有 MC 实例里的服务端接入统一管理（互补能力：实例式服务端 + 本模块的管理功能）
        actions.Children.Add(Button(Lang.Text("Server.Local.ImportFromInstance"),
            async (_, _) => await ImportFromInstancesAsync(), false, 10));
        actions.Children.Add(Button(Lang.Text("Server.Local.OpenFolder"), (_, _) =>
        {
            _store.EnsureRoot();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_store.Root}\"",
                UseShellExecute = true
            });
        }, false, 10));
        headerContent.Children.Add(actions);
        PanMain.Children.Add(Card(Lang.Text("Server.Local.Title"), headerContent));

        var servers = _store.List();
        if (servers.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            empty.Children.Add(new TextBlock
            {
                Text = Lang.Text("Server.Local.Empty"),
                Foreground = Brush("ColorBrushGray2"),
                TextWrapping = TextWrapping.Wrap
            });
            var emptyCreate = Button(Lang.Text("Server.Local.Create"), (_, _) =>
            {
                Launcher.frmMain?.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadServer);
            }, true);
            emptyCreate.MinWidth = 140;
            emptyCreate.Margin = new Thickness(0, 10, 0, 0);
            empty.Children.Add(emptyCreate);
            PanMain.Children.Add(Card("", empty));
            return;
        }

        foreach (var server in servers)
            PanMain.Children.Add(BuildServerRow(server));
    }

    private MyCard BuildServerRow(ServerInstance server)
    {
        server.RuntimeState = ServerProcessManager.Instance.GetState(server.Id);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = server.DisplayName,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("ColorBrush1")
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{KindText(server.Kind)} {server.MinecraftVersion}  ·  {StateText(server.RuntimeState)}  ·  " +
                   Lang.Text("Server.Local.Port", server.Port),
            FontSize = 12,
            Foreground = server.RuntimeState switch
            {
                ServerRuntimeState.Running => Brush("ColorBrush2"),
                ServerRuntimeState.Crashed => Brush("ColorBrushRedLight"),
                _ => Brush("ColorBrushGray2")
            },
            Margin = new Thickness(0, 4, 0, 0)
        });
        info.Children.Add(new TextBlock
        {
            Text = server.Directory,
            FontSize = 11,
            Foreground = Brush("ColorBrushGray3"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        grid.Children.Add(info);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var running = server.RuntimeState is ServerRuntimeState.Running or ServerRuntimeState.Starting;
        buttons.Children.Add(Button(running ? Lang.Text("Server.Action.Stop") : Lang.Text("Server.Action.Start"),
            async (_, _) =>
            {
                if (ServerProcessManager.Instance.IsRunning(server.Id))
                    await StopServerAsync(server);
                else
                    await StartServerAsync(server);
            }, true));
        buttons.Children.Add(Button(Lang.Text("Server.Action.Details"), (_, _) => ShowDetail(server), false, 10));
        buttons.Children.Add(Button(Lang.Text("Server.Action.Delete"), async (_, _) => await DeleteServerAsync(server),
            false, 10));
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var content = new Grid { Margin = new Thickness(15) };
        content.Children.Add(grid);
        return new MyCard { Margin = new Thickness(0, 0, 0, 10), Children = { content } };
    }

    private void ShowDetail(ServerInstance server)
    {
        _selected = server;
        _filePath = "";
        _editingPath = null;
        PanMain.Children.Clear();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = Button(Lang.Text("Server.Action.Back"), (_, _) => ShowList(), false);
        back.MinWidth = 80;
        header.Children.Add(back);
        var titlePanel = new StackPanel { Margin = new Thickness(14, 0, 10, 0) };
        titlePanel.Children.Add(new TextBlock
        {
            Text = server.DisplayName,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("ColorBrush1")
        });
        _statusLabel = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = Brush("ColorBrushGray2")
        };
        titlePanel.Children.Add(_statusLabel);
        Grid.SetColumn(titlePanel, 1);
        header.Children.Add(titlePanel);
        var openFolder = Button(Lang.Text("Server.Action.OpenFolder"), (_, _) => OpenFolder(server), false);
        Grid.SetColumn(openFolder, 2);
        header.Children.Add(openFolder);
        PanMain.Children.Add(Card("", header));

        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _btnStart = Button(Lang.Text("Server.Action.Start"), async (_, _) => await StartServerAsync(server), true);
        _btnStop = Button(Lang.Text("Server.Action.Stop"), async (_, _) => await StopServerAsync(server), false, 10);
        _btnRestart = Button(Lang.Text("Server.Action.Restart"), async (_, _) => await RestartServerAsync(server),
            false, 10);
        actionPanel.Children.Add(_btnStart);
        actionPanel.Children.Add(_btnStop);
        actionPanel.Children.Add(_btnRestart);
        actionPanel.Children.Add(Button(Lang.Text("Server.Action.Delete"), async (_, _) => await DeleteServerAsync(server),
            false, 10));
        PanMain.Children.Add(Card(Lang.Text("Server.Detail.Actions"), actionPanel));

        _eulaHint = new MyHint
        {
            Theme = MyHint.Themes.Yellow,
            Text = Lang.Text("Server.Eula.Warning"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        PanMain.Children.Add(_eulaHint);
        RefreshEulaHint(server);

        PanMain.Children.Add(BuildConsoleCard(server));
        PanMain.Children.Add(BuildConfigCard(server));
        PanMain.Children.Add(BuildFilesCard(server));
        PanMain.Children.Add(BuildBackupsCard(server));

        RefreshActionState();
        RefreshConfigFields(server);
        RefreshFiles();
        RefreshBackups();
        LoadLogHistory(server);
    }

    private MyCard BuildConsoleCard(ServerInstance server)
    {
        var panel = new StackPanel();
        _logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 220,
            MaxHeight = 320,
            Padding = new Thickness(10),
            BorderThickness = new Thickness(0),
            IsReadOnlyCaretVisible = false
        };
        _logBox.SetResourceReference(TextBox.BackgroundProperty, "ColorBrush8");
        _logBox.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1");
        panel.Children.Add(_logBox);
        var commandRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        commandRow.ColumnDefinitions.Add(new ColumnDefinition());
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _commandBox = new MyTextBox
        {
            Height = 31,
            HintText = Lang.Text("Server.Console.CommandHint"),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _commandBox.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter)
                await SendCommandAsync(server);
        };
        commandRow.Children.Add(_commandBox);
        var send = Button(Lang.Text("Server.Console.Send"), async (_, _) => await SendCommandAsync(server), true);
        Grid.SetColumn(send, 1);
        send.Margin = new Thickness(10, 0, 0, 0);
        commandRow.Children.Add(send);
        panel.Children.Add(commandRow);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        tools.Children.Add(Button(Lang.Text("Server.Console.Clear"), (_, _) =>
        {
            ServerProcessManager.Instance.ClearLogs(server.Id);
            if (_logBox is not null)
                _logBox.Clear();
        }));
        panel.Children.Add(tools);
        return Card(Lang.Text("Server.Console.Title"), panel);
    }

    private MyCard BuildConfigCard(ServerInstance server)
    {
        var panel = new StackPanel();
        AddField(panel, Lang.Text("Server.Config.Memory"), out _textMemory);
        AddField(panel, Lang.Text("Server.Config.MinMemory"), out _textMinMemory);
        AddField(panel, Lang.Text("Server.Config.Port"), out _textPort);
        AddField(panel, Lang.Text("Server.Config.Java"), out _textJava);
        AddField(panel, Lang.Text("Server.Config.Args"), out _textArgs);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Button(Lang.Text("Server.Config.Save"), (_, _) => SaveConfig(server), true));
        actions.Children.Add(Button(Lang.Text("Server.Config.BrowseJava"), (_, _) => BrowseJava(), false, 10));
        actions.Children.Add(Button(Lang.Text("Server.Config.EditProperties"),
            (_, _) => EditFile(server, "server.properties"), false, 10));
        panel.Children.Add(actions);

        var note = new TextBlock
        {
            Text = Lang.Text("Server.Config.Note"),
            FontSize = 11,
            Foreground = Brush("ColorBrushGray3"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(note);
        return Card(Lang.Text("Server.Config.Title"), panel);
    }

    private MyCard BuildFilesCard(ServerInstance server)
    {
        var panel = new StackPanel();
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition());
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _filePathLabel = new TextBlock
        {
            Text = "/",
            FontSize = 12,
            Foreground = Brush("ColorBrushGray2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        pathRow.Children.Add(_filePathLabel);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Button(Lang.Text("Server.Files.Up"), (_, _) => GoUp(), false));
        tools.Children.Add(Button(Lang.Text("Server.Files.Refresh"), (_, _) => RefreshFiles(), false, 8));
        tools.Children.Add(Button(Lang.Text("Server.Files.NewFolder"), async (_, _) => await NewFolderAsync(server),
            false, 8));
        tools.Children.Add(Button(Lang.Text("Server.Files.Upload"), (_, _) => UploadFile(server), false, 8));
        Grid.SetColumn(tools, 1);
        pathRow.Children.Add(tools);
        panel.Children.Add(pathRow);

        _filePanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_filePanel);

        _fileEditorCard = Card(Lang.Text("Server.Files.Editor"), BuildFileEditor(server));
        _fileEditorCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_fileEditorCard);
        return Card(Lang.Text("Server.Files.Title"), panel);
    }

    private UIElement BuildFileEditor(ServerInstance server)
    {
        var panel = new StackPanel();
        _fileEditorLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush("ColorBrushGray2"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(_fileEditorLabel);
        _fileEditorBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 220,
            Padding = new Thickness(8)
        };
        _fileEditorBox.SetResourceReference(TextBox.BackgroundProperty, "ColorBrush8");
        _fileEditorBox.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1");
        panel.Children.Add(_fileEditorBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Button(Lang.Text("Server.Files.Save"), (_, _) => SaveEditingFile(server), true));
        actions.Children.Add(Button(Lang.Text("Server.Files.Cancel"), (_, _) => CloseFileEditor(), false, 10));
        actions.Children.Add(Button(Lang.Text("Server.Files.Rename"), async (_, _) => await RenameEditingFile(server),
            false, 10));
        actions.Children.Add(Button(Lang.Text("Server.Files.Delete"), async (_, _) => await DeleteEditingFile(server),
            false, 10));
        panel.Children.Add(actions);
        return panel;
    }

    private MyCard BuildBackupsCard(ServerInstance server)
    {
        var panel = new StackPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button(Lang.Text("Server.Backup.Create"), async (_, _) => await CreateBackupAsync(server),
            true));
        actions.Children.Add(Button(Lang.Text("Server.Backup.Refresh"), (_, _) => RefreshBackups(), false, 10));
        actions.Children.Add(Button(Lang.Text("Server.Backup.OpenFolder"), (_, _) =>
        {
            var directory = _backups.BackupDirectory(server.Directory);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
        }, false, 10));
        panel.Children.Add(actions);
        panel.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Backup.Note"),
            FontSize = 11,
            Foreground = Brush("ColorBrushGray3"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 6)
        });
        _backupPanel = new StackPanel();
        panel.Children.Add(_backupPanel);
        return Card(Lang.Text("Server.Backup.Title"), panel);
    }

    private void AddField(StackPanel parent, string label, out MyTextBox textBox)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brush("ColorBrushGray2"),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(text);
        textBox = new MyTextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetColumn(textBox, 1);
        row.Children.Add(textBox);
        parent.Children.Add(row);
    }

    private void RefreshConfigFields(ServerInstance server)
    {
        if (_textMemory is not null) _textMemory.Text = server.MemoryMb.ToString();
        if (_textMinMemory is not null) _textMinMemory.Text = server.MinMemoryMb.ToString();
        if (_textPort is not null) _textPort.Text = server.Port.ToString();
        if (_textJava is not null) _textJava.Text = server.JavaPath;
        if (_textArgs is not null) _textArgs.Text = server.ExtraArgs;
    }

    private void SaveConfig(ServerInstance server)
    {
        if (!int.TryParse(_textMemory?.Text.Trim(), out var memory) || memory is < 512 or > 65536)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Config.Error.Memory")));
            return;
        }

        if (!int.TryParse(_textMinMemory?.Text.Trim(), out var minMemory) || minMemory < 256 ||
            minMemory > memory)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Config.Error.MinMemory")));
            return;
        }

        if (!int.TryParse(_textPort?.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Config.Error.Port")));
            return;
        }

        server.MemoryMb = memory;
        server.MinMemoryMb = minMemory;
        server.Port = port;
        server.JavaPath = _textJava?.Text.Trim() ?? "";
        server.ExtraArgs = _textArgs?.Text.Trim() ?? "";
        try
        {
            _store.Save(server);
            ServerProvisioner.WriteServerProperties(server.Directory, server);
            ServerProvisioner.WriteStartScript(server.Directory, server);
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Config.Saved")));
            RefreshActionState();
        }
        catch (Exception ex)
        {
            Common.Log(ex, "保存服务器配置失败", Common.LogLevel.Feedback);
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    private void BrowseJava()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Lang.Text("Server.Config.JavaFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true && _textJava is not null)
            _textJava.Text = dialog.FileName;
    }

    private void RefreshActionState()
    {
        if (_selected is null)
            return;
        var state = ServerProcessManager.Instance.GetState(_selected.Id);
        _selected.RuntimeState = state;
        if (_statusLabel is not null)
        {
            _statusLabel.Text = StateText(state) + "  ·  " + _selected.Directory;
            _statusLabel.SetResourceReference(TextBlock.ForegroundProperty, state switch
            {
                ServerRuntimeState.Running => "ColorBrush2",
                ServerRuntimeState.Crashed => "ColorBrushRedLight",
                _ => "ColorBrushGray2"
            });
        }

        if (_btnStart is not null)
            _btnStart.IsEnabled = state is not (ServerRuntimeState.Running or ServerRuntimeState.Starting or
                ServerRuntimeState.Stopping);
        if (_btnStop is not null)
            _btnStop.IsEnabled = state is ServerRuntimeState.Running or ServerRuntimeState.Starting;
        if (_btnRestart is not null)
            _btnRestart.IsEnabled = state is not (ServerRuntimeState.Starting or ServerRuntimeState.Stopping);
        RefreshEulaHint(_selected);
    }

    private void RefreshEulaHint(ServerInstance server)
    {
        if (_eulaHint is null)
            return;
        server.EulaAccepted = ServerStore.IsEulaAccepted(server.Directory);
        _eulaHint.Visibility = server.EulaAccepted ? Visibility.Collapsed : Visibility.Visible;
        _eulaHint.Text = Lang.Text("Server.Eula.Warning");
    }

    private async Task StartServerAsync(ServerInstance server)
    {
        try
        {
            if (!ServerStore.IsEulaAccepted(server.Directory))
            {
                var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
                {
                    Title = Lang.Text("Server.Eula.Title"),
                    Content = Lang.Text("Server.Eula.Content"),
                    Buttons = DialogButtons.From(
                        DialogButton.Create(Lang.Text("Server.Eula.Agree")),
                        DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                    IsWarn = true,
                    ForceWait = true
                });
                if (result.Button != 1)
                {
                    PCL.Core.UI.Hints.HintService.Show(Hints.Info(Lang.Text("Server.Eula.Cancelled")));
                    return;
                }

                ServerStore.AcceptEula(server.Directory, true);
                server.EulaAccepted = true;
                RefreshEulaHint(server);
            }

            PCL.Core.UI.Hints.HintService.Show(Hints.Info(Lang.Text("Server.Action.Starting", server.DisplayName)));
            await ServerProcessManager.Instance.StartAsync(server);
        }
        catch (EulaRequiredException)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Eula.Required")));
        }
        catch (Exception ex)
        {
            Common.Log(ex, "启动服务器失败", Common.LogLevel.Feedback);
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Action.StartFailed", ex.Message)));
        }
    }

    private async Task StopServerAsync(ServerInstance server)
    {
        try
        {
            await ServerProcessManager.Instance.StopAsync(server.Id);
        }
        catch (Exception ex)
        {
            Common.Log(ex, "停止服务器失败", Common.LogLevel.Feedback);
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Action.StopFailed", ex.Message)));
        }
    }

    private async Task RestartServerAsync(ServerInstance server)
    {
        try
        {
            await ServerProcessManager.Instance.RestartAsync(server);
        }
        catch (Exception ex)
        {
            Common.Log(ex, "重启服务器失败", Common.LogLevel.Feedback);
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Action.RestartFailed", ex.Message)));
        }
    }

    private async Task DeleteServerAsync(ServerInstance server)
    {
        var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
        {
            Title = Lang.Text("Server.Delete.Title"),
            Content = Lang.Text("Server.Delete.Content", server.DisplayName, server.Directory),
            Buttons = DialogButtons.From(
                DialogButton.Create(Lang.Text("Server.Action.Delete")),
                DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
            IsWarn = true,
            ForceWait = true
        });
        if (result.Button != 1)
            return;

        try
        {
            if (ServerProcessManager.Instance.IsRunning(server.Id))
                await ServerProcessManager.Instance.StopAsync(server.Id);
            _store.Delete(server.Id);
            if (_selected?.Id == server.Id)
                ShowList();
            else
                ShowList();
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Delete.Done")));
        }
        catch (Exception ex)
        {
            Common.Log(ex, "删除服务器失败", Common.LogLevel.Feedback);
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Delete.Failed", ex.Message)));
        }
    }

    private void OpenFolder(ServerInstance server)
    {
        if (!Directory.Exists(server.Directory))
            return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{server.Directory}\"",
            UseShellExecute = true
        });
    }

    private async Task SendCommandAsync(ServerInstance server)
    {
        if (_commandBox is null)
            return;
        var command = _commandBox.Text;
        try
        {
            await ServerProcessManager.Instance.SendCommandAsync(server.Id, command);
            _commandBox.Clear();
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    private void LoadLogHistory(ServerInstance server)
    {
        if (_logBox is null)
            return;
        _logBox.Clear();
        foreach (var line in ServerProcessManager.Instance.GetLogs(server.Id))
            _logBox.AppendText(line.Text + Environment.NewLine);
        _logBox.ScrollToEnd();
    }

    private void OnServerStateChanged(object? sender, ServerStateChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnServerStateChanged(sender, e));
            return;
        }

        if (_selected?.Id == e.ServerId)
            RefreshActionState();
    }

    private void OnServerLog(object? sender, ServerLogEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnServerLog(sender, e));
            return;
        }

        if (_selected?.Id != e.ServerId || _logBox is null)
            return;
        _logBox.AppendText(e.Line.Text + Environment.NewLine);
        if (_logBox.LineCount > 5000)
        {
            var lines = _logBox.Text.Split('\n');
            _logBox.Text = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 4000)));
        }

        _logBox.ScrollToEnd();
    }

    #region 文件

    private void RefreshFiles()
    {
        if (_filePanel is null)
            return;
        if (_selected is null)
            return;
        _filePanel.Children.Clear();
        if (_filePathLabel is not null)
            _filePathLabel.Text = "/" + _filePath.Replace('\\', '/');
        try
        {
            var entries = _files.List(_selected.Directory, _filePath);
            if (entries.Count == 0)
            {
                _filePanel.Children.Add(new TextBlock
                {
                    Text = Lang.Text("Server.Files.Empty"),
                    FontSize = 12,
                    Foreground = Brush("ColorBrushGray3"),
                    Margin = new Thickness(3, 6, 0, 6)
                });
                return;
            }

            foreach (var entry in entries)
            {
                var item = new MyListItem
                {
                    Title = entry.Name,
                    Info = entry.IsDirectory
                        ? Lang.Text("Server.Files.Folder")
                        : ServerBackupService.FormatSize(entry.Size) + "  ·  " +
                          entry.Modified.ToString("yyyy-MM-dd HH:mm"),
                    SvgIcon = entry.IsDirectory ? "lucide/folder" : "lucide/file",
                    Type = MyListItem.CheckType.Clickable,
                    Height = 40,
                    Margin = new Thickness(0, 0, 0, 2),
                    Tag = entry
                };
                item.Click += (_, _) =>
                {
                    if (entry.IsDirectory)
                    {
                        _filePath = Path.Combine(_filePath, entry.Name);
                        RefreshFiles();
                    }
                    else
                    {
                        EditFile(_selected, Path.Combine(_filePath, entry.Name));
                    }
                };
                _filePanel.Children.Add(item);
            }
        }
        catch (Exception ex)
        {
            _filePanel.Children.Add(new TextBlock
            {
                Text = ex.Message,
                Foreground = Brush("ColorBrushRedLight"),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private void GoUp()
    {
        if (string.IsNullOrEmpty(_filePath))
            return;
        _filePath = Path.GetDirectoryName(_filePath) ?? "";
        RefreshFiles();
    }

    private async Task NewFolderAsync(ServerInstance server)
    {
        var result = await DialogService.Instance.ShowAsync(new InputDialogRequest
        {
            Title = Lang.Text("Server.Files.NewFolder"),
            Content = Lang.Text("Server.Files.NewFolderPrompt"),
            Buttons = DialogButtons.OkCancel,
            ForceWait = true
        });
        if (result.Button != 1 || string.IsNullOrWhiteSpace(result.InputText))
            return;
        try
        {
            _files.CreateDirectory(server.Directory, Path.Combine(_filePath, result.InputText.Trim()));
            RefreshFiles();
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    private void UploadFile(ServerInstance server)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            _files.CopyIn(server.Directory, Path.Combine(_filePath, Path.GetFileName(dialog.FileName)),
                dialog.FileName);
            RefreshFiles();
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Files.Uploaded")));
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    private void EditFile(ServerInstance server, string relativePath)
    {
        try
        {
            var text = _files.ReadText(server.Directory, relativePath);
            _editingPath = relativePath;
            if (_fileEditorLabel is not null)
                _fileEditorLabel.Text = "/" + relativePath.Replace('\\', '/');
            if (_fileEditorBox is not null)
            {
                _fileEditorBox.Text = text;
                _fileEditorBox.Visibility = Visibility.Visible;
            }

            if (_fileEditorCard is not null)
            {
                _fileEditorCard.Visibility = Visibility.Visible;
                _fileEditorCard.BringIntoView();
            }
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    private void SaveEditingFile(ServerInstance server)
    {
        if (_editingPath is null || _fileEditorBox is null)
            return;
        try
        {
            _files.WriteText(server.Directory, _editingPath, _fileEditorBox.Text);
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Files.Saved")));
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    private void CloseFileEditor()
    {
        _editingPath = null;
        if (_fileEditorCard is not null)
            _fileEditorCard.Visibility = Visibility.Collapsed;
    }

    private async Task RenameEditingFile(ServerInstance server)
    {
        if (_editingPath is null)
            return;
        var result = await DialogService.Instance.ShowAsync(new InputDialogRequest
        {
            Title = Lang.Text("Server.Files.Rename"),
            Content = Lang.Text("Server.Files.RenamePrompt"),
            DefaultValue = Path.GetFileName(_editingPath),
            Buttons = DialogButtons.OkCancel,
            ForceWait = true
        });
        if (result.Button != 1 || string.IsNullOrWhiteSpace(result.InputText))
            return;
        try
        {
            _files.Rename(server.Directory, _editingPath, result.InputText.Trim());
            _editingPath = null;
            CloseFileEditor();
            RefreshFiles();
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    private async Task DeleteEditingFile(ServerInstance server)
    {
        if (_editingPath is null)
            return;
        var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
        {
            Title = Lang.Text("Server.Files.Delete"),
            Content = Lang.Text("Server.Files.DeletePrompt", _editingPath),
            Buttons = DialogButtons.From(
                DialogButton.Create(Lang.Text("Common.Action.Delete")),
                DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
            IsWarn = true,
            ForceWait = true
        });
        if (result.Button != 1)
            return;
        try
        {
            _files.Delete(server.Directory, _editingPath);
            _editingPath = null;
            CloseFileEditor();
            RefreshFiles();
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    #endregion

    #region 备份

    private void RefreshBackups()
    {
        if (_backupPanel is null || _selected is null)
            return;
        _backupPanel.Children.Clear();
        var backups = _backups.List(_selected.Directory);
        if (backups.Count == 0)
        {
            _backupPanel.Children.Add(new TextBlock
            {
                Text = Lang.Text("Server.Backup.Empty"),
                FontSize = 12,
                Foreground = Brush("ColorBrushGray3"),
                Margin = new Thickness(3, 6, 0, 6)
            });
            return;
        }

        foreach (var backup in backups)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = $"{backup.FileName}  ·  {ServerBackupService.FormatSize(backup.Size)}  ·  " +
                       backup.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                FontSize = 12,
                Foreground = Brush("ColorBrush1"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.Children.Add(label);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Button(Lang.Text("Server.Backup.Restore"),
                async (_, _) => await RestoreBackupAsync(backup), false));
            actions.Children.Add(Button(Lang.Text("Server.Backup.Delete"),
                async (_, _) => await DeleteBackupAsync(backup), false, 6));
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            _backupPanel.Children.Add(row);
        }
    }

    private async Task CreateBackupAsync(ServerInstance server)
    {
        try
        {
            var path = await Task.Run(() => _backups.Create(server.Directory));
            RefreshBackups();
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Backup.Created", Path.GetFileName(path))));
        }
        catch (Exception ex)
        {
            Common.Log(ex, "创建服务器备份失败", Common.LogLevel.Feedback);
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Backup.CreateFailed", ex.Message)));
        }
    }

    private async Task RestoreBackupAsync(ServerBackupEntry backup)
    {
        if (_selected is null)
            return;
        if (ServerProcessManager.Instance.IsRunning(_selected.Id))
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Backup.StopFirst")));
            return;
        }

        var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
        {
            Title = Lang.Text("Server.Backup.Restore"),
            Content = Lang.Text("Server.Backup.RestorePrompt", backup.FileName),
            Buttons = DialogButtons.From(
                DialogButton.Create(Lang.Text("Server.Backup.Restore")),
                DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
            IsWarn = true,
            ForceWait = true
        });
        if (result.Button != 1)
            return;

        try
        {
            var safety = await Task.Run(() => _backups.Restore(_selected.Directory, backup.FullPath));
            RefreshBackups();
            RefreshFiles();
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(
                Lang.Text("Server.Backup.Restored", Path.GetFileName(safety))));
        }
        catch (Exception ex)
        {
            Common.Log(ex, "恢复服务器备份失败", Common.LogLevel.Feedback);
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Backup.RestoreFailed", ex.Message)));
        }
    }

    private async Task DeleteBackupAsync(ServerBackupEntry backup)
    {
        if (_selected is null)
            return;
        var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
        {
            Title = Lang.Text("Server.Backup.Delete"),
            Content = Lang.Text("Server.Backup.DeletePrompt", backup.FileName),
            Buttons = DialogButtons.From(
                DialogButton.Create(Lang.Text("Common.Action.Delete")),
                DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
            IsWarn = true,
            ForceWait = true
        });
        if (result.Button != 1)
            return;
        try
        {
            _backups.Delete(_selected.Directory, backup.FullPath);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ex.Message));
        }
    }

    #endregion

    #region 控件工具

    private static MyCard Card(string title, UIElement content)
    {
        var panel = new StackPanel { Margin = new Thickness(20, string.IsNullOrEmpty(title) ? 15 : 40, 20, 15) };
        panel.Children.Add(content);
        return new MyCard { Title = title, Margin = new Thickness(0, 0, 0, 12), Children = { panel } };
    }

    private static MyButton Button(string text, MyButton.ClickEventHandler click, bool highlight = false,
        double marginLeft = 0)
    {
        var button = new MyButton
        {
            Text = text,
            Height = 35,
            MinHeight = 35,
            Padding = new Thickness(13, 0, 13, 0),
            Margin = new Thickness(marginLeft, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ColorType = highlight ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal
        };
        button.Click += click;
        return button;
    }

    private static Brush Brush(string key) =>
        (Brush)Application.Current.TryFindResource(key) ?? Brushes.Gray;

    private static string KindText(ServerKind kind) => kind switch
    {
        ServerKind.Vanilla => "Vanilla",
        ServerKind.Paper => "Paper",
        ServerKind.Purpur => "Purpur",
        ServerKind.Fabric => "Fabric",
        ServerKind.Forge => "Forge",
        ServerKind.NeoForge => "NeoForge",
        _ => kind.ToString()
    };

    private static string StateText(ServerRuntimeState state) => state switch
    {
        ServerRuntimeState.Running => Lang.Text("Server.State.Running"),
        ServerRuntimeState.Starting => Lang.Text("Server.State.Starting"),
        ServerRuntimeState.Stopping => Lang.Text("Server.State.Stopping"),
        ServerRuntimeState.Crashed => Lang.Text("Server.State.Crashed"),
        _ => Lang.Text("Server.State.Stopped")
    };

    #endregion

    /// <summary>
    ///     扫描 MC 实例中包含服务端核心的目录，并以「外部目录」方式接入服务器管理。
    /// </summary>
    private async Task ImportFromInstancesAsync()
    {
        List<(string Name, string Directory, string Jar)> candidates = [];
        foreach (var instance in ModServerInstance.ScanInstances())
        {
            var jar = ModServerInstance.FindJar(instance.PathInstance, ModServerInstance.ReadMeta(instance.PathInstance));
            if (string.IsNullOrWhiteSpace(jar)) continue;
            candidates.Add((instance.Name, instance.PathInstance, jar));
        }

        if (candidates.Count == 0)
        {
            PCL.Core.UI.Hints.HintService.Show(
                PCL.Core.UI.Hints.Hints.Info(Lang.Text("Server.Local.ImportNone")));
            return;
        }

        var radios = candidates.Select(candidate =>
            (IMyRadio)new MyRadioBox { Text = candidate.Name, Tag = candidate }).ToList();
        var index = ModMain.MyMsgBoxSelect(radios, Lang.Text("Server.Local.ImportTitle"),
            Lang.Text("Server.Local.ImportConfirm"));
        if (index is null) return;

        var picked = candidates[index.Value];
        try
        {
            var entry = new ServerInstance
            {
                Name = Path.GetFileName(picked.Directory),
                Kind = ServerKind.Vanilla,
                MinecraftVersion = ModBase.ReadIni(Path.Combine(picked.Directory, "PCL", "Setup.ini"), "Info", "Vanilla") ?? "",
                JarFile = Path.GetFileName(picked.Jar),
                LaunchMode = "jar",
                ExternalDirectory = picked.Directory
            };
            _store.Create(entry);
            PCL.Core.UI.Hints.HintService.Show(
                PCL.Core.UI.Hints.Hints.Success(Lang.Text("Server.Local.ImportDone", entry.Name)));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "接入实例服务端失败", ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Server.Local.ImportFailed", ex.Message));
        }

        await Task.CompletedTask;
        ShowList();
    }
}
