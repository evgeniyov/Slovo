#!/bin/bash
# Collects voice clips the phone app recorded, over the USB cable, and drops them in
# Slovo's inbox, where the importer turns each one into a meeting.
#
#     Scripts/pull_phone_clips.sh
#
# Slovo collects the same clips by itself while it is running; this is the way to do it
# by hand. Nothing listens on the network: this runs only while a cable is connected and
# adb sees the phone.
#
# The phone publishes a clip only once it is complete, with the sidecar that holds its
# marked moments already beside it. This side does not take that on trust: audio with no
# sidecar waits until it has sat still for a minute, which is far longer than publishing
# takes, so a recording another app left in the folder still arrives while a clip caught
# mid-publish never does.
set -u

ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
# Without this the adb server browses Bonjour for phones on Wi-Fi, and macOS asks whether
# the app that started it may find devices on the local network. Clips come over USB only.
export ADB_MDNS=0
PHONE_FOLDER="/sdcard/Download/Slovo"
INBOX="$HOME/Library/Application Support/Slovo/Inbox"
# Where the importer moves a clip once it has become a meeting.
IMPORTED="$INBOX/Imported"
GRACE_SECONDS=60
# The audio the app's importer takes, matched without regard to case.
AUDIO_PATTERN='\.(m4a|mp3|wav|caf|aac|aif|aiff|mp4|mov)$'
# A file is copied under this hidden prefix and renamed once whole, so the importer, which
# skips hidden files, never takes a copy that stalled half way for a finished clip.
PARTIAL_PREFIX=".partial-"

if [ ! -x "$ADB" ]; then
    echo "adb not found at $ADB" >&2
    exit 1
fi

# Every adb call reads its input from /dev/null and every `adb shell` gets -n: adb forwards
# its own input to the phone, so inside a `while read` loop it would swallow the names still
# waiting to be read and end the loop after the first clip.
serials=$("$ADB" devices </dev/null | sed -n 's/\(.*\)\tdevice$/\1/p')
if [ -z "$serials" ]; then
    echo "No phone connected. Plug in the cable, unlock the phone and allow USB debugging." >&2
    exit 1
fi

mkdir -p "$INBOX"
collected=0

# Refuses every name that must not reach a shell or a path: anything that leaves the folder
# or ends the quoting around it, anything beginning with a dot — MediaStore parks a clip that
# is still being written under `.pending-<n>-<name>` — and a name too long to be copied under
# its partial name.
is_safe_name() {
    case "$1" in
        ""|.*|*/*|*\'*|*\"*|*\\*|*\`*|*\$*) return 1 ;;
    esac
    [ "$(printf '%s' "$1" | wc -c)" -le $((255 - ${#PARTIAL_PREFIX})) ]
}

# Copies one file under its hidden partial name, gives it its own name once it arrived whole,
# and leaves nothing behind when it fails.
pull_file() {
    partial="$INBOX/$PARTIAL_PREFIX$2"
    rm -f "$partial"
    if "$ADB" -s "$1" pull "$PHONE_FOLDER/$2" "$partial" </dev/null >/dev/null 2>&1 && [ -s "$partial" ] \
        && mv -f "$partial" "$INBOX/$2"; then
        return 0
    fi
    rm -f "$partial"
    return 1
}

# Deletes one file from the phone, and fails when the phone refuses to.
remove_file() {
    "$ADB" -s "$1" shell -n "rm -f '$PHONE_FOLDER/$2'" </dev/null >/dev/null 2>&1
}

# True once audio with no sidecar has sat still long enough to be what it looks like. Both
# times come from the phone: a Mac clock running ahead of it would make a clip written a
# second ago look a minute old.
has_waited() {
    times=$("$ADB" -s "$1" shell -n "date +%s; stat -c %Y '$PHONE_FOLDER/$2'" </dev/null 2>/dev/null | tr -d '\r')
    now=$(printf '%s\n' "$times" | sed -n 1p)
    modified=$(printf '%s\n' "$times" | sed -n 2p)
    case "$now" in ""|*[!0-9]*) return 1 ;; esac
    case "$modified" in ""|*[!0-9]*) return 1 ;; esac
    [ "$((now - modified))" -ge "$GRACE_SECONDS" ]
}

collect_from() {
    serial="$1"
    names=$("$ADB" -s "$serial" shell -n "ls -1 $PHONE_FOLDER 2>/dev/null" </dev/null | tr -d '\r')
    clips=$(printf '%s\n' "$names" | grep -iE "$AUDIO_PATTERN" || true)
    if [ -z "$clips" ]; then
        echo "$serial: nothing waiting on the phone."
        return 0
    fi

    while IFS= read -r name; do
        [ -n "$name" ] || continue
        if ! is_safe_name "$name"; then
            echo "$serial: skipped, not a plain file name: $name" >&2
            continue
        fi
        # Already here: still in the inbox, or moved on to Imported once it became a
        # meeting. Without the second place to look, a clip the phone would not delete
        # would come back on the next run as a second meeting.
        if [ -e "$INBOX/$name" ] || [ -e "$IMPORTED/$name" ]; then
            echo "$serial: skipped, already here: $name"
            continue
        fi
        sidecar="${name%.*}.json"
        has_sidecar=no
        if printf '%s\n' "$names" | grep -qxF "$sidecar"; then
            has_sidecar=yes
        elif ! has_waited "$serial" "$name"; then
            echo "$serial: skipped, too new to be sure it is finished: $name"
            continue
        fi
        if [ "$has_sidecar" = yes ]; then
            # The sidecar carries the phone's time, title and marked moments. It goes first:
            # an import starts from the audio, so the metadata is there before the clip is.
            if ! pull_file "$serial" "$sidecar"; then
                echo "$serial: failed, left on the phone: $sidecar" >&2
                continue
            fi
        fi
        if ! pull_file "$serial" "$name"; then
            # The sidecar arrived and its audio did not: it goes, since nothing on the Mac
            # belongs to it, and the pair stays on the phone for a later run.
            if [ "$has_sidecar" = yes ]; then
                rm -f "$INBOX/$sidecar"
            fi
            echo "$serial: failed, left on the phone: $name" >&2
            continue
        fi
        collected=$((collected + 1))
        echo "$serial: collected: $name"
        # Removed only once both files are on the Mac and neither is empty. The audio goes
        # first so the sidecar outlives it: while the pair is whole the phone's own rule
        # still holds, so a delete that fails in between leaves a clip the next run pairs
        # again, not audio that would later be taken for a plain note. MediaStore keeps a
        # stale row until Android rescans, so the app's counter can lag by a few minutes.
        if remove_file "$serial" "$name"; then
            if [ "$has_sidecar" = yes ] && ! remove_file "$serial" "$sidecar"; then
                echo "$serial: collected, but the sidecar stays on the phone: $sidecar" >&2
            fi
        else
            echo "$serial: collected, but it stays on the phone: $name" >&2
        fi
    done <<< "$clips"
}

# Every phone that answered, not the first of them: a second phone on the cable carries its
# own clips, and adb needs to be told which one each command is for.
while IFS= read -r serial; do
    [ -n "$serial" ] || continue
    collect_from "$serial"
done <<< "$serials"

echo "Done: $collected clip(s) in the inbox. Slovo imports them within a few seconds."
