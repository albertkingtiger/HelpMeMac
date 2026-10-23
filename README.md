# Help Me!

A C# desktop application with three text panels and three independent Start/Stop toggles. Captions use the bundled offline Whisper engine:

The current UI and audio backend are still WPF and NAudio, so the checked-in executable remains Windows-only. The Windows Live Captions integration has been removed; a complete macOS build requires replacing WPF with a cross-platform UI and replacing WASAPI capture with CoreAudio/AVFoundation capture.

1. **PC audio**: captions from the default playback device.
2. **Korean translation**: translates newly recognized PC audio into Korean. It can run with the PC caption display stopped; it still captures PC audio internally.
3. **Microphone**: English captions from the default microphone. Never translated.

Both PC audio and microphone speech recognition are fixed to English. PC captions translate from English into Korean. Text remains visible when stopped. Stopping discards pending work for that panel. Windows audio device changes require stopping and restarting capture.

## Caption text editing and files

The PC AUDIO text area is editable: type, paste, or correct text there. Translation reads this text area, rather than a separate audio-only stream. With Translation started, complete sentences are translated with their context. Growing or corrected sentences are retranslated as a whole; unchanged sentence translations are cached. Editing and AI Send leave capture running and preserve the text. Translation results for text edited away are discarded. Long input is translated in token-sized chunks instead of truncated to the model's context limit.

Translation input is scheduled after 200 ms, then takes the model's processing time. Recognition latency varies with the audio and system; these intervals are not total end-to-end latency guarantees.

**Save** in each panel exports that panel?s current text as a UTF-8 `.txt` file. **Format** clears that panel. Translation Format stays clear until the input changes. Automatic caption chunks, including microphone captions, are separated by spaces rather than forced newlines, and wrap naturally at the text area's edge. Manual line breaks are preserved when saving.

Windows captions accumulate stable text in the editable area instead of replacing it with a rolling snapshot, so typing and corrections stay intact. Changing the caption method preserves this document. Translation can process existing typed text without starting audio capture; if Translation is started with an empty input, it starts PC capture automatically.

## Caption method settings

Click **Settings** at the top right. Whisper is the only caption method and runs locally with the downloaded model.

Microphone captions and PC captions both use Whisper. Preferences are stored as JSON under the user's application-data directory, so they do not depend on the Windows Registry.

## First setup (internet required)

Install **.NET 8 SDK x64**, **Python 3.11 x64**, and the **Microsoft Visual C++ 2015–2022 x64 Redistributable** for native model runtimes. Restore/build needs internet once; running after setup does not.

From this directory in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/setup.ps1 -Translation
dotnet build HelpMe.csproj -c Release
dotnet run --project HelpMe.csproj -c Release
```

Omit `-Translation` to download just Whisper and use both caption panels. Use `-Python C:\path\to\python.exe` if Python is not on PATH.

After building, double-click **Start-HelpMe.cmd** to launch the Release build. It builds Release automatically if missing, and also supports the private SDK in `.tools/dotnet` when present.

The C# app handles the UI, capture, speech recognition, queues, and lifecycle. Translation uses the included **local Python subprocess**, exchanging JSON through pipes, without an HTTP server. This project is not a Python-free distribution.

## Models and offline operation

| Feature | Model | Storage |
| --- | --- | --- |
| Both caption streams | English Whisper Base Q8 (`ggml-base.en-q8_0.bin`) | ~150 MB |
| Korean translation | NLLB-200 distilled 600M, converted to CTranslate2 INT8 | 647 MB including tokenizer |

600M means parameter count, not file size. Default setup downloads a [pinned community CTranslate2 conversion](https://huggingface.co/JustFrederik/nllb-200-distilled-600M-ct2-int8/tree/302d78f00e6fdb50a1064059df7c392b735e9d05), checking file sizes and large-file SHA256 hashes. The finished model size is not a hard 600 MB guarantee. Runtime memory is greater than model storage. To convert the original Meta checkpoint yourself instead, add `-ConvertTranslation` during first setup; that path downloads several GB and installs PyTorch for conversion. Normal setup/inference does not require PyTorch.

At runtime both Hugging Face offline environment flags and `local_files_only=True` are enforced. No models download automatically; missing models produce setup instructions. No audio is saved to disk or sent to a service. The app writes recognized text only to its UI.

Edit `appsettings.json` to change model locations, Python executable, speech threshold, or segmentation. Relative paths first resolve beside the executable, then fall back to the project folder found above the build output. This works when starting the Release executable directly from Explorer, regardless of the working directory. Published copies can use models beside the executable or absolute paths. Default microphone/playback devices are selected when each capture starts. Enable microphone access for desktop applications in Windows Settings if needed.

Panels display plain text without timestamps, using 12px text and compact 26px buttons. Routine caption, translation, and microphone status messages are hidden; errors appear only when needed. Speech is emitted after about 0.15 seconds of silence, or after one second of continuous speech, plus inference time. A wall-clock timer also flushes the last phrase when loopback stops delivering audio frames. This is phrase-based captioning rather than token streaming. Long speech may be split mid-word at the segment limit. Simple energy-based voice detection may need threshold adjustments in noisy/quiet rooms. Both sources share one Whisper model and serialize recognition to control memory; slower CPUs may fall behind. Use headphones to avoid microphone echo from speakers.

The default Base model, beam search, and longer six-second segments favor natural wording and accuracy over minimum latency. Use `models/ggml-small-q8_0.bin` for higher accuracy on a faster machine, or `models/ggml-tiny.en-q8_0.bin` when latency matters more. Recognition processors are reused between segments, and translation uses beam search with beam size 4 for improved phrasing at some additional computation cost. Actual live delay varies with hardware and audio.

## Build an offline application folder

```powershell
dotnet publish HelpMe.csproj -c Release -r win-x64 --self-contained true -o publish
Copy-Item models publish/models -Recurse
```

Keep Python's `.venv` in its original location and set `publish/appsettings.json` → `PythonPath` to its absolute executable path, or create a new Python environment on the destination machine using `translation/requirements.txt` and point to it. Virtual environments are not portable; copying a `.venv` alone is insufficient. Include model folders and the Python runtime/dependencies when deploying without internet. App settings and `translation/worker.py` are copied by the project automatically.

## AI Answer panel

Help Me! opens a dedicated Chrome window containing ChatGPT and Claude. Sign in there once; its profile is kept in `%LOCALAPPDATA%\HelpMe\ChromeDebug` so login state survives restarts. This optional AI feature requires internet. Captioning and local translation remain separate.

In Settings, select the AI provider. Open the conversation you want in that provider's managed Chrome tab. **Send** uses that existing page without selecting a project/session, reloading, or creating a new conversation. Project and Session preferences remain saved but are not used when sending. Settings, including the debugging port (default 9111), are saved under `HKEY_CURRENT_USER\Software\HelpMe`.

Click a position in PC AUDIO to save the text from that point through the current end to the Windows clipboard. The same saved suffix is used by **Send** and **Ctrl+Shift+Z**. Without a saved position, the whole text is sent. Project/session names are never appended to the prompt. An existing matching draft in Chrome is reused; unrelated drafts must be cleared or sent there first.

The Answer panel and panel 03 receive assistant text as it streams. One request runs at a time on a background task. Closing Help Me! cancels the reader and closes its Chrome instance. Browser integration depends on the providers' page structure and can need updates; local fixture tests verify repeated submission without navigation, streaming, cancellation and error handling, but do not verify signed-in live provider pages.

The reply reader tracks messages after the submitted user turn and tolerates older messages being removed from the page. If no reply can be read, an error is shown rather than silently leaving the panel empty. The latest request's diagnostic metadata is saved at `%LOCALAPPDATA%\HelpMe\logs\ai-reader.log`: provider, detected message counts, character counts, streaming state and failure type. Captions, replies, page URLs, conversation titles and cookies are not written to this log.

**01 Start** and **01 Stop** control Whisper capture independently for PC audio, translation, and microphone panels. Translation can process text already in the box without starting capture.

## Validation

```powershell
dotnet build HelpMe.csproj -c Release
dotnet run --project tests/ChromeAiSmoke/ChromeAiSmoke.csproj -c Release
dotnet run --project tests/AnswerUiSmoke/AnswerUiSmoke.csproj -c Release
python -m unittest discover -s tests -v
dotnet run --project tests/SettingsSmoke/SettingsSmoke.csproj
dotnet run --project tests/EditorSmoke/EditorSmoke.csproj -c Release
dotnet run --project tests/ShutdownSmoke/ShutdownSmoke.csproj -c Release
dotnet run --project tests/CaptionSmoke/CaptionSmoke.csproj -c Release -- path/to/mono-16khz-speech.wav
```

After models are installed, disconnect the network and verify: PC speech appears in panel 1; panel 2 shows Korean; panel 3 shows microphone speech without translation. Toggle each off/on independently, including translation alone. Test quiet audio, device disconnection, and closing while recognizing. Actual microphone/loopback and model accuracy need testing on the target PC.

Verified in this workspace: C# build with zero warnings/errors; offscreen WPF rendering; four translation protocol tests; real Whisper transcription of the upstream JFK audio sample; and actual C# → Python → NLLB Korean translation with offline model loading. Live microphone and loopback capture have not been exercised here. Both models and the local Python environment have been prepared, so `Start-HelpMe.cmd` can launch the built application directly.

## Upstream references and licenses

- [Whisper model files](https://huggingface.co/ggerganov/whisper.cpp/tree/main), MIT.
- [Whisper.net](https://github.com/sandrohanea/whisper.net), MIT.
- [NAudio](https://github.com/naudio/NAudio), MIT.
- [CTranslate2 NLLB instructions](https://opennmt.net/CTranslate2/guides/transformers.html#nllb), MIT runtime.
- [NLLB model card](https://huggingface.co/facebook/nllb-200-distilled-600M): **CC-BY-NC-4.0**, noncommercial model, primarily intended for research. Reassess the translation model/license before distributing a commercial product.
