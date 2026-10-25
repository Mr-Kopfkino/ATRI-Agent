"""Create the audited inference-only Python source tree. Never changes upstream files."""
from __future__ import annotations

import ast
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UPSTREAM = ROOT / "GPT-SoVITS-v2pro-20261006"
SOURCE = UPSTREAM / "GPT_SoVITS"
TARGET = ROOT / "runtime/voice/vendor"
HEADER = "# Derived from GPT-SoVITS (MIT), Copyright (c) 2024 RVC-Boss.\n# Inference-only extraction; see LICENSE.GPT-SoVITS and source-manifest.json.\n"


def write(relative: str, content: str):
    p = TARGET / relative
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding="utf-8")


class Specialize(ast.NodeTransformer):
    """The exported vocoder is deliberately only the v2ProPlus architecture."""
    def visit_If(self, node):
        expression = ast.unparse(node.test)
        true = expression in {"self.is_v2pro", "self.is_v2pro == True", "self.version in v2pro_set"}
        false = expression in {"self.version == 'v1'", "self.version == 'v2'"}
        if true or false:
            result = []
            for child in node.body if true else node.orelse:
                visited = self.visit(child)
                result.extend(visited if isinstance(visited, list) else [visited])
            return result
        return self.generic_visit(node)


def extract(relative, allowed=None, edit=None):
    raw = (SOURCE / relative).read_text(encoding="utf-8-sig")
    tree = ast.parse(raw)
    tree.body = [n for n in tree.body if not (isinstance(n, ast.If) and ast.unparse(n.test) == "__name__ == '__main__'")]
    if allowed:
        tree.body = [n for n in tree.body if not isinstance(n, (ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)) or n.name in allowed]
    if edit:
        tree = edit(tree)
    ast.fix_missing_locations(tree)
    # Preserve upstream copyright/license/reference comments, including third-party notices.
    notices = []
    for line in raw.splitlines():
        if line.startswith("#") or not line.strip():
            notices.append(line)
        else:
            break
    write(relative, HEADER + "\n".join(notices) + "\n" + ast.unparse(tree) + "\n")
    return {"path": relative, "upstreamSha256": hashlib.sha256(raw.encode()).hexdigest(), "mode": "AST inference extraction"}


def vocoder(tree):
    tree.body = [n for n in tree.body if not (isinstance(n, ast.ImportFrom) and (n.module == "f5_tts.model" or n.module == "text" and any(a.name == "symbols" for a in n.names)))]
    for n in tree.body:
        if isinstance(n, ast.Assign) and any(ast.unparse(t) == "v2pro_set" for t in n.targets):
            n.value = ast.parse("{'v2ProPlus'}", mode="eval").body
        if isinstance(n, ast.ClassDef) and n.name == "SynthesizerTrn":
            n.body = [m for m in n.body if isinstance(m, ast.FunctionDef) and m.name in {"__init__", "decode", "extract_latent"}]
            init = n.body[0]
            init.body = [m for m in init.body if not (isinstance(m, ast.Assign) and any(ast.unparse(t) == "self.enc_q" for t in m.targets))]
            init.body.insert(1, ast.parse("if version != 'v2ProPlus': raise ValueError('Only v2ProPlus is supported')").body[0])
        if isinstance(n, ast.ClassDef):
            for function in n.body:
                if isinstance(function, ast.FunctionDef):
                    args = function.args.args[-len(function.args.defaults):] if function.args.defaults else []
                    for i, arg in enumerate(args):
                        if arg.arg == "version":
                            function.args.defaults[i] = ast.Constant("v2ProPlus")
    return Specialize().visit(tree)


def semantic(tree):
    tree.body = [n for n in tree.body if not (isinstance(n, ast.ImportFrom) and n.module == "torchmetrics.classification")]
    for n in tree.body:
        if isinstance(n, ast.ImportFrom) and n.module == "AR.models.utils":
            n.names = [a for a in n.names if a.name in {"make_pad_mask", "make_pad_mask_left", "sample", "topk_sampling"}]
        if isinstance(n, ast.ClassDef) and n.name == "Text2SemanticDecoder":
            n.body = [m for m in n.body if not isinstance(m, ast.FunctionDef) or m.name in {"__init__", "infer_panel", "infer_panel_naive"}]
            init = next(m for m in n.body if isinstance(m, ast.FunctionDef) and m.name == "__init__")
            init.body = [m for m in init.body if not (isinstance(m, ast.Assign) and any(ast.unparse(t) in {"self.loss_fct", "self.ar_accuracy_metric"} for t in m.targets))]
            naive = next(m for m in n.body if isinstance(m, ast.FunctionDef) and m.name == "infer_panel_naive")
            token_loop = next(m for m in naive.body if isinstance(m, ast.For))
            token_loop.body.insert(0, ast.parse("if getattr(self, 'cancel_check', lambda: False)(): raise InterruptedError('Speech request cancelled')").body[0])
    return tree


def main():
    TARGET.mkdir(parents=True, exist_ok=True)
    manifest = []
    manifest.append(extract("module/models.py", {"TextEncoder", "ResidualCouplingBlock", "Generator", "SynthesizerTrn"}, vocoder))
    manifest.append(extract("AR/models/t2s_model.py", edit=semantic))
    for relative in [
        "module/commons.py", "module/modules.py", "module/attentions.py", "module/transforms.py",
        "module/mrte_model.py", "module/quantize.py", "module/core_vq.py", "module/distrib.py", "module/ddp_utils.py",
        "AR/models/utils.py", "AR/modules/embedding.py", "AR/modules/transformer.py", "AR/modules/scaling.py",
        "AR/modules/activation.py", "AR/modules/patched_mha_with_cache.py", "text/symbols2.py", "text/symbols.py",
        "text/japanese.py", "eres2net/ERes2NetV2.py", "eres2net/fusion.py", "eres2net/pooling_layers.py", "eres2net/kaldi.py",
    ]:
        manifest.append(extract(relative))
    for package in ["module", "AR", "AR/models", "AR/modules", "text", "eres2net"]:
        write(package + "/__init__.py", "")
    # Only the v2 phone inventory and Japanese frontend are public in this distribution.
    write("text/__init__.py", "from .symbols2 import symbols\n_symbol_to_id = {s:i for i,s in enumerate(symbols)}\ndef cleaned_text_to_sequence(phones, version='v2ProPlus'):\n    if version not in ('v2', 'v2ProPlus'): raise ValueError('Unsupported phone inventory')\n    return [_symbol_to_id.get(p, _symbol_to_id['UNK']) for p in phones]\n")
    # HParams is a data-only allowlisted compatibility class for historical checkpoints.
    raw = (SOURCE / "utils.py").read_text(encoding="utf-8-sig")
    hp = next(n for n in ast.parse(raw).body if isinstance(n, ast.ClassDef) and n.name == "HParams")
    write("utils.py", HEADER + ast.unparse(hp) + "\n")
    userdic = SOURCE / "text/ja_userdic/userdict.csv"
    if userdic.exists():
        write("text/ja_userdic/userdict.csv", userdic.read_text(encoding="utf-8-sig"))
    shutil.copyfile(UPSTREAM / "LICENSE", ROOT / "runtime/voice/LICENSE.GPT-SoVITS")
    apache = UPSTREAM / "runtime/Lib/site-packages/cryptography-49.0.0.dist-info/licenses/LICENSE.APACHE"
    if apache.exists():
        shutil.copyfile(apache, ROOT / "runtime/voice/LICENSE.APACHE-2.0")
    (ROOT / "runtime/voice/source-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    # Hardlinks save space, but inference only ever opens these frozen files for reading.
    # Never write through a model path or update a link in place.
    models = ROOT / "models/voice/AtriTurbo"
    models.mkdir(parents=True, exist_ok=True)
    for source in [UPSTREAM / "GPT_weights_v2ProPlus/AtriTurbo-e15.ckpt", UPSTREAM / "SoVITS_weights_v2ProPlus/AtriTurbo_e12_s2448.pth"]:
        destination = models / source.name
        if not destination.exists():
            import os
            try:
                os.link(source, destination)
            except OSError:
                shutil.copyfile(source, destination)
    print(json.dumps({"files": len(manifest), "destination": str(TARGET)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
