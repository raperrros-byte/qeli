package com.qeli.geo

import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * Live geoip.dat sample → TUN exclude list. Requires network; skipped offline.
 */
class GeoIpBypassIntegrationTest {
    @Test
    fun `bypass-ru geoip produces ipv4 excludes for full-tunnel wiring`() {
        val geoip = downloadOrSkip(
            listOf(
                "https://cdn.jsdelivr.net/gh/runetfreedom/russia-v2ray-rules-dat@release/geoip.dat",
                "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
            ),
        )
        val codes = ProxyRoutePreset.tunBypassIpCodes(ProxyRoutePreset.BYPASS_RU)
        val index = GeoIpIndex.load(geoip, codes)
        val raw = index.allCidrsFor(codes)
        assumeTrue("geoip.dat has no ru/private CIDRs", raw.isNotEmpty())

        val capped = CidrAggregator.prioritizeForCoverage(raw, GeoAssetStore.tunExcludeRouteBudget())
        assertTrue("expected aggregated geo excludes, got ${capped.size}", capped.size >= 8)

        val wired = GeoTunRoutes.fullTunnelIpv4Excludes(
            allowLan = false,
            lanBypass = emptyList(),
            excludeRoutes = capped,
        )
        assertTrue(wired.size >= 8)
        assertTrue(wired.all { !GeoTunRoutes.isIpv6Cidr(it) })
        // Sanity: yandex/mail.ru space often lands in RU aggregates — at least one public /8-/16.
        assertTrue(wired.any { it.endsWith("/8") || it.endsWith("/9") || it.endsWith("/10") ||
            it.endsWith("/11") || it.endsWith("/12") || it.endsWith("/13") ||
            it.endsWith("/14") || it.endsWith("/15") || it.endsWith("/16") })
    }

    private fun downloadOrSkip(urls: List<String>): File {
        val dest = File.createTempFile("qeli-geoip-", ".dat")
        dest.deleteOnExit()
        var last: Exception? = null
        for (url in urls) {
            try {
                val conn = (URL(url).openConnection() as HttpURLConnection).apply {
                    connectTimeout = 20_000
                    readTimeout = 60_000
                    instanceFollowRedirects = true
                    setRequestProperty("User-Agent", "qeli-geo-test/0.8.1")
                }
                conn.inputStream.use { input ->
                    dest.outputStream().use { input.copyTo(it) }
                }
                conn.disconnect()
                if (dest.length() > 1024) return dest
            } catch (e: Exception) {
                last = e
            }
        }
        assumeTrue("geoip download unavailable: ${last?.message}", false)
        return dest
    }
}
