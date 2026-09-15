package com.qeli.geo

/**
 * Pure helpers for wiring geo/user excludes into Android VpnService.Builder.
 * Kept free of Android framework types so unit tests can lock the merge regression
 * where API 33+ full-tunnel only carved LAN and dropped geo IPv4 excludes.
 */
object GeoTunRoutes {
    /**
     * IPv4 destinations that must be [excludeRoute]'d from a full tunnel on API 33+.
     * Always includes profile/geo [excludeRoutes]; optionally LAN bypass ranges.
     */
    fun fullTunnelIpv4Excludes(
        allowLan: Boolean,
        lanBypass: List<String>,
        excludeRoutes: List<String>,
    ): List<String> = buildList {
        if (allowLan) addAll(lanBypass)
        addAll(excludeRoutes.filterNot { isIpv6Cidr(it) })
    }.distinct()

    /**
     * Remaining excludes to apply via excludeRoute after the default route is installed.
     * On a full tunnel IPv4 was already applied in [fullTunnelIpv4Excludes]; only IPv6
     * remains. Split-tunnel applies every exclude (nothing is routed in by default).
     */
    fun pendingExcludeRoutes(
        useFullTunnel: Boolean,
        excludeRoutes: List<String>,
    ): List<String> =
        if (useFullTunnel) excludeRoutes.filter { isIpv6Cidr(it) }
        else excludeRoutes

    fun isIpv6Cidr(cidr: String): Boolean = ':' in cidr.substringBefore('/')
}
