using System.Net;
using System.Net.Http;
using PCL.Core.App;

namespace PCL.Network;

public static class RequestSigning
{
    internal static string SecretCdnSign(string urlWithMark)
    {
        if (!urlWithMark.EndsWithF("{CDN}"))
            return urlWithMark;
        return urlWithMark.Replace("{CDN}", "").Replace(" ", "%20");
    }
    
    /// <summary>
    ///     设置 Headers 的 UA、Referer。
    /// </summary>
    internal static void SecretHeadersSign(string url, ref HttpRequestMessage client, bool useBrowserUserAgent = false,
        string customUserAgent = "")
    {
        // 仅对 CurseForge 系强制 HTTP/2（其 API 需要）；
        // 其余请求保持默认协商——经代理（如 Clash）访问部分源时，HTTP/2 隧道会导致连接被强制关闭
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
            && (parsedUri.Host == "api.curseforge.com"
            || parsedUri.Host == "edge.forgecdn.net"
            || parsedUri.Host == "mediafilez.forgecdn.net"))
        {
            client.Version = HttpVersion.Version20;
            client.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            client.Headers.Add("x-api-key", Secrets.CurseForgeAPIKey);
        }
        var userAgent = !string.IsNullOrEmpty(customUserAgent)
            ? customUserAgent
            : useBrowserUserAgent
                ? $"PCL2/{ModBase.upstreamVersion}.{ModBase.versionBranchCode} PCLCE/{ModBase.versionStandardCode} Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0"
                : $"PCL2/{ModBase.upstreamVersion}.{ModBase.versionBranchCode} PCLCE/{ModBase.versionStandardCode}";
        client.Headers.Add("User-Agent", userAgent);
    }
}