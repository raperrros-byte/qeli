package com.qeli

import android.content.Context
import java.util.Locale
import java.util.TreeSet

/** Built-in + user SNI / TLS front-host catalog (mirrors Qeli.Shared.SniCatalog). */
object SniCatalog {
    @Volatile private var builtin: List<String>? = null

    fun builtin(context: Context): List<String> {
        builtin?.let { return it }
        val loaded = try {
            context.assets.open("sni_hosts.txt").bufferedReader().useLines { lines ->
                lines.mapNotNull { normalize(it) }.distinct().sorted().toList()
            }
        } catch (_: Exception) {
            TransportAuto.SNI_PRESETS
        }
        builtin = loaded
        return loaded
    }

    fun normalize(raw: String?): String? {
        if (raw.isNullOrBlank()) return null
        var h = raw.trim().lowercase(Locale.US)
        val hash = h.indexOf('#')
        if (hash >= 0) h = h.substring(0, hash).trim()
        if (h.isEmpty() || h.startsWith(';')) return null
        if (h.contains("://")) h = h.substringAfter("://")
        h = h.substringBefore('/')
        val colon = h.lastIndexOf(':')
        if (colon > 0 && h.substring(colon + 1).all { it.isDigit() }) {
            h = h.substring(0, colon)
        }
        h = h.trim('.')
        if (h.length !in 3..80 || !h.contains('.')) return null
        return h
    }

    fun merge(context: Context, custom: Collection<String>?): List<String> {
        val set = TreeSet(String.CASE_INSENSITIVE_ORDER)
        set.addAll(builtin(context))
        custom?.forEach { normalize(it)?.let(set::add) }
        return set.toList()
    }
}
