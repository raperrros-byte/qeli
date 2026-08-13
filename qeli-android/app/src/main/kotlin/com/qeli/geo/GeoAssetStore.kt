package com.qeli.geo

import android.content.Context
import android.os.Build
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/** Download + cache geosite.dat / geoip.dat (same sources as Windows GeoAssetStore). */
object GeoAssetStore {
    const val GEOSITE_FILE = "geosite.dat"
    const val GEOIP_FILE = "geoip.dat"

    private val geositeUrls = listOf(
        "https://cdn.jsdelivr.net/gh/runetfreedom/russia-v2ray-rules-dat@release/geosite.dat",
        "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/geosite.dat",
        "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geosite.dat",
        "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
    )
    private val geoipUrls = listOf(
        "https://cdn.jsdelivr.net/gh/runetfreedom/russia-v2ray-rules-dat@release/geoip.dat",
        "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/geoip.dat",
        "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geoip.dat",
        "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
    )

    @Volatile private var site: GeoSiteIndex? = null
    @Volatile private var ip: GeoIpIndex? = null
    @Volatile private var loadedPreset = ""

    fun dir(ctx: Context): File = File(ctx.filesDir, "geo").also { it.mkdirs() }
    fun geositePath(ctx: Context) = File(dir(ctx), GEOSITE_FILE)
    fun geoipPath(ctx: Context) = File(dir(ctx), GEOIP_FILE)
    fun hasFiles(ctx: Context) = geositePath(ctx).isFile && geoipPath(ctx).isFile

    fun statusText(ctx: Context): String {
        if (!hasFiles(ctx)) return "not downloaded"
        val gs = geositePath(ctx)
        val gi = geoipPath(ctx)
        return "geosite ${gs.length() / 1024} KB, geoip ${gi.length() / 1024} KB"
    }

    suspend fun download(ctx: Context, log: (String) -> Unit = {}) = withContext(Dispatchers.IO) {
        dir(ctx)
        downloadOne(geositeUrls, geositePath(ctx), "geosite.dat", log)
        downloadOne(geoipUrls, geoipPath(ctx), "geoip.dat", log)
        site = null
        ip = null
        loadedPreset = ""
    }

    fun getOrLoad(ctx: Context, presetId: String): Pair<GeoSiteIndex?, GeoIpIndex?> {
        val id = ProxyRoutePreset.normalize(presetId)
        if (site != null && ip != null && loadedPreset == id) return site to ip
        if (!hasFiles(ctx)) return null to null
        val (siteTags, ipTags) = ProxyRoutePreset.tagsFor(id)
        site = if (siteTags.isEmpty()) GeoSiteIndex() else
            runCatching { GeoSiteIndex.load(geositePath(ctx), siteTags) }.getOrNull()
        ip = if (ipTags.isEmpty()) GeoIpIndex() else
            runCatching { GeoIpIndex.load(geoipPath(ctx), ipTags) }.getOrNull()
        loadedPreset = id
        return site to ip
    }

    /**
     * CIDRs to exclude from TUN for bypass presets.
     * Aggregates geoip blocks and caps the route table on older Android builds.
     */
    fun tunExcludeCidrs(ctx: Context, presetId: String, maxRoutes: Int = tunExcludeRouteBudget()): List<String> {
        val codes = ProxyRoutePreset.tunBypassIpCodes(presetId)
        if (codes.isEmpty()) return emptyList()
        val (_, ipIdx) = getOrLoad(ctx, presetId)
        val raw = ipIdx?.allCidrsFor(codes) ?: return emptyList()
        if (raw.isEmpty()) return emptyList()
        return CidrAggregator.prioritizeForCoverage(raw, maxRoutes)
    }

    /** CIDRs routed into the tunnel for proxy-only presets (ru-blocked, gfw-blacklist). */
    fun tunIncludeCidrs(ctx: Context, presetId: String, maxRoutes: Int = tunIncludeRouteBudget()): List<String> {
        val codes = ProxyRoutePreset.tunIncludeIpCodes(presetId)
        if (codes.isEmpty()) return emptyList()
        val (_, ipIdx) = getOrLoad(ctx, presetId)
        val raw = ipIdx?.allCidrsFor(codes) ?: return emptyList()
        if (raw.isEmpty()) return emptyList()
        return CidrAggregator.prioritizeForCoverage(raw, maxRoutes)
    }

    fun tunIncludeRouteBudget(): Int =
        if (Build.VERSION.SDK_INT >= 33) 2048 else 120

    /** Route budget for geo excludes; pre-Android-13 complement splits are much tighter. */
    fun tunExcludeRouteBudget(): Int =
        if (Build.VERSION.SDK_INT >= 33) 4096 else 160

    private fun downloadOne(urls: List<String>, dest: File, label: String, log: (String) -> Unit) {
        var last: Exception? = null
        for (url in urls) {
            try {
                log("Downloading $label…")
                val conn = (URL(url).openConnection() as HttpURLConnection).apply {
                    connectTimeout = 30_000
                    readTimeout = 120_000
                    setRequestProperty("User-Agent", "qeli-geo/0.7")
                    instanceFollowRedirects = true
                }
                conn.inputStream.use { input ->
                    val tmp = File(dest.absolutePath + ".tmp")
                    tmp.outputStream().use { input.copyTo(it) }
                    if (tmp.length() < 1024) throw IllegalStateException("$label too small")
                    tmp.copyTo(dest, overwrite = true)
                    tmp.delete()
                }
                conn.disconnect()
                log("$label OK (${dest.length() / 1024} KB)")
                return
            } catch (e: Exception) {
                last = e
                log("$label fail (${e.message}) — try next mirror")
            }
        }
        throw last ?: IllegalStateException("download $label failed")
    }
}
