package com.qeli.geo

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class GeoTunRoutesTest {
    private val lan = listOf(
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
        "224.0.0.0/24", "239.255.255.250/32",
    )

    @Test
    fun `api33 full-tunnel includes geo ipv4 excludes without allowLan`() {
        // Regression: post-0.8.1 merge only applied LAN when allowLan=true and later
        // dropped IPv4 from the pending excludeRoute loop on full tunnel.
        val geo = listOf("5.0.0.0/8", "31.0.0.0/8", "77.88.0.0/18", "fd00::/8")
        val applied = GeoTunRoutes.fullTunnelIpv4Excludes(
            allowLan = false,
            lanBypass = lan,
            excludeRoutes = geo,
        )
        assertEquals(listOf("5.0.0.0/8", "31.0.0.0/8", "77.88.0.0/18"), applied)
        assertFalse(applied.any { GeoTunRoutes.isIpv6Cidr(it) })
    }

    @Test
    fun `api33 full-tunnel merges lan bypass with geo ipv4 excludes`() {
        val geo = listOf("5.0.0.0/8", "10.0.0.0/8")
        val applied = GeoTunRoutes.fullTunnelIpv4Excludes(
            allowLan = true,
            lanBypass = lan,
            excludeRoutes = geo,
        )
        assertTrue(applied.containsAll(lan))
        assertTrue(applied.contains("5.0.0.0/8"))
        assertEquals(1, applied.count { it == "10.0.0.0/8" })
    }

    @Test
    fun `pending excludes on full tunnel keep only ipv6`() {
        val all = listOf("5.0.0.0/8", "2001:db8::/32", "fc00::/7")
        assertEquals(
            listOf("2001:db8::/32", "fc00::/7"),
            GeoTunRoutes.pendingExcludeRoutes(useFullTunnel = true, excludeRoutes = all),
        )
    }

    @Test
    fun `pending excludes on split tunnel keep all`() {
        val all = listOf("5.0.0.0/8", "2001:db8::/32")
        assertEquals(
            all,
            GeoTunRoutes.pendingExcludeRoutes(useFullTunnel = false, excludeRoutes = all),
        )
    }

    @Test
    fun `bypass-ru preset wires excludes not split-tunnel includes`() {
        assertTrue(ProxyRoutePreset.usesBypassExcludes(ProxyRoutePreset.BYPASS_RU))
        assertFalse(ProxyRoutePreset.usesIncludeRoutes(ProxyRoutePreset.BYPASS_RU))
        assertEquals(listOf("ru", "private"), ProxyRoutePreset.tunBypassIpCodes(ProxyRoutePreset.BYPASS_RU))
        assertTrue(ProxyRoutePreset.tunIncludeIpCodes(ProxyRoutePreset.BYPASS_RU).isEmpty())
    }

    @Test
    fun `ru-blocked preset wires split-tunnel includes`() {
        assertTrue(ProxyRoutePreset.usesIncludeRoutes(ProxyRoutePreset.RU_BLOCKED))
        assertFalse(ProxyRoutePreset.usesBypassExcludes(ProxyRoutePreset.RU_BLOCKED))
        assertEquals(listOf("ru-blocked"), ProxyRoutePreset.tunIncludeIpCodes(ProxyRoutePreset.RU_BLOCKED))
    }

    @Test
    fun `applyGeoRouting-style merge keeps profile excludes and adds geo`() {
        val profileExcludes = listOf("203.0.113.0/24")
        val geo = listOf("5.0.0.0/8", "31.0.0.0/8")
        val merged = (profileExcludes + geo).distinct()
        val ipv4 = GeoTunRoutes.fullTunnelIpv4Excludes(
            allowLan = true,
            lanBypass = lan,
            excludeRoutes = merged,
        )
        assertTrue(ipv4.contains("203.0.113.0/24"))
        assertTrue(ipv4.contains("5.0.0.0/8"))
        assertTrue(ipv4.containsAll(lan))
        // Must be larger than LAN-only — this is what the TUN log should reflect.
        assertTrue(ipv4.size > lan.size)
    }
}
