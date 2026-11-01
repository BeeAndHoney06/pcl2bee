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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using PCL.Core.App;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// MCSManager 连接设置的持久化入口。
/// API Key 存放在配置服务的 SharedEncrypt 源中，不会以明文写入普通配置文件。
/// </summary>
public static class McsmSettings
{
    public static string Address
    {
        get => Config.Server.McsmAddress ?? "";
        set => Config.Server.McsmAddress = (value ?? "").Trim();
    }

    public static string ApiKey
    {
        get => Config.Server.McsmApiKey ?? "";
        set => Config.Server.McsmApiKey = (value ?? "").Trim();
    }

    public static string UserName
    {
        get => Config.Server.McsmUserName ?? "";
        set => Config.Server.McsmUserName = (value ?? "").Trim();
    }

    public static bool AutoConnect
    {
        get => Config.Server.McsmAutoConnect;
        set => Config.Server.McsmAutoConnect = value;
    }

    public static void Save(string address, string apiKey)
    {
        Address = address;
        ApiKey = apiKey;
    }

    public static void SaveSession(string address, string apiKey, string userName)
    {
        Address = address;
        ApiKey = apiKey;
        UserName = userName;
    }

    public static void ClearCredential()
    {
        ApiKey = "";
    }

    public static bool HasCredential =>
        !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(ApiKey);

    public static string MaskKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "";
        if (apiKey.Length <= 8)
            return new string('*', apiKey.Length);
        return $"{apiKey[..4]}{new string('*', Math.Min(24, apiKey.Length - 8))}{apiKey[^4..]}";
    }
}
