"""Read-only PSB v2/v3 structural reader used for local game asset inspection.

No decryption, extraction to arbitrary paths, or code execution. Format details
were checked against FreeMote's public PSB reader; see THIRD_PARTY_NOTICES.md.
"""
from pathlib import Path
import struct


class PsbError(ValueError):
    pass


class Psb:
    def __init__(self, source):
        self.data = Path(source).read_bytes() if isinstance(source, (str, Path)) else bytes(source)
        if len(self.data) < 40 or self.data[:4] != b"PSB\0":
            raise PsbError("Expected an unencrypted PSB container")
        self.version, flags = struct.unpack_from("<HH", self.data, 4)
        if self.version not in (2, 3) or flags:
            raise PsbError(f"Unsupported PSB version/flags: {self.version}/{flags}")
        (self.header_size, names, strings, self.string_data,
         chunks, lengths, self.chunk_data, entries) = struct.unpack_from("<8I", self.data, 8)
        for p in (names, strings, self.string_data, chunks, lengths, self.chunk_data, entries):
            if not 0 <= p <= len(self.data):
                raise PsbError("PSB header offset outside container")
        self.string_offsets, _ = self.array(strings)
        self.chunk_offsets, _ = self.array(chunks)
        self.chunk_lengths, _ = self.array(lengths)
        charset, p = self.array(names)
        trie, p = self.array(p)
        terminals, p = self.array(p)
        self.names = []
        for terminal in terminals:
            node = trie[terminal]
            chars = []
            seen = set()
            while node:
                if node in seen or len(chars) > 4096:
                    raise PsbError("Cyclic PSB name trie")
                seen.add(node)
                parent = trie[node]
                chars.append(node - charset[parent])
                node = parent
            self.names.append(bytes(reversed(chars)).decode("utf-8"))
        self._strings = {}
        self._active = set()
        self.root = self.value(entries)

    def uint(self, p, size):
        if size < 0 or size > 8 or p < 0 or p + size > len(self.data):
            raise PsbError("Invalid integer range")
        return int.from_bytes(self.data[p:p + size], "little")

    def array(self, p):
        count_size = self.uint(p, 1) - 0x0C
        if not 1 <= count_size <= 8:
            raise PsbError(f"Invalid PSB array marker at {p}")
        count = self.uint(p + 1, count_size)
        p += count_size + 1
        item_size = self.uint(p, 1) - 0x0C
        p += 1
        if not 1 <= item_size <= 8 or count > 2000000 or p + count * item_size > len(self.data):
            raise PsbError("Invalid PSB array extent")
        return [self.uint(p + i * item_size, item_size) for i in range(count)], p + count * item_size

    def string(self, index):
        if index not in self._strings:
            p = self.string_data + self.string_offsets[index]
            end = self.data.find(b"\0", p)
            if end < p:
                raise PsbError("Unterminated PSB string")
            self._strings[index] = self.data[p:end].decode("utf-8")
        return self._strings[index]

    def value(self, p, depth=0):
        if depth > 96 or p in self._active:
            raise PsbError("Excessive or cyclic PSB nesting")
        kind = self.uint(p, 1)
        p += 1
        if kind in (0, 1):
            return None
        if kind in (2, 3):
            return kind == 3
        if kind == 4:
            return 0
        if 5 <= kind <= 12:
            size = kind - 4
            self.uint(p, size)
            return int.from_bytes(self.data[p:p + size], "little", signed=True)
        if 0x0D <= kind <= 0x14:
            return self.array(p - 1)[0]
        if 0x15 <= kind <= 0x18:
            return self.string(self.uint(p, kind - 0x14))
        if 0x19 <= kind <= 0x1C:
            index = self.uint(p, kind - 0x18)
            return {"$resource": index, "offset": self.chunk_data + self.chunk_offsets[index],
                    "length": self.chunk_lengths[index]}
        if kind == 0x1D:
            return 0.0
        if kind in (0x1E, 0x1F):
            return struct.unpack_from("<f" if kind == 0x1E else "<d", self.data, p)[0]
        if kind in (0x20, 0x21):
            object_address = p - 1
            self._active.add(object_address)
            try:
                if kind == 0x21:
                    keys, p = self.array(p)
                offsets, p = self.array(p)
                values = [self.value(p + off, depth + 1) for off in offsets]
                if kind == 0x20:
                    return values
                if len(keys) != len(values) or len(set(keys)) != len(keys):
                    raise PsbError("Invalid PSB object key count")
                return {self.names[k]: v for k, v in zip(keys, values)}
            finally:
                self._active.remove(object_address)
        raise PsbError(f"Unsupported PSB value 0x{kind:02x} at {p - 1}")

    def resource(self, index):
        start = self.chunk_data + self.chunk_offsets[index]
        end = start + self.chunk_lengths[index]
        if start < 0 or end > len(self.data):
            raise PsbError("Resource outside container")
        return self.data[start:end]


def load(path):
    return Psb(path).root


if __name__ == "__main__":
    import argparse
    import json
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = load(args.source)
    value = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(value + "\n", encoding="utf-8")
    else:
        print(value)
