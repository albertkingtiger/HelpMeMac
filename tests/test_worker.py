"""Exercise the worker protocol without downloading model weights."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("worker", Path(__file__).parents[1] / "translation/worker.py")
worker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(worker)


class FakeTokenizer:
    src_lang = None

    def encode(self, text, **kwargs):
        tokens = text.split()
        return tokens if kwargs.get("add_special_tokens") is False else self.build_inputs_with_special_tokens(tokens)

    def build_inputs_with_special_tokens(self, tokens):
        return tokens + [self.src_lang]

    def convert_ids_to_tokens(self, ids):
        return ids

    def convert_tokens_to_ids(self, tokens):
        return tokens

    def decode(self, tokens, **kwargs):
        return " ".join(tokens)


class WorkerTests(unittest.TestCase):
    def run_worker(self, requests):
        calls = []
        tokenizer = FakeTokenizer()

        class Translator:
            def __init__(self, *args, **kwargs):
                self.options = kwargs

            def translate_batch(self, tokens, **kwargs):
                calls.append((tokens, kwargs))
                return [types.SimpleNamespace(hypotheses=[["kor_Hang", "안녕하세요"]])]

        class AutoTokenizer:
            @staticmethod
            def from_pretrained(path, **kwargs):
                assert kwargs["local_files_only"] is True
                return tokenizer

        output = io.StringIO()
        modules = {"ctranslate2": types.SimpleNamespace(Translator=Translator),
                   "transformers": types.SimpleNamespace(AutoTokenizer=AutoTokenizer)}
        payload = "\n".join(json.dumps(x) if not isinstance(x, str) else x for x in requests) + "\n"
        with patch.dict(sys.modules, modules), patch.object(sys, "argv", ["worker.py", "--model", "local-model"]), \
                patch.object(sys, "stdin", io.StringIO(payload)), contextlib.redirect_stdout(output):
            worker.main()
        return [json.loads(x) for x in output.getvalue().splitlines()], calls

    def test_korean_target_and_source_language(self):
        replies, calls = self.run_worker([{"text": "Hello", "source": "eng_Latn"}])
        self.assertEqual(replies, [{"ready": True}, {"text": "안녕하세요"}])
        self.assertEqual(calls[0][0], [["Hello", "eng_Latn"]])
        self.assertEqual(calls[0][1]["target_prefix"], [["kor_Hang"]])
        self.assertEqual(calls[0][1]["max_decoding_length"], 48)
        self.assertEqual(calls[0][1]["no_repeat_ngram_size"], 3)
        self.assertGreater(calls[0][1]["repetition_penalty"], 1)

    def test_korean_source_bypasses_inference(self):
        replies, calls = self.run_worker([{"text": "안녕", "source": "kor_Hang"}])
        self.assertEqual(replies[1], {"text": "안녕"})
        self.assertEqual(calls, [])

    def test_leading_byte_order_mark_is_not_part_of_the_request(self):
        replies, calls = self.run_worker(["﻿" + json.dumps({"text": "Hello", "source": "eng_Latn"})])
        self.assertEqual(replies[1], {"text": "안녕하세요"})

    def test_malformed_request_does_not_break_following_request(self):
        replies, calls = self.run_worker(["not-json", {"text": "Hello", "source": "eng_Latn"}])
        self.assertIn("error", replies[1])
        self.assertEqual(replies[2], {"text": "안녕하세요"})

    def test_blank_input_bypasses_inference(self):
        replies, calls = self.run_worker([{"text": "  ", "source": "eng_Latn"}])
        self.assertEqual(replies[1], {"text": ""})
        self.assertEqual(calls, [])

    def test_long_document_is_chunked_without_truncating_words(self):
        replies, calls = self.run_worker([{"text": " ".join(["word"] * 700), "source": "eng_Latn"}])
        self.assertEqual(len(calls), 3)
        self.assertEqual(sum(len(call[0][0]) - 1 for call in calls), 700)
        self.assertTrue(all(call[1]["max_decoding_length"] <= 384 for call in calls))
        self.assertEqual(replies[1]["text"], "안녕하세요 안녕하세요 안녕하세요")


if __name__ == "__main__":
    unittest.main()
