"""Early-game scene replay from actual PSB records; no Kurisu runtime or AI call."""
import json
from pathlib import Path
import re
from psb import Psb


class SceneReplay:
    def __init__(self, root, state):
        self.root = Path(root)
        self.state = Path(state) / "vn"
        self.safe(self.state)
        self.state.mkdir(parents=True, exist_ok=True)
        self.scenes = {}
        for name in ("b101.ks.scn", "b102.ks.scn", "b103.ks.scn"):
            chapter = Psb(self.root / "assets/ATRIplay" / name).root
            for index, scene in enumerate(chapter["scenes"]):
                rows = [(i, row) for i, row in enumerate(scene.get("texts", [])) if row[2] is not None]
                if rows:
                    identifier = f"{name}:{index}"
                    self.scenes[identifier] = {"id": identifier, "chapter": name, "scene": index,
                        "label": scene.get("label", ""), "titleJa": scene.get("title", [""])[0],
                        "titleZh": (scene.get("title") or ["", "", ""])[2], "rows": rows}

    def safe(self, path):
        if not path.absolute().is_relative_to(self.root.resolve()):
            raise ValueError("Replay state path is outside the ATRI root")
        current = path
        while current != self.root:
            if current.exists() and (current.is_symlink() or getattr(current.stat(), "st_file_attributes", 0) & 0x400):
                raise ValueError("Replay state links are not allowed")
            current = current.parent

    def catalog(self):
        return [{key: value for key, value in scene.items() if key != "rows"} | {"count": len(scene["rows"])}
                for scene in self.scenes.values()]

    def step(self, args):
        session = str(args.get("sessionId", "default"))
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", session):
            raise ValueError("Invalid replay session id")
        path = self.state / (session + ".json")
        self.safe(path)
        current = json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {}
        identifier = str(args.get("sceneId") or current.get("sceneId") or next(iter(self.scenes)))
        if identifier not in self.scenes:
            raise ValueError("Scene is outside the registered early-game timeline")
        scene = self.scenes[identifier]
        position = int(args.get("position", 0 if identifier != current.get("sceneId") else current.get("position", 0)))
        delta = int(args.get("delta", 0))
        if delta not in (-1, 0, 1):
            raise ValueError("Replay step must be previous, current, or next")
        position = max(0, min(position + delta, len(scene["rows"]) - 1))
        source_index, row = scene["rows"][position]
        state = {"sceneId": identifier, "position": position}
        temporary = path.with_suffix(".tmp")
        self.safe(temporary)
        temporary.write_text(json.dumps(state), encoding="utf-8")
        temporary.replace(path)
        character = None
        environment = row[5] if len(row) > 5 and isinstance(row[5], dict) else {}
        for data in environment.get("data", []):
            if isinstance(data, list) and len(data) > 2 and data[0] == "tatr":
                item = data[2]
                image = item.get("redraw", {}).get("imageFile", {})
                if isinstance(image, dict):
                    options = image.get("options") or {}
                    character = {"identity": "アトリ", "dress": options.get("dress"), "pose": options.get("pose"),
                        "face": options.get("face"), "visible": item.get("showmode") not in (0, 2),
                        "positionName": item.get("redraw", {}).get("posName"), "actions": item.get("action", [])}
        return {**state, "count": len(scene["rows"]), "atStart": position == 0, "atEnd": position == len(scene["rows"]) - 1,
            "timelineId": "game_early_common", "speaker": row[0] or "narration", "displayAlias": row[1],
            "ja": row[2][0][1], "zh": row[2][2][1], "voiceIds": [v["voice"] for v in row[3] or []],
            "characterState": character,
            "source": f"assets/ATRIplay/{scene['chapter']}#scenes[{scene['scene']}].texts[{source_index}]",
            "note": "Original game data; separate from real user events and persona memory. Voice IDs are source metadata, not claimed audio paths."}
