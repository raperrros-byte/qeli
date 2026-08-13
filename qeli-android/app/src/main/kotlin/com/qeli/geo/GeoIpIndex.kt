package com.qeli.geo

import java.util.TreeMap

data class Ipv4Cidr(val network: Int, val prefix: Int) {
    fun toCidrString(): String {
        val a = (network ushr 24) and 0xff
        val b = (network ushr 16) and 0xff
        val c = (network ushr 8) and 0xff
        val d = network and 0xff
        return "$a.$b.$c.$d/$prefix"
    }

    fun contains(ip: Int): Boolean {
        if (prefix == 0) return true
        if (prefix >= 32) return ip == network
        val mask = (-1) shl (32 - prefix)
        return (ip and mask) == (network and mask)
    }
}

/** Loaded subset of geoip.dat for selected country codes. */
class GeoIpIndex {
    private val byCode = TreeMap<String, MutableList<Ipv4Cidr>>(String.CASE_INSENSITIVE_ORDER)

    fun cidrsFor(codes: Collection<String>, max: Int = Int.MAX_VALUE): List<String> {
        val out = ArrayList<String>()
        for (code in codes) {
            val list = byCode[code] ?: continue
            for (c in list) {
                if (out.size >= max) return out
                out.add(c.toCidrString())
            }
        }
        return out
    }

    fun allCidrsFor(codes: Collection<String>): List<String> = cidrsFor(codes)

    fun matchAny(ipBytes: ByteArray, vararg codes: String): Boolean {
        if (ipBytes.size != 4) return false
        val v = ((ipBytes[0].toInt() and 0xff) shl 24) or
            ((ipBytes[1].toInt() and 0xff) shl 16) or
            ((ipBytes[2].toInt() and 0xff) shl 8) or
            (ipBytes[3].toInt() and 0xff)
        for (code in codes) {
            val list = byCode[code] ?: continue
            if (list.any { it.contains(v) }) return true
        }
        return false
    }

    companion object {
        fun load(path: java.io.File, codes: Collection<String>): GeoIpIndex {
            val want = codes.map { it.lowercase() }.toHashSet()
            val bytes = path.readBytes()
            val index = GeoIpIndex()
            val reader = ProtoReader(bytes)
            while (reader.hasMore) {
                val tag = reader.tryReadTag() ?: break
                val (field, wt) = tag
                if (field != 1 || wt != 2) { reader.skip(wt); continue }
                parseGeoIp(reader.readBytes(), want, index)
            }
            return index
        }

        private fun parseGeoIp(msg: ByteArray, want: Set<String>, index: GeoIpIndex) {
            var code: String? = null
            val cidrs = ArrayList<Ipv4Cidr>()
            val r = ProtoReader(msg)
            while (r.hasMore) {
                val tag = r.tryReadTag() ?: break
                val (field, wt) = tag
                when {
                    field == 1 && wt == 2 -> code = r.readString()
                    field == 2 && wt == 2 -> tryParseCidr(r.readBytes())?.let { cidrs.add(it) }
                    else -> r.skip(wt)
                }
            }
            val c = code ?: return
            if (c.lowercase() !in want) return
            index.byCode.getOrPut(c) { ArrayList() }.addAll(cidrs)
        }

        private fun tryParseCidr(msg: ByteArray): Ipv4Cidr? {
            var ip: ByteArray? = null
            var prefix = 0
            val r = ProtoReader(msg)
            while (r.hasMore) {
                val tag = r.tryReadTag() ?: break
                val (field, wt) = tag
                when {
                    field == 1 && wt == 2 -> ip = r.readBytes()
                    field == 2 && wt == 0 -> prefix = r.readVarint().toInt().coerceIn(0, 32)
                    else -> r.skip(wt)
                }
            }
            val b = ip ?: return null
            if (b.size != 4) return null
            val network = ((b[0].toInt() and 0xff) shl 24) or
                ((b[1].toInt() and 0xff) shl 16) or
                ((b[2].toInt() and 0xff) shl 8) or
                (b[3].toInt() and 0xff)
            return Ipv4Cidr(network, prefix)
        }
    }
}
