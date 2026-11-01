// ─────────────────────────────────────────────────────────────────────────────
// 服务器管理模块：移植自 ErieneNeko/PCL-CE-ServerControlPad
//   https://github.com/ErieneNeko/PCL-CE-ServerControlPad
//   原作者：ErieneNeko    许可：Apache License 2.0
//   本文件在原作基础上做了适配修改（命名空间/本地化/对话框 API 对齐本项目）。
// ─────────────────────────────────────────────────────────────────────────────
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Server;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;

namespace PCL;

public partial class PageServerManageRemote
{
    private McsmSession? _session;
    private CancellationTokenSource? _loadCts;
    private string _section = "dashboard";

    private MyTextBox? _textAddress;
    private PasswordBox? _textApiKey;
    private TextBlock? _connectionStatus;
    private MyButton? _btnConnect;
    private StackPanel? _navPanel;
    private StackPanel? _contentPanel;
    private readonly Dictionary<string, MyButton> _sectionButtons = new();

    private List<McsmDaemon> _daemons = [];
    private List<McsmInstance> _instances = [];
    private McsmInstance? _selectedInstance;
    private ComboBox? _instanceDaemonCombo;
    private StackPanel? _instanceListPanel;
    private TextBox? _instanceLogBox;
    private MyTextBox? _instanceCommandBox;

    private ComboBox? _fileDaemonCombo;
    private ComboBox? _fileInstanceCombo;
    private StackPanel? _fileListPanel;
    private TextBlock? _filePathLabel;
    private TextBox? _fileEditorBox;
    private TextBlock? _fileEditorLabel;
    private string _filePath = "/";
    private string? _editingRemotePath;
    private McsmInstance? _fileInstance;

    private ComboBox? _imageDaemonCombo;
    private StackPanel? _imageListPanel;
    private TextBlock? _imageProgressLabel;

    private bool _autoConnectTried;

    public PageServerManageRemote()
    {
        InitializeComponent();
        BuildShell();
        Loaded += (_, _) =>
        {
            LoadSavedSettings();
            if (!_autoConnectTried)
            {
                _autoConnectTried = true;
                if (McsmSettings.AutoConnect && McsmSettings.HasCredential)
                    _ = ConnectAsync();
            }
        };
        PageEnter += () =>
        {
            if (_session is null && McsmSettings.AutoConnect && McsmSettings.HasCredential && !_autoConnectTried)
            {
                _autoConnectTried = true;
                _ = ConnectAsync();
            }
        };
    }

    private void BuildShell()
    {
        PanMain.Children.Clear();
        _textAddress = new MyTextBox
        {
            Height = 31,
            HintText = Lang.Text("Server.Mcsm.AddressHint"),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _textApiKey = new PasswordBox
        {
            Height = 31,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        _textApiKey.SetResourceReference(Control.BackgroundProperty, "ColorBrush8");
        _textApiKey.SetResourceReference(Control.ForegroundProperty, "ColorBrush1");
        _textApiKey.SetResourceReference(Control.BorderBrushProperty, "ColorBrush6");
        _connectionStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Text = Lang.Text("Server.Mcsm.NotConnected")
        };
        _btnConnect = Button(Lang.Text("Server.Mcsm.Connect"), async (_, _) => await ConnectAsync(), true);

        var connection = new StackPanel();
        connection.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.Description"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var addressRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        addressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        addressRow.ColumnDefinitions.Add(new ColumnDefinition());
        addressRow.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.Address"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_textAddress, 1);
        addressRow.Children.Add(_textAddress);
        connection.Children.Add(addressRow);

        var keyRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition());
        keyRow.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.ApiKey"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_textApiKey, 1);
        keyRow.Children.Add(_textApiKey);
        connection.Children.Add(keyRow);

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        actionRow.Children.Add(_btnConnect);
        actionRow.Children.Add(Button(Lang.Text("Server.Mcsm.ClearKey"), (_, _) => ClearCredential(), false, 10));
        connection.Children.Add(actionRow);
        connection.Children.Add(_connectionStatus);
        connection.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.SecurityNote"),
            FontSize = 11,
            Foreground = GetBrush("ColorBrushGray3"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        });
        PanMain.Children.Add(Card(Lang.Text("Server.Mcsm.Title"), connection));

        _navPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var navCard = new MyCard { Margin = new Thickness(0, 0, 0, 12), Children = { new StackPanel { Margin = new Thickness(14, 14, 14, 14), Children = { _navPanel } } } };
        PanMain.Children.Add(navCard);
        _contentPanel = new StackPanel();
        PanMain.Children.Add(_contentPanel);
        RebuildNav();
        ShowSection("dashboard");
    }

    private void LoadSavedSettings()
    {
        if (_textAddress is not null)
            _textAddress.Text = McsmSettings.Address;
        if (_textApiKey is not null)
            _textApiKey.Password = McsmSettings.ApiKey;
        if (McsmSettings.HasCredential && _connectionStatus is not null)
            _connectionStatus.Text = Lang.Text("Server.Mcsm.SavedCredential", McsmSettings.MaskKey(McsmSettings.ApiKey));
    }

    private async Task ConnectAsync()
    {
        if (_textAddress is null || _textApiKey is null)
            return;
        var address = _textAddress.Text.Trim();
        var apiKey = _textApiKey.Password.Trim();
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(apiKey))
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Mcsm.Error.Empty")));
            return;
        }

        SetConnecting(true);
        try
        {
            var client = new McsmClient(address, apiKey);
            var session = await client.ConnectAsync();
            _session = session;
            McsmSettings.SaveSession(address, apiKey, session.User.UserName);
            if (_connectionStatus is not null)
            {
                _connectionStatus.Text = Lang.Text("Server.Mcsm.Connected",
                    session.User.UserName, session.User.Permission);
                _connectionStatus.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2");
            }

            RebuildNav();
            ShowSection("dashboard");
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Mcsm.ConnectSuccess")));
        }
        catch (Exception ex)
        {
            _session = null;
            if (_connectionStatus is not null)
            {
                _connectionStatus.Text = ErrorText(ex);
                _connectionStatus.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushRedLight");
            }

            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
            RebuildNav();
        }
        finally
        {
            SetConnecting(false);
        }
    }

    private void SetConnecting(bool connecting)
    {
        if (_btnConnect is not null)
        {
            _btnConnect.IsEnabled = !connecting;
            _btnConnect.Text = connecting ? Lang.Text("Server.Mcsm.Connecting") : Lang.Text("Server.Mcsm.Connect");
        }
    }

    private void ClearCredential()
    {
        McsmSettings.ClearCredential();
        _session = null;
        if (_textApiKey is not null)
            _textApiKey.Clear();
        if (_connectionStatus is not null)
        {
            _connectionStatus.Text = Lang.Text("Server.Mcsm.NotConnected");
            _connectionStatus.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
        }

        RebuildNav();
        ShowSection("dashboard");
        PCL.Core.UI.Hints.HintService.Show(Hints.Info(Lang.Text("Server.Mcsm.KeyCleared")));
    }

    private void RebuildNav()
    {
        if (_navPanel is null)
            return;
        _navPanel.Children.Clear();
        _sectionButtons.Clear();
        AddNav("dashboard", Lang.Text("Server.Mcsm.Nav.Dashboard"), true);
        if (_session is not null)
        {
            AddNav("instances", Lang.Text("Server.Mcsm.Nav.Instances"), true);
            if (_session.CanUseFileManager)
                AddNav("files", Lang.Text("Server.Mcsm.Nav.Files"), true);
            if (_session.CanManageUsers)
                AddNav("users", Lang.Text("Server.Mcsm.Nav.Users"), true);
            if (_session.CanManageDaemons)
                AddNav("daemons", Lang.Text("Server.Mcsm.Nav.Daemons"), true);
            if (_session.CanManageImages)
                AddNav("images", Lang.Text("Server.Mcsm.Nav.Images"), true);
        }

        UpdateNavSelection();
    }

    private void AddNav(string id, string text, bool enabled)
    {
        var button = Button(text, (_, _) => ShowSection(id), false);
        button.IsEnabled = enabled;
        button.Margin = new Thickness(0, 0, 10, 8);
        _navPanel!.Children.Add(button);
        _sectionButtons[id] = button;
    }

    private void UpdateNavSelection()
    {
        foreach (var pair in _sectionButtons)
            pair.Value.ColorType = pair.Key == _section
                ? MyButton.ColorState.Highlight
                : MyButton.ColorState.Normal;
    }

    private void ShowSection(string section)
    {
        _section = section;
        UpdateNavSelection();
        if (_contentPanel is null)
            return;
        _contentPanel.Children.Clear();
        if (_session is null)
        {
            _contentPanel.Children.Add(Card("", new TextBlock
            {
                Text = Lang.Text("Server.Mcsm.PleaseConnect"),
                Foreground = GetBrush("ColorBrushGray2"),
                TextWrapping = TextWrapping.Wrap
            }));
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        _contentPanel.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Common.Loading"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2"),
            Margin = new Thickness(4)
        });
        _ = LoadSectionAsync(section, token);
    }

    private async Task LoadSectionAsync(string section, CancellationToken token)
    {
        try
        {
            switch (section)
            {
                case "dashboard":
                    await BuildDashboardAsync(token);
                    break;
                case "users":
                    await BuildUsersAsync(token);
                    break;
                case "instances":
                    await BuildInstancesAsync(token);
                    break;
                case "daemons":
                    await BuildDaemonsAsync(token);
                    break;
                case "files":
                    await BuildFilesAsync(token);
                    break;
                case "images":
                    await BuildImagesAsync(token);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || _section != section)
                return;
            if (_contentPanel is not null)
            {
                _contentPanel.Children.Clear();
                _contentPanel.Children.Add(Card(Lang.Text("Server.Mcsm.Error.Title"), new TextBlock
                {
                    Text = ErrorText(ex),
                    Foreground = GetBrush("ColorBrushRedLight"),
                    TextWrapping = TextWrapping.Wrap
                }));
            }
        }
    }

    private void ReplaceContent(UIElement content)
    {
        if (_contentPanel is null)
            return;
        _contentPanel.Children.Clear();
        _contentPanel.Children.Add(content);
    }

    #region Dashboard

    private async Task BuildDashboardAsync(CancellationToken token)
    {
        if (_session is null)
            return;
        var overview = _session.Overview;
        if (overview is null && _session.IsAdmin)
        {
            overview = await _session.Client.GetOverviewAsync(token);
            _session.Overview = overview;
        }

        token.ThrowIfCancellationRequested();
        var panel = new StackPanel();
        panel.Children.Add(Button(Lang.Text("Server.Mcsm.Refresh"), (_, _) => ShowSection("dashboard"), false));
        var summary = new Grid { Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        for (var i = 0; i < 4; i++)
            summary.ColumnDefinitions.Add(new ColumnDefinition());
        AddSummary(summary, 0, Lang.Text("Server.Mcsm.PanelVersion"), overview?.Version ?? "-");
        AddSummary(summary, 1, Lang.Text("Server.Mcsm.Daemons"),
            overview is null ? "-" : $"{overview.RemoteAvailable}/{overview.RemoteTotal}");
        AddSummary(summary, 2, Lang.Text("Server.Mcsm.Instances"),
            overview is null ? "-" : $"{overview.RunningInstances}/{overview.TotalInstances}");
        AddSummary(summary, 3, Lang.Text("Server.Mcsm.OnlineUsers"), overview?.Logined.ToString() ?? "-");
        panel.Children.Add(summary);

        if (overview?.Remotes.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.Text("Server.Mcsm.Nodes"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 16, 0, 8),
                Foreground = GetBrush("ColorBrush1")
            });
            foreach (var daemon in overview.Remotes)
                panel.Children.Add(BuildNodeRow(daemon));
        }
        else if (!_session.IsAdmin)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.Text("Server.Mcsm.NonAdminDashboard"),
                FontSize = 12,
                Foreground = GetBrush("ColorBrushGray2"),
                Margin = new Thickness(0, 14, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        ReplaceContent(Card(Lang.Text("Server.Mcsm.Nav.Dashboard"), panel));
    }

    private void AddSummary(Grid grid, int column, string label, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("ColorBrush2")
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = GetBrush("ColorBrushGray2"),
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private UIElement BuildNodeRow(McsmDaemon daemon)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(daemon.Remarks) ? daemon.Ip : daemon.Remarks,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("ColorBrush1")
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{daemon.Ip}:{daemon.Port}  ·  {daemon.Version}  ·  " +
                   $"{daemon.RunningInstances}/{daemon.TotalInstances}",
            FontSize = 11,
            Foreground = GetBrush("ColorBrushGray2"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        if (daemon.System is not null)
        {
            info.Children.Add(new TextBlock
            {
                Text = $"{daemon.System.Hostname}  ·  CPU {daemon.System.CpuUsage:P0}  ·  内存 " +
                       $"{ServerBackupService.FormatSize(daemon.System.TotalMem - daemon.System.FreeMem)} / " +
                       $"{ServerBackupService.FormatSize(daemon.System.TotalMem)}",
                FontSize = 11,
                Foreground = GetBrush("ColorBrushGray3"),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        row.Children.Add(info);
        var status = new TextBlock
        {
            Text = daemon.Available ? Lang.Text("Server.Mcsm.Available") : Lang.Text("Server.Mcsm.Unavailable"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = daemon.Available ? GetBrush("ColorBrush2") : GetBrush("ColorBrushRedLight")
        };
        Grid.SetColumn(status, 1);
        row.Children.Add(status);
        return row;
    }

    #endregion

    #region Users

    private async Task BuildUsersAsync(CancellationToken token)
    {
        if (_session is null || !_session.CanManageUsers)
            throw new McsmApiException(Lang.Text("Server.Mcsm.NoPermission"), McsmErrorKind.Forbidden);

        var search = new MyTextBox { Height = 31, HintText = Lang.Text("Server.Mcsm.SearchUser") };
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var panel = new StackPanel();
        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(search);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button(Lang.Text("Server.Mcsm.Refresh"), async (_, _) =>
        {
            await LoadUsersAsync(search.Text, list);
        }, false, 8));
        buttons.Children.Add(Button(Lang.Text("Server.Mcsm.CreateUser"), async (_, _) =>
        {
            await CreateUserAsync();
            await LoadUsersAsync(search.Text, list);
        }, false, 8));
        Grid.SetColumn(buttons, 1);
        toolbar.Children.Add(buttons);
        panel.Children.Add(toolbar);
        panel.Children.Add(list);
        ReplaceContent(Card(Lang.Text("Server.Mcsm.Nav.Users"), panel));
        await LoadUsersAsync("", list);
    }

    private async Task LoadUsersAsync(string keyword, StackPanel list)
    {
        if (_session is null)
            return;
        list.Children.Clear();
        list.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Common.Loading"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2")
        });
        try
        {
            var page = await _session.Client.SearchUsersAsync(keyword, 1, 50, ct: _loadCts?.Token ?? default);
            list.Children.Clear();
            if (page.Items.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = Lang.Text("Server.Mcsm.NoUsers"),
                    FontSize = 12,
                    Foreground = GetBrush("ColorBrushGray2")
                });
                return;
            }

            foreach (var user in page.Items)
                list.Children.Add(BuildUserRow(user, list, keyword));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock
            {
                Text = ErrorText(ex),
                FontSize = 12,
                Foreground = GetBrush("ColorBrushRedLight"),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private UIElement BuildUserRow(McsmUser user, StackPanel list, string keyword)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = user.UserName,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("ColorBrush1")
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{PermissionText(user.Permission)}  ·  {user.Uuid}  ·  " +
                   Lang.Text("Server.Mcsm.UserInstances", user.InstanceCount),
            FontSize = 11,
            Foreground = GetBrush("ColorBrushGray2"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        row.Children.Add(info);
        var delete = Button(Lang.Text("Common.Action.Delete"), async (_, _) =>
        {
            var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
            {
                Title = Lang.Text("Server.Mcsm.DeleteUser"),
                Content = Lang.Text("Server.Mcsm.DeleteUserPrompt", user.UserName),
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
                await _session!.Client.DeleteUsersAsync([user.Uuid]);
                await LoadUsersAsync(keyword, list);
            }
            catch (Exception ex)
            {
                PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
            }
        }, false);
        Grid.SetColumn(delete, 1);
        row.Children.Add(delete);
        return row;
    }

    private async Task CreateUserAsync()
    {
        if (_session is null)
            return;
        var name = await InputAsync(Lang.Text("Server.Mcsm.CreateUser"), Lang.Text("Server.Mcsm.UserName"));
        if (name is null) return;
        var password = await InputAsync(Lang.Text("Server.Mcsm.CreateUser"), Lang.Text("Server.Mcsm.Password"));
        if (password is null) return;
        var permissionResult = await DialogService.Instance.ShowAsync(new SelectDialogRequest
        {
            Title = Lang.Text("Server.Mcsm.CreateUser"),
            Options =
            [
                new DialogOption { Text = Lang.Text("Server.Mcsm.Permission.User"), Tag = 1, IsChecked = true },
                new DialogOption { Text = Lang.Text("Server.Mcsm.Permission.Admin"), Tag = 10 }
            ],
            Buttons = DialogButtons.OkCancel,
            ForceWait = true
        });
        if (permissionResult.Button != 1)
            return;
        var permission = permissionResult.SelectedIndex == 1 ? 10 : 1;
        try
        {
            await _session.Client.CreateUserAsync(name, password, permission);
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Mcsm.UserCreated")));
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    #endregion

    #region Instances

    private async Task BuildInstancesAsync(CancellationToken token)
    {
        if (_session is null)
            return;
        _daemons = await LoadDaemonsCoreAsync(token);
        var panel = new StackPanel();
        var selector = new StackPanel { Orientation = Orientation.Horizontal };
        selector.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.Daemon"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _instanceDaemonCombo = new MyComboBox { Height = 30, MinWidth = 220 };
        foreach (var daemon in _daemons)
            _instanceDaemonCombo.Items.Add(new MyComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(daemon.Remarks)
                    ? $"{daemon.Ip}:{daemon.Port}"
                    : daemon.Remarks,
                Tag = daemon
            });
        if (_instanceDaemonCombo.Items.Count > 0)
            _instanceDaemonCombo.SelectedIndex = 0;
        _instanceDaemonCombo.SelectionChanged += async (_, _) => await LoadInstancesAsync();
        selector.Children.Add(_instanceDaemonCombo);
        selector.Children.Add(Button(Lang.Text("Server.Mcsm.Refresh"), async (_, _) => await LoadInstancesAsync(),
            false, 8));
        panel.Children.Add(selector);

        _instanceListPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(_instanceListPanel);

        panel.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.Console"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 14, 0, 6),
            Foreground = GetBrush("ColorBrush1")
        });
        _instanceLogBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Height = 210,
            Padding = new Thickness(8)
        };
        _instanceLogBox.SetResourceReference(TextBox.BackgroundProperty, "ColorBrush8");
        _instanceLogBox.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1");
        panel.Children.Add(_instanceLogBox);
        var commandRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        commandRow.ColumnDefinitions.Add(new ColumnDefinition());
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _instanceCommandBox = new MyTextBox
        {
            Height = 31,
            HintText = Lang.Text("Server.Console.CommandHint"),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _instanceCommandBox.KeyDown += async (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter)
                await SendRemoteCommandAsync();
        };
        commandRow.Children.Add(_instanceCommandBox);
        var send = Button(Lang.Text("Server.Console.Send"), async (_, _) => await SendRemoteCommandAsync(), true);
        Grid.SetColumn(send, 1);
        send.Margin = new Thickness(10, 0, 0, 0);
        commandRow.Children.Add(send);
        panel.Children.Add(commandRow);
        panel.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.ConsoleNote"),
            FontSize = 11,
            Foreground = GetBrush("ColorBrushGray3"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        ReplaceContent(Card(Lang.Text("Server.Mcsm.Nav.Instances"), panel));
        await LoadInstancesAsync();
    }

    private async Task<List<McsmDaemon>> LoadDaemonsCoreAsync(CancellationToken token)
    {
        if (_session is null)
            return [];
        if (_session.Overview?.Remotes.Count > 0)
            return _session.Overview.Remotes.ToList();
        return (await _session.Client.GetDaemonsAsync(token)).ToList();
    }

    private async Task LoadInstancesAsync()
    {
        if (_session is null || _instanceListPanel is null)
            return;
        if (_instanceDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon })
        {
            _instanceListPanel.Children.Clear();
            _instanceListPanel.Children.Add(new TextBlock
            {
                Text = Lang.Text("Server.Mcsm.NoDaemons"),
                FontSize = 12,
                Foreground = GetBrush("ColorBrushGray2")
            });
            return;
        }

        _instanceListPanel.Children.Clear();
        _instanceListPanel.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Common.Loading"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2")
        });
        try
        {
            var page = await _session.Client.GetInstancesAsync(daemon.Uuid, 1, 50,
                ct: _loadCts?.Token ?? default);
            _instances = page.Items;
            _instanceListPanel.Children.Clear();
            if (_instances.Count == 0)
            {
                _instanceListPanel.Children.Add(new TextBlock
                {
                    Text = Lang.Text("Server.Mcsm.NoInstances"),
                    FontSize = 12,
                    Foreground = GetBrush("ColorBrushGray2")
                });
                return;
            }

            foreach (var instance in _instances)
                _instanceListPanel.Children.Add(BuildInstanceRow(instance, daemon));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _instanceListPanel.Children.Clear();
            _instanceListPanel.Children.Add(new TextBlock
            {
                Text = ErrorText(ex),
                FontSize = 12,
                Foreground = GetBrush("ColorBrushRedLight"),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private UIElement BuildInstanceRow(McsmInstance instance, McsmDaemon daemon)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(instance.Nickname) ? instance.Uuid : instance.Nickname,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("ColorBrush1")
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{instance.StatusText}  ·  {instance.Version}  ·  " +
                   $"{instance.CurrentPlayers}/{instance.MaxPlayers}  ·  {instance.Uuid}",
            FontSize = 11,
            Foreground = instance.Status == 3 ? GetBrush("ColorBrush2") : GetBrush("ColorBrushGray2"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        row.Children.Add(info);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Button(Lang.Text("Server.Action.Start"),
            async (_, _) => await RemoteInstanceActionAsync(daemon, instance, "open"), false));
        actions.Children.Add(Button(Lang.Text("Server.Action.Stop"),
            async (_, _) => await RemoteInstanceActionAsync(daemon, instance, "stop"), false, 8));
        actions.Children.Add(Button(Lang.Text("Server.Action.Restart"),
            async (_, _) => await RemoteInstanceActionAsync(daemon, instance, "restart"), false, 8));
        actions.Children.Add(Button(Lang.Text("Server.Mcsm.Kill"),
            async (_, _) => await RemoteInstanceActionAsync(daemon, instance, "kill"), false, 8));
        actions.Children.Add(Button(Lang.Text("Server.Mcsm.Log"),
            async (_, _) => await SelectRemoteInstanceAsync(daemon, instance), false, 8));
        actions.Children.Add(Button(Lang.Text("Common.Action.Delete"), async (_, _) =>
        {
            var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
            {
                Title = Lang.Text("Server.Mcsm.DeleteInstance"),
                Content = Lang.Text("Server.Mcsm.DeleteInstancePrompt", instance.Nickname),
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
                await _session!.Client.DeleteInstancesAsync(daemon.Uuid, [instance.Uuid]);
                await LoadInstancesAsync();
            }
            catch (Exception ex)
            {
                PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
            }
        }, false, 8));
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private async Task RemoteInstanceActionAsync(McsmDaemon daemon, McsmInstance instance, string action)
    {
        if (_session is null)
            return;
        try
        {
            switch (action)
            {
                case "open":
                    await _session.Client.StartInstanceAsync(daemon.Uuid, instance.Uuid);
                    break;
                case "stop":
                    await _session.Client.StopInstanceAsync(daemon.Uuid, instance.Uuid);
                    break;
                case "restart":
                    await _session.Client.RestartInstanceAsync(daemon.Uuid, instance.Uuid);
                    break;
                case "kill":
                    await _session.Client.KillInstanceAsync(daemon.Uuid, instance.Uuid);
                    break;
            }

            await Task.Delay(500);
            await LoadInstancesAsync();
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    private async Task SelectRemoteInstanceAsync(McsmDaemon daemon, McsmInstance instance)
    {
        _selectedInstance = instance;
        await RefreshRemoteLogAsync(daemon, instance);
    }

    private async Task SendRemoteCommandAsync()
    {
        if (_session is null || _selectedInstance is null || _instanceCommandBox is null)
            return;
        var daemon = _instanceDaemonCombo?.SelectedItem as MyComboBoxItem;
        if (daemon?.Tag is not McsmDaemon selectedDaemon)
            return;
        var command = _instanceCommandBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return;
        try
        {
            await _session.Client.SendCommandAsync(selectedDaemon.Uuid, _selectedInstance.Uuid, command);
            _instanceCommandBox.Clear();
            await Task.Delay(400);
            await RefreshRemoteLogAsync(selectedDaemon, _selectedInstance);
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    private async Task RefreshRemoteLogAsync(McsmDaemon daemon, McsmInstance instance)
    {
        if (_session is null || _instanceLogBox is null)
            return;
        try
        {
            var log = await _session.Client.GetOutputLogAsync(daemon.Uuid, instance.Uuid);
            _instanceLogBox.Text = log;
            _instanceLogBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            _instanceLogBox.Text = ErrorText(ex);
        }
    }

    #endregion

    #region Daemons

    private async Task BuildDaemonsAsync(CancellationToken token)
    {
        if (_session is null || !_session.CanManageDaemons)
            throw new McsmApiException(Lang.Text("Server.Mcsm.NoPermission"), McsmErrorKind.Forbidden);
        var daemons = await _session.Client.GetDaemonsAsync(token);
        var panel = new StackPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button(Lang.Text("Server.Mcsm.AddDaemon"), async (_, _) =>
        {
            await AddDaemonAsync();
            ShowSection("daemons");
        }, true));
        actions.Children.Add(Button(Lang.Text("Server.Mcsm.Refresh"), (_, _) => ShowSection("daemons"), false, 8));
        panel.Children.Add(actions);
        if (daemons.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.Text("Server.Mcsm.NoDaemons"),
                FontSize = 12,
                Foreground = GetBrush("ColorBrushGray2"),
                Margin = new Thickness(0, 10, 0, 0)
            });
        }

        foreach (var daemon in daemons)
            panel.Children.Add(BuildDaemonRow(daemon));
        ReplaceContent(Card(Lang.Text("Server.Mcsm.Nav.Daemons"), panel));
    }

    private UIElement BuildDaemonRow(McsmDaemon daemon)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(daemon.Remarks) ? daemon.Ip : daemon.Remarks,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("ColorBrush1")
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{daemon.Ip}:{daemon.Port}  ·  {daemon.Uuid}  ·  " +
                   (daemon.Available ? Lang.Text("Server.Mcsm.Available") : Lang.Text("Server.Mcsm.Unavailable")),
            FontSize = 11,
            Foreground = daemon.Available ? GetBrush("ColorBrushGray2") : GetBrush("ColorBrushRedLight"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        row.Children.Add(info);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Button(Lang.Text("Server.Mcsm.Reconnect"), async (_, _) =>
        {
            try
            {
                await _session!.Client.ConnectDaemonAsync(daemon.Uuid);
                PCL.Core.UI.Hints.HintService.Show(Hints.Info(Lang.Text("Server.Mcsm.ReconnectSent")));
            }
            catch (Exception ex)
            {
                PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
            }
        }, false));
        actions.Children.Add(Button(Lang.Text("Common.Action.Delete"), async (_, _) =>
        {
            var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
            {
                Title = Lang.Text("Server.Mcsm.DeleteDaemon"),
                Content = Lang.Text("Server.Mcsm.DeleteDaemonPrompt", daemon.Remarks, daemon.Ip),
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
                await _session!.Client.DeleteDaemonAsync(daemon.Uuid);
                ShowSection("daemons");
            }
            catch (Exception ex)
            {
                PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
            }
        }, false, 8));
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private async Task AddDaemonAsync()
    {
        if (_session is null)
            return;
        var ip = await InputAsync(Lang.Text("Server.Mcsm.AddDaemon"), Lang.Text("Server.Mcsm.DaemonIp"));
        if (ip is null) return;
        var portText = await InputAsync(Lang.Text("Server.Mcsm.AddDaemon"), Lang.Text("Server.Mcsm.DaemonPort"),
            "24444");
        if (portText is null || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(Lang.Text("Server.Mcsm.Error.Port")));
            return;
        }

        var remarks = await InputAsync(Lang.Text("Server.Mcsm.AddDaemon"), Lang.Text("Server.Mcsm.DaemonRemarks"),
            ip);
        if (remarks is null) return;
        var apiKey = await InputAsync(Lang.Text("Server.Mcsm.AddDaemon"), Lang.Text("Server.Mcsm.DaemonApiKey"));
        if (apiKey is null) return;
        try
        {
            await _session.Client.AddDaemonAsync(ip, port, remarks, apiKey);
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Mcsm.DaemonAdded")));
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    #endregion

    #region Files

    private async Task BuildFilesAsync(CancellationToken token)
    {
        if (_session is null || !_session.CanUseFileManager)
            throw new McsmApiException(Lang.Text("Server.Mcsm.NoPermission"), McsmErrorKind.Forbidden);
        _filePath = "/";
        _editingRemotePath = null;
        _daemons = await LoadDaemonsCoreAsync(token);
        var panel = new StackPanel();
        var selector = new StackPanel { Orientation = Orientation.Horizontal };
        _fileDaemonCombo = new MyComboBox { Height = 30, MinWidth = 200 };
        foreach (var daemon in _daemons)
            _fileDaemonCombo.Items.Add(new MyComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(daemon.Remarks) ? $"{daemon.Ip}:{daemon.Port}" : daemon.Remarks,
                Tag = daemon
            });
        if (_fileDaemonCombo.Items.Count > 0)
            _fileDaemonCombo.SelectedIndex = 0;
        _fileDaemonCombo.SelectionChanged += async (_, _) => await LoadFileInstancesAsync();
        selector.Children.Add(_fileDaemonCombo);

        _fileInstanceCombo = new MyComboBox { Height = 30, MinWidth = 200, Margin = new Thickness(8, 0, 0, 0) };
        _fileInstanceCombo.SelectionChanged += async (_, _) => await LoadRemoteFilesAsync();
        selector.Children.Add(_fileInstanceCombo);
        selector.Children.Add(Button(Lang.Text("Server.Mcsm.Refresh"),
            async (_, _) => await LoadRemoteFilesAsync(), false, 8));
        panel.Children.Add(selector);

        var pathRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition());
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _filePathLabel = new TextBlock
        {
            Text = "/",
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2"),
            VerticalAlignment = VerticalAlignment.Center
        };
        pathRow.Children.Add(_filePathLabel);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Button(Lang.Text("Server.Files.Up"), (_, _) => RemoteUp(), false));
        tools.Children.Add(Button(Lang.Text("Server.Files.Refresh"),
            async (_, _) => await LoadRemoteFilesAsync(), false, 8));
        tools.Children.Add(Button(Lang.Text("Server.Files.NewFolder"),
            async (_, _) => await NewRemoteFolderAsync(), false, 8));
        tools.Children.Add(Button(Lang.Text("Server.Files.Upload"),
            async (_, _) => await UploadRemoteFileAsync(), false, 8));
        Grid.SetColumn(tools, 1);
        pathRow.Children.Add(tools);
        panel.Children.Add(pathRow);

        _fileListPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_fileListPanel);

        var editorPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _fileEditorLabel = new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.NoFileSelected"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        editorPanel.Children.Add(_fileEditorLabel);
        _fileEditorBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Height = 230,
            Padding = new Thickness(8),
            IsEnabled = false
        };
        _fileEditorBox.SetResourceReference(TextBox.BackgroundProperty, "ColorBrush8");
        _fileEditorBox.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1");
        editorPanel.Children.Add(_fileEditorBox);
        var editorActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        editorActions.Children.Add(Button(Lang.Text("Server.Files.Save"),
            async (_, _) => await SaveRemoteFileAsync(), true));
        editorActions.Children.Add(Button(Lang.Text("Server.Mcsm.Download"),
            async (_, _) => await DownloadRemoteFileAsync(), false, 8));
        editorActions.Children.Add(Button(Lang.Text("Server.Files.Delete"),
            async (_, _) => await DeleteRemoteFileAsync(), false, 8));
        editorPanel.Children.Add(editorActions);
        panel.Children.Add(editorPanel);

        ReplaceContent(Card(Lang.Text("Server.Mcsm.Nav.Files"), panel));
        await LoadFileInstancesAsync();
    }

    private async Task LoadFileInstancesAsync()
    {
        if (_fileInstanceCombo is null || _session is null)
            return;
        _fileInstanceCombo.Items.Clear();
        if (_fileDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon })
            return;
        try
        {
            var page = await _session.Client.GetInstancesAsync(daemon.Uuid, 1, 50,
                ct: _loadCts?.Token ?? default);
            foreach (var instance in page.Items)
                _fileInstanceCombo.Items.Add(new MyComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(instance.Nickname) ? instance.Uuid : instance.Nickname,
                    Tag = instance
                });
            if (_fileInstanceCombo.Items.Count > 0)
                _fileInstanceCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            if (_fileListPanel is not null)
            {
                _fileListPanel.Children.Clear();
                _fileListPanel.Children.Add(new TextBlock
                {
                    Text = ErrorText(ex),
                    Foreground = GetBrush("ColorBrushRedLight"),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
    }

    private async Task LoadRemoteFilesAsync()
    {
        if (_session is null || _fileListPanel is null)
            return;
        if (_fileDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon } ||
            _fileInstanceCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmInstance instance })
            return;
        _fileInstance = instance;
        _fileListPanel.Children.Clear();
        _fileListPanel.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Common.Loading"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2")
        });
        try
        {
            var list = await _session.Client.ListFilesAsync(daemon.Uuid, instance.Uuid, _filePath,
                ct: _loadCts?.Token ?? default);
            _fileListPanel.Children.Clear();
            if (_filePathLabel is not null)
                _filePathLabel.Text = _filePath;
            foreach (var entry in list.Items)
            {
                var item = new MyListItem
                {
                    Title = entry.Name,
                    Info = entry.IsDirectory ? Lang.Text("Server.Files.Folder") :
                        ServerBackupService.FormatSize(entry.Size),
                    SvgIcon = entry.IsDirectory ? "lucide/folder" : "lucide/file",
                    Type = MyListItem.CheckType.Clickable,
                    Height = 38,
                    Tag = entry
                };
                item.Click += async (_, _) =>
                {
                    if (entry.IsDirectory)
                    {
                        _filePath = JoinRemote(_filePath, entry.Name);
                        await LoadRemoteFilesAsync();
                    }
                    else
                    {
                        await ReadRemoteFileAsync(daemon, instance, JoinRemote(_filePath, entry.Name));
                    }
                };
                _fileListPanel.Children.Add(item);
            }
        }
        catch (Exception ex)
        {
            _fileListPanel.Children.Clear();
            _fileListPanel.Children.Add(new TextBlock
            {
                Text = ErrorText(ex),
                Foreground = GetBrush("ColorBrushRedLight"),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private async Task ReadRemoteFileAsync(McsmDaemon daemon, McsmInstance instance, string path)
    {
        if (_session is null || _fileEditorBox is null)
            return;
        try
        {
            var content = await _session.Client.ReadFileAsync(daemon.Uuid, instance.Uuid, path);
            _editingRemotePath = path;
            _fileEditorBox.Text = content;
            _fileEditorBox.IsEnabled = true;
            if (_fileEditorLabel is not null)
                _fileEditorLabel.Text = path;
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    private async Task SaveRemoteFileAsync()
    {
        if (_session is null || _fileEditorBox is null || _editingRemotePath is null)
            return;
        if (_fileDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon } ||
            _fileInstance is null)
            return;
        try
        {
            await _session.Client.WriteFileAsync(daemon.Uuid, _fileInstance.Uuid, _editingRemotePath,
                _fileEditorBox.Text);
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Files.Saved")));
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    private async Task DeleteRemoteFileAsync()
    {
        if (_session is null || _editingRemotePath is null || _fileInstance is null ||
            _fileDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon })
            return;
        var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
        {
            Title = Lang.Text("Server.Files.Delete"),
            Content = Lang.Text("Server.Files.DeletePrompt", _editingRemotePath),
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
            await _session.Client.DeleteFilesAsync(daemon.Uuid, _fileInstance.Uuid, [_editingRemotePath]);
            _editingRemotePath = null;
            if (_fileEditorBox is not null)
            {
                _fileEditorBox.Clear();
                _fileEditorBox.IsEnabled = false;
            }

            await LoadRemoteFilesAsync();
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    private async Task NewRemoteFolderAsync()
    {
        if (_session is null || _fileInstance is null ||
            _fileDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon })
            return;
        var name = await InputAsync(Lang.Text("Server.Files.NewFolder"),
            Lang.Text("Server.Files.NewFolderPrompt"));
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            await _session.Client.CreateDirectoryAsync(daemon.Uuid, _fileInstance.Uuid,
                JoinRemote(_filePath, name));
            await LoadRemoteFilesAsync();
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    private async Task UploadRemoteFileAsync()
    {
        if (_session is null || _fileInstance is null ||
            _fileDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon })
            return;
        var dialog = new OpenFileDialog { CheckFileExists = true };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await _session.Client.UploadFileAsync(daemon.Uuid, _fileInstance.Uuid, _filePath, dialog.FileName);
            await LoadRemoteFilesAsync();
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Files.Uploaded")));
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    private async Task DownloadRemoteFileAsync()
    {
        if (_session is null || _fileInstance is null || _editingRemotePath is null ||
            _fileDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon })
            return;
        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(_editingRemotePath)
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await _session.Client.DownloadFileAsync(daemon.Uuid, _fileInstance.Uuid, _editingRemotePath,
                dialog.FileName);
            PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Mcsm.Downloaded")));
        }
        catch (Exception ex)
        {
            PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
        }
    }

    private void RemoteUp()
    {
        if (_filePath == "/")
            return;
        var index = _filePath.TrimEnd('/').LastIndexOf('/');
        _filePath = index <= 0 ? "/" : _filePath[..index];
        _ = LoadRemoteFilesAsync();
    }

    private static string JoinRemote(string basePath, string name)
    {
        var root = string.IsNullOrWhiteSpace(basePath) ? "/" : basePath;
        return root == "/" ? "/" + name : root.TrimEnd('/') + "/" + name;
    }

    #endregion

    #region Images

    private async Task BuildImagesAsync(CancellationToken token)
    {
        if (_session is null || !_session.CanManageImages)
            throw new McsmApiException(Lang.Text("Server.Mcsm.NoPermission"), McsmErrorKind.Forbidden);
        _daemons = await LoadDaemonsCoreAsync(token);
        var panel = new StackPanel();
        var selector = new StackPanel { Orientation = Orientation.Horizontal };
        _imageDaemonCombo = new MyComboBox { Height = 30, MinWidth = 200 };
        foreach (var daemon in _daemons)
            _imageDaemonCombo.Items.Add(new MyComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(daemon.Remarks) ? $"{daemon.Ip}:{daemon.Port}" : daemon.Remarks,
                Tag = daemon
            });
        if (_imageDaemonCombo.Items.Count > 0)
            _imageDaemonCombo.SelectedIndex = 0;
        _imageDaemonCombo.SelectionChanged += async (_, _) => await LoadImagesAsync();
        selector.Children.Add(_imageDaemonCombo);
        selector.Children.Add(Button(Lang.Text("Server.Mcsm.Refresh"),
            async (_, _) => await LoadImagesAsync(), false, 8));
        panel.Children.Add(selector);

        _imageListPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(_imageListPanel);

        panel.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Mcsm.BuildImage"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 14, 0, 6),
            Foreground = GetBrush("ColorBrush1")
        });
        var name = new MyTextBox { Height = 30, HintText = Lang.Text("Server.Mcsm.ImageName") };
        var tag = new MyTextBox { Height = 30, HintText = Lang.Text("Server.Mcsm.ImageTag"), Text = "latest" };
        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition());
        nameRow.ColumnDefinitions.Add(new ColumnDefinition());
        nameRow.Children.Add(name);
        Grid.SetColumn(tag, 1);
        tag.Margin = new Thickness(8, 0, 0, 0);
        nameRow.Children.Add(tag);
        panel.Children.Add(nameRow);
        var dockerFile = new TextBox
        {
            Height = 120,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 0),
            Text = "FROM eclipse-temurin:21-jre\n"
        };
        dockerFile.SetResourceReference(TextBox.BackgroundProperty, "ColorBrush8");
        dockerFile.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1");
        panel.Children.Add(dockerFile);
        _imageProgressLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        panel.Children.Add(_imageProgressLabel);
        panel.Children.Add(Button(Lang.Text("Server.Mcsm.BuildImage"), async (_, _) =>
        {
            if (_imageDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon })
                return;
            try
            {
                await _session.Client.BuildImageAsync(daemon.Uuid, dockerFile.Text, name.Text.Trim(),
                    tag.Text.Trim());
                await LoadImagesAsync();
                PCL.Core.UI.Hints.HintService.Show(Hints.Success(Lang.Text("Server.Mcsm.BuildStarted")));
            }
            catch (Exception ex)
            {
                PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
            }
        }, true, 0));

        ReplaceContent(Card(Lang.Text("Server.Mcsm.Nav.Images"), panel));
        await LoadImagesAsync();
    }

    private async Task LoadImagesAsync()
    {
        if (_session is null || _imageListPanel is null)
            return;
        if (_imageDaemonCombo?.SelectedItem is not MyComboBoxItem { Tag: McsmDaemon daemon })
            return;
        _imageListPanel.Children.Clear();
        _imageListPanel.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Common.Loading"),
            FontSize = 12,
            Foreground = GetBrush("ColorBrushGray2")
        });
        try
        {
            var images = await _session.Client.GetImagesAsync(daemon.Uuid);
            var progress = await _session.Client.GetImageProgressAsync(daemon.Uuid);
            var containers = await _session.Client.GetContainersAsync(daemon.Uuid);
            var networks = await _session.Client.GetNetworkModesAsync(daemon.Uuid);
            _imageListPanel.Children.Clear();
            foreach (var image in images)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = image.DisplayName,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = GetBrush("ColorBrush1")
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"{ServerBackupService.FormatSize(image.Size)}  ·  {image.Id}",
                    FontSize = 11,
                    Foreground = GetBrush("ColorBrushGray2"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                row.Children.Add(info);
                var delete = Button(Lang.Text("Common.Action.Delete"), async (_, _) =>
                {
                    var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
                    {
                        Title = Lang.Text("Server.Mcsm.DeleteImage"),
                        Content = Lang.Text("Server.Mcsm.DeleteImagePrompt", image.DisplayName),
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
                        await _session!.Client.DeleteImageAsync(daemon.Uuid, image.Id);
                        await LoadImagesAsync();
                    }
                    catch (Exception ex)
                    {
                        PCL.Core.UI.Hints.HintService.Show(Hints.Error(ErrorText(ex)));
                    }
                }, false);
                Grid.SetColumn(delete, 1);
                row.Children.Add(delete);
                _imageListPanel.Children.Add(row);
            }

            if (images.Count == 0)
            {
                _imageListPanel.Children.Add(new TextBlock
                {
                    Text = Lang.Text("Server.Mcsm.NoImages"),
                    FontSize = 12,
                    Foreground = GetBrush("ColorBrushGray2")
                });
            }

            if (progress.Count > 0)
            {
                _imageListPanel.Children.Add(new TextBlock
                {
                    Text = Lang.Text("Server.Mcsm.ImageProgress") + " " +
                           string.Join("，", progress.Select(pair =>
                               $"{pair.Key}: {ProgressText(pair.Value)}")),
                    FontSize = 11,
                    Foreground = GetBrush("ColorBrushGray2"),
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            _imageListPanel.Children.Add(new TextBlock
            {
                Text = Lang.Text("Server.Mcsm.Containers", containers.Count) + "  ·  " +
                       Lang.Text("Server.Mcsm.NetworkModes", networks.Count),
                FontSize = 11,
                Foreground = GetBrush("ColorBrushGray3"),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        catch (Exception ex)
        {
            _imageListPanel.Children.Clear();
            _imageListPanel.Children.Add(new TextBlock
            {
                Text = ErrorText(ex),
                Foreground = GetBrush("ColorBrushRedLight"),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private static string ProgressText(int value) => value switch
    {
        -1 => Lang.Text("Server.Mcsm.Progress.Failed"),
        1 => Lang.Text("Server.Mcsm.Progress.Building"),
        2 => Lang.Text("Server.Mcsm.Progress.Done"),
        _ => value.ToString()
    };

    #endregion

    #region Helpers

    private static string PermissionText(int permission) => permission switch
    {
        10 => Lang.Text("Server.Mcsm.Permission.Admin"),
        -1 => Lang.Text("Server.Mcsm.Permission.Banned"),
        _ => Lang.Text("Server.Mcsm.Permission.User")
    };

    private static string ErrorText(Exception ex)
    {
        if (ex is not McsmApiException api)
            return ex.Message;
        return api.Kind switch
        {
            McsmErrorKind.InvalidConfiguration => Lang.Text("Server.Mcsm.Error.InvalidConfig") + " " + api.Message,
            McsmErrorKind.Network => Lang.Text("Server.Mcsm.Error.Network") + " " + api.Message,
            McsmErrorKind.Timeout => Lang.Text("Server.Mcsm.Error.Timeout") + " " + api.Message,
            McsmErrorKind.Unauthorized => Lang.Text("Server.Mcsm.Error.Unauthorized"),
            McsmErrorKind.Forbidden => Lang.Text("Server.Mcsm.Error.Forbidden"),
            McsmErrorKind.Parse => Lang.Text("Server.Mcsm.Error.Parse") + " " + api.Message,
            _ => api.Message
        };
    }

    private static async Task<string?> InputAsync(string title, string content, string defaultValue = "")
    {
        var result = await DialogService.Instance.ShowAsync(new InputDialogRequest
        {
            Title = title,
            Content = content,
            DefaultValue = defaultValue,
            Buttons = DialogButtons.OkCancel,
            ForceWait = true
        });
        return result.Button == 1 && !string.IsNullOrWhiteSpace(result.InputText)
            ? result.InputText.Trim()
            : null;
    }

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

    private static Brush GetBrush(string key) =>
        (Brush)Application.Current.TryFindResource(key) ?? Brushes.Gray;

    #endregion
}


