using ServiceLib.Models.Configs;

namespace v2rayN.Cli;

internal static class CliSettings
{
    public static IReadOnlyList<ConfigSetting> All { get; } =
    [
        new("inbound.mixed-port", c => Inbound(c).LocalPort.ToString(), (c, v) => Inbound(c).LocalPort = ParseInt(v, 1, 65514, "inbound.mixed-port")),
        new("inbound.socks-port", c => Inbound(c).LocalPort.ToString(), (c, v) => Inbound(c).LocalPort = ParseInt(v, 1, 65514, "inbound.socks-port")),
        new("inbound.http-port", c => Inbound(c).LocalPort.ToString(), (c, v) => Inbound(c).LocalPort = ParseInt(v, 1, 65514, "inbound.http-port")),
        new("inbound.second-port-enabled", c => Inbound(c).SecondLocalPortEnabled.ToString().ToLowerInvariant(), (c, v) => Inbound(c).SecondLocalPortEnabled = ParseBool(v)),
        new("inbound.second-mixed-port", c => (Inbound(c).LocalPort + 1).ToString(), (_, _) => throw new CliException("inbound.second-mixed-port 由 mixed-port + 1 自动计算，请设置 inbound.mixed-port。")),
        new("inbound.allow-lan", c => Inbound(c).AllowLANConn.ToString().ToLowerInvariant(), (c, v) => Inbound(c).AllowLANConn = ParseBool(v)),
        new("inbound.udp", c => Inbound(c).UdpEnabled.ToString().ToLowerInvariant(), (c, v) => Inbound(c).UdpEnabled = ParseBool(v)),
        new("inbound.sniffing", c => Inbound(c).SniffingEnabled.ToString().ToLowerInvariant(), (c, v) => Inbound(c).SniffingEnabled = ParseBool(v)),
        new("tun.enabled", c => c.TunModeItem.EnableTun.ToString().ToLowerInvariant(), (c, v) => c.TunModeItem.EnableTun = ParseBool(v)),
        new("tun.auto-route", c => c.TunModeItem.AutoRoute.ToString().ToLowerInvariant(), (c, v) => c.TunModeItem.AutoRoute = ParseBool(v)),
        new("tun.strict-route", c => c.TunModeItem.StrictRoute.ToString().ToLowerInvariant(), (c, v) => c.TunModeItem.StrictRoute = ParseBool(v)),
        new("tun.stack", c => c.TunModeItem.Stack ?? string.Empty, (c, v) => c.TunModeItem.Stack = v),
        new("tun.mtu", c => c.TunModeItem.Mtu.ToString(), (c, v) => c.TunModeItem.Mtu = ParseInt(v, 576, 65535, "tun.mtu")),
        new("core.log-enabled", c => c.CoreBasicItem.LogEnabled.ToString().ToLowerInvariant(), (c, v) => c.CoreBasicItem.LogEnabled = ParseBool(v)),
        new("core.log-level", c => c.CoreBasicItem.Loglevel ?? string.Empty, (c, v) => c.CoreBasicItem.Loglevel = v),
        new("core.send-through", c => c.CoreBasicItem.SendThrough ?? string.Empty, (c, v) => c.CoreBasicItem.SendThrough = v),
        new("core.bind-interface", c => c.CoreBasicItem.BindInterface ?? string.Empty, (c, v) => c.CoreBasicItem.BindInterface = v),
        new("routing.domain-strategy", c => c.RoutingBasicItem.DomainStrategy ?? string.Empty, (c, v) => c.RoutingBasicItem.DomainStrategy = v),
        new("dns.direct", c => c.SimpleDNSItem.DirectDNS ?? string.Empty, (c, v) => c.SimpleDNSItem.DirectDNS = v),
        new("dns.remote", c => c.SimpleDNSItem.RemoteDNS ?? string.Empty, (c, v) => c.SimpleDNSItem.RemoteDNS = v),
        new("dns.bootstrap", c => c.SimpleDNSItem.BootstrapDNS ?? string.Empty, (c, v) => c.SimpleDNSItem.BootstrapDNS = v),
    ];

    public static ConfigSetting Find(string key)
    {
        return All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? throw new CliException($"未知配置键: {key}");
    }

    private static bool ParseBool(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" or "enable" or "enabled" => true,
            "false" or "0" or "no" or "off" or "disable" or "disabled" => false,
            _ => throw new CliException($"无效布尔值: {value}，请使用 true/false。"),
        };
    }

    private static int ParseInt(string value, int minimum, int maximum, string key)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new CliException($"{key} 必须在 {minimum} 到 {maximum} 之间。");
        }
        return parsed;
    }

    private static InItem Inbound(Config config) => config.Inbound.First();
}

internal sealed record ConfigSetting(string Key, Func<Config, string> Get, Action<Config, string> Set);
