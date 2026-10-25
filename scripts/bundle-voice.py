"""Copy an isolated inference runtime from the measured import closure, never delete originals."""
from __future__ import annotations
import importlib.metadata
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "GPT-SoVITS-v2pro-20261006/runtime"
SITE = SOURCE / "Lib/site-packages"
TARGET = ROOT / "runtime/voice/python"
TARGET_SITE = TARGET / "Lib/site-packages"


def copy(source: Path, target: Path):
    if source.is_dir():
        shutil.copytree(source, target, dirs_exist_ok=True, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "tests", "test"))
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists() or target.stat().st_size != source.stat().st_size:
            shutil.copyfile(source, target)


def main():
    evidence = json.loads((ROOT / "data/verification/voice/report.json").read_text(encoding="utf-8"))
    if evidence.get("passed") is not True:
        raise ValueError("A successful actual inference report is required before runtime packaging")
    packages = set()
    for raw in evidence["diagnostics"]["importedModules"]:
        path = Path(raw)
        if path.is_relative_to(SITE):
            packages.add(path.relative_to(SITE).parts[0])
        elif path.is_relative_to(TARGET_SITE):
            packages.add(path.relative_to(TARGET_SITE).parts[0])
    # Copy metadata and every top-level runtime entry belonging to actually used distributions.
    distributions = []
    for dist in importlib.metadata.distributions(path=[str(SITE)]):
        files = list(dist.files or [])
        roots = {f.parts[0] for f in files if f.parts and f.parts[0] not in {".", ".."}}
        if roots & packages:
            distributions.append({"name": dist.metadata.get("Name"), "version": dist.version})
            packages.update(p for p in roots if not p.endswith(".data") and (SITE / p).exists())
    for name in sorted(packages):
        source = SITE / name
        if source.exists():
            copy(source, TARGET_SITE / name)
        for companion in SITE.glob(name + ".libs"):
            copy(companion, TARGET_SITE / companion.name)
    for source in SOURCE.iterdir():
        if source.is_file() and (source.suffix.lower() in {".dll", ".pyd", ".zip", ".txt"} or source.name == "python.exe"):
            copy(source, TARGET / source.name)
    (TARGET / "python312._pth").write_text("python312.zip\n.\nLib\\site-packages\n", encoding="utf-8")
    pretrained = ROOT / "GPT-SoVITS-v2pro-20261006/GPT_SoVITS/pretrained_models"
    for relative in ["chinese-hubert-base/config.json", "chinese-hubert-base/pytorch_model.bin", "sv/pretrained_eres2netv2w24s4ep4.ckpt"]:
        copy(pretrained / relative, ROOT / "runtime/voice/pretrained" / relative)
    total_bytes = sum(p.stat().st_size for p in TARGET.rglob("*") if p.is_file())
    manifest = {"source": str(SOURCE.relative_to(ROOT)), "selection": "Actual load + short/long Japanese synth + cancellation import closure", "excluded": ["training entry points", "Gradio", "WebUI", "pip", "other-version pretrained models", "training datasets", "Chinese BERT"], "topLevelEntries": sorted(packages), "distributions": sorted(distributions, key=lambda d: d["name"] or ""), "bytes": total_bytes}
    (ROOT / "runtime/voice/runtime-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"runtime": str(TARGET), "bytes": total_bytes, "entries": len(packages), "distributions": len(distributions)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
