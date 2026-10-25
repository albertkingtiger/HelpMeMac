"""Run context samples against the installed offline model (no downloads)."""
import json
import os
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
samples = [
    "Alex bought a laptop. It was expensive, so he returned it.",
    "If the server fails, we switch to the backup without losing any data.",
]
payload = "\n".join(json.dumps({"text": text, "source": "eng_Latn"}) for text in samples) + "\n"
result = subprocess.run(
    [sys.executable, "-u", str(root / "translation/worker.py"),
     "--model", str(root / "models/nllb-200-distilled-600M-int8")],
    input=payload, capture_output=True, text=True, encoding="utf-8", timeout=150, cwd=root,
    env={**os.environ, "PYTHONIOENCODING": "utf-8"},
)
if result.returncode:
    raise RuntimeError(result.stderr)
replies = [json.loads(line) for line in result.stdout.splitlines()]
assert replies[0] == {"ready": True}, replies
assert len(replies) == len(samples) + 1, replies
for source, reply in zip(samples, replies[1:]):
    assert "error" not in reply and reply.get("text", "").strip(), reply
    assert any("\uac00" <= char <= "\ud7a3" for char in reply["text"]), reply
    print(source)
    print(reply["text"].encode("unicode_escape").decode("ascii"))
print("PASS: context samples produce Korean translations with the installed offline model")
