using BlueOath.Protocol;
using BlueOath.Core;

namespace BlueOath.Server;

/// <summary>
/// 服务器启动参数（由 <c>--xxx=yyy</c> 命令行开关解析而来）。
/// </summary>
internal sealed record ServerOptions(
    int Port,
    ProtocolProfile Profile,
    string DataRoot,
    string ClientPath,
    bool EnableTls,
    string? TlsOutputRoot,
    string? CaptureRoot,
    bool TlsMaterialOnly,
    int? GameLoginPort,
    int? KcpGameLoginPort,
    int? GmPort,
    string ProfileId,
    string ProfileName)
{
    /// <summary>作弊与原规则选项（--cheat-* / --real-* 开关），默认作弊全开、按原规则的选项关闭。</summary>
    public CheatOptions Cheats { get; init; } = CheatOptions.Default;

    /// <summary>解析命令行参数；未显式指定的项使用默认值（JP 服、临时端口、本地 data 目录）。</summary>
    public static ServerOptions Parse(string[] args)
    {
        var port = 0;
        var profile = ProtocolProfile.Japan;
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "data");
        var clientPath = "";
        var enableTls = false;
        string? tlsOutputRoot = null;
        string? captureRoot = null;
        var tlsMaterialOnly = false;
        int? gameLoginPort = null;
        int? kcpGameLoginPort = null;
        int? gmPort = null;
        var profileId = PlayerAccountFactory.DefaultProfileId;
        string? profileName = null;
        CheatOptions cheats = CheatOptions.Default;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(arg[7..], out port);
            else if (arg.StartsWith("--region=", StringComparison.OrdinalIgnoreCase) &&
                arg[9..].Equals("cn", StringComparison.OrdinalIgnoreCase))
                profile = ProtocolProfile.China;
            else if (arg.StartsWith("--data=", StringComparison.OrdinalIgnoreCase))
                dataRoot = arg[7..];
            else if (arg.StartsWith("--client-path=", StringComparison.OrdinalIgnoreCase))
                clientPath = arg[14..];
            else if (arg.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase))
                captureRoot = Path.GetFullPath(arg[10..]);
            else if (arg.StartsWith("--tls-output=", StringComparison.OrdinalIgnoreCase))
                tlsOutputRoot = Path.GetFullPath(arg[13..]);
            else if (arg.Equals("--tls-auto", StringComparison.OrdinalIgnoreCase))
                enableTls = true;
            else if (arg.Equals("--tls-material-only", StringComparison.OrdinalIgnoreCase))
                tlsMaterialOnly = true;
            else if (arg.StartsWith("--game-login-port=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg[18..], out var parsedGameLoginPort) && parsedGameLoginPort is >= 0 and <= 65535)
                gameLoginPort = parsedGameLoginPort;
            else if (arg.StartsWith("--kcp-game-login-port=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg[22..], out var parsedKcpGameLoginPort) && parsedKcpGameLoginPort is >= 0 and <= 65535)
                kcpGameLoginPort = parsedKcpGameLoginPort;
            else if (arg.StartsWith("--gm-port=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg[10..], out var parsedGmPort) && parsedGmPort is >= 0 and <= 65535)
                gmPort = parsedGmPort;
            else if (arg.StartsWith("--profile-id=", StringComparison.OrdinalIgnoreCase))
                profileId = NormalizeProfileId(arg[13..]);
            else if (arg.StartsWith("--profile-name=", StringComparison.OrdinalIgnoreCase))
                profileName = arg[15..];
            else if (arg.Equals("--no-cheats", StringComparison.OrdinalIgnoreCase))
                cheats = cheats.WithAllCheats(false);
            else if (TryParseSwitch(arg, "--cheat-production", out bool value))
                cheats = cheats with { Production = value };
            else if (TryParseSwitch(arg, "--cheat-strength", out value))
                cheats = cheats with { Strength = value };
            else if (TryParseSwitch(arg, "--cheat-vow", out value))
                cheats = cheats with { Vow = value };
            else if (TryParseSwitch(arg, "--cheat-mood", out value))
                cheats = cheats with { Mood = value };
            else if (TryParseSwitch(arg, "--cheat-medals", out value))
                cheats = cheats with { Medals = value };
            else if (TryParseSwitch(arg, "--cheat-drops", out value))
                cheats = cheats with { Drops = value };
            else if (TryParseSwitch(arg, "--cheat-sweep", out value))
                cheats = cheats with { Sweep = value };
            else if (TryParseSwitch(arg, "--cheat-battle", out value))
                cheats = cheats with { Battle = value };
            else if (TryParseSwitch(arg, "--real-resource-cost", out value))
                cheats = cheats with { RealResourceCost = value };
            else if (TryParseSwitch(arg, "--real-shop-stock", out value))
                cheats = cheats with { RealShopStock = value };
        }

        return new ServerOptions(port, profile, dataRoot, clientPath, enableTls, tlsOutputRoot, captureRoot,
            tlsMaterialOnly, gameLoginPort, kcpGameLoginPort, gmPort, profileId,
            NormalizeProfileName(profileName, profileId))
        {
            Cheats = cheats,
        };
    }

    /// <summary>
    /// 解析开关 <paramref name="name"/>：不带值为开启，<c>=on/off</c>、<c>=true/false</c>、<c>=1/0</c>、<c>=yes/no</c>
    /// 指定开关；值无法识别时视为不匹配（保留之前的值）。同一开关出现多次时以最后一次为准。
    /// </summary>
    private static bool TryParseSwitch(string arg, string name, out bool value)
    {
        value = true;
        if (!arg.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        if (arg.Length == name.Length) return true;
        if (arg[name.Length] != '=') return false;
        switch (arg[(name.Length + 1)..].Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1" or "yes":
                return true;
            case "off" or "false" or "0" or "no":
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeProfileId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return PlayerAccountFactory.DefaultProfileId;
        string normalized = new(value.Trim().Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.').ToArray());
        if (normalized.Length == 0) return PlayerAccountFactory.DefaultProfileId;
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string NormalizeProfileName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string normalized = new(value.Trim().Where(character => !char.IsControl(character)).ToArray());
        if (normalized.Length == 0) return fallback;
        return normalized.Length <= 32 ? normalized : normalized[..32];
    }
}
