package com.qeli

import java.io.InputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager
import kotlin.math.max

data class SniSpeedRow(
    val host: String,
    val ok: Boolean,
    val tlsMs: Long? = null,
    val mbps: Double? = null,
    val bytes: Long = 0,
    val error: String? = null,
) {
    val status: String
        get() = when {
            ok && mbps != null -> String.format("%.2f Mbit/s", mbps)
            ok -> "ok"
            else -> error ?: "fail"
        }
}

/**
 * LibreSpeed-style probe: TCP + TLS (SNI = host) + short HTTPS download.
 * Trusts any cert — we measure front-host reachability, not PKI for that site.
 */
object SniSpeedProbe {
    const val DEFAULT_BYTES = 512 * 1024

    private val trustAll = arrayOf<X509TrustManager>(object : X509TrustManager {
        override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) {}
        override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {}
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    })

    fun probe(host: String, bytes: Int = DEFAULT_BYTES, timeoutMs: Int = 12_000): SniSpeedRow {
        val normalized = SniCatalog.normalize(host)
            ?: return SniSpeedRow(host, false, error = "invalid host")
        val want = bytes.coerceIn(64 * 1024, 4 * 1024 * 1024)
        return try {
            val t0 = System.nanoTime()
            val plain = Socket()
            plain.connect(InetSocketAddress(normalized, 443), timeoutMs)
            plain.soTimeout = timeoutMs
            val ctx = SSLContext.getInstance("TLS")
            ctx.init(null, trustAll, SecureRandom())
            val ssl = ctx.socketFactory.createSocket(plain, normalized, 443, true) as SSLSocket
            ssl.soTimeout = timeoutMs
            ssl.startHandshake()
            val tlsMs = (System.nanoTime() - t0) / 1_000_000

            val req = buildString {
                append("GET /?qeli-sni-probe=$want HTTP/1.1\r\n")
                append("Host: $normalized\r\n")
                append("Connection: close\r\n")
                append("User-Agent: qeli-sni-speed/0.7\r\n")
                append("Accept: */*\r\n")
                append("Range: bytes=0-${want - 1}\r\n\r\n")
            }.toByteArray(Charsets.US_ASCII)

            val tDl = System.nanoTime()
            ssl.outputStream.write(req)
            ssl.outputStream.flush()
            val total = readBody(ssl.inputStream, want)
            val secs = max(0.001, (System.nanoTime() - tDl) / 1e9)
            val mbps = if (total > 0) total * 8.0 / secs / 1_000_000.0 else null
            runCatching { ssl.close() }
            SniSpeedRow(normalized, true, tlsMs, mbps, total)
        } catch (e: Exception) {
            SniSpeedRow(normalized, false, error = e.message ?: e.javaClass.simpleName)
        }
    }

    private fun readBody(input: InputStream, limit: Int): Long {
        val buf = ByteArray(16 * 1024)
        var total = 0L
        var headerDone = false
        val header = StringBuilder()
        while (total < limit) {
            val n = input.read(buf)
            if (n <= 0) break
            if (!headerDone) {
                header.append(String(buf, 0, n, Charsets.ISO_8859_1))
                val sep = header.indexOf("\r\n\r\n")
                if (sep >= 0) {
                    headerDone = true
                    val bodyStart = sep + 4
                    val already = header.length - bodyStart
                    if (already > 0) total += already.toLong().coerceAtMost(limit.toLong())
                }
            } else {
                total += n.toLong()
            }
        }
        return total.coerceAtMost(limit.toLong())
    }
}
