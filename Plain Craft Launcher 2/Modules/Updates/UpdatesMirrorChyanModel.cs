using System.Net.Http;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;
using PCL.Core.IO.Net.Http;

namespace PCL;

public class UpdatesMirrorChyanModel : IUpdateSource // Mirror 酱的更新格式
{
    private const string mirrorChyanBaseUrl =
        "https://mirrorchyan.com/api/resources/{cid}/latest?cdk={cdk}&os=win&arch={arch}&channel={channel}";

    private const string myCid = "PCL2-CE";
    public string SourceName { get; set; } = "MirrorChyan";

    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(Config.Update.MirrorChyanKey);
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        using (var response = HttpRequest.Create(GetUrl(channel, arch))
                   .SendAsync()
                   .GetAwaiter()
                   .GetResult())
        {
            var ret = (JsonObject)ModBase.GetJson(response.AsString());
            if ((int)ret["code"] != 0)
                throw new Exception("Mirror 酱获取数据不成功");
            var data = ret["data"];
            var upd_url = data["url"]?.ToString();
            if (data is not null && string.IsNullOrWhiteSpace(upd_url))
                throw new Exception("无效 CDK");
            return new VersionDataModel
            {
                Source = SourceName,
                VersionCode = (int)data["version_number"],
                VersionName = (string)data["version_name"],
                Sha256 = (string)data["sha256"],
                Changelog = (string)data["release_note"]
            };
        }
    }

    public bool RefreshCache()
    {
        return true;
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var latest = GetLatestVersion(channel, arch);
        return currentVersion >= SemVer.Parse(latest.VersionName);
    }


    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var loaders = new List<ModLoader.LoaderBase>();
        string expectedSha = null;
        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(Lang.Text("Update.Task.GetDownloadInfo"), load =>
        {
            var ret = (JsonObject)Requester.FetchJson(GetUrl(channel, arch), RequestParam.WithRetry);
            var dlUrl = ret["data"]["url"]?.ToString();
            if (dlUrl is null)
                throw new Exception("Mirror 酱下载源不可用");
            expectedSha = ret["data"]["sha256"]?.ToString();
            load.output = new List<DownloadFile> { new(new[] { dlUrl }, output) };
        }));
        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"), new List<DownloadFile>()));
        loaders.Add(new ModLoader.LoaderTask<string, int>("校验更新文件", _ =>
        {
            // 校验更新文件，防止被篡改的更新包被替换并执行
            if (!string.IsNullOrWhiteSpace(expectedSha))
            {
                var actualSha = ModBase.GetFileSHA256(output) ?? "";
                if (!string.Equals(actualSha, expectedSha.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("更新文件 SHA256 校验失败，更新包可能已被篡改，已中止更新");
            }
        }));
        return loaders;
    }

    private string GetUrl(UpdateChannel channel, UpdateArch arch)
    {
        var reqUrl = mirrorChyanBaseUrl;
        reqUrl = reqUrl.Replace("{cid}", myCid);
        reqUrl = reqUrl.Replace("{cdk}", Config.Update.MirrorChyanKey);
        reqUrl = reqUrl.Replace("{arch}", arch.ToString());
        reqUrl = reqUrl.Replace("{channel}", channel.ToString());
        return reqUrl;
    }
}
