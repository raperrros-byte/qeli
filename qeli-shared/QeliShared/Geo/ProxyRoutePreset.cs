using System.Net;

namespace Qeli.Shared.Geo;

/// <summary>v2rayN-like routing presets for the local SOCKS/HTTP proxy.</summary>
public static class ProxyRoutePreset
{
    public const string ProxyAll = "proxy-all";           // RUv1-Всё / V4-全局
    public const string BypassRu = "bypass-ru";           // RUv1-Всё, кроме РФ
    public const string RuBlocked = "ru-blocked";         // RUv1-Заблокированное
    public const string BypassCn = "bypass-cn";           // V4-绕过大陆 (Whitelist)
    public const string GfwBlacklist = "gfw-blacklist";   // V4-黑名单

    public static IReadOnlyList<(string Id, string En, string Ru)> All { get; } =
    [
        (ProxyAll, "RUv1 / V4 — Everything (proxy all)", "RUv1-Всё / V4-全局"),
        (BypassRu, "RUv1 — Everything except RU", "RUv1-Всё, кроме РФ"),
        (RuBlocked, "RUv1 — Blocked only", "RUv1-Заблокированное"),
        (BypassCn, "V4 — Bypass mainland CN (whitelist)", "V4-绕过大陆 (Whitelist)"),
        (GfwBlacklist, "V4 — GFW blacklist", "V4-黑名单 (Blacklist)"),
    ];

    public static string Normalize(string? id) =>
        All.Any(p => p.Id == id) ? id! : ProxyAll;

    public static (string[] SiteTags, string[] IpTags) TagsFor(string presetId) =>
        Normalize(presetId) switch
        {
            BypassRu => (
                ["category-ru", "ru", "geolocation-ru", "private"],
                ["ru", "private"]),
            RuBlocked => (
                ["ru-blocked", "category-ru-blocked", "ru-blocked-community", "geolocation-!cn"],
                ["ru-blocked", "private"]),
            BypassCn => (
                ["cn", "geolocation-cn", "category-ads-all", "private"],
                ["cn", "private"]),
            GfwBlacklist => (
                ["gfw", "greatfire", "category-ads-all", "geolocation-!cn"],
                ["cn", "private"]),
            _ => ([], ["private"]),
        };

    /// <summary>
    /// True = send via tunnel proxy; false = dial direct (bypass VPN).
    /// Private IPs always direct.
    /// </summary>
    public static bool ShouldProxy(
        string presetId,
        string host,
        IPAddress? resolvedIp,
        GeoSiteIndex? site,
        GeoIpIndex? ip)
    {
        presetId = Normalize(presetId);
        if (resolvedIp != null && GeoIpIndex.IsPrivate(resolvedIp))
            return false;
        if (ip != null && resolvedIp != null && ip.MatchAny(resolvedIp, "private"))
            return false;

        return presetId switch
        {
            ProxyAll => true,

            BypassRu => !(
                (site != null && site.MatchAny(host, "category-ru", "ru", "geolocation-ru"))
                || (ip != null && resolvedIp != null && ip.MatchAny(resolvedIp, "ru"))),

            RuBlocked =>
                (site != null && site.MatchAny(host, "ru-blocked", "category-ru-blocked", "ru-blocked-community"))
                || (ip != null && resolvedIp != null && ip.MatchAny(resolvedIp, "ru-blocked")),

            BypassCn => !(
                (site != null && site.MatchAny(host, "cn", "geolocation-cn"))
                || (ip != null && resolvedIp != null && ip.MatchAny(resolvedIp, "cn"))),

            GfwBlacklist =>
                site != null && site.MatchAny(host, "gfw", "greatfire", "geolocation-!cn"),

            _ => true,
        };
    }
}

/// <summary>Process-wide proxy routing preference (set from desktop settings).</summary>
public static class ProxyRouteConfig
{
    public static string PresetId { get; set; } = ProxyRoutePreset.ProxyAll;
}
