package com.qeli

import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.io.File
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class DiagnosticLogStoreInstrumentedTest {
    @Test
    fun privateNoBackupJournalSurvivesReopenAndClears() {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val directory = File(
            context.noBackupFilesDir,
            "diagnostic-test-${UUID.randomUUID()}",
        )
        try {
            DiagnosticLogStore.append(
                directory,
                message = "screen off",
                sessionId = "device-test",
                level = "debug",
                timestampMs = 123L,
            )
            assertEquals(
                listOf(DiagnosticLogEntry(123L, "device-test", "debug", "screen off")),
                DiagnosticLogStore.read(directory),
            )

            DiagnosticLogStore.clear(directory)
            assertFalse(File(directory, DiagnosticLogStore.FILE_NAME).exists())
        } finally {
            directory.deleteRecursively()
        }
    }
}
