package local.slovo.inbox

import android.os.Build
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.TimeUnit

/// The small JSON file that travels next to every clip. It is written only once the
/// audio is closed and complete, so the Mac can treat its arrival as "this recording is
/// whole, collect it" without ever reading a file that is still growing.
object Sidecar {

    private const val VERSION = 1

    /// Vendors keep the name printed on the box in a read-only system property, and each
    /// one picked a different key for it.
    private val MARKETING_NAME_KEYS = listOf(
        "ro.vivo.market.name",
        "ro.product.marketname",
        "ro.oppo.market.name",
        "ro.config.marketing_name"
    )

    fun text(
        kind: String,
        recordedAt: Date,
        durationMs: Long,
        device: String,
        title: String?,
        moments: List<Long>
    ): String {
        val highlights = JSONArray()
        for (moment in moments) {
            // `note` is in the contract for a thought typed next to a moment; nothing on
            // this side types one yet, so it is honestly null rather than an empty string.
            highlights.put(JSONObject().put("atMs", moment).put("note", JSONObject.NULL))
        }
        return JSONObject()
            .put("version", VERSION)
            .put("kind", kind)
            .put("recordedAt", timestamp(recordedAt))
            .put("durationMs", durationMs)
            .put("device", device)
            .put("title", title ?: JSONObject.NULL)
            .put("highlights", highlights)
            .toString(2)
    }

    /// ISO 8601 with the phone's own offset, so the Mac knows what "12:54" meant where
    /// the recording happened even if the two machines sit in different time zones.
    fun timestamp(date: Date): String =
        SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ssXXX", Locale.US).format(date)

    fun parseTimestamp(text: String?): Long? {
        if (text.isNullOrBlank()) return null
        return try {
            SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ssXXX", Locale.US).parse(text)?.time
        } catch (_: Exception) {
            null
        }
    }

    /// Both files of a pair are named from the same instant, the one the recording began
    /// at, so a clip sorts by when it was spoken rather than by when it finished copying.
    fun fileStamp(date: Date): String =
        SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(date)

    /// What the owner calls this phone. `Build.MODEL` is a part number here ("V2419A"),
    /// and Android exposes no public marketing name, so the platform's own `getprop` is
    /// asked first; where that is unavailable the manufacturer and model still identify
    /// the phone, just less kindly.
    val device: String by lazy {
        MARKETING_NAME_KEYS.firstNotNullOfOrNull { readProperty(it) } ?: makerAndModel()
    }

    private fun makerAndModel(): String {
        val maker = Build.MANUFACTURER.trim()
        val model = Build.MODEL.trim()
        return if (model.startsWith(maker, ignoreCase = true)) model else "$maker $model"
    }

    private fun readProperty(key: String): String? = try {
        val process = ProcessBuilder("/system/bin/getprop", key).redirectErrorStream(true).start()
        val value = process.inputStream.bufferedReader().use { it.readText() }.trim()
        process.waitFor(1, TimeUnit.SECONDS)
        process.destroy()
        value.ifBlank { null }
    } catch (_: Exception) {
        null
    }
}
