package com.qeli

import java.net.HttpURLConnection
import java.net.Inet4Address
import java.net.URL
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/** Authenticated download against panel `/api/speedtest` (lab-friendly TLS). */
object PanelSpeedTest {
    private const val TRUST_ALL = true

    fun mbps(
        preferredBaseUrl: String?,
        user: String,
        password: String,
        bytes: Int = 1_048_576,
        vpnServerHost: String? = null,
    ): Double {
        val size = bytes.coerceIn(64 * 1024, 8 * 1024 * 1024)
        var last: Exception? = null
        for (base in buildCandidates(preferredBaseUrl, vpnServerHost)) {
            try {
                return runOnce(base, user, password, size)
            } catch (e: Exception) {
                last = e
            }
        }
        throw last ?: IllegalStateException("speed test: no panel URL candidates")
    }

    fun buildCandidates(preferredBaseUrl: String?, vpnServerHost: String?): List<String> {
        val out = LinkedHashSet<String>()
        fun add(raw: String?) {
            var u = raw?.trim()?.trimEnd('/') ?: return
            if (u.isEmpty()) return
            if (!u.startsWith("http://", ignoreCase = true) &&
                !u.startsWith("https://", ignoreCase = true)
            ) {
                u = "https://$u"
            }
            out += u
        }
        add(preferredBaseUrl)
        val host = vpnServerHost?.trim()?.trim('[', ']') ?: ""
        if (host.isNotEmpty()) {
            val isIp = runCatching { Inet4Address.getByName(host) }.isSuccess &&
                !host.any { it.isLetter() }
            if (isIp) {
                add("http://$host:8080")
                add("https://$host:8080")
            } else {
                add("https://$host")
                add("http://$host:8080")
                add("https://$host:8080")
                add("http://$host")
            }
        }
        if (out.isEmpty()) out += "http://127.0.0.1:8080"
        return out.toList()
    }

    private fun runOnce(baseUrl: String, user: String, password: String, bytes: Int): Double {
        val cookieManager = java.net.CookieManager()
        java.net.CookieHandler.setDefault(cookieManager)
        if (password.isNotEmpty()) {
            login(baseUrl, user, password)
        }
        val conn = open("$baseUrl/api/speedtest?bytes=$bytes")
        conn.requestMethod = "GET"
        conn.connectTimeout = 15_000
        conn.readTimeout = 60_000
        val t0 = System.nanoTime()
        val code = conn.responseCode
        if (code !in 200..299) {
            conn.disconnect()
            throw IllegalStateException("speedtest HTTP $code @ $baseUrl")
        }
        val payload = conn.inputStream.use { it.readBytes() }
        conn.disconnect()
        val secs = (System.nanoTime() - t0) / 1e9
        if (payload.size < bytes / 4) {
            throw IllegalStateException("speedtest short body ${payload.size}B @ $baseUrl")
        }
        return payload.size * 8.0 / secs.coerceAtLeast(0.001) / 1_000_000.0
    }

    private fun login(baseUrl: String, user: String, password: String) {
        val conn = open("$baseUrl/api/login")
        conn.requestMethod = "POST"
        conn.doOutput = true
        conn.connectTimeout = 10_000
        conn.readTimeout = 10_000
        conn.setRequestProperty("Content-Type", "application/json")
        conn.outputStream.use { os ->
            os.write("""{"username":"$user","password":"$password"}""".toByteArray())
        }
        val code = conn.responseCode
        if (code !in 200..299) {
            conn.disconnect()
            throw IllegalStateException("panel login HTTP $code @ $baseUrl")
        }
        conn.inputStream.use { it.readBytes() }
        conn.disconnect()
    }

    private fun open(url: String): HttpURLConnection {
        val conn = URL(url).openConnection() as HttpURLConnection
        if (TRUST_ALL && conn is HttpsURLConnection) {
            val trustAll = arrayOf<TrustManager>(
                object : X509TrustManager {
                    override fun checkClientTrusted(chain: Array<java.security.cert.X509Certificate>?, authType: String?) {}
                    override fun checkServerTrusted(chain: Array<java.security.cert.X509Certificate>?, authType: String?) {}
                    override fun getAcceptedIssuers() = arrayOf<java.security.cert.X509Certificate>()
                },
            )
            val ssl = SSLContext.getInstance("TLS")
            ssl.init(null, trustAll, java.security.SecureRandom())
            conn.sslSocketFactory = ssl.socketFactory
            conn.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true }
        }
        return conn
    }
}
