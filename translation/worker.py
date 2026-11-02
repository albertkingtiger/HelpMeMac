"""Local JSON-lines worker. stdout is reserved for protocol messages; never uses network."""
import argparse
import json
import os
import sys

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

# Keep Windows DLL search-directory handles alive for the process lifetime.
# CTranslate2's native extension depends on DLLs beside the package and the
# bundled Python runtime; neither location is guaranteed to be searched on a
# clean Windows 11 installation.
_dll_directory_handles = []
if sys.platform == "win32":
    for path in list(sys.path):
        if not path:
            continue
        package_dir = os.path.join(path, "ctranslate2")
        for directory in (path, package_dir, os.path.dirname(sys.executable)):
            if os.path.isdir(directory) and hasattr(os, "add_dll_directory"):
                try:
                    _dll_directory_handles.append(os.add_dll_directory(directory))
                except OSError:
                    pass


def send(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    args = parser.parse_args()
    import ctranslate2
    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(args.model, local_files_only=True)
    translator = ctranslate2.Translator(
        args.model, device="cpu", compute_type="int8",
        inter_threads=1, intra_threads=max(1, min(4, (os.cpu_count() or 2) // 2)),
    )
    send({"ready": True})
    for line in sys.stdin:
        try:
            # A caller may open the stream with a UTF-8 signature: Windows
            # PowerShell writes one ahead of the first line it pipes in. The
            # mark belongs to the encoding, not to the request.
            request = json.loads(line.lstrip("﻿"))
            text = request["text"].strip()
            source = request["source"]
            if not text:
                send({"text": ""})
                continue
            if source == "kor_Hang":
                send({"text": text})
                continue
            tokenizer.src_lang = source
            # Editable documents can exceed the model's context. Translate bounded
            # token chunks instead of silently dropping everything after 512 tokens.
            encoded = tokenizer.encode(text, add_special_tokens=False)
            translated_parts = []
            for offset in range(0, len(encoded), 240):
                chunk = tokenizer.build_inputs_with_special_tokens(encoded[offset:offset + 240])
                tokens = tokenizer.convert_ids_to_tokens(chunk)
                # Short caption fragments should not be allowed to generate a
                # document-sized answer. NLLB can otherwise loop on phrases
                # such as "yes" or "2 or 3 PM" hundreds of times.
                output_limit = min(384, max(48, 3 * len(chunk)))
                result = translator.translate_batch(
                    [tokens], target_prefix=[["kor_Hang"]], beam_size=4,
                    max_decoding_length=output_limit,
                    no_repeat_ngram_size=3, repetition_penalty=1.1,
                )
                target = result[0].hypotheses[0]
                if target and target[0] == "kor_Hang":
                    target = target[1:]
                translated = tokenizer.decode(tokenizer.convert_tokens_to_ids(target), skip_special_tokens=True)
                translated_parts.append(translated.strip())
            send({"text": " ".join(translated_parts)})
        except Exception as exc:
            send({"error": str(exc)})


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(str(exc), file=sys.stderr, flush=True)
        sys.exit(1)
