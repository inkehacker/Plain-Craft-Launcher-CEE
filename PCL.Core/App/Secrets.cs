using System.Linq;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.OS;

namespace PCL.Core.App;

// ReSharper disable InconsistentNaming
public static class Secrets
{
    /// <summary>
    /// 微软 OAuth 的 Client ID
    /// 默认内置官方 PCL-CE 注册的微软公共客户端 ID（公共客户端 ID 非机密，公开可见）。
    /// 自构建版本可用环境变量 PCL_MS_CLIENT_ID 覆盖（配合 PCL_WRITE_SECRET=1 编译期注入）。
    /// </summary>
    public static string MSOAuthClientId { get; } = EnvironmentInterop
        .GetSecret("MS_CLIENT_ID", readEnvDebugOnly: true)
        .ReplaceNullOrEmpty("c14b0370-8d75-42f8-b329-5b60d39e319f");

    /// <summary>
    /// CurseForge API 的 Client ID
    /// </summary>
    public static string CurseForgeAPIKey { get; } = EnvironmentInterop.GetSecret("CURSEFORGE_API_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 遥测密钥
    /// </summary>
    public static string TelemetryKey { get; } = EnvironmentInterop.GetSecret("TELEMETRY_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// Natayark ID OAuth 的 Client ID
    /// </summary>
    public static string NatayarkClientId { get; } = EnvironmentInterop.GetSecret("NAID_CLIENT_ID", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// Natayark ID OAuth 的 Client ID
    /// </summary>
    public static string NatayarkClientSecret { get; } = EnvironmentInterop.GetSecret("NAID_CLIENT_SECRET", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 联机根服务器（自构建版本未内置时可通过 PCL_LINK_SERVER_ROOT 环境变量注入，多个用 | 分隔）
    /// </summary>
    public static string[] LinkServers { get; } = EnvironmentInterop.GetSecret("LINK_SERVER_ROOT").ReplaceNullOrEmpty()
        .Split("|").Where(server => !string.IsNullOrWhiteSpace(server)).ToArray();

    /// <summary>
    /// 当前版本的 Git 提交 SHA
    /// </summary>
    public static string CommitHash { get; } = EnvironmentInterop.GetSecret("GITHUB_SHA", readEnvDebugOnly: true).ReplaceNullOrEmpty();
}
