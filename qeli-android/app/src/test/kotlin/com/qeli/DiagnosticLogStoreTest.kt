package com.qeli

import java.io.File
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DiagnosticLogStoreTest {
    private fun withTempDirectory(block: (File) -> Unit) {
        val directory = Files.createTempDirectory("qeli-diagnostic-log-").toFile()
        try {
            block(directory)
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun `entries survive a new reader with structured metadata`() = withTempDirectory { directory ->
        DiagnosticLogStore.append(
            directory = directory,
            message = "first\r\nsecond\u0000",
            sessionId = "session-01",
            level = "debug",
            timestampMs = 1_234L,
        )

        assertEquals(
            listOf(DiagnosticLogEntry(1_234L, "session-01", "debug", "first\\nsecond")),
            DiagnosticLogStore.read(directory),
        )
    }

    @Test
    fun `entry limit keeps the newest records`() = withTempDirectory { directory ->
        repeat(5) { index ->
            DiagnosticLogStore.append(
                directory,
                "event-$index",
                timestampMs = index.toLong(),
                maxEntries = 3,
                maxBytes = 16_384L,
            )
        }

        assertEquals(
            listOf("event-2", "event-3", "event-4"),
            DiagnosticLogStore.read(directory, maxEntries = 3, maxBytes = 16_384L)
                .map { it.message },
        )
    }

    @Test
    fun `byte limit keeps a bounded newest tail`() = withTempDirectory { directory ->
        repeat(5) { index ->
            DiagnosticLogStore.append(
                directory,
                "event-$index-${"x".repeat(60)}",
                timestampMs = index.toLong(),
                maxEntries = 100,
                maxBytes = 180L,
            )
        }

        val file = File(directory, DiagnosticLogStore.FILE_NAME)
        val restored = DiagnosticLogStore.read(directory, maxEntries = 100, maxBytes = 180L)
        assertTrue(restored.isNotEmpty())
        assertEquals("event-4-${"x".repeat(60)}", restored.last().message)
        assertTrue(file.length() <= 180L)
    }

    @Test
    fun `truncated tail is ignored and compacted`() = withTempDirectory { directory ->
        DiagnosticLogStore.append(directory, "complete", timestampMs = 10L)
        val file = File(directory, DiagnosticLogStore.FILE_NAME)
        file.appendText("truncated-record", StandardCharsets.UTF_8)

        assertEquals(listOf("complete"), DiagnosticLogStore.read(directory).map { it.message })
        assertFalse(file.readText(StandardCharsets.UTF_8).contains("truncated-record"))
        DiagnosticLogStore.append(directory, "after-recovery", timestampMs = 11L)
        assertEquals(
            listOf("complete", "after-recovery"),
            DiagnosticLogStore.read(directory).map { it.message },
        )
    }

    @Test
    fun `concurrent service and activity writers do not lose records`() =
        withTempDirectory { directory ->
            val executor = Executors.newFixedThreadPool(4)
            repeat(4) { writer ->
                executor.submit {
                    repeat(100) { index ->
                        DiagnosticLogStore.append(
                            directory,
                            "writer-$writer-event-$index",
                            sessionId = "session",
                        )
                    }
                }
            }
            executor.shutdown()
            assertTrue(executor.awaitTermination(30, TimeUnit.SECONDS))
            assertEquals(400, DiagnosticLogStore.read(directory).size)
        }

    @Test
    fun `clear removes durable history`() = withTempDirectory { directory ->
        DiagnosticLogStore.append(directory, "before-clear")
        DiagnosticLogStore.clear(directory)

        assertTrue(DiagnosticLogStore.read(directory).isEmpty())
        assertFalse(File(directory, DiagnosticLogStore.FILE_NAME).exists())
    }
}
