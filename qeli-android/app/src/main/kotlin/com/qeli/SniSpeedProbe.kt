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
            ok && bytes >= SniSpeedProbe.MIN_BODY_FOR_MBPS -> "ok"
            ok -> "tls ok"
            else -> error ?: "fail"
        }
}

/**
 * LibreSpeed-style probe: TCP + TLS (SNI = host) + HTTPS download for at least
 * [minDurationMs] (or until [bytes] cap).
 */
object SniSpeedProbe {
    const val DEFAULT_BYTES = 2 * 1024 * 1024
    const val DEFAULT_MIN_DURATION_MS = 3_000
    const val MIN_BODY_FOR_MBPS = 256 * 1024

    private val trustAll = arrayOf<X509TrustManager>(object : X509TrustManager {
        override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) {}
        override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {}
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    })

    fun probe(
        host: String,
        bytes: Int = DEFAULT_BYTES,
        minDurationMs: Int = DEFAULT_MIN_DURATION_MS,
        timeoutMs: Int = 0,
    ): SniSpeedRow {
        val normalized = SniCatalog.normalize(host)
            ?: return SniSpeedRow(host, false, error = "invalid host")
        val want = bytes.coerceIn(256 * 1024, 8 * 1024 * 1024)
        val minMs = minDurationMs.coerceIn(500, 60_000)
        val timeout = if (timeoutMs > 0) timeoutMs
        else (minMs + 12_000 + want / (256 * 1024) * 1000).coerceIn(15_000, 120_000)
        return try {
            val t0 = System.nanoTime()
            val plain = Socket()
            plain.connect(InetSocketAddress(normalized, 443), timeout)
            plain.soTimeout = timeout
            val ctx = SSLContext.getInstance("TLS")
            ctx.init(null, trustAll, SecureRandom())
            val ssl = ctx.socketFactory.createSocket(plain, normalized, 443, true) as SSLSocket
            ssl.soTimeout = timeout
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
            val total = readBody(ssl.inputStream, want, minMs)
            val secs = max(0.001, (System.nanoTime() - tDl) / 1e9)
            val mbps = if (total >= MIN_BODY_FOR_MBPS) total * 8.0 / secs / 1_000_000.0 else null
            runCatching { ssl.close() }
            SniSpeedRow(normalized, true, tlsMs, mbps, total)
        } catch (e: Exception) {
            SniSpeedRow(normalized, false, error = e.message ?: e.javaClass.simpleName)
        }
    }

    private fun readBody(input: InputStream, limit: Int, minDurationMs: Int): Long {
        val buf = ByteArray(32 * 1024)
        val pending = ByteArray(16 * 1024)
        var pendingLen = 0
        var headerDone = false
        var total = 0L
        val t0 = System.nanoTime()
        while (true) {
            val n = input.read(buf)
            if (n <= 0) break
            var offset = 0
            while (offset < n) {
                if (headerDone) {
                    total += (n - offset).toLong()
                    offset = n
                    continue
                }
                val take = minOf(n - offset, pending.size - pendingLen)
                System.arraycopy(buf, offset, pending, pendingLen, take)
                pendingLen += take
                offset += take
                val sep = indexOfHeaderEnd(pending, pendingLen)
                if (sep < 0) {
                    if (pendingLen >= pending.size) {
                        headerDone = true
                        total += pendingLen.toLong()
                        pendingLen = 0
                    }
                    continue
                }
                headerDone = true
                total += (pendingLen - (sep + 4)).toLong()
                pendingLen = 0
            }
            val elapsedMs = (System.nanoTime() - t0) / 1_000_000
            if (total >= limit) break
            if (elapsedMs >= minDurationMs && total >= MIN_BODY_FOR_MBPS) break
        }
        return total.coerceAtMost(limit.toLong())
    }

    private fun indexOfHeaderEnd(data: ByteArray, len: Int): Int {
        if (len < 4) return -1
        for (i in 0..len - 4) {
            if (data[i] == '\r'.code.toByte() && data[i + 1] == '\n'.code.toByte() &&
                data[i + 2] == '\r'.code.toByte() && data[i + 3] == '\n'.code.toByte())
                return i
        }
        return -1
    }
}
