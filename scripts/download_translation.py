"""Download a pinned CTranslate2 conversion; verify large-file SHA256 from HF metadata."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
import time

REPO = "JustFrederik/nllb-200-distilled-600M-ct2-int8"
REVISION = "302d78f00e6fdb50a1064059df7c392b735e9d05"
FILES = ["config.json", "model.bin", "sentencepiece.bpe.model", "shared_vocabulary.txt",
         "special_tokens_map.json", "tokenizer.json", "tokenizer_config.json"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    output = Path(parser.parse_args().output).resolve()
    if all((output / name).is_file() for name in FILES):
        print(f"Translation model already exists: {output}")
        return
    if output.exists():
        raise RuntimeError(f"Incomplete model directory: {output}. Rename it and run setup again.")
    output.parent.mkdir(parents=True, exist_ok=True)
    metadata_url = f"https://huggingface.co/api/models/{REPO}/revision/{REVISION}?blobs=true"
    with urllib.request.urlopen(metadata_url, timeout=60) as response:
        metadata = json.load(response)
    entries = {item["rfilename"]: item for item in metadata["siblings"]}
    staging = output.with_name(output.name + ".partial")
    staging.mkdir(exist_ok=True)
    for name in FILES:
        print(f"Downloading {name}...", flush=True)
        url = f"https://huggingface.co/{REPO}/resolve/{REVISION}/{name}"
        entry = entries[name]
        file_path = staging / name
        position = file_path.stat().st_size if file_path.exists() else 0
        if position > entry["size"]:
            raise RuntimeError(f"Invalid partial file: {file_path}")
        # Short range requests avoid losing the whole download on a stalled CDN connection.
        with file_path.open("ab") as file:
            while position < entry["size"]:
                end = min(position + 16 * 1024 * 1024, entry["size"]) - 1
                request = urllib.request.Request(url, headers={"Range": f"bytes={position}-{end}"})
                for attempt in range(3):
                    try:
                        with urllib.request.urlopen(request, timeout=30) as response:
                            if response.status == 206:
                                expected_range = f"bytes {position}-{end}/{entry['size']}"
                                if response.headers.get("Content-Range") != expected_range:
                                    raise RuntimeError("Unexpected download range")
                            elif position != 0 or end + 1 != entry["size"]:
                                raise RuntimeError("Model host did not support partial downloads")
                            block = response.read(end - position + 2)
                        if len(block) != end - position + 1:
                            raise RuntimeError("Incomplete download range")
                        break
                    except Exception:
                        if attempt == 2:
                            raise
                        time.sleep(1)
                file.write(block)
                file.flush()
                position += len(block)
        digest = hashlib.sha256()
        with file_path.open("rb") as file:
            while block := file.read(1024 * 1024):
                digest.update(block)
        expected = entry.get("lfs", {}).get("sha256")
        if expected and digest.hexdigest() != expected:
            raise RuntimeError(f"SHA256 mismatch: {name}. Rename {file_path} and retry.")
    staging.rename(output)
    size = sum(p.stat().st_size for p in output.iterdir() if p.is_file())
    print(f"Translation ready: {size / 1_000_000:.0f} MB")


if __name__ == "__main__":
    main()
