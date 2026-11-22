package local.slovo.inbox

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.MediaRecorder
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.provider.MediaStore
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import java.io.File
import java.io.IOException
import java.io.OutputStream
import java.util.Date
import java.util.concurrent.Executors

/// Recording lives in a foreground service, not in the screen: a thought is often
/// dictated while walking with the phone in a pocket, and a meeting runs for two hours
/// with the screen dark. An activity would be killed mid-file in either case. The
/// notification is what Android demands in exchange, and it doubles as the only honest
/// indicator that the microphone is open.
class RecordingService : Service() {

    /// Saving is a state of its own because a two-hour clip takes real seconds to copy
    /// into shared storage, and the screen should say so rather than look frozen.
    enum class State { IDLE, RECORDING, SAVING }

    companion object {
        const val ACTION_START = "local.slovo.inbox.action.START"
        const val ACTION_STOP = "local.slovo.inbox.action.STOP"
        const val ACTION_MARK = "local.slovo.inbox.action.MARK"
        const val EXTRA_KIND = "local.slovo.inbox.extra.KIND"

        /// The two values the sidecar's `kind` may hold.
        const val KIND_NOTE = "note"
        const val KIND_MEETING = "meeting"

        private const val CHANNEL_ID = "recording"
        private const val NOTIFICATION_ID = 1

        /// Longer than any meeting. The lock is released the moment capture ends, so
        /// this is only a seatbelt against a crash leaving the CPU held awake.
        private const val WAKE_LOCK_TIMEOUT_MS = 4L * 60 * 60 * 1000

        @Volatile
        var state: State = State.IDLE
            private set

        /// What is being recorded right now; meaningless while idle.
        @Volatile
        var kind: String = KIND_NOTE
            private set

        /// `SystemClock.elapsedRealtime()` when capture began; it does not jump when the
        /// clock is corrected.
        @Volatile
        var startedAt = 0L
            private set

        /// Offsets into the recording, in milliseconds. Replaced whole rather than
        /// appended to, so the screen can read the list from its own thread.
        @Volatile
        var moments: List<Long> = emptyList()
            private set

        val isRecording: Boolean get() = state == State.RECORDING

        /// The screen redraws itself when this fires; null while no screen is on top.
        var onStateChange: (() -> Unit)? = null

        fun start(context: Context, kind: String) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, RecordingService::class.java)
                    .setAction(ACTION_START)
                    .putExtra(EXTRA_KIND, kind)
            )
        }

        fun stop(context: Context) {
            context.startService(Intent(context, RecordingService::class.java).setAction(ACTION_STOP))
        }

        fun mark(context: Context) {
            context.startService(Intent(context, RecordingService::class.java).setAction(ACTION_MARK))
        }
    }

    private val main = Handler(Looper.getMainLooper())

    /// Copying the finished clip into shared storage happens here, never on the main
    /// thread: tens of megabytes would freeze the screen at the moment it says "saving".
    private val io = Executors.newSingleThreadExecutor()

    private var recorder: MediaRecorder? = null
    private var clip: File? = null
    private var recordedAt: Date? = null
    private var notificationSince = 0L
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> beginRecording(intent.getStringExtra(EXTRA_KIND) ?: KIND_NOTE)
            ACTION_STOP -> endRecording()
            ACTION_MARK -> markMoment()
            else -> stopSelf()
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        // A service killed for any other reason still leaves a playable clip.
        if (state == State.RECORDING) endRecording()
        releaseWakeLock()
        // Shutdown, not shutdownNow: a clip already being written finishes writing.
        io.shutdown()
        super.onDestroy()
    }

    private fun beginRecording(requestedKind: String) {
        if (state != State.IDLE) return
        // One reading of the clock names both files of the pair and fills `recordedAt`,
        // so a clip is stamped with when it was spoken, not when it finished copying.
        val startedOn = Date()
        val file = File(cacheDir, "clip-${startedOn.time}.m4a")
        val next = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) MediaRecorder(this)
        else @Suppress("DEPRECATION") MediaRecorder()
        kind = requestedKind
        moments = emptyList()
        notificationSince = System.currentTimeMillis()
        try {
            next.setAudioSource(MediaRecorder.AudioSource.MIC)
            next.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
            next.setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
            // 16 kHz mono is what the Mac transcribes at; anything richer is resampled
            // away there and only costs transfer time.
            next.setAudioSamplingRate(16_000)
            next.setAudioChannels(1)
            next.setAudioEncodingBitRate(32_000)
            next.setOutputFile(file.absolutePath)
            next.prepare()
            holdWakeLock()
            startForegroundWithNotification()
            next.start()
        } catch (_: Exception) {
            next.release()
            file.delete()
            releaseWakeLock()
            main.post { Toast.makeText(this, R.string.record_failed, Toast.LENGTH_LONG).show() }
            stopForegroundAndSelf()
            return
        }
        recorder = next
        clip = file
        recordedAt = startedOn
        startedAt = SystemClock.elapsedRealtime()
        state = State.RECORDING
        onStateChange?.invoke()
    }

    /// A moment is only an offset into the clip. The transcript on the Mac is what gives
    /// it meaning, so nothing is captured here beyond when the tap happened.
    private fun markMoment() {
        if (state != State.RECORDING) return
        moments = moments + (SystemClock.elapsedRealtime() - startedAt)
        // Re-posting the notification is how a tap on the lock screen answers back.
        updateNotification()
        onStateChange?.invoke()
    }

    private fun endRecording() {
        if (state != State.RECORDING) return
        val current = recorder ?: return
        recorder = null
        val durationMs = SystemClock.elapsedRealtime() - startedAt
        var usable = true
        try {
            current.stop()
        } catch (_: Exception) {
            // Stopped within a fraction of a second: the file has no frames and is
            // unusable either way.
            usable = false
        } finally {
            current.release()
        }
        releaseWakeLock()

        val file = clip
        clip = null
        val startedOn = recordedAt ?: Date()
        recordedAt = null
        val capturedKind = kind
        val capturedMoments = moments

        if (!usable || file == null) {
            // Nothing was recorded and nothing was lost, so nothing is announced.
            file?.delete()
            finish(null)
            return
        }

        state = State.SAVING
        onStateChange?.invoke()
        updateNotification()
        io.execute {
            // MediaStore can also throw while inserting or cleaning up a row. Always
            // return to idle and report failure; the original clip stays for recovery.
            val problem = try {
                publish(file, startedOn, durationMs, capturedKind, capturedMoments)
            } catch (_: Exception) {
                R.string.save_failed
            }
            main.post { finish(problem) }
        }
    }

    /// Back to idle. `problem` is the string resource to show when the clip did not reach
    /// the folder whole, and null when it did; a toast is the only report the owner gets.
    private fun finish(problem: Int?) {
        state = State.IDLE
        moments = emptyList()
        if (problem != null) Toast.makeText(this, problem, Toast.LENGTH_LONG).show()
        onStateChange?.invoke()
        stopForegroundAndSelf()
    }

    /// Moves the finished clip into the shared Download/Slovo folder, where the Mac reads
    /// it over the cable without this app holding any storage permission. Returns null when
    /// the pair got there whole, and otherwise what the owner should be told.
    ///
    /// The order is the point of this method. Both files are written while still pending,
    /// which on disk means a placeholder name no listing of the folder shows; the sidecar
    /// is then revealed, and the audio only after it. So an .m4a visible in the folder
    /// always has its .json already beside it, and the Mac can read a missing sidecar as a
    /// real absence instead of a file that may still be on its way.
    private fun publish(
        file: File,
        recordedAt: Date,
        durationMs: Long,
        kind: String,
        moments: List<Long>
    ): Int? {
        val stamp = Sidecar.fileStamp(recordedAt)
        val audio = stage("slovo-$stamp.m4a", "audio/mp4") { output ->
            file.inputStream().use { it.copyTo(output) }
        } ?: return R.string.save_failed
        // Written with the audio closed and complete, so its duration is the real one.
        val json = Sidecar.text(kind, recordedAt, durationMs, Sidecar.device, null, moments)
        val sidecar = stage("slovo-$stamp.json", "application/json") { output ->
            output.write(json.toByteArray())
        }
        val described = sidecar != null && reveal(sidecar)
        // The audio is revealed even when its sidecar could not be written: it is the
        // recording itself, and throwing it away over a few hundred bytes of metadata is
        // not this code's decision to make. This is the only way a clip can reach the
        // folder without a sidecar, which is why both the waiting list and the Mac treat
        // that as the rare accident it now is rather than as a clip still arriving.
        if (!reveal(audio)) return R.string.save_failed
        file.delete()
        return if (described) null else R.string.save_without_details
    }

    /// Creates a row in the shared folder and fills it, leaving it pending: until `reveal`
    /// it carries a placeholder name on disk, so nothing watching the folder can see it.
    private fun stage(name: String, mime: String, body: (OutputStream) -> Unit): Uri? {
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, mime)
            put(MediaStore.MediaColumns.RELATIVE_PATH, Inbox.relativePath())
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val target = contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
        return try {
            contentResolver.openOutputStream(target)?.use(body) ?: throw IOException("no stream for $name")
            target
        } catch (_: Exception) {
            contentResolver.delete(target, null, null)
            null
        }
    }

    /// Clears pending, which is the instant the file appears in the folder under its real
    /// name. Everything the file will ever hold is already written by then.
    private fun reveal(target: Uri): Boolean {
        val shown = try {
            val values = ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }
            contentResolver.update(target, values, null, null) > 0
        } catch (_: Exception) {
            false
        }
        // A row that will not come out of pending is dropped rather than left behind: it is
        // invisible to both halves of Slovo and would only take up space until MediaStore
        // expires it days later.
        if (!shown) contentResolver.delete(target, null, null)
        return shown
    }

    /// A foreground service is allowed to keep recording, but it is not promised CPU time
    /// once the screen sleeps. A partial wake lock is what actually keeps the encoder fed
    /// through an hour of a phone lying face down on a table.
    private fun holdWakeLock() {
        if (wakeLock != null) return
        val lock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "$packageName:recording")
        lock.setReferenceCounted(false)
        lock.acquire(WAKE_LOCK_TIMEOUT_MS)
        wakeLock = lock
    }

    private fun releaseWakeLock() {
        wakeLock?.takeIf { it.isHeld }?.release()
        wakeLock = null
    }

    private fun buildNotification(): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val saving = state == State.SAVING
        val meeting = kind == KIND_MEETING
        val text = when {
            saving -> getString(R.string.notification_saving)
            meeting && moments.isNotEmpty() -> getString(R.string.status_recording_meeting) + " · " +
                resources.getQuantityString(R.plurals.moments_count, moments.size, moments.size)
            meeting -> getString(R.string.status_recording_meeting)
            else -> getString(R.string.status_recording_note)
        }
        val builder = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(ContextCompat.getColor(this, R.color.accent))
            .setOngoing(true)
            .setSilent(true)
            .setContentIntent(open)
        if (saving) builder.setShowWhen(false) else builder.setUsesChronometer(true).setWhen(notificationSince)
        // A meeting is marked with the phone face down on the table; the moment has to be
        // reachable without unlocking the screen first.
        if (!saving && meeting) {
            val mark = PendingIntent.getService(
                this, 1,
                Intent(this, RecordingService::class.java).setAction(ACTION_MARK),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
            )
            builder.addAction(R.drawable.ic_moment, getString(R.string.notification_moment), mark)
        }
        return builder.build()
    }

    private fun startForegroundWithNotification() {
        val manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && manager.getNotificationChannel(CHANNEL_ID) == null) {
            manager.createNotificationChannel(
                NotificationChannel(
                    CHANNEL_ID, getString(R.string.notification_channel), NotificationManager.IMPORTANCE_LOW
                )
            )
        }
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
        } else {
            0
        }
        ServiceCompat.startForeground(this, NOTIFICATION_ID, buildNotification(), type)
    }

    /// Recording survives a denied notification permission — it just runs unseen — so the
    /// permission is checked here rather than assumed.
    private fun updateNotification() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, buildNotification())
    }

    private fun stopForegroundAndSelf() {
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }
}
