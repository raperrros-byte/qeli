package com.qeli

/**
 * Auto transport ranking — mirrors Rust `config::auto_transport`.
 * Preference: reality-tls → fake-tls → obfs → plain → quic.
 */
object TransportAuto {
    val DEFAULT_ORDER = listOf(
        "reality-tls", "fake-tls", "obfs", "plain", "quic", "udp-quic", "udp-obfs", "udp"
    )

    val SNI_PRESETS = listOf(
        "www.cloudflare.com",
        "www.google.com",
        "www.microsoft.com",
        "www.apple.com",
        "www.amazon.com",
        "www.speedtest.net",
        "github.com",
        "cdn.jsdelivr.net",
        "www.bing.com",
        "login.live.com",
        "www.nvidia.com",
        "www.wikipedia.org",
    )

    fun transportLabel(protocol: String, mode: String, quic: Boolean): String {
        val proto = protocol.trim().lowercase()
        val m = mode.trim().lowercase()
        if (proto == "udp") {
            if (quic || m == "udp-quic") return "quic"
            if (m == "obfs" || m == "udp-obfs") return "udp-obfs"
            return "udp"
        }
        return when (m) {
            "reality-tls", "reality" -> "reality-tls"
            "obfs" -> "obfs"
            "plain" -> "plain"
            "fake-tls", "tls", "" -> "fake-tls"
            else -> m
        }
    }

    fun preferenceRank(label: String, order: List<String> = DEFAULT_ORDER): Int {
        val idx = order.indexOfFirst { it.equals(label, ignoreCase = true) }
        return if (idx >= 0) idx else 900 + label.length
    }

    data class Ranked(val index: Int, val rttMs: Long?, val preference: Int)

    fun rank(
        labels: List<String>,
        ok: List<Boolean>,
        rttMs: List<Long?>,
        order: List<String> = DEFAULT_ORDER,
    ): List<Ranked> {
        require(labels.size == ok.size && ok.size == rttMs.size)
        val picks = ArrayList<Ranked>()
        for (i in labels.indices) {
            if (!ok[i]) continue
            picks += Ranked(i, rttMs[i], preferenceRank(labels[i], order))
        }
        picks.sortWith(
            compareBy<Ranked> { (it.rttMs ?: Long.MAX_VALUE) / 40 }
                .thenBy { it.preference }
                .thenBy { it.rttMs ?: Long.MAX_VALUE }
                .thenBy { it.index }
        )
        return picks
    }

    fun applySniToIni(ini: String, sni: String): String {
        val host = sni.trim()
        require(host.isNotEmpty()) { "sni must not be empty" }
        val lines = ini.replace("\r\n", "\n").replace('\r', '\n').lines().toMutableList()
        var replaced = false
        for (i in lines.indices) {
            val t = lines[i].trim()
            if (t.startsWith("sni", ignoreCase = true) && t.contains('=')) {
                lines[i] = "sni = $host"
                replaced = true
                break
            }
        }
        if (!replaced) {
            val insertAt = lines.indexOfFirst { it.trim().equals("[qeli]", ignoreCase = true) }
                .let { if (it >= 0) it + 1 else lines.size }
            lines.add(insertAt, "sni = $host")
        }
        return lines.joinToString("\n")
    }
}
