"""Offline SenseVoiceSmall/Silero VAD; private JSON Lines and in-memory WAV only."""
from __future__ import annotations
import argparse
import base64
import gc
import hashlib
import io
import json
import math
import os
import re
import sys
import time
import traceback
import wave
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROTOCOL = io.TextIOWrapper(os.fdopen(os.dup(sys.stdout.fileno()), "wb", buffering=0), encoding="utf-8", line_buffering=True)
os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
sys.stdout = sys.stderr
sys.stdin.reconfigure(encoding="utf-8", errors="strict")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, str(HERE / "site-packages"))
_DLL_HANDLES = []


def configure(root):
    os.chdir(HERE)
    for name, relative in {"TEMP": "data/temp/asr", "TMP": "data/temp/asr", "HF_HOME": "data/cache/huggingface", "TORCH_HOME": "data/cache/torch", "NUMBA_CACHE_DIR": "data/cache/numba", "XDG_CACHE_HOME": "data/cache"}.items():
        path = root / relative
        path.mkdir(parents=True, exist_ok=True)
        os.environ[name] = str(path)
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1", OPENBLAS_NUM_THREADS="1", OMP_NUM_THREADS="1", MKL_NUM_THREADS="1")
    if os.name == "nt":
        library = HERE / "site-packages/sherpa_onnx/lib"
        if library.exists():
            _DLL_HANDLES.append(os.add_dll_directory(str(library)))
            os.environ["PATH"] = str(library) + os.pathsep + os.environ.get("PATH", "")


def decode_wav(encoded):
    import numpy as np
    if not isinstance(encoded, str) or not 60 <= len(encoded) <= 5_400_000:
        raise ValueError("录音须为 44 字节至 4 MB 的 WAV")
    try:
        data = base64.b64decode(encoded, validate=True)
    except Exception:
        raise ValueError("录音 Base64 格式无效") from None
    if len(data) > 4_000_000:
        raise ValueError("录音超出 4 MB 限制")
    with wave.open(io.BytesIO(data), "rb") as wav:
        sample_rate, channels, width, frames = wav.getframerate(), wav.getnchannels(), wav.getsampwidth(), wav.getnframes()
        if channels not in {1, 2} or width != 2 or not 8000 <= sample_rate <= 48000:
            raise ValueError("仅支持 8–48 kHz 单/双声道 PCM16 WAV")
        if not sample_rate * .1 <= frames <= sample_rate * 60:
            raise ValueError("录音时长须为 0.1–60 秒")
        audio = np.frombuffer(wav.readframes(frames), dtype="<i2").astype(np.float32) / 32768
        if audio.size != frames * channels:
            raise ValueError("WAV 数据不完整")
        audio = audio.reshape(-1, channels).mean(axis=1)
    if sample_rate != 16000:
        from scipy.signal import resample_poly
        divisor = math.gcd(sample_rate, 16000)
        audio = resample_poly(audio, 16000 // divisor, sample_rate // divisor).astype(np.float32)
    return audio, hashlib.sha256(data).hexdigest()


class OfflineAsr:
    def __init__(self, root):
        import sherpa_onnx
        self.root = root
        self.model = root / "data/models/asr/SenseVoiceSmall"
        self.vad_path = root / "data/models/asr/silero-vad.onnx"
        self.recognizer = None
        self.language = None
        self.loaded_seconds = None
        self.streaming_vad = None
        for name in ["model.int8.onnx", "tokens.txt"]:
            if not (self.model / name).is_file():
                raise FileNotFoundError("SenseVoiceSmall 模型缺失，请运行本地依赖部署脚本")
        if not self.vad_path.is_file():
            raise FileNotFoundError("Silero VAD 模型缺失")
        self.version = sherpa_onnx.__version__
        if self.version != "1.13.8":
            raise RuntimeError("未验证的 Sherpa-ONNX 版本，请恢复锁定依赖 1.13.8")
        expected = {self.model / "model.int8.onnx": "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51", self.model / "tokens.txt": "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc", self.vad_path: "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6"}
        for path, checksum in expected.items():
            digest = hashlib.sha256()
            with path.open("rb") as file:
                for block in iter(lambda: file.read(4*1024*1024), b""):
                    digest.update(block)
            if digest.hexdigest() != checksum:
                raise ValueError("本地识别模型校验失败，请重新运行 setup-asr.ps1")

    def health(self):
        return {"status": "ready", "pid": os.getpid(), "model": "SenseVoiceSmall-2024-07-17-int8", "revision": "2365baeacb507f821a0c8120fcee3d484dba7a07", "provider": "cpu", "languages": ["zh", "ja", "auto"], "language": self.language, "loaded": self.recognizer is not None, "vad": "silero-vad-sherpa", "sherpaOnnx": self.version, "audioStorage": "memory-only"}

    def load(self, language):
        import sherpa_onnx
        if language not in {"zh", "ja", "auto"}:
            raise ValueError("本地识别语言须为 zh、ja 或 auto")
        if self.recognizer is not None and self.language == language:
            return
        self.recognizer = None
        gc.collect()
        start = time.perf_counter()
        self.recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(model=str(self.model / "model.int8.onnx"), tokens=str(self.model / "tokens.txt"), num_threads=2, sample_rate=16000, feature_dim=80, language=language, use_itn=True, debug=False, provider="cpu")
        self.language = language
        self.loaded_seconds = time.perf_counter() - start

    def create_vad(self):
        import sherpa_onnx
        config = sherpa_onnx.VadModelConfig()
        config.silero_vad.model = str(self.vad_path)
        config.silero_vad.threshold = .5
        config.silero_vad.min_silence_duration = .35
        config.silero_vad.min_speech_duration = .18
        config.silero_vad.window_size = 512
        config.silero_vad.max_speech_duration = 25
        config.sample_rate = 16000
        config.num_threads = 1
        config.provider = "cpu"
        return sherpa_onnx.VoiceActivityDetector(config, buffer_size_in_seconds=65)

    def vad(self, audio):
        import numpy as np
        detector = self.create_vad()
        for start in range(0, len(audio), 512):
            piece = audio[start:start+512]
            if len(piece) < 512:
                piece = np.pad(piece, (0, 512-len(piece)))
            detector.accept_waveform(piece)
        detector.flush()
        intervals = []
        while not detector.empty():
            segment = detector.front
            end = min(len(audio), int(segment.start + len(segment.samples)))
            begin = max(0, int(segment.start))
            if end > begin:
                intervals.append({"start": begin / 16000, "end": end / 16000})
            detector.pop()
        return intervals

    def vad_frame(self, encoded):
        import numpy as np
        if not isinstance(encoded, str) or len(encoded) > 43000:
            raise ValueError("VAD 输入须为不超过 1 秒的 16 kHz PCM16")
        raw = base64.b64decode(encoded, validate=True)
        if not 1024 <= len(raw) <= 32000 or len(raw) % 2:
            raise ValueError("VAD 输入须为 512–16000 采样的 16 kHz PCM16")
        samples = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768
        if self.streaming_vad is None:
            self.streaming_vad = self.create_vad()
        self.streaming_vad.accept_waveform(samples)
        utterances = []
        while not self.streaming_vad.empty():
            segment = self.streaming_vad.front
            pcm = (np.clip(np.asarray(segment.samples), -1, 1) * 32767).astype("<i2").tobytes()
            output = io.BytesIO()
            with wave.open(output, "wb") as wav:
                wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(16000); wav.writeframes(pcm)
            utterances.append({"audioBase64": base64.b64encode(output.getvalue()).decode("ascii"), "duration": len(pcm)/32000, "startSeconds": segment.start/16000})
            self.streaming_vad.pop()
        return {"speechDetected": self.streaming_vad.is_speech_detected(), "utterances": utterances, "energyRms": float(np.sqrt(np.mean(samples*samples)))}

    def transcribe(self, audio, language):
        start = time.perf_counter()
        intervals = self.vad(audio)
        if not intervals:
            raise ValueError("未检测到有效人声，请靠近麦克风后重试")
        self.load(language)
        pieces = []
        # The VAD bounds utterances. Include 100 ms context on each side, never duplicate speech.
        for interval in intervals:
            begin, end = max(0, int(interval["start"]*16000)-1600), min(len(audio), int(interval["end"]*16000)+1600)
            stream = self.recognizer.create_stream()
            stream.accept_waveform(16000, audio[begin:end])
            self.recognizer.decode_stream(stream)
            text = re.sub(r"<\|[^|]*\|>", "", stream.result.text).strip()
            if text:
                pieces.append(text)
        text = "".join(pieces).strip()
        if not text:
            raise ValueError("识别结果为空，请重新录音")
        return {"text": text, "language": language, "duration": len(audio)/16000, "elapsedSeconds": time.perf_counter()-start, "speechSegments": intervals, "provider": "cpu", "model": "SenseVoiceSmall", "audioStorage": "memory-only"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=HERE.parents[1])
    args = parser.parse_args()
    root = args.root.resolve()
    configure(root)
    import numpy
    import sherpa_onnx
    engine = OfflineAsr(root)
    def emit(value):
        PROTOCOL.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
        PROTOCOL.flush()
    emit({"id": None, "event": "ready", "data": engine.health()})
    while True:
        line = sys.stdin.readline(5_500_001)
        if not line:
            break
        request = {}
        try:
            if len(line) > 5_500_000:
                raise ValueError("识别请求超过大小限制")
            request = json.loads(line)
            line = ""
            identifier, method, params = request["id"], request["method"], request.get("params") or {}
            if not isinstance(identifier, str) or len(identifier) > 100 or not isinstance(params, dict):
                raise ValueError("识别请求格式无效")
            if method == "health":
                result = engine.health()
            elif method == "transcribe":
                audio, _ = decode_wav(params["audioBase64"])
                result = engine.transcribe(audio, params.get("language", "auto"))
                del audio
            elif method == "vad":
                audio, _ = decode_wav(params["audioBase64"])
                result = {"segments": engine.vad(audio), "duration": len(audio)/16000}
                del audio
            elif method == "vad_frame":
                result = engine.vad_frame(params["pcm16Base64"])
            elif method == "vad_reset":
                engine.streaming_vad = None
                result = {"reset": True}
            elif method == "shutdown":
                emit({"id": identifier, "ok": True, "result": {"stopping": True}})
                break
            else:
                raise ValueError("未知本地识别方法")
            emit({"id": identifier, "ok": True, "result": result})
        except Exception as exc:
            # Never log incoming base64, recognized text, or raw audio.
            print("[asr] " + type(exc).__name__, file=sys.stderr, flush=True)
            emit({"id": request.get("id") if isinstance(request, dict) else None, "ok": False, "error": {"code": "asr_error", "message": str(exc)[:300]}})
        finally:
            # Do not retain the last base64 recording while waiting for the next command.
            if isinstance(request, dict):
                request.clear()
            line = ""
            params = result = None


if __name__ == "__main__":
    main()
