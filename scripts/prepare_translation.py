"""One-time ONLINE model conversion. The app never calls this script."""
import argparse
import os
from pathlib import Path
import tempfile

os.environ.setdefault("HF_HOME", str(Path(__file__).resolve().parents[1] / ".tools" / "huggingface"))
os.environ.setdefault("HF_HUB_DISABLE_XET", "1")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    from ctranslate2.converters import TransformersConverter
    from transformers import AutoTokenizer

    model = "facebook/nllb-200-distilled-600M"
    output = Path(args.output).resolve()
    if (output / "model.bin").exists() and (output / "tokenizer_config.json").exists():
        print(f"Translation model already exists: {output}")
        return
    if output.exists():
        raise RuntimeError(f"Incomplete model directory: {output}. Rename it and run setup again.")
    output.parent.mkdir(parents=True, exist_ok=True)
    # Only publish a model directory after both conversion and tokenizer saving succeed.
    with tempfile.TemporaryDirectory(prefix="nllb-preparing-", dir=output.parent) as temporary:
        staging = Path(temporary) / "model"
        converter = TransformersConverter(model)
        converter.convert(str(staging), quantization="int8")
        AutoTokenizer.from_pretrained(model).save_pretrained(str(staging))
        staging.rename(output)
    size = sum(p.stat().st_size for p in output.rglob("*") if p.is_file())
    print(f"Translation model ready: {size / 1_000_000:.0f} MB at {output}")


if __name__ == "__main__":
    main()
