"""CPU-only E5 embedding worker. JSON lines over inherited stdin/stdout only.

No socket listener, remote model code, network lookup, microphone, or arbitrary
file operation is exposed. The trusted parent supplies only bounded text batches.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

os.environ["CUDA_VISIBLE_DEVICES"] = ""
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"
os.environ["TRANSFORMERS_VERBOSITY"] = "error"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    args = parser.parse_args()
    root = Path(os.path.abspath(args.root))
    if not root.is_dir():
        raise ValueError("Project root is missing")
    model_path = root / "data" / "models" / "multilingual-e5-small"
    if model_path.is_symlink() or os.path.isjunction(model_path) or not (model_path / "model.safetensors").is_file():
        raise ValueError("Local E5 safetensors model is missing or is a link")
    # Pin the exact reviewed model assets. An index must never silently reuse
    # vectors after someone swaps the model/tokenizer under the same directory.
    expected = {
        "config.json": "69137736cab8b8903a07fe8afaafdda25aac55415a12a55d1bffa9f581abf959",
        "model.safetensors": "1a55775f53449dac10a2bcbc312469fac40b96d53198c407081a831f81c98477",
        "sentencepiece.bpe.model": "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865",
        "special_tokens_map.json": "d05497f1da52c5e09554c0cd874037a083e1dc1b9cfd48034d1c717f1afc07a7",
        "tokenizer_config.json": "a1d6bc8734a6f635dc158508bef000f8e2e5a759c7d92f984b2c86e5ff53425b",
        "tokenizer.json": "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39",
    }
    for name, digest in expected.items():
        path = model_path / name
        if path.is_symlink() or os.path.isjunction(path):
            raise ValueError("Local E5 asset may not be a link")
        with path.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != digest:
                raise ValueError("Local E5 asset fingerprint mismatch: " + name)
    # Set all controlled caches before importing model libraries.
    os.environ["HF_HOME"] = str(root / "data" / "cache" / "huggingface")
    os.environ["TORCH_HOME"] = str(root / "data" / "cache" / "torch")
    os.environ["PYTHONPYCACHEPREFIX"] = str(root / "data" / "cache" / "pycache")
    import torch
    from transformers import AutoModel, AutoTokenizer

    torch.set_num_threads(max(1, min(4, (os.cpu_count() or 2) // 2)))
    tokenizer = AutoTokenizer.from_pretrained(model_path, local_files_only=True, trust_remote_code=False)
    model = AutoModel.from_pretrained(model_path, local_files_only=True, trust_remote_code=False, use_safetensors=True)
    model.to("cpu").eval()
    model.requires_grad_(False)
    print(json.dumps({"ready": True, "dimension": 384, "device": "cpu"}), flush=True)
    for line in sys.stdin:
        try:
            if len(line) > 600_000:
                raise ValueError("Embedding request exceeds bound")
            request = json.loads(line)
            if request.get("op") == "close":
                break
            texts = request.get("texts")
            mode = request.get("mode")
            if mode not in ("query", "passage") or not isinstance(texts, list) or not 1 <= len(texts) <= 16:
                raise ValueError("Invalid embedding mode or batch")
            if any(not isinstance(text, str) or not 1 <= len(text) <= 32_000 for text in texts):
                raise ValueError("Invalid embedding text")
            with torch.inference_mode():
                batch = tokenizer([mode + ": " + text for text in texts], max_length=512, padding=True, truncation=True, return_tensors="pt")
                hidden = model(**batch).last_hidden_state
                mask = batch["attention_mask"].unsqueeze(-1)
                pooled = (hidden * mask).sum(dim=1) / mask.sum(dim=1).clamp(min=1)
                vectors = torch.nn.functional.normalize(pooled, p=2, dim=1).tolist()
            print(json.dumps({"vectors": vectors}, separators=(",", ":")), flush=True)
        except Exception as exc:
            print(json.dumps({"error": type(exc).__name__ + ": " + str(exc)[:300]}), flush=True)


if __name__ == "__main__":
    main()
