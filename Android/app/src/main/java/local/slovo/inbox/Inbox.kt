package local.slovo.inbox

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import org.json.JSONObject

/// One recording still sitting in the shared folder. The Mac deletes both files as it
/// collects them, so whatever is still here is what has not reached the Mac yet.
data class WaitingClip(
    val audio: Uri,
    /// Null only when the sidecar could not be written at all. The pair is published
    /// sidecar first, so audio that has none is an accident rather than a clip mid-save.
    val sidecar: Uri?,
    val kind: String,
    val recordedAt: Long,
    /// Zero when neither the sidecar nor MediaStore knows the length.
    val durationMs: Long,
    val moments: Int
)

/// Reads the shared folder the two halves of Slovo meet in. Everything here touches the
/// disk, so it is called from a background thread.
object Inbox {

    private const val FOLDER = "Slovo"

    fun relativePath(): String = "${Environment.DIRECTORY_DOWNLOADS}/$FOLDER"

    fun waiting(context: Context): List<WaitingClip> {
        val audio = ArrayList<Pair<String, Uri>>()
        val sidecars = HashMap<String, Uri>()
        val added = HashMap<String, Long>()
        val durations = HashMap<String, Long>()

        val projection = arrayOf(
            MediaStore.MediaColumns._ID,
            MediaStore.MediaColumns.DISPLAY_NAME,
            MediaStore.MediaColumns.DATE_ADDED,
            MediaStore.MediaColumns.DURATION,
            MediaStore.MediaColumns.IS_PENDING
        )
        // Match this folder exactly: a prefix also includes SlovoBackup and nested
        // folders, where matching basenames could pair unrelated audio and sidecars.
        val selection = "${MediaStore.MediaColumns.RELATIVE_PATH} IN (?, ?)"
        val arguments = arrayOf(relativePath(), "${relativePath()}/")
        context.contentResolver.query(
            MediaStore.Downloads.EXTERNAL_CONTENT_URI, projection, selection, arguments,
            "${MediaStore.MediaColumns.DATE_ADDED} DESC"
        )?.use { cursor ->
            val idColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns._ID)
            val nameColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DISPLAY_NAME)
            val addedColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DATE_ADDED)
            val durationColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DURATION)
            val pendingColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.IS_PENDING)
            while (cursor.moveToNext()) {
                // MediaStore hands an app its own pending rows. On disk such a file has no
                // real name yet, and while a clip is being saved its audio is pending and
                // its sidecar not written, so listing them here would show every clip as
                // missing its details for as long as the copy takes.
                if (cursor.getInt(pendingColumn) != 0) continue
                val name = cursor.getString(nameColumn) ?: continue
                val uri = ContentUris.withAppendedId(
                    MediaStore.Downloads.EXTERNAL_CONTENT_URI, cursor.getLong(idColumn)
                )
                when {
                    name.endsWith(".m4a") -> {
                        val base = name.removeSuffix(".m4a")
                        audio.add(base to uri)
                        added[base] = cursor.getLong(addedColumn) * 1000
                        durations[base] = if (cursor.isNull(durationColumn)) 0 else cursor.getLong(durationColumn)
                    }
                    name.endsWith(".json") -> sidecars[name.removeSuffix(".json")] = uri
                }
            }
        }

        return audio.map { (base, uri) ->
            val sidecar = sidecars[base]
            val facts = sidecar?.let { read(context, it) }
            WaitingClip(
                audio = uri,
                sidecar = sidecar,
                kind = facts?.optString("kind").takeUnless { it.isNullOrBlank() } ?: RecordingService.KIND_NOTE,
                recordedAt = Sidecar.parseTimestamp(facts?.optString("recordedAt"))
                    ?: added[base] ?: System.currentTimeMillis(),
                durationMs = facts?.optLong("durationMs")?.takeIf { it > 0 } ?: durations[base] ?: 0,
                moments = facts?.optJSONArray("highlights")?.length() ?: 0
            )
        }
    }

    /// Both files go, audio first — the reverse of the order they were published in, and
    /// for the same reason. A Mac collecting at this exact moment must never find audio
    /// whose sidecar has already gone: it would take the clip as a plain note and lose the
    /// marked moments. A sidecar left alone for an instant costs nothing, since a sidecar
    /// without audio is not a clip.
    fun delete(context: Context, clip: WaitingClip): Boolean = try {
        val gone = context.contentResolver.delete(clip.audio, null, null) > 0
        // Only once the audio is really gone, so a refused delete leaves a whole pair
        // behind rather than a broken one.
        if (gone) clip.sidecar?.let { context.contentResolver.delete(it, null, null) }
        gone
    } catch (_: SecurityException) {
        false
    }

    private fun read(context: Context, uri: Uri): JSONObject? = try {
        context.contentResolver.openInputStream(uri)?.use { JSONObject(it.reader().readText()) }
    } catch (_: Exception) {
        null
    }
}
