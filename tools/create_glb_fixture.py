"""Create explicitly synthetic mapping metadata; never infer engineering evidence."""
import argparse
import hashlib
import json
import struct
from pathlib import Path


def create_fixture(path: Path, root: int):
    raw = path.read_bytes()
    magic, version, total, size, kind = struct.unpack_from("<5I", raw)
    if (magic, version, total, kind) != (0x46546C67, 2, len(raw), 0x4E4F534A):
        raise ValueError("Expected a complete GLB 2 file")
    gltf = json.loads(raw[20:20 + size])
    nodes = gltf["nodes"]
    entries = []
    visited = set()

    def visit(index, parent):
        if index in visited:
            raise ValueError("GLB node hierarchy is not a tree")
        visited.add(index)
        node = nodes[index]
        children = node.get("children", [])
        entries.append({
            "id": f"NODE_{index:04}", "name": node.get("name", f"Node {index}"),
            "type": "assembly" if children else "part", "glbNodeIndex": index,
            "parentId": parent, "childIds": [f"NODE_{child:04}" for child in children],
            "material": {"assigned": None, "name": None, "density": None},
            "mass": None, "volume": None, "definitionStatus": "unknown",
            "customProperties": {},
        })
        for child in children:
            visit(child, f"NODE_{index:04}")

    visit(root, None)
    return {
        "schemaVersion": "1.0",
        "project": {"id": "SYNTHETIC_GLB_FIXTURE", "name": path.stem,
                    "rootObjectId": f"NODE_{root:04}",
                    "units": {"length": "m", "mass": "kg", "angle": "deg"}},
        "provenance": {"synthetic": True, "sourceGlbSha256": hashlib.sha256(raw).hexdigest(),
                       "warning": "Geometry mapping only. Object types are structural guesses, not CAD evidence."},
        "objects": entries, "mates": [], "interferences": [],
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("glb", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--root-node", type=int, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(create_fixture(args.glb, args.root_node), indent=2), encoding="utf-8")
