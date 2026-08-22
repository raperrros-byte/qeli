package com.qeli.geo

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import java.net.Inet4Address
import java.util.concurrent.Callable
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException

/**
 * Pre-resolve geosite domain entries to /32 exclude routes for Android TUN bypass.
 * Wildcard TLD rules (*.ru / *.su) still rely on geoip excludes at connection time.
 */
object GeoDirectResolver {
    private const val MAX_DOMAINS = 64
    private const val RESOLVE_TIMEOUT_MS = 200L
    private const val TOTAL_BUDGET_MS = 1_500L

    private val executor = Executors.newFixedThreadPool(4) { runnable ->
        Thread(runnable, "qeli-geo-dns").apply { isDaemon = true }
    }

    /** Pre-resolve domains to /32 routes that bypass the tunnel (bypass-ru). */
    fun resolveBypassIps(
        ctx: Context,
        presetId: String,
        log: (String) -> Unit = {},
    ): List<String> = resolveDomainIps(ctx, presetId, forBypass = true, log)

    /** Pre-resolve blocked domains to /32 routes through the tunnel (ru-blocked, gfw). */
    fun resolveIncludeIps(
        ctx: Context,
        presetId: String,
        log: (String) -> Unit = {},
    ): List<String> = resolveDomainIps(ctx, presetId, forBypass = false, log)

    private fun resolveDomainIps(
        ctx: Context,
        presetId: String,
        forBypass: Boolean,
        log: (String) -> Unit,
    ): List<String> {
        val preset = ProxyRoutePreset.normalize(presetId)
        val wantsDomains = when {
            forBypass -> preset == ProxyRoutePreset.BYPASS_RU
            else -> preset == ProxyRoutePreset.RU_BLOCKED ||
                preset == ProxyRoutePreset.GFW_BLACKLIST
        }
        if (!wantsDomains) return emptyList()
        val (site, _) = GeoAssetStore.getOrLoad(ctx, presetId)
        if (site == null) return emptyList()

        val network = physicalNetwork(ctx) ?: run {
            log("Geo DNS: no physical network — skipping domain pre-resolve")
            return emptyList()
        }

        val domains = site.collectResolvableDomains(MAX_DOMAINS)
        if (domains.isEmpty()) return emptyList()

        val deadline = System.nanoTime() + TOTAL_BUDGET_MS * 1_000_000L
        val ips = LinkedHashSet<String>()
        for (domain in domains) {
            val remainingMs = ((deadline - System.nanoTime()) / 1_000_000L).coerceAtLeast(1L)
            if (remainingMs <= 1L) break
            val timeout = minOf(RESOLVE_TIMEOUT_MS, remainingMs)
            val resolved = resolveOne(network, domain, timeout) ?: continue
            for (ip in resolved) {
                ips += "$ip/32"
            }
        }
        if (ips.isNotEmpty()) {
            val mode = if (forBypass) "direct" else "via VPN"
            log("Geo DNS: pre-resolved ${ips.size} IP(s) ($mode) from ${domains.size} domain(s)")
        }
        return ips.toList()
    }

    private fun physicalNetwork(ctx: Context): android.net.Network? {
        val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return null
        val selected = cm.activeNetwork ?: return null
        val caps = cm.getNetworkCapabilities(selected) ?: return null
        return if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
            cm.allNetworks.firstOrNull { candidate ->
                val candidateCaps = cm.getNetworkCapabilities(candidate) ?: return@firstOrNull false
                !candidateCaps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
            }
        } else {
            selected
        }
    }

    private fun resolveOne(
        network: android.net.Network,
        domain: String,
        timeoutMs: Long,
    ): List<String>? {
        val future = executor.submit(
            Callable {
                network.getAllByName(domain)
                    .filterIsInstance<Inet4Address>()
                    .mapNotNull { it.hostAddress }
                    .distinct()
            },
        )
        return try {
            val result = future.get(timeoutMs, TimeUnit.MILLISECONDS)
            if (result.isEmpty()) null else result
        } catch (_: TimeoutException) {
            future.cancel(true)
            null
        } catch (_: Exception) {
            null
        }
    }
}
