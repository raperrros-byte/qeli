package com.qeli.geo

/** Minimal protobuf wire reader for v2ray geosite/geoip .dat files. */
internal class ProtoReader(private val data: ByteArray, private var pos: Int = 0) {
    val hasMore: Boolean get() = pos < data.size

    fun tryReadTag(): Pair<Int, Int>? {
        if (!hasMore) return null
        val key = readVarint()
        return ((key ushr 3).toInt()) to ((key and 7L).toInt())
    }

    fun readVarint(): Long {
        var result = 0L
        var shift = 0
        while (pos < data.size) {
            val b = data[pos++].toInt() and 0xff
            result = result or ((b and 0x7f).toLong() shl shift)
            if (b and 0x80 == 0) return result
            shift += 7
            if (shift > 63) throw IllegalArgumentException("varint too long")
        }
        throw IllegalArgumentException("truncated varint")
    }

    fun readBytes(): ByteArray {
        val len = readVarint().toInt()
        if (len < 0 || pos + len > data.size) throw IllegalArgumentException("bad length-delimited")
        val s = data.copyOfRange(pos, pos + len)
        pos += len
        return s
    }

    fun readString(): String = String(readBytes(), Charsets.UTF_8)

    fun skip(wireType: Int) {
        when (wireType) {
            0 -> readVarint()
            1 -> pos += 8
            2 -> readBytes()
            5 -> pos += 4
            else -> throw IllegalArgumentException("unsupported wire type $wireType")
        }
    }
}
