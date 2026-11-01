// ─────────────────────────────────────────────────────────────────────────────
// 服务器管理模块：移植自 ErieneNeko/PCL-CE-ServerControlPad
//   https://github.com/ErieneNeko/PCL-CE-ServerControlPad
//   原作者：ErieneNeko    许可：Apache License 2.0
//   本文件在原作基础上做了适配修改（命名空间/本地化/对话框 API 对齐本项目）。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// MCSManager 面板 API 客户端。
/// 路由、参数与权限说明严格依据官方文档：
/// https://docs.mcsmanager.com/zh_cn/apis/get_apikey.html
/// https://docs.mcsmanager.com/zh_cn/apis/api_dashboard.html
/// https://docs.mcsmanager.com/zh_cn/apis/api_users.html
/// https://docs.mcsmanager.com/zh_cn/apis/api_instance.html
/// https://docs.mcsmanager.com/zh_cn/apis/api_daemon.html
/// https://docs.mcsmanager.com/zh_cn/apis/api_fileManager.html
/// https://docs.mcsmanager.com/zh_cn/apis/api_imageManager.html
/// </summary>
public sealed class McsmClient
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(12)
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public McsmClient(string address, string apiKey, HttpClient? httpClient = null)
    {
        BaseUri = NormalizeAddress(address);
        _apiKey = (apiKey ?? "").Trim();
        _http = httpClient ?? SharedHttp;
        Timeout = TimeSpan.FromSeconds(25);
    }

    public Uri BaseUri { get; }
    public TimeSpan Timeout { get; set; }

    public async Task<McsmSession> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        var session = new McsmSession(this, user);
        if (session.IsAdmin)
        {
            try
            {
                var setting = await GetPanelSettingAsync(cancellationToken).ConfigureAwait(false);
                session.CanUseFileManager = GetBool(setting, "canFileManager") ?? true;
                session.EnableApiKey = GetBool(setting, "enableApiKey") ?? false;
                session.Overview = await GetOverviewAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (McsmApiException ex) when (ex.Kind is McsmErrorKind.Forbidden or McsmErrorKind.Api)
            {
                LogWrapper.Debug(ex, "MCSM", "当前 API Key 无法读取面板概览或设置，已按普通权限处理。");
            }
        }

        return session;
    }

    #region Dashboard

    public async Task<McsmUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/auth", ct: ct).ConfigureAwait(false);
        if (data is null)
            throw new McsmApiException("面板未返回当前用户信息。", McsmErrorKind.Parse);
        return ParseUser(data);
    }

    public async Task<McsmOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/overview", ct: ct).ConfigureAwait(false);
        return ParseOverview(data);
    }

    public async Task<JsonNode?> GetPanelSettingAsync(CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Get, "api/overview/setting", ct: ct).ConfigureAwait(false);

    #endregion

    #region Users

    public async Task<McsmPage<McsmUser>> SearchUsersAsync(
        string? keyword = null, int page = 1, int pageSize = 20, string? role = null,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = Math.Max(1, page).ToString(),
            ["page_size"] = Math.Clamp(pageSize, 1, 50).ToString()
        };
        if (!string.IsNullOrWhiteSpace(keyword))
            query["userName"] = keyword.Trim();
        if (!string.IsNullOrWhiteSpace(role))
            query["role"] = role;

        var data = await SendAsync(HttpMethod.Get, "api/auth/search", query, ct: ct).ConfigureAwait(false);
        var result = new McsmPage<McsmUser>();
        if (data is null)
            return result;
        result.Page = GetInt(data, "page") ?? 1;
        result.PageSize = GetInt(data, "pageSize") ?? pageSize;
        result.MaxPage = GetInt(data, "maxPage") ?? 1;
        result.Total = GetInt(data, "total") ?? 0;
        if (Prop(data, "data") is JsonArray users)
        {
            foreach (var user in users)
            {
                if (user is not null)
                    result.Items.Add(ParseUser(user));
            }
        }

        return result;
    }

    public async Task<McsmUser> CreateUserAsync(
        string userName, string password, int permission = 1, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Post, "api/auth", body: new JsonObject
        {
            ["username"] = userName,
            ["password"] = password,
            ["permission"] = permission
        }, ct: ct).ConfigureAwait(false);
        if (data is null)
            throw new McsmApiException("创建用户成功但面板未返回用户信息。", McsmErrorKind.Parse);
        return ParseUser(data);
    }

    public async Task DeleteUsersAsync(IEnumerable<string> uuids, CancellationToken ct = default)
    {
        var array = new JsonArray();
        foreach (var uuid in uuids)
            array.Add(uuid);
        await SendAsync(HttpMethod.Delete, "api/auth", body: array, ct: ct).ConfigureAwait(false);
    }

    #endregion

    #region Daemons

    public async Task<IReadOnlyList<McsmDaemon>> GetDaemonsAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/service/remote_services", ct: ct).ConfigureAwait(false);
        var result = new List<McsmDaemon>();
        if (data is not JsonArray array)
            return result;
        foreach (var node in array)
        {
            if (node is not null)
                result.Add(ParseDaemon(node));
        }

        return result;
    }

    public async Task<IReadOnlyList<McsmDaemon>> GetDaemonSystemsAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/service/remote_services_system", ct: ct)
            .ConfigureAwait(false);
        var result = new List<McsmDaemon>();
        if (data is not JsonArray array)
            return result;
        foreach (var node in array)
        {
            if (node is null)
                continue;
            result.Add(new McsmDaemon
            {
                Available = true,
                Version = GetString(node, "version") ?? "",
                System = ParseSystem(Prop(node, "system")),
                RunningInstances = GetInt(Prop(node, "instance"), "running") ?? 0,
                TotalInstances = GetInt(Prop(node, "instance"), "total") ?? 0
            });
        }

        return result;
    }

    public async Task<string> AddDaemonAsync(
        string ip, int port, string remarks, string apiKey, string prefix = "", CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Post, "api/service/remote_service", body: new JsonObject
        {
            ["ip"] = ip,
            ["port"] = port,
            ["prefix"] = prefix ?? "",
            ["remarks"] = remarks,
            ["apiKey"] = apiKey
        }, ct: ct).ConfigureAwait(false);
        return data?.GetValue<string>() ?? "";
    }

    public async Task DeleteDaemonAsync(string uuid, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, "api/service/remote_service",
            new Dictionary<string, string?> { ["uuid"] = uuid }, ct: ct).ConfigureAwait(false);

    public async Task ConnectDaemonAsync(string uuid, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Get, "api/service/link_remote_service",
            new Dictionary<string, string?> { ["uuid"] = uuid }, ct: ct).ConfigureAwait(false);

    #endregion

    #region Instances

    public async Task<McsmPage<McsmInstance>> GetInstancesAsync(
        string daemonId, int page = 1, int pageSize = 20, string? name = null,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["page"] = Math.Max(1, page).ToString(),
            ["page_size"] = Math.Clamp(pageSize, 1, 50).ToString()
        };
        if (!string.IsNullOrWhiteSpace(name))
            query["instance_name"] = name.Trim();

        var data = await SendAsync(HttpMethod.Get, "api/service/remote_service_instances", query, ct: ct)
            .ConfigureAwait(false);
        var result = new McsmPage<McsmInstance>();
        if (data is null)
            return result;
        result.Page = GetInt(data, "page") ?? 1;
        result.MaxPage = GetInt(data, "maxPage") ?? 1;
        result.PageSize = GetInt(data, "pageSize") ?? pageSize;
        if (Prop(data, "data") is JsonArray instances)
        {
            foreach (var node in instances)
            {
                if (node is not null)
                    result.Items.Add(ParseInstance(node, daemonId));
            }
        }

        result.Total = result.Items.Count;
        return result;
    }

    public async Task<McsmInstance> GetInstanceAsync(
        string daemonId, string uuid, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/instance", new Dictionary<string, string?>
        {
            ["uuid"] = uuid,
            ["daemonId"] = daemonId
        }, ct: ct).ConfigureAwait(false);
        if (data is null)
            throw new McsmApiException("面板未返回实例详情。", McsmErrorKind.Parse);
        return ParseInstance(data, daemonId);
    }

    public Task StartInstanceAsync(string daemonId, string uuid, CancellationToken ct = default) =>
        InstanceActionAsync("open", daemonId, uuid, ct);

    public Task StopInstanceAsync(string daemonId, string uuid, CancellationToken ct = default) =>
        InstanceActionAsync("stop", daemonId, uuid, ct);

    public Task RestartInstanceAsync(string daemonId, string uuid, CancellationToken ct = default) =>
        InstanceActionAsync("restart", daemonId, uuid, ct);

    public Task KillInstanceAsync(string daemonId, string uuid, CancellationToken ct = default) =>
        InstanceActionAsync("kill", daemonId, uuid, ct);

    public async Task DeleteInstancesAsync(
        string daemonId, IEnumerable<string> uuids, bool deleteFile = false, CancellationToken ct = default)
    {
        var array = new JsonArray();
        foreach (var uuid in uuids)
            array.Add(uuid);
        await SendAsync(HttpMethod.Delete, "api/instance",
            new Dictionary<string, string?> { ["daemonId"] = daemonId },
            new JsonObject { ["uuids"] = array, ["deleteFile"] = deleteFile }, ct).ConfigureAwait(false);
    }

    private async Task InstanceActionAsync(string action, string daemonId, string uuid, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Get, $"api/protected_instance/{action}", new Dictionary<string, string?>
        {
            ["uuid"] = uuid,
            ["daemonId"] = daemonId
        }, ct: ct).ConfigureAwait(false);
    }

    public async Task SendCommandAsync(
        string daemonId, string uuid, string command, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Get, "api/protected_instance/command", new Dictionary<string, string?>
        {
            ["uuid"] = uuid,
            ["daemonId"] = daemonId,
            ["command"] = command
        }, ct: ct).ConfigureAwait(false);
    }

    public async Task<string> GetOutputLogAsync(
        string daemonId, string uuid, string size = "100KB", CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/protected_instance/outputlog",
            new Dictionary<string, string?>
            {
                ["uuid"] = uuid,
                ["daemonId"] = daemonId,
                ["size"] = size
            }, ct: ct).ConfigureAwait(false);
        return data?.GetValue<string>() ?? "";
    }

    #endregion

    #region Files

    public async Task<McsmFileList> ListFilesAsync(
        string daemonId, string uuid, string target, int page = 0, int pageSize = 100,
        string? fileName = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid,
            ["target"] = string.IsNullOrWhiteSpace(target) ? "/" : target,
            ["page"] = Math.Max(0, page).ToString(),
            ["page_size"] = Math.Clamp(pageSize, 1, 100).ToString()
        };
        if (!string.IsNullOrWhiteSpace(fileName))
            query["file_name"] = fileName;

        var data = await SendAsync(HttpMethod.Get, "api/files/list", query, ct: ct).ConfigureAwait(false);
        var result = new McsmFileList();
        if (data is null)
            return result;
        result.AbsolutePath = GetString(data, "absolutePath") ?? "";
        result.Page = GetInt(data, "page") ?? 0;
        result.Total = GetInt(data, "total") ?? 0;
        if (Prop(data, "items") is JsonArray items)
        {
            foreach (var node in items)
            {
                if (node is null)
                    continue;
                result.Items.Add(new McsmFileEntry
                {
                    Name = GetString(node, "name") ?? "",
                    Size = GetLong(node, "size") ?? 0,
                    Time = GetString(node, "time") ?? "",
                    Mode = GetInt(node, "mode") ?? 0,
                    Type = GetInt(node, "type") ?? 1
                });
            }
        }

        return result;
    }

    public async Task<string> ReadFileAsync(
        string daemonId, string uuid, string target, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Put, "api/files/", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid
        }, new JsonObject { ["target"] = target }, ct).ConfigureAwait(false);
        return data?.GetValue<string>() ?? "";
    }

    public async Task WriteFileAsync(
        string daemonId, string uuid, string target, string text, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Put, "api/files/", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid
        }, new JsonObject { ["target"] = target, ["text"] = text }, ct).ConfigureAwait(false);
    }

    public async Task DeleteFilesAsync(
        string daemonId, string uuid, IEnumerable<string> targets, CancellationToken ct = default)
    {
        var array = new JsonArray();
        foreach (var target in targets)
            array.Add(target);
        await SendAsync(HttpMethod.Delete, "api/files", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid
        }, new JsonObject { ["targets"] = array }, ct).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(
        string daemonId, string uuid, string target, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "api/files/mkdir", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid
        }, new JsonObject { ["target"] = target }, ct).ConfigureAwait(false);

    public async Task MoveFileAsync(
        string daemonId, string uuid, string source, string target, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Put, "api/files/move", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid
        }, new JsonObject
        {
            ["targets"] = new JsonArray(new JsonArray(source, target))
        }, ct).ConfigureAwait(false);

    public async Task<JsonNode?> GetFileStatusAsync(
        string daemonId, string uuid, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Get, "api/files/status", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid
        }, ct: ct).ConfigureAwait(false);

    public async Task UploadFileAsync(
        string daemonId, string uuid, string uploadDirectory, string localPath,
        CancellationToken ct = default)
    {
        var config = await SendAsync(HttpMethod.Post, "api/files/upload", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid,
            ["upload_dir"] = uploadDirectory
        }, ct: ct).ConfigureAwait(false);
        var password = GetString(config, "password")
                       ?? throw new McsmApiException("面板未返回上传凭据。", McsmErrorKind.Parse);
        var address = GetString(config, "addr")
                      ?? throw new McsmApiException("面板未返回节点地址。", McsmErrorKind.Parse);
        var uri = new Uri(DaemonBaseUri(address), $"upload/{Uri.EscapeDataString(password)}");
        await using var stream = File.OpenRead(localPath);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(localPath));
        using var response = await _http.PostAsync(uri, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new McsmApiException($"上传文件失败（HTTP {(int)response.StatusCode}）。", McsmErrorKind.Api,
                (int)response.StatusCode);
    }

    public async Task DownloadFileAsync(
        string daemonId, string uuid, string remotePath, string localPath,
        CancellationToken ct = default)
    {
        var config = await SendAsync(HttpMethod.Get, "api/files/download", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["uuid"] = uuid,
            ["file_name"] = remotePath
        }, ct: ct).ConfigureAwait(false);
        var password = GetString(config, "password")
                       ?? throw new McsmApiException("面板未返回下载凭据。", McsmErrorKind.Parse);
        var address = GetString(config, "addr")
                      ?? throw new McsmApiException("面板未返回节点地址。", McsmErrorKind.Parse);
        var fileName = remotePath.TrimStart('/');
        var uri = new Uri(DaemonBaseUri(address),
            $"download/{Uri.EscapeDataString(password)}/{string.Join("/", fileName.Split('/').Select(Uri.EscapeDataString))}");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new McsmApiException($"下载文件失败（HTTP {(int)response.StatusCode}）。", McsmErrorKind.Api,
                (int)response.StatusCode);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        await using var target = File.Create(localPath);
        await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    #endregion

    #region Images / Docker

    public async Task<IReadOnlyList<McsmImage>> GetImagesAsync(string daemonId, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/environment/image",
            new Dictionary<string, string?> { ["daemonId"] = daemonId }, ct: ct).ConfigureAwait(false);
        var result = new List<McsmImage>();
        if (data is not JsonArray array)
            return result;
        foreach (var node in array)
        {
            if (node is null)
                continue;
            var image = new McsmImage
            {
                Id = GetString(node, "Id") ?? GetString(node, "id") ?? "",
                Size = GetLong(node, "Size") ?? GetLong(node, "size") ?? 0,
                Created = GetLong(node, "Created") ?? 0
            };
            if (Prop(node, "RepoTags") is JsonArray tags)
            {
                foreach (var tag in tags)
                {
                    var value = tag?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(value))
                        image.Tags.Add(value);
                }
            }

            result.Add(image);
        }

        return result;
    }

    public async Task<IReadOnlyList<McsmContainer>> GetContainersAsync(
        string daemonId, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/environment/containers",
            new Dictionary<string, string?> { ["daemonId"] = daemonId }, ct: ct).ConfigureAwait(false);
        var result = new List<McsmContainer>();
        if (data is not JsonArray array)
            return result;
        foreach (var node in array)
        {
            if (node is null)
                continue;
            var container = new McsmContainer
            {
                Id = GetString(node, "Id") ?? "",
                Image = GetString(node, "Image") ?? "",
                State = GetString(node, "State") ?? "",
                Status = GetString(node, "Status") ?? ""
            };
            if (Prop(node, "Names") is JsonArray names)
                container.Names.AddRange(names.Select(name => name?.GetValue<string>() ?? "")
                    .Where(name => name.Length > 0));
            result.Add(container);
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetNetworkModesAsync(
        string daemonId, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/environment/networkModes",
            new Dictionary<string, string?> { ["daemonId"] = daemonId }, ct: ct).ConfigureAwait(false);
        return data is JsonArray array
            ? array.Select(node => node?.GetValue<string>() ?? "").Where(value => value.Length > 0).ToList()
            : [];
    }

    public async Task<Dictionary<string, int>> GetImageProgressAsync(
        string daemonId, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "api/environment/progress",
            new Dictionary<string, string?> { ["daemonId"] = daemonId }, ct: ct).ConfigureAwait(false);
        var result = new Dictionary<string, int>();
        if (data is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var value))
                    result[pair.Key] = value;
            }
        }

        return result;
    }

    public async Task BuildImageAsync(
        string daemonId, string dockerFile, string name, string tag, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Post, "api/environment/image",
            new Dictionary<string, string?> { ["daemonId"] = daemonId }, new JsonObject
            {
                ["dockerFile"] = dockerFile,
                ["name"] = name,
                ["tag"] = tag
            }, ct).ConfigureAwait(false);
    }

    public async Task DeleteImageAsync(
        string daemonId, string imageId, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, "api/environment/image", new Dictionary<string, string?>
        {
            ["daemonId"] = daemonId,
            ["imageId"] = imageId
        }, ct: ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetImagePlatformsAsync(
        string daemonId, string imageName, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Post, "api/environment/image_platforms",
            new Dictionary<string, string?> { ["daemonId"] = daemonId },
            new JsonObject { ["imageName"] = imageName }, ct).ConfigureAwait(false);
        return data is JsonArray array
            ? array.Select(node => node?.GetValue<string>() ?? "").Where(value => value.Length > 0).ToList()
            : [];
    }

    #endregion

    #region HTTP core

    private async Task<JsonNode?> SendAsync(
        HttpMethod method, string path, IReadOnlyDictionary<string, string?>? query = null,
        JsonNode? body = null, CancellationToken ct = default)
    {
        var uri = BuildUri(path, query);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("PCL-CE-ServerManager");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        HttpResponseMessage response;
        string text;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new McsmApiException($"连接 MCSManager 超时（{Timeout.TotalSeconds:0} 秒）。",
                McsmErrorKind.Timeout);
        }
        catch (HttpRequestException ex)
        {
            throw new McsmApiException($"无法连接 MCSManager：{ex.Message}", McsmErrorKind.Network, inner: ex);
        }

        using (response)
        {
            JsonNode? root = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    root = JsonNode.Parse(text);
                }
                catch (Exception ex)
                {
                    throw new McsmApiException(
                        $"MCSManager 返回了无法解析的响应（HTTP {(int)response.StatusCode}）。",
                        McsmErrorKind.Parse, (int)response.StatusCode, inner: ex);
                }
            }

            var apiStatus = GetInt(root, "status");
            if (!response.IsSuccessStatusCode)
            {
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => McsmErrorKind.Unauthorized,
                    HttpStatusCode.Forbidden => McsmErrorKind.Forbidden,
                    _ => McsmErrorKind.Api
                };
                throw new McsmApiException(
                    ExtractApiMessage(root) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    kind, (int)response.StatusCode, apiStatus);
            }

            if (apiStatus is not null && apiStatus != 200)
            {
                var kind = apiStatus switch
                {
                    401 => McsmErrorKind.Unauthorized,
                    403 => McsmErrorKind.Forbidden,
                    _ => McsmErrorKind.Api
                };
                throw new McsmApiException(
                    ExtractApiMessage(root) ?? $"MCSManager 返回状态 {apiStatus}。",
                    kind, (int)response.StatusCode, apiStatus);
            }

            return Prop(root, "data");
        }
    }

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_apiKey))
            parts.Add("apikey=" + Uri.EscapeDataString(_apiKey));
        if (query is not null)
        {
            foreach (var pair in query)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value is null)
                    continue;
                parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
            }
        }

        var relative = path.TrimStart('/');
        if (parts.Count > 0)
            relative += "?" + string.Join("&", parts);
        return new Uri(BaseUri, relative);
    }

    private Uri DaemonBaseUri(string address)
    {
        var trimmed = address.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = $"{BaseUri.Scheme}://{trimmed}";
        return new Uri(trimmed.TrimEnd('/') + "/");
    }

    private static Uri NormalizeAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new McsmApiException("请填写 MCSManager 面板地址。", McsmErrorKind.InvalidConfiguration);
        var trimmed = address.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "http://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new McsmApiException("MCSManager 面板地址格式不正确。", McsmErrorKind.InvalidConfiguration);
        return new Uri(uri.ToString().TrimEnd('/') + "/");
    }

    private static string? ExtractApiMessage(JsonNode? root)
    {
        var data = Prop(root, "data");
        if (data is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            return text;
        if (data is JsonObject obj)
        {
            var message = GetString(obj, "msg") ?? GetString(obj, "message");
            if (!string.IsNullOrWhiteSpace(message))
                return message;
        }

        return GetString(root, "message") ?? GetString(root, "msg");
    }

    #endregion

    #region Parsing

    private static McsmUser ParseUser(JsonNode node)
    {
        var user = new McsmUser
        {
            Uuid = GetString(node, "uuid") ?? "",
            UserName = GetString(node, "userName") ?? "",
            Permission = GetInt(node, "permission") ?? 0,
            RegisterTime = GetString(node, "registerTime") ?? "",
            LoginTime = GetString(node, "loginTime") ?? "",
            Open2Fa = GetBool(node, "open2FA") ?? false,
            IsInit = GetBool(node, "isInit") ?? false
        };
        if (Prop(node, "instances") is JsonArray instances)
            user.InstanceCount = instances.Count;
        return user;
    }

    private static McsmOverview ParseOverview(JsonNode? data)
    {
        var overview = new McsmOverview();
        if (data is null)
            return overview;
        overview.Version = GetString(data, "version") ?? "";
        overview.SpecifiedDaemonVersion = GetString(data, "specifiedDaemonVersion") ?? "";
        overview.PanelCwd = GetString(Prop(data, "process"), "cwd") ?? "";
        overview.PanelMemory = GetLong(Prop(data, "process"), "memory") ?? 0;
        overview.System = ParseSystem(Prop(data, "system"));
        overview.RemoteAvailable = GetInt(Prop(data, "remoteCount"), "available") ?? 0;
        overview.RemoteTotal = GetInt(Prop(data, "remoteCount"), "total") ?? 0;
        overview.Logined = GetInt(Prop(data, "record"), "logined") ?? 0;
        if (Prop(data, "remote") is JsonArray remotes)
        {
            foreach (var node in remotes)
            {
                if (node is null)
                    continue;
                var daemon = ParseDaemon(node);
                daemon.RunningInstances = GetInt(Prop(node, "instance"), "running") ?? 0;
                daemon.TotalInstances = GetInt(Prop(node, "instance"), "total") ?? 0;
                daemon.System = ParseSystem(Prop(node, "system"));
                daemon.Version = GetString(node, "version") ?? "";
                overview.Remotes.Add(daemon);
                overview.RunningInstances += daemon.RunningInstances;
                overview.TotalInstances += daemon.TotalInstances;
            }
        }

        return overview;
    }

    private static McsmDaemon ParseDaemon(JsonNode node) => new()
    {
        Uuid = GetString(node, "uuid") ?? "",
        Ip = GetString(node, "ip") ?? "",
        Port = GetInt(node, "port") ?? 0,
        Prefix = GetString(node, "prefix") ?? "",
        Available = GetBool(node, "available") ?? false,
        Remarks = GetString(node, "remarks") ?? "",
        Version = GetString(node, "version") ?? ""
    };

    private static McsmSystemInfo? ParseSystem(JsonNode? node)
    {
        if (node is null)
            return null;
        return new McsmSystemInfo
        {
            Type = GetString(node, "type") ?? "",
            Hostname = GetString(node, "hostname") ?? "",
            Platform = GetString(node, "platform") ?? "",
            Release = GetString(node, "release") ?? "",
            Uptime = GetDouble(node, "uptime") ?? 0,
            FreeMem = GetLong(node, "freemem") ?? 0,
            TotalMem = GetLong(node, "totalmem") ?? 0,
            CpuUsage = GetDouble(node, "cpuUsage") ?? 0,
            MemUsage = GetDouble(node, "memUsage") ?? 0
        };
    }

    private static McsmInstance ParseInstance(JsonNode node, string daemonId)
    {
        var config = Prop(node, "config");
        var info = Prop(node, "info");
        var process = Prop(node, "processInfo");
        return new McsmInstance
        {
            Uuid = GetString(node, "instanceUuid") ?? "",
            DaemonId = daemonId,
            Nickname = GetString(config, "nickname") ?? "",
            Status = GetInt(node, "status") ?? 0,
            StartCommand = GetString(config, "startCommand") ?? "",
            Cwd = GetString(config, "cwd") ?? "",
            Type = GetString(config, "type") ?? "",
            Started = GetInt(node, "started") ?? 0,
            AutoRestarted = GetInt(node, "autoRestarted") ?? 0,
            Space = GetLong(node, "space") ?? 0,
            CurrentPlayers = GetInt(info, "currentPlayers") ?? -1,
            MaxPlayers = GetInt(info, "maxPlayers") ?? -1,
            Version = GetString(info, "version") ?? "",
            CpuPercent = GetDouble(process, "cpu") ?? 0,
            MemoryBytes = GetLong(process, "memory") ?? 0,
            Pid = GetInt(process, "pid") ?? 0,
            ElapsedMilliseconds = GetLong(process, "elapsed") ?? 0
        };
    }

    private static JsonNode? Prop(JsonNode? node, string name)
    {
        if (node is not JsonObject obj)
            return null;
        if (obj.TryGetPropertyValue(name, out var exact))
            return exact;
        foreach (var pair in obj)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static string? GetString(JsonNode? node, string name)
    {
        var value = Prop(node, name);
        if (value is null)
            return null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return text;
        return value.ToJsonString().Trim('"');
    }

    private static int? GetInt(JsonNode? node, string name)
    {
        var value = Prop(node, name);
        if (value is null)
            return null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var number))
            return number;
        return int.TryParse(value.ToJsonString().Trim('"'), out var parsed) ? parsed : null;
    }

    private static long? GetLong(JsonNode? node, string name)
    {
        var value = Prop(node, name);
        if (value is null)
            return null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var number))
            return number;
        return long.TryParse(value.ToJsonString().Trim('"'), out var parsed) ? parsed : null;
    }

    private static double? GetDouble(JsonNode? node, string name)
    {
        var value = Prop(node, name);
        if (value is null)
            return null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<double>(out var number))
            return number;
        return double.TryParse(value.ToJsonString().Trim('"'), out var parsed) ? parsed : null;
    }

    private static bool? GetBool(JsonNode? node, string name)
    {
        var value = Prop(node, name);
        if (value is null)
            return null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var boolean))
            return boolean;
        return bool.TryParse(value.ToJsonString().Trim('"'), out var parsed) ? parsed : null;
    }

    #endregion
}

/// <summary>
/// 连接成功后的会话与能力信息。UI 根据能力隐藏不可用操作。
/// </summary>
public sealed class McsmSession(McsmClient client, McsmUser user)
{
    public McsmClient Client { get; } = client;
    public McsmUser User { get; } = user;
    public McsmOverview? Overview { get; set; }
    public bool EnableApiKey { get; set; }
    public bool CanUseFileManager { get; set; } = true;

    public bool IsAdmin => User.Permission >= 10;
    public bool CanManageUsers => IsAdmin;
    public bool CanManageDaemons => IsAdmin;
    public bool CanManageImages => IsAdmin;
    public bool CanCreateInstance => IsAdmin;
}
