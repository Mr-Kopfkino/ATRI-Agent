"""Frozen GPT-SoVITS v2ProPlus inference for Japanese speech, without training/WebUI."""
from __future__ import annotations

import gc
import hashlib
import io
import json
import math
import os
import random
import re
import sys
import threading
import time
import wave
from dataclasses import dataclass
from pathlib import Path

VOICE_DIR = Path(__file__).resolve().parent
_DLL_HANDLES = []
RUNTIME_IDENTITY_FILES = (
    "runtime/voice/engine.py", "runtime/voice/completeness.py", "runtime/voice/worker.py",
    "runtime/voice/vendor/AR/models/t2s_model.py",
)


def capture_runtime_identity(root):
    """Capture the worker's deployed voice policy files once at construction."""
    root = root.resolve()
    files = {}
    for name in RUNTIME_IDENTITY_FILES:
        path = beneath(root, root / name)
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        files[name] = digest.hexdigest()
    return {"root": str(root), "files": files}


def semantic_generation_check(semantic, length, metadata, token_limit, nonlexical_expression):
    """Validate explicit decoder termination; never infer EOS from token count."""
    known = {"sampled_eos", "argmax_eos", "length_limit", "iteration_limit", "cancelled"}
    stop = metadata.get("stopReason") if isinstance(metadata, dict) else None
    stop = stop if isinstance(stop, str) and stop in known else "unknown"
    generated = metadata.get("generatedTokens") if isinstance(metadata, dict) else None
    integer_length = isinstance(length, int) and not isinstance(length, bool)
    integer_generated = isinstance(generated, int) and not isinstance(generated, bool)
    shape = getattr(semantic, "shape", ())
    valid = (integer_length and integer_generated and 0 < length <= token_limit and generated == length
             and isinstance(shape, (tuple, list)) and len(shape) == 2
             and isinstance(shape[1], int) and not isinstance(shape[1], bool) and shape[1] >= length)
    natural_end = stop in {"sampled_eos", "argmax_eos"}
    planned_expression_end = nonlexical_expression and stop == "length_limit"
    valid = bool(valid and (natural_end or planned_expression_end))
    result = {"passed": valid, "reason": "complete" if valid else "semantic_length_bound",
              "stopReason": stop, "semanticTokens": length if integer_length else 0,
              "semanticTokenLimit": token_limit}
    if integer_generated:
        result["generatedTokens"] = generated
    return result


def semantic_token_limit(phone_count, reference_duration, reference_phone_count, maximum_seconds):
    """Allow slow emotional prosody while keeping generation finitely bounded.

    The model emits 25 semantic frames per second. A fixed three frames per
    phone, especially the old 75-frame floor, cut off normal cries and pauses.
    Use the reference's measured pace with a bounded margin and pause allowance.
    Content verification still rejects incomplete or repeated generated speech.
    """
    if (any(not isinstance(value, int) or isinstance(value, bool) or value <= 0 for value in (phone_count, reference_phone_count))
            or any(not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or value <= 0
                   for value in (reference_duration, maximum_seconds))):
        raise ValueError("Invalid phoneme/reference duration for speech budget")
    reference_rate = min(reference_duration, 60) * 25 / min(reference_phone_count, 1_000_000) * 1.75
    frames_per_phone = min(6.0, max(3.0, reference_rate))
    return min(1498, max(1, int(25 * min(maximum_seconds, 60))), max(125, math.ceil(min(phone_count, 1498) * frames_per_phone + 25)))


def reference_candidates(reference, emotional_references, neutral_references, max_attempts=8, failed_ids=()):
    """Selected first, up to three distinct emotional references, then neutral."""
    if not isinstance(max_attempts, int) or isinstance(max_attempts, bool) or max_attempts != 8:
        raise ValueError("Speech candidates must be bounded to eight")
    if not neutral_references:
        raise ValueError("Neutral reference pool is empty")
    failed, used = set(failed_ids), {reference["id"]}

    def unique(pool):
        result, seen = [], set(used)
        for item in pool:
            if item["id"] not in seen and item["id"] not in failed:
                result.append(item); seen.add(item["id"])
        return result

    emotional = unique(emotional_references)
    selected = [reference, *random.sample(emotional, min(2, len(emotional)))]
    used.update(item["id"] for item in selected)
    available = unique(neutral_references)
    selected.extend(random.sample(available, min(8 - len(selected), len(available))))
    # When a tiny/exhausted pool cannot supply eight unique choices, repetition
    # is explicitly bounded. Prefer unfailed neutral references when possible.
    repeat_pool = [item for item in neutral_references if item["id"] not in failed] or neutral_references
    while len(selected) < 8:
        selected.append(random.choice(repeat_pool))
    return selected


def playback_pcm16(pcm, sample_rate):
    """Finalize exactly the samples that will be checked and played."""
    import numpy as np
    values = np.asarray(pcm, dtype=np.float32).copy()
    if values.ndim != 1 or not values.size or not np.isfinite(values).all():
        raise ValueError("Invalid speech PCM")
    if not isinstance(sample_rate, int) or isinstance(sample_rate, bool) or sample_rate <= 0:
        raise ValueError("Invalid speech sample rate")
    peak = float(np.max(np.abs(values)))
    if peak > 1:
        values = values / peak * .98
    fade = min(int(sample_rate * .004), len(values) // 2)
    if fade:
        values[:fade] *= np.linspace(0, 1, fade)
        values[-fade:] *= np.linspace(1, 0, fade)
    return np.clip(np.rint(values * 32768), -32768, 32767).astype(np.int16)


class SpeechCompletenessError(ValueError):
    """Bounded, content-free diagnostics for a rejected synthesis segment."""
    def __init__(self, attempts, segment_index):
        numeric = {"readingErrorRate", "headErrorRate", "tailErrorRate", "edgeHeadErrorRate", "edgeTailErrorRate", "lengthRatio", "expectedReadingLength", "actualReadingLength",
                   "durationSeconds", "checkElapsedSeconds", "semanticTokens", "semanticTokenLimit", "expressiveBodyReadingLength",
                   "expressivePrefixReadingLength", "expressiveSuffixReadingLength", "readbackPrefixIgnored", "readbackSuffixIgnored",
                   "deletedReadingCount", "maxDeletionRun", "headDeletedReadingCount", "tailDeletedReadingCount",
                   "substitutionReadingCount", "insertedReadingCount", "generatedTokens",
                   "fullReadingErrorRate", "fullHeadErrorRate", "fullTailErrorRate", "fullLengthRatio", "fullExpectedReadingLength", "fullActualReadingLength"}
        numeric.update({"strongElapsedSeconds", "strongGeneratedTokens", "strongTokenLimit"})
        numeric.update("primary" + name[0].upper() + name[1:] for name in (
            "readingErrorRate", "headErrorRate", "tailErrorRate", "edgeHeadErrorRate", "edgeTailErrorRate",
            "lengthRatio", "expectedReadingLength", "actualReadingLength", "deletedReadingCount", "maxDeletionRun",
            "headDeletedReadingCount", "tailDeletedReadingCount", "substitutionReadingCount", "insertedReadingCount"))
        boolean = {"passed", "optionalExpressiveEdges", "expressiveBodyPassed", "strongAttempted", "strongPassed"}
        reasons = {"semantic_length_bound", "invalid_pcm", "empty_readback", "empty_expected_reading", "reading_length_bound",
                   "short_readback", "reading_mismatch", "missing_start", "missing_end", "missing_content", "audio_length_bound", "silent_audio",
                   "short_utterance_duration", "expressive_utterance_duration"}
        sanitized = []
        for item in list(attempts)[:8]:
            if not isinstance(item, dict):
                continue
            clean = {}
            for key, value in item.items():
                if key in boolean and isinstance(value, bool):
                    clean[key] = value
                elif key in numeric and isinstance(value, (int, float)) and not isinstance(value, bool) and -1_000_000 <= value <= 1_000_000 and math.isfinite(value):
                    clean[key] = value
                elif key == "reason" and isinstance(value, str) and value in reasons:
                    clean[key] = value
                elif key == "primaryReason" and isinstance(value, str) and value in reasons:
                    clean[key] = value
                elif key == "strongReason" and isinstance(value, str) and value in reasons | {
                        "complete", "natural_eos", "token_bound", "no_eos", "decoder_shape", "deadline",
                        "out_of_memory", "dependency_missing", "model_missing", "model_identity",
                        "cuda_unavailable", "decoder_error"}:
                    clean[key] = value
                elif key == "stopReason" and isinstance(value, str) and value in {"sampled_eos", "argmax_eos", "length_limit", "iteration_limit", "cancelled", "unknown"}:
                    clean[key] = value
                elif key == "referenceId" and isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_-]{1,64}/[A-Za-z0-9_.-]{1,128}", value):
                    clean[key] = value
            sanitized.append(clean)
        self.diagnostics = {"attempts": sanitized}
        if isinstance(segment_index, int) and not isinstance(segment_index, bool) and 0 <= segment_index < 128:
            self.diagnostics["segmentIndex"] = segment_index
        count = {4: "四", 8: "八"}.get(len(sanitized), str(len(sanitized)))
        super().__init__(f"日语语音未能完整合成，{count}次自动重试仍未通过，请重试本段。")


def configure(root: Path):
    root = root.resolve()
    if not root.is_dir():
        raise ValueError("Project root does not exist")
    os.chdir(root / "runtime/voice")
    for name, sub in {
        "TEMP": "data/temp/python", "TMP": "data/temp/python", "HF_HOME": "data/cache/huggingface",
        "TORCH_HOME": "data/cache/torch", "PIP_CACHE_DIR": "data/cache/pip", "NUMBA_CACHE_DIR": "data/cache/numba",
        "MPLCONFIGDIR": "data/cache/matplotlib", "XDG_CACHE_HOME": "data/cache", "GRADIO_TEMP_DIR": "data/temp/gradio",
    }.items():
        p = root / sub
        p.mkdir(parents=True, exist_ok=True)
        os.environ[name] = str(p)
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1", TOKENIZERS_PARALLELISM="false", TQDM_DISABLE="1")
    # Bound native thread pools before NumPy/PyTorch DLL initialization on Windows.
    os.environ.update(OPENBLAS_NUM_THREADS="1", MKL_NUM_THREADS="1", OMP_NUM_THREADS="1")
    # The shared embedded interpreter's ._pth contains unrelated upstream entry paths.
    upstream = root / "GPT-SoVITS-v2pro-20261006"
    sys.path[:] = [p for p in sys.path if not (str(upstream).lower() in p.lower() and "runtime" not in p.lower())]
    sys.path.insert(0, str(VOICE_DIR / "vendor"))
    sys.path.insert(0, str(VOICE_DIR / "vendor/eres2net"))
    if os.name == "nt":
        interpreter = VOICE_DIR / "python" if (VOICE_DIR / "python/python.exe").exists() else upstream / "runtime"
        library = interpreter / "Lib/site-packages"
        for directory in [interpreter, library / "torch/lib", *library.glob("nvidia/*/bin")]:
            if directory.is_dir():
                _DLL_HANDLES.append(os.add_dll_directory(str(directory)))
                os.environ["PATH"] = str(directory) + os.pathsep + os.environ.get("PATH", "")


def _word_boundary(text: str, maximum: int) -> int:
    """Prefer a Japanese word boundary when an unpunctuated sentence is long."""
    import pyopenjtalk
    cursor, boundary = 0, 0
    for word in pyopenjtalk.run_frontend(text[:maximum + 32]):
        surface = word["string"]
        start = text.find(surface, cursor)
        if start < 0:
            continue
        cursor = start + len(surface)
        if 25 <= cursor <= maximum:
            boundary = cursor
        if cursor > maximum:
            break
    return boundary or maximum


def split_japanese(text: str, target=50, maximum=50) -> list[str]:
    """Lossless ordered partition: closing quotes remain attached to sentence punctuation."""
    if not isinstance(text, str) or not text.strip() or len(text) > 12000:
        raise ValueError("Japanese speech text must contain 1–12000 characters")
    if re.search(r"[{}]|```|<\/?(?:script|audio)|\[(?:emotion|情绪)[:：]", text, re.I):
        raise ValueError("Speech text contains structured output or control markup")
    if not re.search(r"[\u3040-\u30ff\u4e00-\u9fff]", text):
        raise ValueError("Speech text must be Japanese; convert foreign words to a Japanese reading")
    # Pure kanji is allowed: script alone cannot reliably distinguish Chinese and Japanese.
    units = re.findall(r".*?[。！？!?](?:[。！？!?…‥]*[」』）】\"']*)|.+$", text, flags=re.S)
    chunks, leading = [], ""
    for unit in units:
        if not chunks and not re.search(r"[\w\u3040-\u30ff\u4e00-\u9fff]", unit):
            leading += unit
            continue
        unit, leading = leading + unit, ""
        # Never combine complete sentences: the semantic model may emit EOS at
        # the first period even though more text is present in its phone input.
        # Overlong sentences split at the last comma/space if available. Every character survives.
        while len(unit) > maximum:
            candidates = [unit.rfind(c, 25, maximum) for c in "、，,；;\n "]
            boundary = max(candidates)
            boundary = boundary + 1 if boundary >= 25 else _word_boundary(unit, maximum)
            chunks.append(unit[:boundary])
            unit = unit[boundary:]
        if unit:
            if chunks and not re.search(r"[\w\u3040-\u30ff\u4e00-\u9fff]", unit):
                chunks[-1] += unit
            else:
                chunks.append(unit)
    # A laugh/cry can otherwise consume the semantic model's ending point and
    # prevent a following short lexical sentence from being generated at all.
    # Give each explicitly delimited sound and the body their own inference.
    from completeness import _expression_parts, split_expression_edges, split_hesitation_prefix
    chunks = [part for chunk in chunks for part in split_expression_edges(chunk)]
    chunks = [part for chunk in chunks for part in split_hesitation_prefix(chunk)]
    # Isolate a short nonlexical filler onset so it cannot consume the body.
    # Kana length alone cannot identify an onset: negatives such as いいえ and
    # いや must retain their lexical context. Require a frontend filler and no
    # lexical POS; particles accommodate unusual spellings such as もぅっ.
    partitioned = []
    for chunk in chunks:
        match = re.fullmatch(r"([\s…‥「『（【\"'“‘]*[ぁ-ゖァ-ヶー]{1,8}[、，,]\s*)(.+)", chunk, re.S)
        if match and not re.search(r"[A-Za-z0-9ぁ-ゖァ-ヶ\u4e00-\u9fff]", match[2]):
            match = None
        if match:
            import pyopenjtalk
            words = pyopenjtalk.run_frontend(match[1])
            if (not any(word["pos"] == "フィラー" for word in words)
                    or any(word["pos"] not in {"フィラー", "助詞", "記号"} for word in words)):
                match = None
        partitioned.extend([match[1], match[2]] if match else [chunk])
    # Hesitation/filler partitioning can expose one explicit edge sound that
    # the initial expression pass could not reach. Apply one guarded final
    # pass, never recurse: the single-edge guard refuses a remainder with two
    # or more sounds at either edge, preserving the original overflow bound.
    chunks = [part for chunk in partitioned
              for part in (split_expression_edges(chunk) if _expression_parts(chunk) is not None else [chunk])]
    if "".join(chunks) != text:
        raise RuntimeError("Internal speech partition lost text")
    if len(chunks) > 128:
        raise ValueError("Speech request exceeds the 128 segment bound")
    return chunks


def beneath(root: Path, path: Path) -> Path:
    resolved = path.resolve()
    if not resolved.is_relative_to(root.resolve()) or resolved == root.resolve():
        raise ValueError("Path is outside the controlled directory")
    return resolved


def plain(value):
    if hasattr(value, "__dict__") and type(value).__name__ == "HParams":
        return {k: plain(v) for k, v in vars(value).items()}
    if isinstance(value, dict):
        return {k: plain(v) for k, v in value.items()}
    if isinstance(value, (tuple, list)):
        return [plain(v) for v in value]
    return value


def safe_checkpoint(path: Path, sovits=False):
    import torch
    from utils import HParams
    if path.stat().st_size > 2_000_000_000:
        raise ValueError("Checkpoint exceeds 2 GB safety bound")
    with torch.serialization.safe_globals([HParams]):
        if sovits:
            raw = path.read_bytes()
            if raw[:2] != b"06":
                raise ValueError("Only v2ProPlus checkpoints (actual header 06) are supported")
            value = torch.load(io.BytesIO(b"PK" + raw[2:]), map_location="cpu", weights_only=True)
        else:
            value = torch.load(path, map_location="cpu", weights_only=True)
    return plain(value)


@dataclass
class ModelPair:
    model_id: str
    name: str
    gpt: Path | None
    sovits: Path | None
    error: str | None = None


class VoiceEngine:
    def __init__(self, root: Path, device="auto"):
        self.root = root.resolve()
        self._runtime_identity = capture_runtime_identity(self.root)
        configure(self.root)
        self.models_dir = self.root / "models/voice"
        self.models_dir.mkdir(parents=True, exist_ok=True)
        self.temp_dir = self.root / "data/temp/voice"
        self.temp_dir.mkdir(parents=True, exist_ok=True)
        self.state_path = self.root / "data/config/voice-state.json"
        self.device_option = device
        self.device = None
        self.current = None
        self.s1 = self.s2 = self.hubert = self.sv = None
        self.ref_cache = None
        self.last_reference = {}
        self.reference_sessions = {}
        self.verifier = None
        self.quality_stats = {"acceptedSegments": 0, "rejectedAttempts": 0, "fallbackSegments": 0, "failedSegments": 0}
        self.last_quality_failure = None
        self.references, self.reference_errors = self.audit_references()
        self.pairs = {}
        self.scan_models()

    def audit_references(self):
        references, errors = {}, []
        base = self.root / "training/atri/ATR_reference"
        for directory in sorted(base.glob("*")):
            if not directory.is_dir():
                continue
            emotion = re.sub(r"^\d+_", "", directory.name)
            for wav_path in sorted(directory.glob("*.wav")):
                try:
                    beneath(base, wav_path)
                    txt = wav_path.with_suffix(".txt")
                    beneath(base, txt)
                    transcript = txt.read_text(encoding="utf-8-sig").strip()
                    if not transcript:
                        raise ValueError("Empty transcript")
                    with wave.open(str(wav_path)) as wav:
                        duration = wav.getnframes() / wav.getframerate()
                        if wav.getsampwidth() not in {2, 3, 4} or wav.getnchannels() not in {1, 2} or not 3 <= duration <= 10:
                            raise ValueError(f"Reference audio must be 3–10 s PCM mono/stereo; duration={duration:.3f}")
                    references.setdefault(emotion, []).append({"id": directory.name + "/" + wav_path.stem, "path": wav_path, "text": transcript, "duration": duration})
                except Exception as exc:
                    errors.append({"file": str(wav_path.relative_to(self.root)), "error": str(exc)})
            for txt in directory.glob("*.txt"):
                if not txt.with_suffix(".wav").is_file():
                    errors.append({"file": str(txt.relative_to(self.root)), "error": "Missing WAV pair"})
        if not references.get("neutral"):
            raise ValueError("No valid neutral Japanese reference WAV/TXT pairs found")
        return references, errors

    def scan_models(self):
        pairs = {}
        for directory in sorted(self.models_dir.iterdir()):
            if not directory.is_dir():
                continue
            model_id = directory.name
            pair = ModelPair(model_id, model_id, None, None)
            try:
                beneath(self.models_dir, directory)
                if not re.fullmatch(r"[\w .-]{1,80}", model_id):
                    raise ValueError("Model directory name is invalid")
                ckpts, pths = list(directory.glob("*.ckpt")), list(directory.glob("*.pth"))
                if len(ckpts) != 1 or len(pths) != 1:
                    raise ValueError(f"Expected exactly one .ckpt and one .pth; found {len(ckpts)} and {len(pths)}")
                pair.gpt, pair.sovits = beneath(self.models_dir, ckpts[0]), beneath(self.models_dir, pths[0])
                with pair.sovits.open("rb") as file:
                    if file.read(2) != b"06":
                        raise ValueError("Unsupported actual SoVITS checkpoint version; v2ProPlus header 06 required")
                with pair.gpt.open("rb") as file:
                    if file.read(2) != b"PK":
                        raise ValueError("Unsupported GPT checkpoint container")
            except Exception as exc:
                pair.error = str(exc)
            pairs[model_id] = pair
        self.pairs = pairs
        return [{"id": p.model_id, "name": p.name, "version": "v2ProPlus", "valid": p.error is None, "error": p.error, "loaded": p.model_id == self.current} for p in pairs.values()]

    def health(self):
        identity = {"root": self._runtime_identity["root"], "files": self._runtime_identity["files"].copy()}
        return {"status": "ready", "pid": os.getpid(), "modelId": self.current, "version": "v2ProPlus", "device": self.device or self.device_option, "referenceCount": sum(map(len, self.references.values())), "referenceErrors": self.reference_errors, "emotions": list(self.references), "protocol": 1, "qualityStats": self.quality_stats.copy(), "lastQualityFailure": self.last_quality_failure,
                "runtimeIdentity": identity, "voicePolicy": {"id": "ja-voice-r4", "maxAttempts": 8}}

    def _reference_state(self, emotion, session_id):
        if session_id is None:
            return None
        if not isinstance(session_id, str) or not 1 <= len(session_id) <= 128 or any(ord(c) < 32 for c in session_id):
            raise ValueError("Invalid voice session ID")
        key = (session_id, emotion)
        state = self.reference_sessions.pop(key, None)
        if state is None:
            if len(self.reference_sessions) >= 64:
                self.reference_sessions.pop(next(iter(self.reference_sessions)))
            state = {"reference": None, "failed": set()}
        self.reference_sessions[key] = state
        return state

    def _successful_reference(self, emotion, session_id, accepted, failed):
        """Keep a proven emotional voice; neutral success never becomes sticky."""
        pool = self.references[emotion]
        emotional_ids = {item["id"] for item in pool}
        if accepted["id"] in emotional_ids:
            reference = accepted
            failed.discard(accepted["id"])
        else:
            available = [item for item in pool if item["id"] not in failed]
            # Every emotional reference can fail on one text. A new chunk still
            # gets an emotional first attempt, rather than inheriting neutrality.
            reference = random.choice(available or pool)
        state = self._reference_state(emotion, session_id)
        if state is not None:
            state["reference"] = reference
        self.last_reference[emotion] = reference["id"]
        return reference

    def choose_reference(self, emotion: str, session_id: str | None):
        fallback = {"serious": "neutral", "curious": "surprised", "comfort": "gentle", "comforting": "gentle", "lost": "sad", "embarrassed": "shy", "crying": "agony_crying"}
        emotion = fallback.get(emotion, emotion)
        if emotion not in self.references:
            emotion = "neutral"
        state = self._reference_state(emotion, session_id)
        failed = state["failed"] if state is not None else set()
        if state is not None and state["reference"] is not None and state["reference"]["id"] not in failed:
            return emotion, state["reference"]
        pool = self.references[emotion]
        available = [r for r in pool if r["id"] not in failed] or pool
        options = [r for r in available if r["id"] != self.last_reference.get(emotion)] or available
        ref = random.choice(options)
        self.last_reference[emotion] = ref["id"]
        if state is not None:
            state["reference"] = ref
        return emotion, ref

    def _initialize_shared(self):
        import torch
        if self.device is None:
            self.device = "cuda" if self.device_option == "auto" and torch.cuda.is_available() else ("cpu" if self.device_option == "auto" else self.device_option)
            self.dtype = torch.float16 if self.device == "cuda" else torch.float32
            torch.set_num_threads(min(8, os.cpu_count() or 4))
        if self.hubert is not None and self.sv is not None:
            return
        from transformers import HubertModel
        from ERes2NetV2 import ERes2NetV2
        pretrained = VOICE_DIR / "pretrained"
        if not (pretrained / "chinese-hubert-base/pytorch_model.bin").exists():
            pretrained = self.root / "GPT-SoVITS-v2pro-20261006/GPT_SoVITS/pretrained_models"
        # Japanese uses zero BERT features; no Chinese BERT weights or tokenizer are loaded.
        # Commit shared models together. A failed speaker load or device
        # allocation must leave the next explicit load able to retry both.
        hubert = HubertModel.from_pretrained(str(pretrained / "chinese-hubert-base"), local_files_only=True, attn_implementation="sdpa").eval().requires_grad_(False).to(device=self.device, dtype=self.dtype)
        speaker = ERes2NetV2(baseWidth=24, scale=4, expansion=4)
        speaker.load_state_dict(torch.load(pretrained / "sv/pretrained_eres2netv2w24s4ep4.ckpt", map_location="cpu", weights_only=True), strict=True)
        speaker = speaker.eval().requires_grad_(False).to(device=self.device, dtype=self.dtype)
        self.hubert, self.sv = hubert, speaker

    def _free_pair(self):
        import torch
        if self.verifier is not None:
            self.verifier.release_strong()
        self.s1 = self.s2 = self.ref_cache = None
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    def _load_pair(self, model_id):
        import torch
        from AR.models.t2s_model import Text2SemanticDecoder
        from module.models import SynthesizerTrn
        pair = self.pairs[model_id]
        # Read and validate both safe tensor containers before releasing the current GPU pair.
        s1_data, s2_data = safe_checkpoint(pair.gpt), safe_checkpoint(pair.sovits, sovits=True)
        c1, c2 = s1_data["config"], s2_data["config"]
        if c2["model"].get("version") != "v2ProPlus":
            raise ValueError("SoVITS internal architecture version is not v2ProPlus")
        if "sv_emb.weight" not in s2_data["weight"] or s2_data["weight"]["sv_emb.weight"].shape[1] != 20480:
            raise ValueError("Missing v2ProPlus speaker embedding architecture")
        gpt_weights = {k.removeprefix("model."): v for k, v in s1_data["weight"].items()}
        if c1["model"]["EOS"] != 1024 or c1["model"]["vocab_size"] != 1025 or c1["model"]["phoneme_vocab_size"] != s2_data["weight"]["enc_p.text_embedding.weight"].shape[0]:
            raise ValueError("GPT semantic vocabulary or phoneme inventory is incompatible with v2ProPlus")
        self._initialize_shared()
        self._free_pair()
        c2["model"]["semantic_frame_rate"] = "25hz"
        self.s1 = Text2SemanticDecoder(c1)
        self.s1.load_state_dict(gpt_weights, strict=True)
        self.s1 = self.s1.eval().requires_grad_(False).to(device=self.device, dtype=self.dtype)
        self.s2 = SynthesizerTrn(c2["data"]["filter_length"] // 2 + 1, c2["train"]["segment_size"] // c2["data"]["hop_length"], n_speakers=c2["data"]["n_speakers"], **c2["model"])
        result = self.s2.load_state_dict(s2_data["weight"], strict=False)
        if result.missing_keys or any(not k.startswith("enc_q.") for k in result.unexpected_keys):
            raise ValueError(f"SoVITS architecture mismatch: {result}")
        self.s2 = self.s2.eval().requires_grad_(False).to(device=self.device, dtype=self.dtype)
        self.config = c2["data"]
        self.sample_rate = int(c2["data"]["sampling_rate"])
        self.max_sec = min(54, int(c1["data"]["max_sec"]))
        self.current = model_id

    def load(self, model_id):
        self.scan_models()
        if model_id not in self.pairs:
            raise ValueError("Unknown registered model ID")
        if self.pairs[model_id].error:
            raise ValueError(self.pairs[model_id].error)
        if self.current == model_id and self.s1 is not None and self.s2 is not None:
            return self.health()
        old = self.current
        try:
            self._load_pair(model_id)
        except Exception:
            self._free_pair()
            self.current = None
            if old and old in self.pairs and not self.pairs[old].error:
                try:
                    self._load_pair(old)
                except Exception:
                    self._free_pair()
                    self.current = None
            raise
        self.state_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self.state_path.with_suffix(".tmp")
        temporary.write_text(json.dumps({"modelId": self.current}), encoding="utf-8")
        temporary.replace(self.state_path)
        return self.health()

    def default_model(self):
        if self.state_path.exists():
            try:
                last = json.loads(self.state_path.read_text(encoding="utf-8"))["modelId"]
                if last in self.pairs and self.pairs[last].error is None:
                    return last
            except (ValueError, KeyError, OSError):
                pass
        valid = [p.model_id for p in self.pairs.values() if not p.error]
        if not valid:
            raise ValueError("No valid v2ProPlus model pair is installed in models/voice")
        return "AtriTurbo" if "AtriTurbo" in valid else valid[0]

    @staticmethod
    def _phones(text):
        from text.japanese import g2p, text_normalize
        from text import cleaned_text_to_sequence
        # Japanese frontend converts Latin words/numbers through OpenJTalk; no language classifier.
        text = text_normalize(text.strip())
        if text and text[-1] not in "。！？.!?、，,；;…‥":
            text += "。"
        return cleaned_text_to_sequence(g2p(text), "v2ProPlus")

    def _prepare_reference(self, reference):
        import numpy as np
        import soundfile as sf
        import torch
        import torchaudio.functional as AF
        import kaldi
        if self.ref_cache and self.ref_cache["id"] == reference["id"]:
            return self.ref_cache
        audio, sr = sf.read(str(reference["path"]), dtype="float32", always_2d=True)
        raw = torch.from_numpy(audio.mean(axis=1)).unsqueeze(0).to(self.device)
        wav16 = AF.resample(raw, sr, 16000)
        # Match upstream prompt padding exactly: 0.3 * output sample rate samples at 16k.
        padded = torch.cat([wav16, torch.zeros((1, int(self.sample_rate * .3)), device=self.device)], dim=1).to(self.dtype)
        feature = self.hubert(padded)["last_hidden_state"].transpose(1, 2)
        semantic = self.s2.extract_latent(feature)[0, 0].unsqueeze(0)
        wav32 = AF.resample(raw, sr, self.sample_rate)
        nfft, hop, win = (int(self.config[k]) for k in ["filter_length", "hop_length", "win_length"])
        padded32 = torch.nn.functional.pad(wav32.unsqueeze(1), (int((nfft-hop)/2), int((nfft-hop)/2)), mode="reflect").squeeze(1)
        complex_spec = torch.stft(padded32, nfft, hop_length=hop, win_length=win, window=torch.hann_window(win, device=self.device), center=False, pad_mode="reflect", normalized=False, onesided=True, return_complex=True)
        spec = torch.sqrt(torch.view_as_real(complex_spec).pow(2).sum(-1) + 1e-6).to(self.dtype)
        fbank = torch.stack([kaldi.fbank(w.unsqueeze(0), num_mel_bins=80, sample_frequency=16000, dither=0) for w in wav16.to(self.dtype)])
        speaker = self.sv.forward3(fbank)
        self.ref_cache = {"id": reference["id"], "semantic": semantic, "spec": spec, "speaker": speaker, "phones": self._phones(reference["text"])}
        return self.ref_cache

    def synthesize(self, text, emotion, request_id, cancel: threading.Event, session_id=None):
        import numpy as np
        import soundfile as sf
        import torch
        chunks = split_japanese(text)
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", request_id):
            raise ValueError("requestId must contain 1–80 ASCII letters, digits, underscore or hyphen")
        if self.current is None:
            self.load(self.default_model())
        chosen_emotion, reference = self.choose_reference(emotion, session_id)
        reference_state = self._reference_state(chosen_emotion, session_id)
        failed_references = reference_state["failed"] if reference_state is not None else set()
        if self.verifier is None:
            from completeness import JapaneseSpeechVerifier
            self.verifier = JapaneseSpeechVerifier(self.root)
        directory = beneath(self.temp_dir, self.temp_dir / request_id)
        directory.mkdir(parents=True, exist_ok=False)
        (directory / ".owner.json").write_text(json.dumps({"pid": os.getpid(), "requestId": request_id, "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "kind": "voice"}), encoding="utf-8")
        with torch.inference_mode():
            self.s1.cancel_check = cancel.is_set
            for index, chunk in enumerate(chunks):
                if cancel.is_set():
                    raise InterruptedError("Speech request cancelled")
                start = time.perf_counter()
                phones = self._phones(chunk)
                from completeness import _pure_expression
                nonlexical_expression = _pure_expression(chunk)
                if not phones:
                    raise ValueError("Japanese frontend produced no speech phones")
                candidates = reference_candidates(reference, self.references[chosen_emotion], self.references["neutral"],
                                                  failed_ids=failed_references)
                attempts = []
                for attempt, candidate in enumerate(candidates):
                    if cancel.is_set():
                        raise InterruptedError("Speech request cancelled")
                    cache = self._prepare_reference(candidate)
                    token_limit = semantic_token_limit(len(phones), candidate["duration"], len(cache["phones"]), self.max_sec)
                    if nonlexical_expression:
                        # A non-lexical cry/laugh has no word ending to wait for.
                        # Give it an explicit <=3s duration, separate from the
                        # following lexical body, which still requires EOS and
                        # full content verification. This cannot accept capped
                        # word-bearing speech as complete.
                        token_limit = min(token_limit, 75)
                    all_phones = torch.tensor(cache["phones"] + phones, dtype=torch.long, device=self.device).unsqueeze(0)
                    features = torch.zeros((1, 1024, all_phones.shape[1]), dtype=self.dtype, device=self.device)
                    semantic, length = self.s1.infer_panel(all_phones, torch.tensor([all_phones.shape[1]], dtype=torch.long, device=self.device), cache["semantic"], features,
                        # A greedy final candidate with no repetition penalty
                        # repeatedly ran to the cap for some short lexical words.
                        # Keep bounded stochastic decoding on every candidate.
                        top_k=5, top_p=.9, temperature=.8,
                        early_stop_num=token_limit, repetition_penalty=1.1)
                    if cancel.is_set():
                        raise InterruptedError("Speech request cancelled")
                    generation = semantic_generation_check(semantic, length, getattr(self.s1, "last_generation", None), token_limit, nonlexical_expression)
                    if not generation["passed"]:
                        attempts.append({**generation, "referenceId": candidate["id"]})
                        failed_references.add(candidate["id"])
                        self.quality_stats["rejectedAttempts"] += 1
                        continue
                    pcm = self.s2.decode(semantic[:, -length:].unsqueeze(0), torch.tensor(phones, dtype=torch.long, device=self.device).unsqueeze(0), [cache["spec"]], sv_emb=[cache["speaker"]])[0, 0].float().cpu().numpy()
                    if not np.isfinite(pcm).all() or pcm.size < self.sample_rate * .1:
                        attempts.append({**generation, "passed": False, "reason": "invalid_pcm", "referenceId": candidate["id"]})
                        failed_references.add(candidate["id"])
                        self.quality_stats["rejectedAttempts"] += 1
                        continue
                    # Verify the same normalized, faded, quantized samples
                    # that the PCM16 WAV will contain, not an earlier float
                    # waveform whose weak onset can yield a different readback.
                    pcm16 = playback_pcm16(pcm, self.sample_rate)
                    pcm = pcm16.astype(np.float32) / 32768
                    quality = self.verifier.check(chunk, pcm, self.sample_rate, cancel)
                    if nonlexical_expression and generation["stopReason"] == "length_limit":
                        quality["boundedExpression"] = True
                    attempts.append({**generation, **quality, "referenceId": candidate["id"]})
                    if quality["passed"]:
                        accepted_reference = candidate
                        self.quality_stats["acceptedSegments"] += 1
                        self.quality_stats["fallbackSegments"] += int(chosen_emotion != "neutral" and candidate["id"] not in {item["id"] for item in self.references[chosen_emotion]})
                        reference = self._successful_reference(chosen_emotion, session_id, candidate, failed_references)
                        break
                    self.quality_stats["rejectedAttempts"] += 1
                    failed_references.add(candidate["id"])
                else:
                    self.quality_stats["failedSegments"] += 1
                    error = SpeechCompletenessError(attempts, index)
                    self.last_quality_failure = error.diagnostics
                    raise error
                if cancel.is_set():
                    raise InterruptedError("Speech request cancelled")
                filename = directory / f"{index:04d}.wav"
                sf.write(str(filename), pcm16, self.sample_rate, subtype="PCM_16")
                yield {"index": index, "count": len(chunks), "text": chunk, "path": str(filename), "duration": len(pcm)/self.sample_rate, "sampleRate": self.sample_rate,
                       "emotion": re.sub(r"^\d+_", "", accepted_reference["id"].split("/")[0]), "referenceId": accepted_reference["id"],
                       "modelId": self.current, "elapsedSeconds": time.perf_counter()-start, "attemptCount": len(attempts), "verification": attempts}

    def release(self, request_id):
        import shutil
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", request_id):
            raise ValueError("Invalid requestId")
        directory = beneath(self.temp_dir, self.temp_dir / request_id)
        if directory.exists():
            # Never follow symlinks/reparse points inside a controlled task directory.
            for p in directory.rglob("*"):
                beneath(directory, p)
                if p.is_symlink() or getattr(p.stat(), "st_file_attributes", 0) & 0x400:
                    raise ValueError("Refusing to clean a linked task path")
            shutil.rmtree(directory)
        return {"released": request_id}
