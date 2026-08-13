package com.qeli.geo

/**
 * TUN bypass decisions (inverse of Windows [Qeli.Shared.Geo.ProxyRoutePreset.ShouldProxy]).
 * Domain suffixes are always checked; geosite/geoip tags apply when indexes are loaded.
 */
object GeoRoutePolicy {
    /** TLD suffixes that must bypass the tunnel under bypass-ru (direct on physical network). */
    val DIRECT_TLD_SUFFIXES = listOf("ru", "su")

    /** True when traffic to [host]/[ip] should stay outside the encrypted tunnel. */
    fun shouldBypassTunnel(
        presetId: String,
        host: String,
        ip: ByteArray?,
        site: GeoSiteIndex?,
        ipIndex: GeoIpIndex?,
    ): Boolean {
        val preset = ProxyRoutePreset.normalize(presetId)
        if (ip != null && ip.size == 4 && isPrivateIpv4(ip)) return true
        if (ipIndex != null && ip != null && ipIndex.matchAny(ip, "private")) return true

        return when (preset) {
            ProxyRoutePreset.PROXY_ALL -> false

            ProxyRoutePreset.BYPASS_RU -> matchesRuDirect(host, site) ||
                (ipIndex != null && ip != null && ipIndex.matchAny(ip, "ru"))

            ProxyRoutePreset.RU_BLOCKED ->
                (site != null && site.matchAny(
                    host,
                    "ru-blocked",
                    "category-ru-blocked",
                    "ru-blocked-community",
                )) ||
                    (ipIndex != null && ip != null && ipIndex.matchAny(ip, "ru-blocked"))

            ProxyRoutePreset.BYPASS_CN -> (
                site != null && site.matchAny(host, "cn", "geolocation-cn")
                ) ||
                (ipIndex != null && ip != null && ipIndex.matchAny(ip, "cn"))

            ProxyRoutePreset.GFW_BLACKLIST ->
                site != null && site.matchAny(host, "gfw", "greatfire", "geolocation-!cn")

            else -> false
        }
    }

    fun matchesRuDirect(host: String, site: GeoSiteIndex?): Boolean {
        if (GeoSiteIndex.matchesDirectTld(host, DIRECT_TLD_SUFFIXES)) return true
        return site != null && site.matchAny(
            host,
            "category-ru",
            "ru",
            "geolocation-ru",
            "su",
        )
    }

    private fun isPrivateIpv4(ip: ByteArray): Boolean {
        val b0 = ip[0].toInt() and 0xff
        val b1 = ip[1].toInt() and 0xff
        return when {
            b0 == 10 -> true
            b0 == 172 && b1 in 16..31 -> true
            b0 == 192 && b1 == 168 -> true
            b0 == 127 -> true
            b0 == 169 && b1 == 254 -> true
            else -> false
        }
    }
}
