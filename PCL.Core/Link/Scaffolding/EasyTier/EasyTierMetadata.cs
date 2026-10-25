using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.Utils.Hash;
namespace PCL.Core.Link.Scaffolding.EasyTier;

public static class EasyTierMetadata
{
    public const string CurrentEasyTierVer = "2.6.4";

    public static string EasyTierFilePath => Path.Combine(Paths.SharedLocalData, "EasyTier",
        CurrentEasyTierVer,
        $"easytier-windows-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64")}");

    /// <summary>
    ///     easytier-core.exe 的 SHA256 基线，取自 EasyTier 官方 GitHub Release
    ///     （EasyTier/EasyTier v2.6.4）的对应架构产物。
    ///     分发用的 ZIP 因 CDN 重新打包外层哈希会变，故校验的是包内真正被执行的可执行文件。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CoreHashBaselines =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x86_64"] = "DA7EB2D24B5416F3D3407636949E964A0750E3F9DC53A828CB6799A57EAD445D",
            ["arm64"] = "DA912E2BEFD4C59C81228C0DAC7E19F0190AA4221475117108AD2F0045B3065D"
        };

    /// <summary>
    ///     校验即将执行的 easytier-core.exe 是否与官方基线一致。
    /// </summary>
    /// <returns>null 表示校验通过；否则返回失败原因（调用方必须拒绝启动）。</returns>
    public static string? VerifyCoreIntegrity()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
        if (!CoreHashBaselines.TryGetValue(arch, out var expected))
            return $"未知架构 {arch}，缺少完整性校验基线";
        var corePath = Path.Combine(EasyTierFilePath, "easytier-core.exe");
        if (!File.Exists(corePath))
            return "easytier-core.exe 不存在";
        try
        {
            using var fs = File.OpenRead(corePath);
            var actual = Convert.ToHexString(SHA256Provider.Instance.ComputeHash(fs));
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return null;
            return $"easytier-core.exe 哈希不匹配（期望 {expected[..16]}…，实际 {actual[..Math.Min(16, actual.Length)]}…）";
        }
        catch (Exception ex)
        {
            return "读取 easytier-core.exe 失败：" + ex.Message;
        }
    }
}
