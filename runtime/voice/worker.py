"""Parent-owned JSON Lines worker; exposes no sockets and no arbitrary file operations."""
from __future__ import annotations

import argparse
import io
import importlib.abc
import importlib.machinery
import importlib.util
import json
import os
import queue
import sys
import threading
import time
import traceback
from pathlib import Path

VOICE_SOURCE_FILES = {
    "engine": Path(__file__).resolve().parent / "engine.py",
    "completeness": Path(__file__).resolve().parent / "completeness.py",
    "AR.models.t2s_model": Path(__file__).resolve().parent / "vendor/AR/models/t2s_model.py",
}


class _VoiceSourceLoader(importlib.machinery.SourceFileLoader):
    """Compile policy source bytes directly, without reading or writing pyc."""
    def get_code(self, fullname):
        source_path = self.get_filename(fullname)
        return self.source_to_code(self.get_data(source_path), source_path)


class _VoiceSourceFinder(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        source = VOICE_SOURCE_FILES.get(fullname)
        if source is None:
            return None
        return importlib.util.spec_from_file_location(fullname, source,
            loader=_VoiceSourceLoader(fullname, str(source)))


def _install_voice_source_imports():
    # Existing sys.modules entries retain their normal identity. Only these
    # three policy modules bypass stale caches; dependencies keep normal imports.
    if not any(isinstance(finder, _VoiceSourceFinder) for finder in sys.meta_path):
        sys.meta_path.insert(0, _VoiceSourceFinder())


_install_voice_source_imports()

# Native OpenJTalk/MeCab writes to fd 1, bypassing Python's sys.stdout.
# Keep a duplicate exclusively for JSON and direct the actual stdout fd to stderr.
PROTOCOL = io.TextIOWrapper(os.fdopen(os.dup(sys.stdout.fileno()), "wb", buffering=0), encoding="utf-8", line_buffering=True)
os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
sys.stdout = sys.stderr
sys.stderr.reconfigure(encoding="utf-8", errors="replace")
PROTOCOL.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
sys.stdin.reconfigure(encoding="utf-8", errors="strict")
sys.path.insert(0, str(Path(__file__).resolve().parent))
from engine import VoiceEngine, SpeechCompletenessError


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--device", choices=["auto", "cuda", "cpu"], default="auto")
    args = parser.parse_args()
    import faulthandler
    faulthandler.enable(file=sys.stderr)
    if os.environ.get("ATRI_VOICE_TRACE") == "1":
        faulthandler.dump_traceback_later(15, repeat=True, file=sys.stderr)
    lock = threading.Lock()
    engine = VoiceEngine(args.root, args.device)
    # Windows NumPy/OpenBLAS DLL initialization can wait indefinitely when the
    # control thread is already blocked in a redirected stdin ReadFile. Initialize
    # native runtimes on the main thread before starting the control reader.
    import numpy
    import torch
    tasks = queue.Queue(maxsize=2)
    cancels = {}
    active = set()
    stopping = threading.Event()

    def input_lines():
        if os.name != "nt":
            yield from sys.stdin
            return
        # A blocking CRT stdin reader on another thread can deadlock NumPy and
        # other native DLL initializers. Poll the private anonymous pipe, then
        # read only bytes already available. This also permits prompt shutdown.
        import ctypes
        import msvcrt
        from ctypes import wintypes
        peek = ctypes.windll.kernel32.PeekNamedPipe
        peek.argtypes = [wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD, wintypes.LPVOID, ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
        peek.restype = wintypes.BOOL
        handle = msvcrt.get_osfhandle(sys.stdin.fileno())
        pending = b""
        while not stopping.is_set():
            available = wintypes.DWORD()
            if not peek(handle, None, 0, None, ctypes.byref(available), None):
                break
            if not available.value:
                time.sleep(.025)
                continue
            pending += os.read(sys.stdin.fileno(), min(available.value, 100001))
            if len(pending) > 100000 and b"\n" not in pending:
                raise ValueError("Protocol line exceeds 100 KB")
            while b"\n" in pending:
                raw, pending = pending.split(b"\n", 1)
                yield raw.decode("utf-8")

    def emit(message):
        with lock:
            PROTOCOL.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
            PROTOCOL.flush()

    def failure(identifier, error, code="voice_error"):
        detail = {"code": code, "message": str(error)}
        if isinstance(error, SpeechCompletenessError):
            detail.update(code="incomplete_speech", diagnostics=error.diagnostics)
        emit({"id": identifier, "ok": False, "error": detail})

    def reader():
        try:
            for line in input_lines():
                request = {}
                try:
                    if len(line) > 100000:
                        raise ValueError("Protocol line exceeds 100 KB")
                    request = json.loads(line)
                    identifier, method, params = request["id"], request["method"], request.get("params") or {}
                    if not isinstance(identifier, str) or not 1 <= len(identifier) <= 100 or not isinstance(params, dict):
                        raise ValueError("Invalid request envelope")
                    if method == "health":
                        emit({"id": identifier, "ok": True, "result": engine.health()})
                    elif method == "cancel":
                        request_id = params["requestId"]
                        if request_id in cancels:
                            cancels[request_id].set()
                        emit({"id": identifier, "ok": True, "result": {"cancelled": request_id}})
                    elif method == "shutdown":
                        stopping.set()
                        for event in list(cancels.values()):
                            event.set()
                        emit({"id": identifier, "ok": True, "result": {"stopping": True}})
                        break
                    elif method in {"models", "load", "synthesize", "release", "diagnostics"}:
                        event = None
                        if method == "synthesize":
                            request_id = params.get("requestId", identifier)
                            if request_id in cancels:
                                raise ValueError("Duplicate requestId")
                            event = threading.Event()
                            cancels[request_id] = event
                        try:
                            tasks.put_nowait((identifier, method, params, event))
                        except queue.Full:
                            if method == "synthesize":
                                cancels.pop(params.get("requestId", identifier), None)
                            failure(identifier, "Voice queue is full; wait or cancel the current response", "busy")
                    else:
                        raise ValueError("Unknown voice method")
                except Exception as exc:
                    failure(request.get("id") if isinstance(request, dict) else None, exc, "invalid_request")
        except (BrokenPipeError, UnicodeError):
            pass
        finally:
            stopping.set()
            for event in list(cancels.values()):
                event.set()

    emit({"id": None, "event": "ready", "data": engine.health()})
    reader_thread = threading.Thread(target=reader, name="voice-control", daemon=True)
    reader_thread.start()
    try:
        while not stopping.is_set():
            # Main-thread ownership prevents unloading during a generation call.
            if engine.verifier is not None:
                try:
                    engine.verifier.release_idle()
                except Exception:
                    traceback.print_exc(file=sys.stderr)
                    stopping.set()
                    break
            try:
                identifier, method, params, event = tasks.get(timeout=.15)
            except queue.Empty:
                continue
            request_id = params.get("requestId", identifier)
            try:
                if method == "models":
                    result = {"models": engine.scan_models()}
                elif method == "load":
                    print("[voice] Loading registered v2ProPlus model", file=sys.stderr, flush=True)
                    result = engine.load(params["modelId"])
                elif method == "synthesize":
                    active.add(request_id)
                    segments = []
                    for segment in engine.synthesize(params["text"], params.get("emotion", "neutral"), request_id, event, params.get("sessionId")):
                        if event.is_set() or stopping.is_set():
                            raise InterruptedError("Speech request cancelled")
                        segments.append(segment)
                        emit({"id": identifier, "event": "segment", "data": segment})
                    if event.is_set():
                        raise InterruptedError("Speech request cancelled")
                    result = {"requestId": request_id, "segments": segments, "modelId": engine.current}
                elif method == "release":
                    if request_id in active:
                        raise ValueError("Cannot release an active speech request")
                    result = engine.release(request_id)
                else:
                    import importlib.metadata
                    import torch
                    result = {**engine.health(), "python": sys.version, "torch": torch.__version__, "cuda": torch.cuda.is_available(), "gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None, "gpuAllocatedBytes": torch.cuda.memory_allocated() if torch.cuda.is_available() else 0, "gpuPeakAllocatedBytes": torch.cuda.max_memory_allocated() if torch.cuda.is_available() else 0, "gpuReservedBytes": torch.cuda.memory_reserved() if torch.cuda.is_available() else 0, "dependencies": {name: importlib.metadata.version(name) for name in ["transformers", "numpy", "soundfile", "pyopenjtalk", "torchaudio", "einops"]}, "importedModules": sorted({str(Path(module.__file__).resolve()) for module in sys.modules.values() if getattr(module, "__file__", None)})}
                emit({"id": identifier, "ok": True, "result": result})
            except InterruptedError as exc:
                try:
                    engine.release(request_id)
                except OSError:
                    # Windows may still hold a player handle; parent releases after stopping playback.
                    pass
                failure(identifier, exc, "cancelled")
            except Exception as exc:
                traceback.print_exc(file=sys.stderr)
                if method == "synthesize":
                    try:
                        engine.release(request_id)
                    except Exception:
                        pass
                failure(identifier, exc)
            finally:
                active.discard(request_id)
                if method == "synthesize":
                    cancels.pop(request_id, None)
                tasks.task_done()
        # Queued work cannot leak into the next application session.
        while not tasks.empty():
            identifier, _, _, _ = tasks.get_nowait()
            failure(identifier, "Voice worker is shutting down", "cancelled")
    finally:
        try:
            engine._free_pair() if engine.current else None
        finally:
            if engine.verifier is not None:
                engine.verifier.close()


if __name__ == "__main__":
    main()
