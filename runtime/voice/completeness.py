"""Private, in-memory Japanese TTS coverage check using the locked local CPU ASR.

This recognizer belongs to the voice worker. It does not use the microphone,
the microphone ASR session, VAD, the network, or an audio/text log. A successful
check is evidence of phonetic coverage, not a proof of perfect pronunciation.
"""
from __future__ import annotations

import hashlib
import importlib
import math
import os
from pathlib import Path
import re
import sys
import time
import unicodedata


MODEL_HASHES = {
    "model.int8.onnx": "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51",
    "tokens.txt": "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc",
}
SHERPA_VERSION = "1.13.8"
MAX_READING_LENGTH = 2048
# These are non-lexical sounds, not an open-ended list of short Japanese words.
# In particular, acknowledgements/negations such as はい, うん, いや, だめ and
# もう must never acquire an optional lexical boundary through this mechanism.
_EXPRESSIVE_PATTERN = r"(?:えへへっ?|あははっ?|うふふっ?|んふふっ?|むふふっ?|えっ?へんっ?|む[うー]{1,4}っ|ふんっ|ふふ[っん]?|へへっ?|くすくす|ぐすっ?|ひっく|え{2,6}っ|うわあ{1,4}っ?|う{1,6}っ|う{2,6}|はあ{1,4}っ?)"
_EXPRESSIVE_SEPARATOR = r"[\s、，,。.!！?？…‥]+"
_EXPRESSIVE_PADDING = r"[\s、，,。.!！?？…‥「」『』（）【】\"'“”‘’]*"
_PURE_EXPRESSION = re.compile(rf"{_EXPRESSIVE_PADDING}({_EXPRESSIVE_PATTERN}){_EXPRESSIVE_PADDING}\Z")
_EXPRESSION_PREFIX = re.compile(rf"\A[\s…‥「『（【\"'“‘]*({_EXPRESSIVE_PATTERN})({_EXPRESSIVE_SEPARATOR})(.+)\Z", re.S)
_EXPRESSION_SUFFIX = re.compile(rf"\A(.+?)({_EXPRESSIVE_SEPARATOR})({_EXPRESSIVE_PATTERN}){_EXPRESSIVE_PADDING}\Z", re.S)
_HESITATION_PATTERN = r"(?:あっ?|あの|その|えっ?|えっと|ええと)"
_HESITATION_PREFIX = re.compile(rf"\A[\s…‥「『（【\"'“‘]*({_HESITATION_PATTERN})({_EXPRESSIVE_SEPARATOR})(.+)\Z", re.S)
_STUTTER_PREFIX = re.compile(r"\A[\s…‥「『（【\"'“‘]*([ぁ-ゖ])([、，,]\s*)(.+)\Z", re.S)
# A strict inference-only subset. Short sounds that could start ordinary words
# remain ambiguous without punctuation: e.g. うっかり, ひっくり, はは母が.
# Lexical coverage never treats an unsplit sound as optional.
_UNDELIMITED_EXPRESSION = re.compile(r"(?:えへへっ?|あははっ?|うふふっ?|んふふっ?|くすくす|ふふ(?:っ|ん)|へへっ|ぐすっ|う{2,6}っ|え{2,6}っ|うわあ{1,4}っ)\Z")
_PREFIX_OPENING = re.compile(r"\A[\s…‥「『（【\"'“‘]*")


def _expression_text(text: str) -> str:
    """Canonical spelling for an exact, bounded non-lexical-sound allowlist."""
    value = unicodedata.normalize("NFKC", text)
    value = "".join(chr(ord(c) - 0x60) if "ァ" <= c <= "ヶ" else c for c in value)
    return value.translate(str.maketrans("ぁぃぅぇぉ", "あいうえお"))


def _pure_expression(text: str) -> bool:
    match = _PURE_EXPRESSION.fullmatch(_expression_text(text))
    return match is not None and len(match[1]) <= 8


def _span_text(text: str) -> str:
    """One-for-one kana normalization so original indices remain exact."""
    canonical = "".join(chr(ord(c) - 0x60) if "ァ" <= c <= "ヶ" else c for c in text)
    return canonical.translate(str.maketrans("ぁぃぅぇぉ", "あいうえお"))


def _undelimited_prefix_end(text: str):
    """An exact, longest sound followed by a nonempty lexical Japanese body.

    The original character spans survive. No suffix or arbitrary substring is
    treated as an undelimited expression. Matching is bounded to eight kana.
    """
    opening = _PREFIX_OPENING.match(text).end()
    options = []
    for end in range(opening + 1, min(len(text), opening + 8) + 1):
        sound, body = text[opening:end], text[end:]
        if not _UNDELIMITED_EXPRESSION.fullmatch(sound) or not body:
            continue
        if not re.match(r"[\u3041-\u3096\u30a1-\u30fa\u4e00-\u9fff]", body):
            continue
        if len(re.findall(r"[\u3041-\u3096\u30a1-\u30fa\u4e00-\u9fff]", body)) < 2:
            continue
        # Do not peel a supported prefix from a longer unsupported sound, a
        # hesitation (ええっと), or a negation (ううん). Longest matching alone
        # cannot distinguish those from an independent lexical body.
        if body.startswith(("っ", "ー")):
            continue
        if sound.startswith("えへへ") and body.startswith("へ"):
            continue
        if sound.startswith("あはは") and body.startswith("は"):
            continue
        if sound.startswith(("うふふ", "んふふ", "ふふ")) and body.startswith("ふ"):
            continue
        if sound.startswith("へへ") and body.startswith("へ"):
            continue
        if sound == "くすくす" and body.startswith("くす"):
            continue
        if sound.startswith("ぐす") and body.startswith("す"):
            continue
        if re.fullmatch(r"う{2,6}っ", sound) and body.startswith(("う", "ん")):
            continue
        if re.fullmatch(r"え{2,6}っ", sound) and body.startswith(("え", "ん", "と")):
            continue
        if sound.startswith("うわあ") and body.startswith("あ"):
            continue
        options.append(end)
    return max(options) if options else None


def _split_expression_edges(text: str, maximum: int, *, undelimited=False) -> list[str]:
    canonical = _span_text(text)
    if _pure_expression(text):
        return [text]
    start, end = 0, len(text)
    prefixes, suffixes = [], []
    for _ in range(maximum):
        match = _EXPRESSION_PREFIX.fullmatch(canonical[start:end])
        if match and len(match[1]) <= 8:
            boundary = start + match.start(3)
        elif undelimited and (relative := _undelimited_prefix_end(canonical[start:end])) is not None:
            boundary = start + relative
        else:
            break
        if not start < boundary < end:
            return [text]
        prefixes.append(text[start:boundary]); start = boundary
    for _ in range(maximum):
        match = _EXPRESSION_SUFFIX.fullmatch(canonical[start:end])
        if not match or len(match[3]) > 8:
            break
        boundary = start + match.start(2)
        if not start < boundary < end:
            return [text]
        suffixes.append(text[boundary:end]); end = boundary
    body = text[start:end]
    body_canonical = canonical[start:end]
    if not (prefixes or suffixes) or not body.strip():
        return [text]
    # The next match is only overflow detection, never another removal. It
    # also prevents a later single-edge guard from admitting multiple sounds.
    if (_EXPRESSION_PREFIX.fullmatch(body_canonical) or _EXPRESSION_SUFFIX.fullmatch(body_canonical) or _pure_expression(body)
            or (undelimited and _undelimited_prefix_end(body_canonical) is not None)):
        return [text]
    result = [*prefixes, body, *reversed(suffixes)]
    if "".join(result) != text:
        raise RuntimeError("Internal expression partition lost text")
    return result


def split_expression_edges(text: str) -> list[str]:
    """Separate at most two explicit non-lexical sounds at each lexical edge.

    Each sound is synthesized separately from the body. Quotes and punctuation
    retain their original spans. A third sound on either side leaves the input
    intact. The engine uses one initial pass and one guarded final pass after
    hesitation/filler partitioning; only _expression_parts-approved single
    edges enter the final pass. Results must never be recursively repartitioned.
    """
    return _split_expression_edges(text, 2, undelimited=True)


def _stutter_prefix(text: str):
    """One explicitly comma-separated kana repeating the body's first mora.

    Compare the actual frontend reading of the original isolated span, so a
    particle such as は (wa) cannot be split before 話 (ha). An initial two-kana
    contracted mora is not an exact one-kana repetition. This selector changes
    inference partitioning only; no sound or lexical reading becomes optional.
    """
    match = _STUTTER_PREFIX.fullmatch(_span_text(text))
    if not match:
        return None
    onset = normalize_reading(chr(ord(match[1]) + 0x60))
    isolated = reading(text[:match.start(3)])
    observed = reading(text[match.start(3):])
    if (len(onset) != 1 or isolated != onset or len(observed) < 2
            or not observed.startswith(onset) or observed[1] in "ァィゥェォャュョヮ"):
        return None
    return match


def split_hesitation_prefix(text: str) -> list[str]:
    """Preserve up to three hesitation/stutter prefixes as independent speech.

    Hesitations keep their existing spelling rules; one-kana stutters require
    an explicit comma and matching actual first-mora readings. Both share the
    three-prefix bound and the fourth-prefix overflow rollback. This is only
    inference partitioning: no new optional expression or acoustic allowance.
    The lexical body must remain nonempty.
    """
    canonical = _span_text(text)
    start, prefixes = 0, []
    for _ in range(3):
        match = _HESITATION_PREFIX.fullmatch(canonical[start:]) or _stutter_prefix(text[start:])
        if not match:
            break
        boundary = start + match.start(3)
        if not start < boundary < len(text):
            return [text]
        prefixes.append(text[start:boundary]); start = boundary
    body = text[start:]
    if (not prefixes or not re.search(r"[A-Za-z0-9\u3040-\u30ff\u4e00-\u9fff]", body)
            or _HESITATION_PREFIX.fullmatch(canonical[start:]) or _stutter_prefix(body)):
        return [text]
    # A bare final hesitation is not a lexical body. Ignore only its surrounding
    # punctuation/quotes for this exact-match check; never discard the samples.
    if re.fullmatch(rf"{_EXPRESSIVE_PADDING}{_HESITATION_PATTERN}{_EXPRESSIVE_PADDING}", canonical[start:]):
        return [text]
    result = [*prefixes, body]
    if "".join(result) != text:
        raise RuntimeError("Internal hesitation partition lost text")
    return result


def _expression_parts(text: str):
    # A conservative selector for the engine's final expression pass: at most
    # one explicit sound per edge, with a real intervening body. A remainder
    # containing two or more sounds at either edge must not be repartitioned.
    parts = _split_expression_edges(text, 1)
    if len(parts) < 2:
        return None
    prefix = parts[0] if _pure_expression(parts[0]) else ""
    suffix = parts[-1] if _pure_expression(parts[-1]) else ""
    body = "".join(parts[bool(prefix):len(parts) - bool(suffix)])
    return (body, prefix, suffix) if body else None


def normalize_reading(value: str) -> str:
    """Canonical kana for coverage, allowing ASR spelling/long-vowel variants."""
    value = unicodedata.normalize("NFKC", value)
    value = "".join(chr(ord(c) + 0x60) if "ぁ" <= c <= "ゖ" else c for c in value)
    value = re.sub(r"[^ァ-ヶー]", "", value).replace("ヲ", "オ").replace("ヂ", "ジ").replace("ヅ", "ズ")
    # Length marks and the common kana spellings of the same long vowels are
    # not reliable evidence of missing speech. Preserve consonants and morae.
    value = value.replace("ー", "")
    for previous, extensions in (
        ("アカガサザタダナハバパマヤャラワァ", "ア"),
        ("イキギシジチニヒビピミリィ", "イ"),
        ("ウクグスズツヌフブプムユュルヴゥ", "ウ"),
        ("エケゲセゼテデネヘベペメレェ", "エイ"),
        ("オコゴソゾトドノホボポモヨョロォ", "オウ"),
    ):
        value = re.sub(f"([{previous}])[{extensions}]+", r"\1", value)
    return value


def reading(text: str) -> str:
    """Convert Japanese spelling to kana without retaining the input or result."""
    import pyopenjtalk
    # A sokuon before an utterance boundary marks an emphatic/glottal ending,
    # not a missing lexical mora. ASR normally writes ですよっ as ですよ.
    # Keep word-internal geminates (切手, 買った, 学校) and all other endings.
    text = re.sub(r"[っッ]+(?=[\s、，,。.!！?？…‥「」『』（）【】\"'“”‘’]|$)", "", text)
    return normalize_reading(pyopenjtalk.g2p(text, kana=True))


def _distance(left: str, right: str) -> int:
    if len(left) < len(right):
        left, right = right, left
    row = list(range(len(right) + 1))
    for i, a in enumerate(left, 1):
        current = [i]
        for j, b in enumerate(right, 1):
            current.append(min(current[-1] + 1, row[j] + 1, row[j - 1] + (a != b)))
        row = current
    return row[-1]


def _anchor_error(anchor: str, actual: str, *, tail: bool) -> float:
    # A few ASR insertions/deletions around the boundary must not make an
    # otherwise complete sentence fail. The anchor stays at its respective end.
    low, high = max(1, len(anchor) - 3), min(len(actual), len(anchor) + 3)
    if low > high:
        return _distance(anchor, actual) / len(anchor)
    return min(_distance(anchor, actual[-n:] if tail else actual[:n]) for n in range(low, high + 1)) / len(anchor)


def deletion_alignment(expected: str, actual: str) -> dict:
    """Content-free edit diagnostics, preferring substitutions over indels.

    This only describes the ASR readings: an apparent deletion can still be an
    ASR error, so it is not independent evidence that a waveform lost speech.
    Among minimum-edit alignments, minimize indels and then boundary deletions.
    These tie-breaks avoid inventing deletions for a one-kana substitution or
    placing an ambiguous repeated-kana deletion at an utterance boundary.
    """
    rows, columns = len(expected), len(actual)
    if rows > MAX_READING_LENGTH or columns > MAX_READING_LENGTH:
        raise ValueError("Alignment exceeds reading bound")
    base = rows + columns + 1
    edit_weight, indel_weight = base * base, base
    traces = [bytearray(columns + 1) for _ in range(rows + 1)]
    previous = [j * (edit_weight + indel_weight) for j in range(columns + 1)]
    for j in range(1, columns + 1):
        traces[0][j] = 3
    for i, wanted in enumerate(expected, 1):
        current = [i * (edit_weight + indel_weight + 1)]
        traces[i][0] = 2
        for j, heard in enumerate(actual, 1):
            diagonal = previous[j - 1] + (wanted != heard) * edit_weight
            delete = previous[j] + edit_weight + indel_weight + (j == columns)
            insert = current[j - 1] + edit_weight + indel_weight
            best = min(diagonal, delete, insert)
            current.append(best)
            traces[i][j] = 1 if best == diagonal else (2 if best == delete else 3)
        previous = current
    operations = []
    i, j = rows, columns
    while i or j:
        operation = traces[i][j]
        if operation == 1:
            operations.append("M" if expected[i - 1] == actual[j - 1] else "S")
            i -= 1; j -= 1
        elif operation == 2:
            operations.append("D"); i -= 1
        else:
            operations.append("I"); j -= 1
    operations.reverse()
    deleted = longest = run = 0
    for operation in operations:
        if operation == "D":
            deleted += 1; run += 1; longest = max(longest, run)
        else:
            run = 0
    head = tail = 0
    for operation in operations:
        if operation in "MS":
            break
        head += operation == "D"
    for operation in reversed(operations):
        if operation in "MS":
            break
        tail += operation == "D"
    return {"deletedReadingCount": deleted, "maxDeletionRun": longest,
            "headDeletedReadingCount": head, "tailDeletedReadingCount": tail,
            "substitutionReadingCount": operations.count("S"),
            "insertedReadingCount": operations.count("I")}


def coverage(expected: str, actual: str) -> dict:
    """Compare readings; return only numeric diagnostics, never speech content."""
    expected, actual = normalize_reading(expected), normalize_reading(actual)
    size, actual_size = len(expected), len(actual)
    result = {
        "passed": False,
        "reason": "empty_readback",
        "readingErrorRate": 1.0,
        "headErrorRate": 1.0,
        "tailErrorRate": 1.0,
        "edgeHeadErrorRate": 1.0,
        "edgeTailErrorRate": 1.0,
        "lengthRatio": actual_size / max(1, size),
        "expectedReadingLength": size,
        "actualReadingLength": actual_size,
    }
    if not size:
        result["reason"] = "empty_expected_reading"
        return result
    if size > MAX_READING_LENGTH or actual_size > MAX_READING_LENGTH:
        result["reason"] = "reading_length_bound"
        return result
    if not actual_size:
        return result
    error = _distance(expected, actual) / size
    anchor_size = min(14, max(4, size // 3), size)
    head = _anchor_error(expected[:anchor_size], actual, tail=False)
    tail = _anchor_error(expected[-anchor_size:], actual, tail=True)
    edge_size = min(4, size)
    edge_head = _anchor_error(expected[:edge_size], actual, tail=False)
    edge_tail = _anchor_error(expected[-edge_size:], actual, tail=True)
    result.update(readingErrorRate=error, headErrorRate=head, tailErrorRate=tail,
                  edgeHeadErrorRate=edge_head, edgeTailErrorRate=edge_tail)
    if size <= 3:
        # Short interjections give the recognizer little context. Permit a
        # substitution, but reject an almost empty or unrelated readback.
        if actual_size < max(1, math.ceil(size * .4)):
            reason = "short_readback"
        elif error > .65:
            reason = "reading_mismatch"
        else:
            reason = "complete"
    elif size <= 8:
        # Emotional onsets can cause one ASR substitution even when a human
        # hears the complete short word. Require no reading-length deficit and
        # an exact ending; never let this tolerance hide a missing first kana
        # or the clipped ending seen in the paired real-waveform controls.
        if result["lengthRatio"] < 1:
            reason = "short_readback"
        elif error > .20:
            reason = "reading_mismatch"
        elif head > .25:
            reason = "missing_start"
        elif tail > 0:
            reason = "missing_end"
        else:
            reason = "complete"
    elif result["lengthRatio"] < .72:
        reason = "short_readback"
    elif error > .30:
        reason = "reading_mismatch"
    elif head > .50:
        reason = "missing_start"
    elif tail > .45:
        reason = "missing_end"
    elif edge_head > .25:
        # A long anchor's fractional tolerance can hide two/three clipped
        # boundary kana. Also check the fixed first four, allowing one ASR
        # error without treating an omitted word as a small sentence-wide CER.
        reason = "missing_start"
    elif edge_tail > .25:
        reason = "missing_end"
    else:
        reason = "complete"
    if size > 3:
        deletion = deletion_alignment(expected, actual)
        result.update(deletion)
        if reason == "complete":
            if deletion["headDeletedReadingCount"]:
                reason = "missing_start"
            elif deletion["tailDeletedReadingCount"]:
                reason = "missing_end"
            elif deletion["maxDeletionRun"] >= 2:
                reason = "missing_content"
            elif deletion["deletedReadingCount"] > max(1, size // 12):
                reason = "missing_content"
    result.update(passed=reason == "complete", reason=reason)
    return result


def expressive_coverage(text: str, expected: str, actual: str) -> dict:
    """Compatibility entry point; all unsplit text uses strict full coverage.

    Nonlexical sounds receive separate acoustic checks only after lossless
    inference partitioning. A whole-text caller never gains an edge exemption.
    """
    return coverage(expected, actual)


def _cancelled(cancel):
    if cancel.is_set():
        raise InterruptedError("Speech request cancelled")


TURBO_MODEL_HASHES = {
    "model.safetensors": "542566a422ae4f3fd23f1ba11add198fca01bbf82e66e6a2857b3f608b1eb9d1",
    "config.json": "c5b526b3e3cd64cd8940dabb45e8ba726629e22d8ed389c29b552f9140daf04a",
    "generation_config.json": "cce11bfe3aaa6ae9e072ea2637caaec8795e68d9b67e655a5af16ee509681a4c",
    "preprocessor_config.json": "7ccc62c6f2765af1f3b46c00c9b5894426835a05021c8b9c01eecb6dfb542711",
    "tokenizer.json": "297b13372ac43916285644fb9687add3cc62ee2a1adb60da3dc25cc94c1871fd",
    "tokenizer_config.json": "844b642c73a91359722f47b35705f7174686df33d252695d8572cf9ac03a6389",
    "vocab.json": "e2aa043ef015641d363d8288e7c241c85e36a5c761fb303598e0710233344387",
    "merges.txt": "2df2990a395e35e8dfbc7511e08c12d56018d8d04691e0133e5d63b21e154dc6",
    "added_tokens.json": "3c51f66c4c21f9e126970078f11ae77a78c74aee8df606ee9daba86e467108e0",
    "special_tokens_map.json": "baea4ea09372eb4fca86b4e4346139fd73cb807d5087e9de0948e971739c3e74",
    "normalizer.json": "bf1c507dc8724ca9cf9903640dacfb69dae2f00edee4f21ceba106a7392f26dd",
}


def _turbo_stop_result(sequence, prefix_length, limit, eos=50257):
    """Inspect the generated suffix, never trim EOS-as-padding (same token).

    Reject a token-cap outcome even if its last token happens to be EOS. This
    conservative boundary cannot turn a truncated transcript into acceptance.
    The caller also checks cancellation/deadline before decoding any text.
    """
    if (not isinstance(sequence, list) or any(not isinstance(value, int) or isinstance(value, bool) for value in sequence)
            or not isinstance(prefix_length, int) or isinstance(prefix_length, bool) or not 0 <= prefix_length < len(sequence)
            or not isinstance(limit, int) or isinstance(limit, bool) or limit <= 0):
        return {"passed": False, "strongReason": "decoder_shape", "strongGeneratedTokens": 0, "strongTokenLimit": limit}
    suffix = sequence[prefix_length:]
    reason = "token_bound" if len(suffix) >= limit else "no_eos" if suffix[-1] != eos or eos in suffix[:-1] else "natural_eos"
    return {"passed": reason == "natural_eos", "strongReason": reason,
            "strongGeneratedTokens": len(suffix), "strongTokenLimit": limit}


class _TurboUnavailable(RuntimeError):
    """Fixed content-free failure code, never an arbitrary dependency error."""


class _TurboSpeechVerifier:
    """Private lazy GPU arbiter, owned only by the serial voice-worker thread.

    Call release_idle() from that worker's idle queue branch; no timer/thread can
    unload a model concurrently with generation. The CPU/cheap path imports no
    torch/transformers and never queries CUDA through this object.
    """
    MAX_NEW_TOKENS = 192
    DECODE_SECONDS = 30
    IDLE_SECONDS = 60

    def __init__(self, root):
        self.root = Path(root).resolve()
        self.model = self.processor = self._torch = None
        self._validated = False
        self._last_used = None
        self._unavailable_request = self._unavailable_reason = None

    def _release(self):
        loaded = self.model is not None
        self.model = self.processor = None
        # A later actual load must revalidate all eleven pinned files. Cached
        # GPU objects keep their captured identity; released objects do not.
        self._validated = False
        if self._torch is not None:
            import gc
            gc.collect()
            if self._torch.cuda.is_initialized():
                self._torch.cuda.empty_cache()
        self._last_used = None
        return loaded

    def release_idle(self, now=None):
        now = time.monotonic() if now is None else now
        if self._last_used is not None and now - self._last_used >= self.IDLE_SECONDS:
            return self._release()
        return False

    def close(self):
        try:
            self._release()
        finally:
            self._torch = None
            self._validated = False
            self._unavailable_request = self._unavailable_reason = None

    def _initialize(self, cancel):
        if self.model is not None:
            return
        _cancelled(cancel)
        directory = self.root / "data/models/asr/WhisperTurbo"
        # Validate every existing ancestor before resolve/import/load. The
        # production installer also requires independent non-linked files.
        for path in (directory, *directory.parents):
            if path.exists() and (path.is_symlink() or getattr(path.stat(), "st_file_attributes", 0) & 0x400):
                raise _TurboUnavailable("model_identity")
        if not self._validated:
            for name, expected in TURBO_MODEL_HASHES.items():
                path = directory / name
                if not path.is_file():
                    raise _TurboUnavailable("model_missing")
                info = path.stat()
                if path.is_symlink() or info.st_nlink != 1 or getattr(info, "st_file_attributes", 0) & 0x400:
                    raise _TurboUnavailable("model_identity")
                digest = hashlib.sha256()
                with path.open("rb") as stream:
                    for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
                        _cancelled(cancel)
                        digest.update(block)
                if digest.hexdigest() != expected:
                    raise _TurboUnavailable("model_identity")
            self._validated = True
        try:
            import torch
            from transformers import WhisperProcessor, WhisperForConditionalGeneration
        except ImportError:
            raise _TurboUnavailable("dependency_missing") from None
        self._torch = torch
        if not torch.cuda.is_available():
            raise _TurboUnavailable("cuda_unavailable")
        processor = model = None
        try:
            processor = WhisperProcessor.from_pretrained(str(directory), local_files_only=True, token=False, trust_remote_code=False)
            model = WhisperForConditionalGeneration.from_pretrained(str(directory), local_files_only=True,
                use_safetensors=True, token=False, trust_remote_code=False, torch_dtype=torch.float16,
                attn_implementation="sdpa").eval().requires_grad_(False).to("cuda")
            _cancelled(cancel)
            # Commit only a fully loaded and configured pair. No global random
            # seed, CPU thread count or TTS decoder setting is modified.
            self.model, self.processor = model, processor
        except BaseException:
            model = processor = None
            self._release()
            raise

    def check(self, text, pcm, sample_rate, cancel):
        import numpy as np
        _cancelled(cancel)
        started = time.monotonic()
        duration = len(pcm) / sample_rate
        if self._unavailable_request is cancel:
            return {"passed": False, "strongReason": self._unavailable_reason}
        self._unavailable_request = self._unavailable_reason = None
        if duration > 30:
            return {"passed": False, "strongReason": "audio_length_bound", "strongElapsedSeconds": 0}
        inputs = features = attention = generated = sequences = samples = None
        release_after = False
        try:
            self.release_idle()
            self._initialize(cancel)
            _cancelled(cancel)
            torch = self._torch
            from transformers import StoppingCriteria, StoppingCriteriaList
            deadline = time.monotonic() + self.DECODE_SECONDS

            class Stop(StoppingCriteria):
                def __init__(self):
                    self.prefix_length = None
                    self.expired = False

                def __call__(self, input_ids, scores, **kwargs):
                    # Transformers 4.57's callback runs after each new token;
                    # capture the actual initial decoder length, including its
                    # language/task tokens, rather than guessing their count.
                    if self.prefix_length is None:
                        self.prefix_length = int(input_ids.shape[-1]) - 1
                    self.expired = time.monotonic() >= deadline
                    return torch.full((input_ids.shape[0],), cancel.is_set() or self.expired,
                                      dtype=torch.bool, device=input_ids.device)

            stop = Stop()
            samples = np.array(pcm, dtype=np.float32, copy=True)
            if sample_rate != 16000:
                from scipy.signal import resample_poly
                divisor = math.gcd(sample_rate, 16000)
                samples = resample_poly(samples, 16000 // divisor, sample_rate // divisor).astype(np.float32)
            # All original samples survive. Only optional silence is shortened
            # at 30 seconds; feature extraction is never allowed to crop speech.
            padding = min(1600, max(0, (480000 - len(samples)) // 2))
            samples = np.pad(samples, (padding, padding))
            if len(samples) > 480000:
                return {"passed": False, "strongReason": "audio_length_bound"}
            inputs = self.processor(samples, sampling_rate=16000, return_tensors="pt", return_attention_mask=True)
            features = inputs.input_features.to(device="cuda", dtype=torch.float16)
            attention = inputs.attention_mask.to("cuda")
            _cancelled(cancel)
            with torch.inference_mode():
                generated = self.model.generate(input_features=features, attention_mask=attention, language="ja", task="transcribe",
                    do_sample=False, num_beams=1, max_new_tokens=self.MAX_NEW_TOKENS, return_timestamps=False,
                    condition_on_prev_tokens=False, temperature=0.0, forced_eos_token_id=None,
                    stopping_criteria=StoppingCriteriaList([stop]), return_dict_in_generate=True,
                    output_scores=False, output_attentions=False, output_hidden_states=False)
            _cancelled(cancel)
            if stop.expired or time.monotonic() >= deadline:
                self._unavailable_request, self._unavailable_reason = cancel, "deadline"
                return {"passed": False, "strongReason": "deadline"}
            sequences = generated.sequences
            ids = sequences[0].detach().cpu().tolist()
            termination = _turbo_stop_result(ids, stop.prefix_length, self.MAX_NEW_TOKENS)
            if not termination["passed"]:
                return termination
            actual = self.processor.batch_decode(sequences, skip_special_tokens=True)[0].strip()
            _cancelled(cancel)
            result = coverage(reading(text), reading(actual) if actual else "")
            # The expected speech text never enters the processor or decoder.
            # Only this unchanged strict post-decode comparison consults it.
            return {**result, **{key: value for key, value in termination.items() if key != "passed"},
                    "strongReason": result["reason"]}
        except InterruptedError:
            release_after = True
            raise
        except _TurboUnavailable as error:
            release_after = True
            self._unavailable_request, self._unavailable_reason = cancel, error.args[0]
            return {"passed": False, "strongReason": error.args[0]}
        except Exception as error:
            # Never serialize exception bodies, waveform text or readback.
            oom = self._torch is not None and isinstance(error, self._torch.cuda.OutOfMemoryError)
            release_after = True
            self._unavailable_request, self._unavailable_reason = cancel, "out_of_memory" if oom else "decoder_error"
            return {"passed": False, "strongReason": "out_of_memory" if oom else "decoder_error"}
        finally:
            inputs = features = attention = generated = sequences = None
            if samples is not None:
                samples.fill(0)
            samples = None
            if release_after:
                self._release()
            elif self.model is not None:
                self._last_used = time.monotonic()


class JapaneseSpeechVerifier:
    """One lazy, reusable CPU recognizer per voice worker; no shared ASR state."""

    def __init__(self, root: Path):
        self.root = Path(root).resolve()
        self.recognizer = None
        self._validated = False
        self._dll_handles = []
        self._strong = None

    def _initialize(self, cancel):
        if self.recognizer is not None:
            return
        _cancelled(cancel)
        package_root = self.root / "runtime/asr/site-packages"
        library = package_root / "sherpa_onnx/lib"
        if not package_root.is_dir():
            raise RuntimeError("Local speech completeness dependency is missing")
        if os.name == "nt" and library.is_dir() and not self._dll_handles:
            self._dll_handles.append(os.add_dll_directory(str(library)))
        # Keep the ASR package path temporary; the imported package retains its
        # own __path__, while unrelated voice dependencies keep their priority.
        sys.path.insert(0, str(package_root))
        try:
            sherpa = importlib.import_module("sherpa_onnx")
        finally:
            sys.path.remove(str(package_root))
        if sherpa.__version__ != SHERPA_VERSION:
            raise RuntimeError("Speech completeness requires locked Sherpa-ONNX 1.13.8")
        model = self.root / "data/models/asr/SenseVoiceSmall"
        if not self._validated:
            for name, expected_hash in MODEL_HASHES.items():
                path = model / name
                if not path.is_file():
                    raise RuntimeError("Local speech completeness model is missing")
                digest = hashlib.sha256()
                with path.open("rb") as file:
                    for block in iter(lambda: file.read(4 * 1024 * 1024), b""):
                        _cancelled(cancel)
                        digest.update(block)
                if digest.hexdigest() != expected_hash:
                    raise RuntimeError("Local speech completeness model checksum failed")
            self._validated = True
        _cancelled(cancel)
        recognizer = sherpa.OfflineRecognizer.from_sense_voice(
            model=str(model / "model.int8.onnx"), tokens=str(model / "tokens.txt"),
            num_threads=2, sample_rate=16000, feature_dim=80, language="ja",
            use_itn=True, debug=False, provider="cpu",
        )
        self.recognizer = recognizer
        _cancelled(cancel)

    def check(self, text, pcm, sample_rate, cancel) -> dict:
        import numpy as np
        _cancelled(cancel)
        started = time.perf_counter()
        # These bounds also bound the synchronous native recognition call.
        if not isinstance(sample_rate, int) or not 8000 <= sample_rate <= 48000:
            raise ValueError("Speech completeness requires an 8–48 kHz sample rate")
        samples = np.asarray(pcm, dtype=np.float32)
        if samples.ndim != 1 or not np.isfinite(samples).all():
            raise ValueError("Speech completeness requires finite mono PCM")
        duration = samples.size / sample_rate
        if not .1 <= duration <= 60:
            return {**coverage("", ""), "reason": "audio_length_bound", "durationSeconds": duration}
        if float(np.max(np.abs(samples))) < 1e-4 or float(np.sqrt(np.mean(samples.astype(np.float64) ** 2))) < 1e-5:
            return {**coverage("", ""), "reason": "silent_audio", "durationSeconds": duration}
        expected = reading(text)
        _cancelled(cancel)
        if not expected:
            return {**coverage("", ""), "reason": "empty_expected_reading", "durationSeconds": duration}
        if _pure_expression(text):
            # A recognizer often emits no words for a laugh/cry/yelp. Only the
            # explicit bounded sound allowlist gets this acoustic-only check;
            # ordinary short words retain their existing, lower time bound.
            return {"passed": .12 <= duration <= 4,
                    "reason": "expressive_utterance_acoustic" if .12 <= duration <= 4 else "expressive_utterance_duration",
                    "expectedReadingLength": len(expected), "durationSeconds": duration,
                    "verifier": "bounded-nonsilent-pcm-for-nonlexical-expression", "verifierProvider": "cpu"}
        if len(expected) <= 3:
            # ASR is not a reliable phonetic judge for isolated one/two-mora
            # interjections (e.g. うん can be decoded as English "one"). Keep
            # these separate and require audible, bounded speech instead of
            # making a valid short response fail four times.
            return {"passed": .12 <= duration <= 2.5,
                    "reason": "short_utterance_acoustic" if .12 <= duration <= 2.5 else "short_utterance_duration",
                    "expectedReadingLength": len(expected), "durationSeconds": duration,
                    "verifier": "bounded-nonsilent-pcm-for-short-interjection", "verifierProvider": "cpu"}
        self._initialize(cancel)
        if sample_rate != 16000:
            from scipy.signal import resample_poly
            divisor = math.gcd(sample_rate, 16000)
            samples = resample_poly(samples, 16000 // divisor, sample_rate // divisor).astype(np.float32)
        # Keep the complete waveform, including the first and final phonemes.
        # Silence is context for ASR, not a VAD crop or an alteration to playback.
        samples = np.pad(samples, (1600, 1600))
        _cancelled(cancel)
        stream = self.recognizer.create_stream()
        stream.accept_waveform(16000, samples)
        self.recognizer.decode_stream(stream)
        _cancelled(cancel)
        actual_text = re.sub(r"<\|[^|]*\|>", "", stream.result.text).strip()
        result = expressive_coverage(text, expected, reading(actual_text) if actual_text else "")
        _cancelled(cancel)
        result.update(durationSeconds=duration, checkElapsedSeconds=time.perf_counter() - started,
                      verifier="SenseVoiceSmall-2024-07-17-int8", verifierProvider="cpu")
        if (not result["passed"] and result["reason"] in {"reading_mismatch", "missing_start", "missing_end"}
                and result.get("headDeletedReadingCount", 0) == 0 and result.get("tailDeletedReadingCount", 0) == 0
                and result.get("maxDeletionRun", 0) < 2
                and result.get("deletedReadingCount", 0) <= max(1, result.get("expectedReadingLength", 0) // 12)):
            # Start with substitution-like disagreement only. ASR deletion
            # diagnostics are not proof of lost samples, but obvious transcript
            # deletions remain rejected in this deliberately narrow first stage.
            if self._strong is None:
                self._strong = _TurboSpeechVerifier(self.root)
            strong = self._strong.check(text, pcm, sample_rate, cancel)
            diagnostic = {"strongAttempted": True, "strongReason": strong["strongReason"],
                "strongPassed": strong["passed"], "strongElapsedSeconds": time.perf_counter() - started - result["checkElapsedSeconds"],
                "primaryReason": result["reason"]}
            for name in ("readingErrorRate", "headErrorRate", "tailErrorRate", "edgeHeadErrorRate", "edgeTailErrorRate",
                         "lengthRatio", "expectedReadingLength", "actualReadingLength", "deletedReadingCount", "maxDeletionRun",
                         "headDeletedReadingCount", "tailDeletedReadingCount", "substitutionReadingCount", "insertedReadingCount"):
                value = result.get(name)
                if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and 0 <= value <= 1_000_000:
                    diagnostic["primary" + name[0].upper() + name[1:]] = value
            diagnostic.update({key: strong[key] for key in ("strongGeneratedTokens", "strongTokenLimit") if key in strong})
            if strong["passed"]:
                result = {**strong, "durationSeconds": duration, "verifier": "Whisper-large-v3-turbo-pinned", "verifierProvider": "cuda"}
            result.update(diagnostic, checkElapsedSeconds=time.perf_counter() - started)
        return result

    def release_idle(self, now=None):
        return self._strong.release_idle(now) if self._strong is not None else False

    def release_strong(self):
        if self._strong is not None:
            self._strong.close()
            self._strong = None

    def close(self):
        try:
            self.release_strong()
        finally:
            self.recognizer = None
            for handle in self._dll_handles:
                handle.close()
            self._dll_handles.clear()
