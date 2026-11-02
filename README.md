# Help Me!

A C# desktop application for PC captions, Korean translation, and AI answers, built for an **Intel Mac mini running macOS**.

The UI is Avalonia and the audio comes from CoreAudio, so this branch builds and runs natively on macOS. The Windows build it was ported from, with WPF, WASAPI capture and Windows 11 Live Captions, lives on the `windows-11` and `UseInnerCom` branches; the two are not kept in step.

1. **PC audio**: captions from what the Mac is playing.
2. **Korean translation**: translates newly recognized PC audio into Korean. It can run with the PC caption display stopped; it still captures PC audio internally.
3. **Microphone**: English captions from the default microphone. Never translated.

Both PC audio and microphone speech recognition are fixed to English. PC captions translate from English into Korean. Text remains visible when stopped. Stopping discards pending work for that panel. Changing audio devices requires stopping and restarting capture.

## Hearing what the Mac is playing

macOS has no equivalent of WASAPI loopback: nothing captures the system's own output unless something is put in its path. Help Me! supports both of the ways around that, and **Settings → PC audio source** chooses between them.

**BlackHole** (the default) is a free virtual audio device. Playback is routed through a Multi-Output Device containing both your speakers and BlackHole, so you still hear the sound and Help Me! reads the same stream as an ordinary input. It needs a one-time setup, works on every macOS version this app runs on, and asks for no permission beyond microphone access.

```bash
scripts/setup.sh --blackhole
```

Then, in **Audio MIDI Setup**: click **+** at the bottom left, choose **Create Multi-Output Device**, tick both your speakers and **BlackHole 2ch**, and make that device the system output in Sound settings.

**ScreenCaptureKit** is Apple's own system-audio tap and installs nothing, but macOS counts it as screen recording: it asks for **Screen Recording** permission, and Sequoia asks again periodically. Captions stop when that lapses, which is why BlackHole is the default. It needs macOS 13 Ventura or newer; Settings says so rather than failing inside a native call if it is unavailable.

Either way, the app transcribes with Whisper. The microphone panel always uses the default input device.

## Caption text editing and files

The PC AUDIO text area is editable: type, paste, or correct text there. Translation reads this text area, rather than a separate audio-only stream. With Translation started, complete sentences are translated with their context. Growing or corrected sentences are retranslated as a whole; unchanged sentence translations are cached. Editing and AI Send leave capture running and preserve the text. Translation results for text edited away are discarded. Long input is translated in token-sized chunks instead of truncated to the model's context limit.

Translation input is scheduled after 200 ms, then takes the model's processing time. Recognition latency varies with the audio and system; these intervals are not total end-to-end latency guarantees.

**Save** in each panel exports that panel's current text as a UTF-8 `.txt` file. **Format** clears that panel. Translation Format stays clear until the input changes. Automatic caption chunks, including microphone captions, are separated by spaces rather than forced newlines, and wrap naturally at the text area's edge. Manual line breaks are preserved when saving.

Preferences are stored as JSON in `~/Library/Application Support/HelpMe/settings.json`.

## First setup (internet required)

Install the **.NET 8 SDK**, **x64 build** — this is an Intel Mac, and the arm64 installer will not run on it. Python 3.11 or newer is needed only for Korean translation; macOS ships a `python3` that will do. Restore/build needs internet once; running after setup does not.

From this directory:

```bash
scripts/setup.sh --translation
dotnet build HelpMe.csproj -c Release -r osx-x64
scripts/run.sh
```

Omit `--translation` to download just Whisper and use both caption panels. Use `--python /path/to/python3` if the one on PATH is not the one you want.

The C# app handles the UI, capture, speech recognition, queues, and lifecycle. Translation uses the included **local Python subprocess**, exchanging JSON through pipes, without an HTTP server. This project is not a Python-free distribution.

## Models and offline operation

| Feature | Model | Storage |
| --- | --- | --- |
| Both caption streams | English Whisper Base Q8 (`ggml-base.en-q8_0.bin`) | ~150 MB |
| Korean translation | NLLB-200 distilled 600M, converted to CTranslate2 INT8 | 647 MB including tokenizer |

600M means parameter count, not file size. Default setup downloads a [pinned community CTranslate2 conversion](https://huggingface.co/JustFrederik/nllb-200-distilled-600M-ct2-int8/tree/302d78f00e6fdb50a1064059df7c392b735e9d05), checking file sizes and large-file SHA256 hashes. The finished model size is not a hard 600 MB guarantee. Runtime memory is greater than model storage. To convert the original Meta checkpoint yourself instead, add `--convert-translation` during first setup; that path downloads several GB and installs PyTorch for conversion. Normal setup/inference does not require PyTorch.

Whisper runs on the CPU here. The macOS x64 runtime has no Metal backend — that is an Apple Silicon build — so recognition uses the six cores of this Mac mini and the Accelerate-backed BLAS library.

At runtime both Hugging Face offline environment flags and `local_files_only=True` are enforced. No models download automatically; missing models produce setup instructions. No audio is saved to disk or sent to a service. The app writes recognized text only to its UI.

Edit `appsettings.json` to change model locations, Python executable, speech threshold, or segmentation. Relative paths first resolve beside the executable, then fall back to the project folder found above the build output. This works when launching `HelpMe.app` from Finder, regardless of the working directory. Published copies can use models beside the executable or absolute paths.

Panels display plain text without timestamps, using compact 26px buttons. Routine caption, translation, and microphone status messages are hidden; errors appear only when needed. Speech is emitted after about 0.15 seconds of silence, or after one second of continuous speech, plus inference time. A wall-clock timer also flushes the last phrase when the source stops delivering audio frames, which is what happens the moment playback stops. This is phrase-based captioning rather than token streaming. Long speech may be split mid-word at the segment limit. Simple energy-based voice detection may need threshold adjustments in noisy/quiet rooms. Both sources share one Whisper model and serialize recognition to control memory. Use headphones to avoid microphone echo from speakers.

Capture is requested as 16 kHz mono directly from the audio queue, which is what Whisper wants; a device that refuses that conversion is taken at 48 kHz and resampled with a windowed-sinc filter rather than by dropping samples, because aliasing above 8 kHz costs recognition accuracy. ScreenCaptureKit always delivers 48 kHz and is resampled the same way.

The default Base model, beam search, and longer six-second segments favor natural wording and accuracy over minimum latency. Use `models/ggml-small-q8_0.bin` for higher accuracy, or `models/ggml-tiny.en-q8_0.bin` when latency matters more. Recognition processors are reused between segments, and translation uses beam search with beam size 4 for improved phrasing at some additional computation cost. Actual live delay varies with hardware and audio.

## Build the application bundle

```bash
scripts/package.sh
```

This writes `dist/HelpMe.app` and `dist/HelpMe-<version>.dmg` with a SHA-256 checksum. The bundle carries its own .NET runtime, the Whisper native libraries and the models, and is signed ad-hoc so Gatekeeper does not call it damaged after it is copied. Drag it to `/Applications` and open it once from there, so macOS asks for microphone access with the app's own name attached.

The bundle declares `LSUIElement`, so it has no Dock icon and no window at launch: it is a menu bar application. Pass `--no-python` to leave the virtual environment out. A Python virtual environment is not relocatable on macOS, so the copied one still refers to the `python3` it was created from; that matters only if the bundle is moved to a Mac without that interpreter.

## The web pages: panels 05 and 06

The AI and chat web clients run inside Help Me! itself. Panel **05** is the AI provider's own page and fills the middle column; panel **06** is the chat destination, in its own column on the right. There is no separate panel for the answer: the page in 05 already shows it, formatted, as it arrives.

They are **WKWebView**s, the same engine Safari uses, embedded in the window through Avalonia's native control host. Nothing is installed and no browser window of any kind is opened. WebKit's default data store is persistent and per-application, so signing in to each page once is remembered between runs; it lives under `~/Library/WebKit/com.helpme.app`. **Cmd+5** and **Cmd+6** collapse their panels to a header strip and give the column's width back to the rest of the window; **Reload** in a panel header reloads that page when a site gets stuck.

On Windows these panels were WebView2 and were driven through the DevTools protocol. WKWebView does not speak that protocol, so the five commands Help Me! actually sends — evaluate a script, navigate, insert text, press a key, emulate focus — are carried out with the page's own scripting instead. Everything above that layer is unchanged, so the reply reader, the composer handling and the messenger delivery are the same code that ran on Windows. Scripts are run with `callAsyncJavaScript`, which awaits a promise the way a DevTools evaluate does, and results come back as JSON.

The panels report a Safari user agent. WKWebView's own omits the `Version`/`Safari` tokens, and WhatsApp Web turns away a browser it cannot recognise.

A WKWebView is only created once its window has been realised, and Help Me! starts hidden in the menu bar. At startup the window is therefore placed off the desktop for the moment the panels need to come up, then hidden again; if the restore shortcut or the menu bar icon is used during those seconds, the window is placed back on screen instead.

## AI Answer panel

Panel 05 carries the AI provider's own web client, ChatGPT or Claude. Sign in there once. This optional AI feature requires internet. Captioning and local translation remain separate.

In Settings, select the AI provider; panel 05 follows it. Open the conversation you want in that panel. **Send** in the 05 header, or **Cmd+Shift+Z**, sends the captions to that existing page without selecting a project/session, reloading, or creating a new conversation. One request runs at a time, because the reader identifies the reply by the message it submitted, and the page disables its own composer while it answers. While a reply is being read, Send becomes **Stop**: that abandons the reply and keeps the rest of it out of the chat, leaving whatever had already been read ready to send by hand. The AI keeps writing it on the page either way. The reply is read back as it is written, so **Save** in the same header writes it to a text file and the send bar below can forward it, but it is never displayed a second time beside the page that is already showing it.

Sending does not need panel 05 to be in front, or Help Me! itself to be. The page is told it is visible and focused for the duration of the request, which is what keeps a composer's send button from staying disabled in a window the user is not looking at.

Project and Session preferences remain saved but are not used when sending. Settings are saved as JSON under `~/Library/Application Support/HelpMe/settings.json`, and the reader's diagnostic log is written to `~/Library/Logs/HelpMe/ai-reader.log`.

Click a position in PC AUDIO to save the text from that point through the current end to the clipboard. The same saved suffix is used by **Send** and **Cmd+Shift+Z**. Without a saved position, the whole text is sent. Project/session names are never appended to the prompt. An existing matching draft on the page is reused. A draft you typed in the AI input yourself is kept: the caption is appended below it, separated by a blank line, and the two are submitted as one message.

## Background mode and the menu bar

Help Me! is a background application. It starts hidden with an icon in the menu bar and begins capturing immediately; the window is never shown at launch. Press **Cmd+Alt+H** to bring it up, or click the menu bar icon. Change the global Restore shortcut in Settings if that combination is already used by another app.

The system-wide shortcut is a Carbon hot key, which is the macOS way to claim one combination without watching every keystroke; it needs no Accessibility permission for that reason. At least two modifiers are required, since the combination is taken away from every other application.

The window's close button returns it to the menu bar, leaving capture running, as does the **Hide** button in the title bar. **Exit** in the menu bar icon's menu is the only way to quit.

Only one Help Me! runs per logged-in user. Launching it again while it is already in the menu bar does not start a second copy: the running instance comes to the front instead. This matters because the app is invisible at startup, so a duplicate would otherwise be silent, with two sets of web panels and two capture pipelines. The claim is an exclusively held lock file in `~/Library/Application Support/HelpMe`, and the request to come forward is one byte over a Unix domain socket beside it; both are released when the process exits, however it exits.

**Start Help Me! when I log in** in Settings is on by default and writes `~/Library/LaunchAgents/com.helpme.app.plist`. Turning it off deletes that file. Registering it needs no administrator rights; if it fails, the app reports it and still runs.

## Hiding sections

**Hide** in a section header collapses that section to its header bar, giving the space to the remaining panels; the header stays so the section can be restored with the mouse. **Cmd+2**, **Cmd+5** and **Cmd+6** do the same from the keyboard. Collapsing 02 leaves its Start/Stop available, so translation keeps running out of sight. Collapsing 05 hides its buttons and its send bar to keep the leftover strip narrow; Cmd+Shift+Z still sends. Both states are remembered, as are the sizes you drag the splitters to.

## Sending captions to Telegram

Panel 06 carries **Telegram Web** (`web.telegram.org/k/`). Sign in there once; like the AI page, the login lives in the panels' data store and survives restarts.

Select **Telegram** as the provider in Settings and open the chat you want in panel 06. **Send** and the send shortcut then deliver the caption text to that open chat instead of to an assistant. Telegram is a destination, not an assistant: nothing is read back, so what was delivered is reported rather than a reply. The text is typed into the chat's message box and submitted with Enter, falling back to the send button if Enter does not submit. An existing unrelated draft in the message box is left alone and reported rather than overwritten.

**Send Selection** at the bottom of panel 05 posts the AI answer to the open Telegram chat as its own new message, whatever provider produced it. Highlight part of the answer **on the page in panel 05** first to send only that part, exactly as you read it there; with nothing selected, or a selection that is only whitespace, it sends the whole answer. **All Send**, beside it, always sends the whole answer, so a selection does not have to be cleared first. A whole answer ends with a row of ten 👌 on a line of its own, marking where it stops; a highlighted passage is a piece you picked out rather than the end of anything, so it goes as it stands. Both send one message and share the Telegram page, so neither starts while the other is sending. The default shortcuts are **Cmd+Shift+T** for Send Selection and **Cmd+Shift+A** for All Send, both changeable in Settings next to the other shortcuts; Restore, Send, Send Selection and All Send must all differ. The button confirms with a brief "Sent"; failures appear on the status line.

**Auto-send**, the checkbox at the bottom of panel 05 beside those buttons, forwards a slow answer in pieces instead of waiting for the end. The reply is read as the AI types, and the opening message goes out as soon as the answer reaches the first size, so the reader sees something straight away; the rest follow at the second size. **Auto-send, first message … then …** in Settings sets both (20 to 4000 characters, 100 and 500 by default). A message is always whole sentences, so a size is a target to reach rather than a place to cut, and the first message of each answer begins with a line of asterisks marking where that answer started. Ticking the box while an answer is already arriving starts from the sentences that come next, so nothing on screen is sent twice. The bar shows how many messages have gone. A sentence is only finished once the text after it proves so, which keeps abbreviations ("Dr.", "e.g."), decimals and lower-case continuations from splitting a message early; a fenced code block is held back until it closes and then travels in one piece. Whatever is still unsent, including a trailing fragment, follows in one last message when the answer ends, and the run then stops: an answer that went out in full while it was being written is not flushed again. That last message closes with a row of ten 👌 on a line of its own, the counterpart of the asterisks that opened the answer; the pieces before it carry no mark, since the answer is still going. Auto-send shares the chat page with the two buttons, so neither interrupts the other. A refused piece stops auto-send for that answer and says so, rather than carrying on and leaving a hole in what was received. Text that has already gone out cannot be recalled if the AI rewrites it, so leave auto-send off when the answer matters more than the wait.

**Send answers to** in Settings chooses Telegram or WhatsApp for those two buttons and their shortcuts, and panel 06 follows that choice; link WhatsApp once by scanning its QR code in the panel, then open the chat you want. WhatsApp selectors depend on its page structure and have only been exercised against local fixtures.

This uses the signed-in web session in panel 06. Help Me! makes no API calls of its own and stores no bot token; audio, captions and local translation stay on this Mac.

**01 Start** and **01 Stop** control Whisper capture independently for PC audio, translation, and microphone panels. Translation can process text already in the box without starting audio capture.

## Validation

```bash
dotnet build HelpMe.csproj -c Release -r osx-x64
dotnet run --project tests/UiSmoke -c Release -- .tmp/ui
dotnet run --project tests/ChromeAiSmoke -c Release
dotnet run --project tests/SettingsSmoke -c Release
dotnet run --project tests/TranslationContextSmoke -c Release
dotnet run --project tests/CaptionSmoke -c Release -- path/to/mono-16khz-speech.wav
python3 -m unittest discover -s tests -v
```

`tests/UiSmoke` runs on Avalonia's headless platform with Skia drawing. It asserts the window behaviour the WPF smoke tests covered — the send bookmark, reply versioning, Stop, the collapse shortcuts, the messenger end mark, auto-send chunking and the auto-send pump — and also writes a picture of the main window and Settings to the directory given, so the layout can be looked at from a machine that is not the Mac.

After models are installed, disconnect the network and verify: PC speech appears in panel 1; panel 2 shows Korean; panel 3 shows microphone speech without translation. Toggle each off/on independently, including translation alone. Test quiet audio, device disconnection, and closing while recognizing.

What has been exercised in this workspace: the macOS cross-build with zero warnings, the headless window checks, real Whisper transcription of the upstream JFK audio sample through the rewritten capture pipeline, the DevTools-protocol reader against local page fixtures, and the translation protocol tests. **Everything that talks to macOS itself is written but unrun**: the WKWebView panels, both audio capture paths, the Carbon hot key, the menu bar item and the LaunchAgent have no counterpart on the machine this was built on, so the first run on the Mac mini is their first run anywhere.

## Upstream references and licenses

- [Whisper model files](https://huggingface.co/ggerganov/whisper.cpp/tree/main), MIT.
- [Whisper.net](https://github.com/sandrohanea/whisper.net), MIT.
- [Avalonia](https://github.com/AvaloniaUI/Avalonia), MIT.
- [BlackHole](https://github.com/ExistentialAudio/BlackHole), GPL-3.0; installed separately, not bundled.
- [CTranslate2 NLLB instructions](https://opennmt.net/CTranslate2/guides/transformers.html#nllb), MIT runtime.
- [NLLB model card](https://huggingface.co/facebook/nllb-200-distilled-600M): **CC-BY-NC-4.0**, noncommercial model, primarily intended for research. Reassess the translation model/license before distributing a commercial product.
