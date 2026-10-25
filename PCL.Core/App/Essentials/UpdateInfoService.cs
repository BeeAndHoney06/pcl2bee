using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.Logging;
using PCL.Core.Utils;

namespace PCL.Core.App.Essentials;

/// <summary>
///     GitHub Pages 投放的更新信息（update.json）模型。
/// </summary>
public sealed record UpdateInfoModel(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("versionCode")] int VersionCode,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("releaseUrl")] string ReleaseUrl
)
{
    /// <summary>是否晚于指定版本。</summary>
    public bool IsNewerThan(int currentVersionCode) => VersionCode > currentVersionCode;
}

/// <summary>
///     从 GitHub Pages 拉取与解析更新信息。
///     设计为只读探测：不做自动下载/替换，引导用户前往 GitHub Release 页。
/// </summary>
public static class UpdateInfoService
{
    /// <summary>
    ///     update.json 的投放地址（GitHub Pages 占位符，部署后替换）。
    /// </summary>
    public const string UpdateInfoUrl = "https://pcl2bee.pages.dev/update.json";

    /// <summary>缓存最近一次成功获取的更新信息与获取时间。</summary>
    public static UpdateInfoModel? LastInfo { get; private set; }

    public static DateTime LastCheckTime { get; private set; } = DateTime.MinValue;

    /// <summary>上次检查是否发生错误（供 UI 区分“无更新”与“检查失败”）。</summary>
    public static Exception? LastError { get; private set; }

    /// <summary>共享 HttpClient（遵循 GitHub Pages 的连接复用）。</summary>
    private static readonly System.Net.Http.HttpClient _HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    ///     拉取并解析更新信息。失败返回 null 并记录 LastError，不抛出。
    /// </summary>
    public static UpdateInfoModel? Fetch(bool forceRefresh = false)
    {
        if (!forceRefresh && LastInfo is not null &&
            (DateTime.Now - LastCheckTime).TotalMinutes < 30)
            return LastInfo;
        try
        {
            using var response = _HttpClient.GetAsync(UpdateInfoUrl).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var info = JsonSerializer.Deserialize<UpdateInfoModel>(json, JsonCompat.SerializerOptions);
            if (info is null || string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.ReleaseUrl))
                throw new InvalidOperationException("update.json 缺少必要字段（version / releaseUrl）");
            LastInfo = info;
            LastCheckTime = DateTime.Now;
            LastError = null;
            return info;
        }
        catch (Exception ex)
        {
            LastError = ex;
            LogWrapper.Warn(ex, "Update", "拉取 GitHub Pages 更新信息失败");
            return null;
        }
    }

    /// <summary>
    ///     检查更新：返回 (更新信息, 是否有新版本)。有新版本时 info 非 null。
    /// </summary>
    public static (UpdateInfoModel? Info, bool HasUpdate, bool CheckFailed) Check(bool forceRefresh = false)
    {
        var info = Fetch(forceRefresh);
        if (info is null)
            return (null, false, true);
        return (info, info.IsNewerThan(Basics.VersionCode), false);
    }
}
