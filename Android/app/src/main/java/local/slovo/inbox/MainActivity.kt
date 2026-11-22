package local.slovo.inbox

import android.Manifest
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.text.format.DateFormat
import android.view.HapticFeedbackConstants
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.isVisible
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import local.slovo.inbox.databinding.ActivityMainBinding
import local.slovo.inbox.databinding.RowClipBinding
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

/// The screen only starts and stops the recording service, shows what it is doing, and
/// lists what has not left the phone yet. Capture itself lives in the service, so leaving
/// this screen — or pocketing the phone mid-thought — does not cut the clip short.
class MainActivity : AppCompatActivity() {

    private companion object {
        const val STATE_KIND = "kind"
    }

    private lateinit var binding: ActivityMainBinding
    private val ui = Handler(Looper.getMainLooper())

    /// Reading the folder touches the disk and parses a file per clip.
    private val reader = Executors.newSingleThreadExecutor()

    /// What the next recording will be. While one is running the service's own kind wins.
    private var kind = RecordingService.KIND_NOTE
    private var momentsShown = 0
    private var lastState = RecordingService.State.IDLE

    private val tick = object : Runnable {
        override fun run() {
            showElapsed()
            ui.postDelayed(this, 500)
        }
    }

    private val askForMicrophone = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) startRecording() else toast(getString(R.string.permission_needed))
    }

    /// Asked for separately and never blocking: without it the recording still runs, it
    /// just runs without its notification.
    private val askForNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
        kind = savedInstanceState?.getString(STATE_KIND) ?: RecordingService.KIND_NOTE
        applyInsets()

        binding.recorder.modeNote.setOnClickListener { chooseKind(RecordingService.KIND_NOTE) }
        binding.recorder.modeMeeting.setOnClickListener { chooseKind(RecordingService.KIND_MEETING) }
        binding.recorder.record.setOnClickListener {
            if (RecordingService.isRecording) RecordingService.stop(this) else requestStart()
        }
        binding.recorder.markMoment.setOnClickListener { button ->
            RecordingService.mark(this)
            button.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putString(STATE_KIND, kind)
    }

    override fun onResume() {
        super.onResume()
        RecordingService.onStateChange = { runOnUiThread { render() } }
        // Adopt the count the service already holds, so coming back to a meeting in
        // progress does not replay a confirmation for a moment marked minutes ago.
        momentsShown = RecordingService.moments.size
        lastState = RecordingService.state
        render()
        refreshList()
    }

    override fun onPause() {
        super.onPause()
        RecordingService.onStateChange = null
        ui.removeCallbacks(tick)
    }

    override fun onDestroy() {
        super.onDestroy()
        reader.shutdown()
    }

    /// The window draws edge to edge, so the screen itself keeps its content out from
    /// under the status bar, the gesture bar and any camera cutout.
    private fun applyInsets() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        ViewCompat.setOnApplyWindowInsetsListener(binding.root) { view, insets ->
            val bars = insets.getInsets(
                WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout()
            )
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
    }

    private fun chooseKind(next: String) {
        // The kind is fixed for the length of a recording: it decides what the Mac does
        // with the clip, and half a meeting cannot become half a note.
        if (RecordingService.state != RecordingService.State.IDLE) return
        kind = next
        render()
    }

    private fun requestStart() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            askForNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) ==
            PackageManager.PERMISSION_GRANTED
        if (granted) startRecording() else askForMicrophone.launch(Manifest.permission.RECORD_AUDIO)
    }

    private fun startRecording() {
        RecordingService.start(this, kind)
        // The service flips the state a moment later; until it does the button still says
        // "record", and a second tap would stop what had just started.
        binding.recorder.record.isEnabled = false
        ui.postDelayed({ render() }, 350)
    }

    private fun render() {
        val state = RecordingService.state
        val idle = state == RecordingService.State.IDLE
        val recording = state == RecordingService.State.RECORDING
        val saving = state == RecordingService.State.SAVING
        val meeting = (if (idle) kind else RecordingService.kind) == RecordingService.KIND_MEETING
        val recorder = binding.recorder

        paintMode(recorder.modeNote, !meeting, idle)
        paintMode(recorder.modeMeeting, meeting, idle)
        recorder.modeNote.isEnabled = idle
        recorder.modeMeeting.isEnabled = idle
        recorder.modeHint.setText(if (meeting) R.string.mode_meeting_hint else R.string.mode_note_hint)

        recorder.status.setText(
            when {
                saving -> R.string.status_saving
                recording && meeting -> R.string.status_recording_meeting
                recording -> R.string.status_recording_note
                else -> R.string.status_idle
            }
        )
        recorder.liveDot.isVisible = recording
        recorder.spinner.isVisible = saving
        recorder.elapsed.setTextColor(shade(if (recording) R.color.text_primary else R.color.text_secondary))

        recorder.record.setText(
            when {
                recording -> R.string.stop
                meeting -> R.string.record_meeting
                else -> R.string.record_note
            }
        )
        recorder.record.setBackgroundResource(if (recording) R.drawable.button_stop else R.drawable.button_accent)
        recorder.record.setTextColor(shade(if (recording) R.color.text_primary else R.color.on_accent))
        // While the clip is being written there is nothing to start and nothing to stop.
        recorder.record.isEnabled = !saving
        recorder.record.alpha = if (saving) 0.45f else 1f

        val marking = recording && meeting
        recorder.markMoment.isVisible = marking
        recorder.meetingBox.isVisible = marking
        val moments = RecordingService.moments
        recorder.moments.text = if (moments.isEmpty()) {
            getString(R.string.moments_none)
        } else {
            resources.getQuantityString(R.plurals.moments_count, moments.size, moments.size)
        }
        if (marking && moments.size > momentsShown) confirmMoment(moments.size, moments.last())
        if (!marking) recorder.momentConfirm.alpha = 0f
        momentsShown = moments.size

        ui.removeCallbacks(tick)
        if (recording) ui.post(tick) else if (idle) recorder.elapsed.text = clock(0)

        // A clip that has just finished saving belongs in the list below.
        if (lastState != state) {
            lastState = state
            if (idle) refreshList()
        }
    }

    /// Three answers at once, because the phone may be face down, across the table or in
    /// a hand: a buzz, the button flinching, and the moment written down under the count.
    private fun confirmMoment(index: Int, atMs: Long) {
        val confirm = binding.recorder.momentConfirm
        confirm.text = getString(R.string.moment_marked, index, clock(atMs))
        confirm.alpha = 0f
        confirm.animate().alpha(1f).setDuration(160).start()
        val button = binding.recorder.markMoment
        button.animate().scaleX(0.97f).scaleY(0.97f).setDuration(80).withEndAction {
            button.animate().scaleX(1f).scaleY(1f).setDuration(140).start()
        }.start()
    }

    private fun paintMode(view: TextView, chosen: Boolean, idle: Boolean) {
        view.setBackgroundResource(if (chosen) R.drawable.segment_selected else R.drawable.segment_plain)
        view.setTextColor(shade(if (chosen) R.color.text_primary else R.color.text_secondary))
        // The half not being recorded says it cannot be chosen by fading, rather than by
        // vanishing and rearranging the screen under the owner's thumb.
        view.alpha = if (idle || chosen) 1f else 0.4f
    }

    private fun refreshList() {
        reader.execute {
            val clips = Inbox.waiting(this)
            ui.post { if (!isFinishing && !isDestroyed) showClips(clips) }
        }
    }

    private fun showClips(clips: List<WaitingClip>) {
        val waiting = binding.waiting
        waiting.clips.removeAllViews()
        for (clip in clips) {
            val row = RowClipBinding.inflate(layoutInflater, waiting.clips, false)
            row.whenText.text = moment(clip.recordedAt)
            row.meta.text = describe(clip)
            row.dot.backgroundTintList = ColorStateList.valueOf(
                shade(if (clip.kind == RecordingService.KIND_MEETING) R.color.accent else R.color.text_secondary)
            )
            row.delete.setOnClickListener { confirmDelete(clip) }
            waiting.clips.addView(row.root)
        }
        val empty = clips.isEmpty()
        waiting.empty.isVisible = empty
        waiting.folderHint.isVisible = !empty
        waiting.waitingCount.isVisible = !empty
        waiting.waitingCount.text =
            resources.getQuantityString(R.plurals.clips_waiting, clips.size, clips.size)
    }

    private fun describe(clip: WaitingClip): String {
        val parts = mutableListOf(
            getString(if (clip.kind == RecordingService.KIND_MEETING) R.string.kind_meeting else R.string.kind_note)
        )
        if (clip.durationMs > 0) parts += clock(clip.durationMs)
        if (clip.moments > 0) parts += resources.getQuantityString(R.plurals.moments_count, clip.moments, clip.moments)
        // A clip is published sidecar first, so one without a sidecar is not a clip still
        // arriving but one whose details could not be written. The Mac still collects it,
        // as a plain note, and the row says that rather than promising a refusal.
        if (clip.sidecar == null) parts += getString(R.string.clip_without_details)
        return parts.joinToString(" · ")
    }

    private fun confirmDelete(clip: WaitingClip) {
        MaterialAlertDialogBuilder(this)
            .setTitle(R.string.delete_title)
            .setMessage(R.string.delete_body)
            .setNegativeButton(R.string.cancel, null)
            .setPositiveButton(R.string.clip_delete) { _, _ -> remove(clip) }
            .show()
    }

    private fun remove(clip: WaitingClip) {
        reader.execute {
            val gone = Inbox.delete(this, clip)
            ui.post {
                if (isFinishing || isDestroyed) return@post
                if (!gone) toast(getString(R.string.delete_failed))
                refreshList()
            }
        }
    }

    /// Today's recordings need only a time; older ones have to say which day.
    private fun moment(epochMs: Long): String {
        val date = Date(epochMs)
        val time = DateFormat.getTimeFormat(this).format(date)
        val now = Calendar.getInstance()
        val then = Calendar.getInstance().apply { timeInMillis = epochMs }
        val sameDay = now.get(Calendar.YEAR) == then.get(Calendar.YEAR) &&
            now.get(Calendar.DAY_OF_YEAR) == then.get(Calendar.DAY_OF_YEAR)
        if (sameDay) return time
        return "${SimpleDateFormat("d MMM", Locale.getDefault()).format(date)}, $time"
    }

    private fun clock(ms: Long): String {
        val total = (ms / 1000).coerceAtLeast(0)
        val hours = total / 3600
        val minutes = (total % 3600) / 60
        val seconds = total % 60
        return if (hours > 0) {
            String.format(Locale.US, "%d:%02d:%02d", hours, minutes, seconds)
        } else {
            String.format(Locale.US, "%d:%02d", minutes, seconds)
        }
    }

    private fun showElapsed() {
        binding.recorder.elapsed.text = clock(SystemClock.elapsedRealtime() - RecordingService.startedAt)
    }

    private fun shade(color: Int): Int = ContextCompat.getColor(this, color)

    private fun toast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()
    }
}
