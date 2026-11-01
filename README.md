# Help Me!

A C# desktop application for PC captions, Korean translation, and AI answers. PC captions can use offline Whisper or Windows 11 Live Captions.

The current UI and audio backend are WPF and NAudio, so the checked-in executable remains Windows-only. A complete macOS build requires replacing WPF with a cross-platform UI and replacing WASAPI capture with CoreAudio/AVFoundation capture.

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

Click **Settings** at the top right to choose **Whisper** or **Windows 11 Live Captions**, and to switch between Dark and Light themes. The app remembers both choices. Live Captions is opened when selected and PC capture starts; complete Windows' first-run language setup if prompted. Help Me! reads the caption text exposed through Windows UI Automation, so this method depends on the Live Captions window and may need adjustment after Windows updates. Whisper remains the default.

Microphone captions continue to use Whisper. Preferences are stored as JSON under the user's application-data directory, so they do not depend on the Windows Registry.

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

## Build the portable Windows 11 package

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package.ps1
```

This writes `dist/HelpMe-Windows11-Portable.zip` and a SHA-256 checksum. Extract the ZIP on an x64 Windows 11 PC and run `Start-HelpMe.cmd`. It includes self-contained .NET, a standalone Python runtime and dependencies, the default Whisper Base model, and the Korean NLLB model. It does not require a Python or .NET installation. Windows Live Captions is part of Windows 11 and may require its first-run speech pack; the AI and chat pages use the WebView2 runtime that ships with Windows 11. The NLLB model is licensed CC-BY-NC-4.0.

## Build the Windows installer

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

This writes `dist/HelpMe-Setup-<version>.exe` and a SHA-256 checksum. The installer contains the self-contained .NET app, Python and its dependencies, the Visual C++ runtime DLLs, all Whisper models and the Korean NLLB model. It installs per user under `%LOCALAPPDATA%\Programs\HelpMe` without administrator rights, and the uninstaller removes the program and its Windows startup entry while leaving settings and the browser panels' sign-ins in place. The script uses an installed Inno Setup 6, or downloads the Tools.InnoSetup package from NuGet into `.tools/innosetup` if there is none.

## The web pages: panels 05 and 06

The AI and chat web clients run inside Help Me! itself. Panel **05** is the AI provider's own page and fills the middle column; panel **06** is the chat destination, in its own column on the right. There is no separate panel for the answer: the page in 05 already shows it, formatted, as it arrives. They run on the Microsoft Edge WebView2 runtime, which ships with Windows 11, so nothing extra is installed and no browser window of any kind is opened.

Their profile lives in `%LOCALAPPDATA%\HelpMe\WebView2`: sign in to each page once there and it is remembered between runs. **Ctrl+5** and **Ctrl+6** collapse their panels to a header strip and give the column's width back to the rest of the window; **Reload** in a panel header reloads that page when a site gets stuck.

The pages are driven through the DevTools protocol, the same way a browser tab would be.

WebView2 creates its browser process only once its window has been shown, and Help Me! starts hidden in the tray. At startup the window is therefore shown off the desktop for the moment the panels need to come up, then returned to the tray; if the restore shortcut or the tray icon is used during those seconds, the window is placed back on screen instead of hidden.

## AI Answer panel

Panel 05 carries the AI provider's own web client, ChatGPT or Claude. Sign in there once. This optional AI feature requires internet. Captioning and local translation remain separate.

In Settings, select the AI provider; panel 05 follows it. Open the conversation you want in that panel. **Send** in the 05 header, or **Ctrl+Shift+Z**, sends the captions to that existing page without selecting a project/session, reloading, or creating a new conversation. The reply is read back as it is written, so **Save** in the same header writes it to a text file and the send bar below can forward it, but it is never displayed a second time beside the page that is already showing it.

Sending does not need panel 05 to be in front, or Help Me! itself to be. The page is marked as focused for the duration of the request, so you can be in another window entirely and the reply is still read back.

Project and Session preferences remain saved but are not used when sending. Settings, including the debugging port (default 9111), are saved as JSON under `%APPDATA%\HelpMe\settings.json`.

Click a position in PC AUDIO to save the text from that point through the current end to the Windows clipboard. The same saved suffix is used by **Send** and **Ctrl+Shift+Z**. Without a saved position, the whole text is sent. Project/session names are never appended to the prompt. An existing matching draft on the page is reused; unrelated drafts must be cleared or sent there first.

## Background mode and the system tray

Help Me! is a background application. It starts hidden in the system tray and begins capturing immediately; the window is never shown at launch. Press **Ctrl+Alt+H** to bring it up, or double-click the tray icon. Change the global Restore shortcut in Settings if that combination is already used by another app.

The title bar has no close button. The minimize button and Alt+F4 both return the window to the tray, leaving capture running. **Exit** in the tray icon's menu is the only way to quit.

Only one Help Me! runs per signed-in user. Launching it again while it is already in the tray does not start a second copy: the running instance comes to the front instead. This matters because the app is invisible at startup, so a duplicate would otherwise be silent, with two sets of browser panels and two processes driving Live Captions.

**Start Help Me! when I sign in to Windows** in Settings is on by default and writes a per-user entry to `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`. Turn it off there to remove the entry. Registering the entry needs no elevation; if it fails, the app reports it and still runs.

When Live Captions is the caption source, its window is moved offscreen as soon as it appears and is re-hidden if Windows brings it back, so it never covers what you are watching. **Show**/**Hide** in section 01 brings it back deliberately.

## Hiding sections

**Hide** in a section header collapses that section to its header bar, giving the space to the remaining panels; the header stays so the section can be restored with the mouse. **Ctrl+2**, **Ctrl+5** and **Ctrl+6** do the same from the keyboard. Collapsing 02 leaves its Start/Stop available, so translation keeps running out of sight. Collapsing 05 hides its buttons and its send bar to keep the leftover strip narrow; Ctrl+Shift+Z still sends. Both states are remembered, as are the sizes you drag the splitters to.

## Sending captions to Telegram

Panel 06 carries **Telegram Web** (`web.telegram.org/k/`). Sign in there once; like the AI page, the login lives in the panels' profile and survives restarts.

Select **Telegram** as the provider in Settings and open the chat you want in panel 06. **Send** and the send shortcut then deliver the caption text to that open chat instead of to an assistant. Telegram is a destination, not an assistant: nothing is read back, so what was delivered is reported rather than a reply. The text is typed into the chat's message box and submitted with Enter, falling back to the send button if Enter does not submit. An existing unrelated draft in the message box is left alone and reported rather than overwritten.

**Send Selection** at the bottom of panel 05 posts the AI answer to the open Telegram chat as its own new message, whatever provider produced it. Highlight part of the answer **on the page in panel 05** first to send only that part, exactly as you read it there; with nothing selected, or a selection that is only whitespace, it sends the whole answer. **All Send**, beside it, always sends the whole answer, so a selection does not have to be cleared first. Both send one message and share the Telegram tab, so neither starts while the other is sending. The default shortcuts are **Ctrl+Shift+T** for Send Selection and **Ctrl+Shift+A** for All Send, both changeable in Settings next to the other shortcuts; Restore, Send, Send Selection and All Send must all differ. The button confirms with a brief "Sent"; failures appear on the status line.

**Auto-send**, the checkbox at the bottom of panel 05 beside those buttons, forwards a slow answer in pieces instead of waiting for the end. The reply is read as the AI types, and the opening message goes out as soon as the answer reaches the first size, so the reader sees something straight away; the rest follow at the second size. **Auto-send, first message … then …** in Settings sets both (20 to 4000 characters, 100 and 500 by default). A message is always whole sentences, so a size is a target to reach rather than a place to cut, and the first message of each answer begins with a line of dashes marking where that answer started. Ticking the box while an answer is already arriving starts from the sentences that come next, so nothing on screen is sent twice. The bar shows how many messages have gone. A sentence is only finished once the text after it proves so, which keeps abbreviations ("Dr.", "e.g."), decimals and lower-case continuations from splitting a message early; a fenced code block is held back until it closes and then travels in one piece. Whatever is still unsent, including a trailing fragment, follows in one last message when the answer ends, and the run then stops: an answer that went out in full while it was being written is not flushed again. Auto-send shares the chat tab with the two buttons, so neither interrupts the other. A refused piece stops auto-send for that answer and says so, rather than carrying on and leaving a hole in what was received. Text that has already gone out cannot be recalled if the AI rewrites it, so leave auto-send off when the answer matters more than the wait.

**Send answers to** in Settings chooses Telegram or WhatsApp for those two buttons and their shortcuts, and panel 06 follows that choice; link WhatsApp once by scanning its QR code in the panel, then open the chat you want. WhatsApp selectors depend on its page structure and have only been exercised against local fixtures.

This uses the signed-in web session in panel 06. Help Me! makes no API calls of its own and stores no bot token; audio, captions and local translation stay on this PC.

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
