package com.qeli.geo

/** v2rayN-like routing presets (same IDs as Windows Qeli.Shared.Geo.ProxyRoutePreset). */
object ProxyRoutePreset {
    const val PROXY_ALL = "proxy-all"
    const val BYPASS_RU = "bypass-ru"
    const val RU_BLOCKED = "ru-blocked"
    const val BYPASS_CN = "bypass-cn"
    const val GFW_BLACKLIST = "gfw-blacklist"

    data class Entry(val id: String, val labelEn: String, val labelRu: String)

    val ALL = listOf(
        Entry(PROXY_ALL, "Proxy all", "Всё через VPN"),
        Entry(BYPASS_RU, "Bypass RU", "Всё, кроме РФ"),
        Entry(RU_BLOCKED, "RU blocked only", "Только заблокированное"),
        Entry(BYPASS_CN, "Bypass CN", "Обход материкового CN"),
        Entry(GFW_BLACKLIST, "GFW blacklist", "Чёрный список GFW"),
    )

    fun normalize(id: String?): String =
        if (ALL.any { it.id == id }) id!! else PROXY_ALL

    /** Site + IP tags to load from geosite.dat / geoip.dat for [presetId]. */
    fun tagsFor(presetId: String): Pair<List<String>, List<String>> =
        when (normalize(presetId)) {
            BYPASS_RU -> listOf("category-ru", "ru", "geolocation-ru", "su", "private") to
                listOf("ru", "private")
            RU_BLOCKED -> listOf("ru-blocked", "category-ru-blocked", "ru-blocked-community", "geolocation-!cn") to
                listOf("ru-blocked", "private")
            BYPASS_CN -> listOf("cn", "geolocation-cn", "category-ads-all", "private") to
                listOf("cn", "private")
            GFW_BLACKLIST -> listOf("gfw", "greatfire", "category-ads-all", "geolocation-!cn") to
                listOf("cn", "private")
            else -> emptyList<String>() to listOf("private")
        }

    /**
     * For TUN: CIDR codes from geoip that should be **excluded** from the tunnel
     * (direct / bypass) under the given preset. Empty = no geo excludes (proxy-all).
     */
    fun tunBypassIpCodes(presetId: String): List<String> =
        when (normalize(presetId)) {
            BYPASS_RU -> listOf("ru", "private")
            BYPASS_CN -> listOf("cn", "private")
            else -> emptyList()
        }

    /** geoip codes whose CIDRs are routed **into** the tunnel (proxy-only presets). */
    fun tunIncludeIpCodes(presetId: String): List<String> =
        when (normalize(presetId)) {
            RU_BLOCKED -> listOf("ru-blocked")
            else -> emptyList()
        }

    /** Bulk geoip excludes for full-tunnel bypass presets. */
    fun usesBypassExcludes(presetId: String): Boolean =
        tunBypassIpCodes(presetId).isNotEmpty()

    /** Split-tunnel: only listed destinations enter the VPN. */
    fun usesIncludeRoutes(presetId: String): Boolean =
        tunIncludeIpCodes(presetId).isNotEmpty() ||
            normalize(presetId) == RU_BLOCKED ||
            normalize(presetId) == GFW_BLACKLIST
}
