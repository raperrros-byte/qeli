package com.qeli.geo

import java.util.TreeMap

enum class GeoDomainType { Plain, Regex, RootDomain, Full }

data class GeoDomainRule(val type: GeoDomainType, val value: String)

/** Loaded subset of geosite.dat for selected tags. */
class GeoSiteIndex {
    private val byTag = TreeMap<String, MutableList<GeoDomainRule>>(String.CASE_INSENSITIVE_ORDER)

    fun matchAny(hostRaw: String, vararg tags: String): Boolean {
        if (hostRaw.isBlank()) return false
        val host = hostRaw.trim().trimEnd('.').lowercase()
        for (tag in tags) {
            val rules = byTag[tag] ?: continue
            if (rules.any { matchOne(host, it) }) return true
        }
        return false
    }

    /** True when [host] ends with `.<suffix>` or equals [suffix] (e.g. `.ru`, `.su`). */
    fun matchesDirectTld(hostRaw: String, suffixes: Collection<String>): Boolean {
        if (hostRaw.isBlank() || suffixes.isEmpty()) return false
        val host = hostRaw.trim().trimEnd('.').lowercase()
        for (suffix in suffixes) {
            val tld = suffix.trim().trim('.').lowercase()
            if (tld.isEmpty()) continue
            if (host == tld || host.endsWith(".$tld")) return true
        }
        return false
    }

    /**
     * Concrete domain names from loaded geosite rules (skips TLD-only wildcards like `ru`).
     * Used to pre-resolve A records into TUN exclude routes at connect time.
     */
    fun collectResolvableDomains(limit: Int): List<String> {
        require(limit > 0) { "limit must be positive" }
        val out = LinkedHashSet<String>()
        for (rules in byTag.values) {
            for (rule in rules) {
                val candidate = when (rule.type) {
                    GeoDomainType.Full -> rule.value
                    GeoDomainType.RootDomain -> rule.value
                    else -> continue
                }.trim().trimEnd('.').lowercase()
                if (candidate.isEmpty()) continue
                if (!candidate.contains('.')) continue
                out += candidate
                if (out.size >= limit) return out.toList()
            }
        }
        return out.toList()
    }

    private fun matchOne(host: String, rule: GeoDomainRule): Boolean {
        val v = rule.value.trim().lowercase()
        if (v.isEmpty()) return false
        return when (rule.type) {
            GeoDomainType.Full -> host == v
            GeoDomainType.RootDomain -> host == v || host.endsWith(".$v")
            GeoDomainType.Plain -> host.contains(v)
            GeoDomainType.Regex -> try { Regex(v).containsMatchIn(host) } catch (_: Exception) { false }
        }
    }

    companion object {
        fun matchesDirectTld(hostRaw: String, suffixes: Collection<String>): Boolean =
            GeoSiteIndex().matchesDirectTld(hostRaw, suffixes)

        fun load(path: java.io.File, tags: Collection<String>): GeoSiteIndex {
            val want = tags.map { it.lowercase() }.toHashSet()
            val bytes = path.readBytes()
            val index = GeoSiteIndex()
            val reader = ProtoReader(bytes)
            while (reader.hasMore) {
                val tag = reader.tryReadTag() ?: break
                val (field, wt) = tag
                if (field != 1 || wt != 2) { reader.skip(wt); continue }
                parseGeoSite(reader.readBytes(), want, index)
            }
            return index
        }

        private fun parseGeoSite(msg: ByteArray, want: Set<String>, index: GeoSiteIndex) {
            var code: String? = null
            val domains = ArrayList<GeoDomainRule>()
            val r = ProtoReader(msg)
            while (r.hasMore) {
                val tag = r.tryReadTag() ?: break
                val (field, wt) = tag
                when {
                    field == 1 && wt == 2 -> code = r.readString()
                    field == 2 && wt == 2 -> domains.add(parseDomain(r.readBytes()))
                    else -> r.skip(wt)
                }
            }
            val c = code ?: return
            if (c.lowercase() !in want) return
            index.byTag.getOrPut(c) { ArrayList() }.addAll(domains)
        }

        private fun parseDomain(msg: ByteArray): GeoDomainRule {
            var type = GeoDomainType.Plain
            var value = ""
            val r = ProtoReader(msg)
            while (r.hasMore) {
                val tag = r.tryReadTag() ?: break
                val (field, wt) = tag
                when {
                    field == 1 && wt == 0 -> type = when (r.readVarint().toInt()) {
                        1 -> GeoDomainType.Regex
                        2 -> GeoDomainType.RootDomain
                        3 -> GeoDomainType.Full
                        else -> GeoDomainType.Plain
                    }
                    field == 2 && wt == 2 -> value = r.readString()
                    else -> r.skip(wt)
                }
            }
            return GeoDomainRule(type, value)
        }
    }
}
