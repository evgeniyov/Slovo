# Speech fixtures

These clips use [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) recordings.
FFmpeg converted all WAV files to 16 kHz mono signed 16-bit PCM with
`-map_metadata -1 -ar 16000 -ac 1 -c:a pcm_s16le`.

| File | Source and credit |
| --- | --- |
| `en-hello.wav` (2.616 s) | Kathleen, [Rhasspy Voice Dataset](https://github.com/rhasspy/dataset-voice-kathleen/tree/c51cb971036a127359b43c621f26ca5081eab13e), [`data/arctic_a0004_1592748478.wav`](https://github.com/rhasspy/dataset-voice-kathleen/blob/c51cb971036a127359b43c621f26ca5081eab13e/data/arctic_a0004_1592748478.wav). |
| `ru-hello.wav` (5.274 s) | Dmitri, [Open Home Foundation Voice dataset](https://github.com/OHF-Voice/voice-datasets/tree/1692a7c509e35eceb5d3bd6a7581a104629ded0f), [`ru_RU-dmitri.zip`, v1.0.0](https://github.com/OHF-Voice/voice-datasets/releases/download/v1.0.0/ru_RU-dmitri.zip), member `ru-RU/0000000001_0300000050_General/0000000155.webm`. |
| `two-voices.wav` (21.529 s) | Dmitri's [`0000000001.mp3`](https://github.com/OHF-Voice/voice-datasets/blob/1692a7c509e35eceb5d3bd6a7581a104629ded0f/ru_RU/dmitri/0000000001.mp3), then Kathleen's `en-hello.wav` and [`data/arctic_a0003_1592748500.wav`](https://github.com/rhasspy/dataset-voice-kathleen/blob/c51cb971036a127359b43c621f26ca5081eab13e/data/arctic_a0003_1592748500.wav), then Dmitri's `ru-hello.wav`. |

The combined clip has 0.8 s of silence between speakers and 0.2 s between
Kathleen's two clips. The recognition and speaker separation tests use the
transcripts in the text files. Some transcripts include timestamps.
