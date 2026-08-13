package com.qeli.geo

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class GeoRoutePolicyTest {
    @Test
    fun `bypass-ru matches ru and su tld suffixes`() {
        assertTrue(GeoSiteIndex.matchesDirectTld("yandex.ru", GeoRoutePolicy.DIRECT_TLD_SUFFIXES))
        assertTrue(GeoSiteIndex.matchesDirectTld("example.su", GeoRoutePolicy.DIRECT_TLD_SUFFIXES))
        assertTrue(GeoRoutePolicy.matchesRuDirect("bank.ru", null))
        assertFalse(GeoSiteIndex.matchesDirectTld("google.com", GeoRoutePolicy.DIRECT_TLD_SUFFIXES))
    }
}

class CidrAggregatorTest {
    @Test
    fun `aggregate merges adjacent blocks`() {
        val merged = CidrAggregator.aggregate(
            listOf("192.168.0.0/24", "192.168.1.0/24", "10.0.0.0/8"),
        )
        assertTrue(merged.contains("192.168.0.0/23"))
        assertTrue(merged.contains("10.0.0.0/8"))
    }

    @Test
    fun `prioritize keeps largest networks first`() {
        val picked = CidrAggregator.prioritizeForCoverage(
            listOf("10.0.0.0/8", "10.0.0.0/24", "192.168.0.0/16"),
            max = 2,
        )
        assertEquals(2, picked.size)
        assertTrue(picked.contains("10.0.0.0/8"))
    }
}
