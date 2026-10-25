using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.IoC;
using PCL.Core.UI;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageSetupLauncherMisc
{
    private bool isFirstLoad = true;

    private new bool isLoaded;

    public PageSetupLauncherMisc()
    {
        InitializeComponent();
        Loaded += PageSetupLink_Loaded;
        Loaded += (_, _) => Reload();
    }

    private void PageSetupLink_Loaded(object sender, RoutedEventArgs e)
    {
        // 重复加载部分
        PanBack.ScrollToHome();

        // 非重复加载部分
        if (isLoaded)
            return;
        isLoaded = true;

        ModAnimation.AniControlEnabled += 1;
        SliderLoad();
        Reload();
        ModAnimation.AniControlEnabled -= 1;

        // 静默检查已发现新版本：直接展示结果（用户进入本页即视为已查看）
        if (SilentUpdateAvailable)
        {
            AcknowledgeUpdateHint();
            var info = PCL.Core.App.Essentials.UpdateInfoService.LastInfo;
            if (info is not null)
            {
                LabUpdateStatus.Text = Lang.Text("Setup.Misc.Update.NewVersion", info.Version);
                LabUpdateDetail.Text = string.IsNullOrEmpty(info.Notes) ? info.Date : $"{info.Date}\n{info.Notes}";
                BtnUpdateOpen.Visibility = Visibility.Visible;
            }
        }
    }

    public void Reload()
    {
        // 系统设置

        CheckSystemDisableHardwareAcceleration.Checked = Config.System.DisableHardwareAcceleration;
        SliderAniFPS.Value = Config.System.AnimationFpsLimit;
        SliderMaxLog.Value = Config.System.MaxGameLog;
        CheckSystemTelemetry.Checked = Config.System.Telemetry;

        // 网络
        TextSystemHttpProxy.Text = Config.Network.HttpProxy.CustomAddress;
        TextSystemHttpProxyCustomUsername.Text = Config.Network.HttpProxy.CustomUsername;
        TextSystemHttpProxyCustomPassword.Text = Config.Network.HttpProxy.CustomPassword;
        ((MyRadioBox)FindName($"RadioHttpProxyType{Config.Network.HttpProxy.Type}")).SetChecked(true, false);
        CheckNetDohEnable.Checked = Config.Network.EnableDoH;

        // 调试选项
        SliderDebugAnim.Value = Config.Debug.AnimationSpeed;
        CheckDebugSkipCopy.Checked = Config.Debug.DontCopy;
        CheckDebugMode.Checked = Config.Debug.Enabled;
        CheckDebugDelay.Checked = Config.Debug.AddRandomDelay;
    }

    // 初始化
    public void Reset()
    {
        try
        {
            Config.Network.Reset();
            Config.Debug.Reset();
            Config.System.Reset();
            ModBase.Log("[Setup] 已初始化启动器-杂项页设置");
            HintService.Hint(Lang.Text("Setup.Misc.Initialized"), HintType.Success, false);
            Reload();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Setup.Misc.Error.InitFailed"),
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Setup.Misc.Error.InitFailed"));
        }

        Reload();
    }

    // 将控件改变路由到设置改变
    private void ComboChange(object senderRaw, SelectionChangedEventArgs e)
    {
        var sender = (MyComboBox)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.SelectedIndex);
    }

    private void RadioBoxChange(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyRadioBox)senderRaw;
        var gotCfg = sender.Tag?.ToString()?.Split("/") ?? Array.Empty<string>();
        if (ModAnimation.AniControlEnabled == 0 && gotCfg.Length >= 2)
            SetByTag(gotCfg[0], int.Parse(gotCfg[1]));
    }

    private void CheckBoxChange(object senderRaw, bool user)
    {
        var sender = (MyCheckBox)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.Checked);
    }

    private void SliderChange(object senderRaw, bool user)
    {
        var sender = (MySlider)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.Value);
    }

    private static void SetByTag(string tag, object value)
        => ConfigService.TrySetValue(tag, value);

    // 网络
    private void ApplyHttpProxyBtn_OnClicked(object sender, MouseButtonEventArgs e)
    {
        Config.Network.HttpProxy.CustomAddress = TextSystemHttpProxy.Text;
        Config.Network.HttpProxy.CustomUsername = TextSystemHttpProxyCustomUsername.Text;
        Config.Network.HttpProxy.CustomPassword = TextSystemHttpProxyCustomPassword.Text;
    }

    // 滑动条
    private void SliderLoad()
    {
        SliderDebugAnim.getHintText = new Func<object, object>(v =>
            (int)v > 29
                ? Lang.Text("Common.Action.Close")
                : Lang.Number(Math.Round(Convert.ToDouble(v) / 10 + 0.1d, 1), "N1") + "x");
        SliderAniFPS.getHintText = new Func<object, string>(v => Lang.Number(Convert.ToInt32(v) + 1, "N0") + " FPS");
        // y = 10x + 50 (0 <= x <= 5, 50 <= y <= 100)
        // y = 50x - 150 (5 < x <= 13, 100 < y <= 500)
        // y = 100x - 800 (13 < x <= 28, 500 < y <= 2000)
        SliderMaxLog.getHintText = new Func<object, object>(v =>
        {
            var val = Convert.ToInt32(v);
            return val switch
            {
                <= 5 => val * 10 + 50,
                <= 13 => val * 50 - 150,
                <= 28 => val * 100 - 800,
                _ => Lang.Text("Setup.Misc.System.MaxLogLines.Unlimited")
            };
        });
    }

    // 硬件加速
    private void Check_DisableHardwareAcceleration(object _, bool __)
    {
        HintService.Hint(Lang.Text("Setup.Misc.HardwareAcceleration.RestartNotice"));
    }

    // 调试模式
    private void CheckDebugMode_Change(object _, bool __)
    {
        if (ModAnimation.AniControlEnabled == 0)
            HintService.Hint(Lang.Text("Setup.Misc.Debug.Mode.Hint"), log: false);
    }


    private void CheckDebugMode_OnChange(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        CheckDebugMode_Change(sender, user);
    }

    private void CheckSystemDisableHardwareAcceleration_OnChange(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        Check_DisableHardwareAcceleration(sender, user);
    }


    #region 导出 / 导入设置

    private void BtnSystemSettingExp_Click(object sender, MouseButtonEventArgs e)
    {
        var savePath =
            SystemDialogs.SelectSaveFile(Lang.Text("Setup.Misc.System.ExportSettings.SaveTitle"), "PCL 全局配置.json", Lang.Text("Setup.Misc.System.ExportSettings.Filter"), ModBase.exePath);
        if (string.IsNullOrWhiteSpace(savePath))
            return;
        File.Copy(ConfigService.SharedConfigPath, savePath, true);
        HintService.Hint(Lang.Text("Setup.Misc.System.ExportSettings.Success"), HintType.Success);
        ModBase.OpenExplorer(savePath);
    }

    private void BtnSystemSettingImp_Click(object sender, MouseButtonEventArgs e)
    {
        var sourcePath = SystemDialogs.SelectFile(Lang.Text("Setup.Misc.System.ExportSettings.Filter"), Lang.Text("Setup.Misc.System.ImportSettings.SelectTitle"));
        if (string.IsNullOrWhiteSpace(sourcePath))
            return;
        File.Copy(sourcePath, ConfigService.SharedConfigPath, true);
        ModMain.MyMsgBox(Lang.Text("Setup.Misc.System.ImportSettings.Success.Message"), button1: Lang.Text("Setup.Misc.System.ImportSettings.Success.Restart"), forceWait: true);
        Process.Start(new ProcessStartInfo(Basics.ExecutablePath));
        FormMain.EndProgramForce();
    }

    #endregion

    #region 停止使用 PCL2 Bee

    private void BtnSystemStopUsingPclCe_Click(object sender, MouseButtonEventArgs e)
    {
        var result = ModMain.MyMsgBox(
            Lang.Text("Setup.Misc.System.StopUsingPclCe.Message"),
            Lang.Text("Setup.Misc.System.StopUsingPclCe.Title"),
            Lang.Text("Common.Action.Continue"),
            Lang.Text("Setup.Misc.System.StopUsingPclCe.ContinueAndRemove"),
            Lang.Text("Common.Action.Cancel"),
            isWarn: true);

        if (result < 3)
        {
            if (ModMain.MyMsgBox(
                    Lang.Text("Setup.Misc.System.StopUsingPclCe.Message.Final"),
                    Lang.Text("Common.Dialog.Warning"),
                    Lang.Text("Common.Action.Continue"),
                    Lang.Text("Common.Action.Cancel"),
                    isWarn: true) == 1)
            {
                StopUsingPClCeCore(result == 2);
            }
        }
    }

    private void StopUsingPClCeCore(bool removeMcResources)
    {
        // 删除 MC 文件夹内的 PCL2 Bee 配置
        if (removeMcResources && States.Game.Folders != "")
        {
            foreach (var path in States.Game.Folders.Split('|'))
            {
                var realPath = path.Split('>')[1];
                Delete([Path.Combine(realPath, "PCL.ini")]);

                var versionsPath = Path.Combine(realPath, "versions");
                if (!Directory.Exists(versionsPath)) continue;
                
                Delete(
                    Directory.EnumerateDirectories(versionsPath)
                        .Select(p => Path.Combine(p, "PCL", "config.v1.yml"))
                );
            }
        }
        
        // 由于 CE 文件夹正在使用，使用延迟调用 CMD 的方法删除
        List<string> foldersToDelete =
        [
            Paths.Data,
            Paths.OldSharedData,
            Paths.SharedData,
            Paths.SharedLocalData,
            Paths.Temp
        ];

        var sb = new StringBuilder();
        sb.Append("/c timeout /t 5 /nobreak >nul & ");
        foreach (var folder in foldersToDelete)
        {
            sb.Append($"rmdir /s /q \"{folder}\" & ");
        }
        
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = sb.ToString(),
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });

        // 强制退出
        KernelInterop.ExitProcess();
            
        void Delete(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                }
                catch (Exception)
                {
                    //
                }
            }
        }
    }

    #endregion

    #region 启动器更新检查

    /// <summary>启动后的静默检查是否已执行（本次进程内只查一次）。</summary>
    private static bool silentCheckDone;

    /// <summary>静默检查发现新版本时置 true，供设置页首次加载时展示。</summary>
    public static bool SilentUpdateAvailable { get; private set; }

    /// <summary>用户已查看更新提示（点开杂项页或手动检查后），左侧栏标记清除。</summary>
    public static bool UpdateHintAcknowledged { get; private set; }

    internal static void RunSilentUpdateCheck()
    {
        if (silentCheckDone) return;
        silentCheckDone = true;
        ModBase.RunInThread(() =>
        {
            try
            {
                var (info, hasUpdate, _) = PCL.Core.App.Essentials.UpdateInfoService.Check();
                SilentUpdateAvailable = hasUpdate;
                if (hasUpdate && info is not null)
                {
                    ModBase.Log("[System] 检测到启动器有新版本（详见 设置 → 其他 → 启动器更新）");
                    // 一次性 Toast 提示（不阻塞、自动消失）
                    ModBase.RunInUi(() => HintService.Hint(
                        Lang.Text("Setup.Misc.Update.SilentFound", info.Version), HintType.Info));
                    // 通知设置左侧栏刷新标记
                    ModMain.frmSetupLeft?.RefreshUpdateBadge();
                }
            }
            catch
            {
                // 静默检查失败不打扰用户
            }
        });
    }

    /// <summary>进入杂项页/手动检查后调用：清除待查看标记。</summary>
    private static void AcknowledgeUpdateHint()
    {
        if (UpdateHintAcknowledged) return;
        UpdateHintAcknowledged = true;
        ModMain.frmSetupLeft?.RefreshUpdateBadge();
    }

    private void BtnUpdateCheck_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        AcknowledgeUpdateHint();
        LabUpdateStatus.Text = Lang.Text("Setup.Misc.Update.Checking");
        LabUpdateDetail.Text = "";
        BtnUpdateCheck.IsEnabled = false;
        ModBase.RunInThread(() =>
        {
            var (info, hasUpdate, failed) = PCL.Core.App.Essentials.UpdateInfoService.Check(true);
            ModBase.RunInUi(() =>
            {
                BtnUpdateCheck.IsEnabled = true;
                if (failed)
                {
                    LabUpdateStatus.Text = Lang.Text("Setup.Misc.Update.CheckFailed");
                    LabUpdateDetail.Text = PCL.Core.App.Essentials.UpdateInfoService.LastError?.Message ?? "";
                    BtnUpdateOpen.Visibility = Visibility.Collapsed;
                    return;
                }

                if (hasUpdate && info is not null)
                {
                    LabUpdateStatus.Text = Lang.Text("Setup.Misc.Update.NewVersion", info.Version);
                    LabUpdateDetail.Text = string.IsNullOrEmpty(info.Notes)
                        ? info.Date
                        : $"{info.Date}\n{info.Notes}";
                    BtnUpdateOpen.Visibility = Visibility.Visible;
                }
                else
                {
                    LabUpdateStatus.Text = Lang.Text("Setup.Misc.Update.UpToDate",
                        ModBase.versionBaseName, ModBase.versionCode);
                    LabUpdateDetail.Text = "";
                    BtnUpdateOpen.Visibility = Visibility.Collapsed;
                }
            });
        });
    }

    private void BtnUpdateOpen_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var info = PCL.Core.App.Essentials.UpdateInfoService.LastInfo;
        if (info is null || string.IsNullOrWhiteSpace(info.ReleaseUrl)) return;
        ModBase.OpenWebsite(info.ReleaseUrl);
    }

    #endregion
}
