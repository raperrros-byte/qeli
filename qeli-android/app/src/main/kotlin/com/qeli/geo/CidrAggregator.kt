package com.qeli.geo

/** Merge and prioritize IPv4 CIDR strings for VpnService route tables. */
internal object CidrAggregator {
    /** Merge overlapping and adjacent IPv4 CIDR blocks into a minimal covering set. */
    fun aggregate(cidrs: Collection<String>): List<String> {
        var blocks = cidrs.mapNotNull(::parse).sortedWith(compareBy({ it.network }, { -it.prefix }))
        if (blocks.isEmpty()) return emptyList()

        blocks = removeContained(blocks)
        var changed = true
        while (changed) {
            changed = false
            val next = ArrayList<Ipv4Cidr>()
            var index = 0
            while (index < blocks.size) {
                if (index + 1 < blocks.size) {
                    val merged = mergeAdjacent(blocks[index], blocks[index + 1])
                    if (merged != null) {
                        next += merged
                        index += 2
                        changed = true
                        continue
                    }
                }
                next += blocks[index]
                index++
            }
            blocks = removeContained(next.sortedWith(compareBy({ it.network }, { -it.prefix })))
        }
        return blocks.map { it.toCidrString() }
    }

    /**
     * Keep the largest networks first when [max] routes cannot fit the full list.
     * This maximizes bypass coverage under VpnService route limits.
     */
    fun prioritizeForCoverage(cidrs: Collection<String>, max: Int): List<String> {
        require(max > 0) { "max must be positive" }
        val unique = aggregate(cidrs)
        if (unique.size <= max) return unique
        return unique
            .mapNotNull(::parse)
            .sortedBy { it.prefix }
            .take(max)
            .map { it.toCidrString() }
    }

    private fun parse(cidr: String): Ipv4Cidr? {
        val slash = cidr.indexOf('/')
        if (slash <= 0) return null
        val prefix = cidr.substring(slash + 1).toIntOrNull() ?: return null
        if (prefix !in 0..32) return null
        val parts = cidr.substring(0, slash).split('.')
        if (parts.size != 4) return null
        var network = 0
        for (part in parts) {
            val octet = part.toIntOrNull() ?: return null
            if (octet !in 0..255) return null
            network = (network shl 8) or octet
        }
        val mask = if (prefix == 0) 0 else (-1) shl (32 - prefix)
        return Ipv4Cidr(network and mask, prefix)
    }

    private fun removeContained(blocks: List<Ipv4Cidr>): List<Ipv4Cidr> {
        val kept = ArrayList<Ipv4Cidr>()
        for (candidate in blocks) {
            if (kept.none { contains(it, candidate) }) {
                kept.removeAll { contains(candidate, it) }
                kept += candidate
            }
        }
        return kept.sortedWith(compareBy({ it.network }, { -it.prefix }))
    }

    private fun contains(outer: Ipv4Cidr, inner: Ipv4Cidr): Boolean {
        if (inner.prefix < outer.prefix) return false
        return outer.contains(inner.network)
    }

    private fun mergeAdjacent(left: Ipv4Cidr, right: Ipv4Cidr): Ipv4Cidr? {
        if (left.prefix != right.prefix) return null
        val leftEnd = endAddress(left)
        if (right.network == leftEnd + 1) {
            return Ipv4Cidr(left.network, left.prefix - 1)
        }
        return null
    }

    private fun endAddress(block: Ipv4Cidr): Int {
        if (block.prefix == 0) return -1
        if (block.prefix >= 32) return block.network
        val hostBits = 32 - block.prefix
        return block.network or ((1 shl hostBits) - 1)
    }
}
