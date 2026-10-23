"""Online setup only: download to a temporary filename before installing the model."""
import os
from pathlib import Path
import sys
import urllib.request

target = Path(sys.argv[1])
partial = target.with_suffix(target.suffix + ".partial")
url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + target.name
with urllib.request.urlopen(url, timeout=60) as response, partial.open("wb") as output:
    while block := response.read(1024 * 1024):
        output.write(block)
os.replace(partial, target)
