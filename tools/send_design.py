"""Send a matching GLB/JSON pair to CAD Vision's local-network test receiver."""
import argparse
import http.client
import io
import json
from pathlib import Path
import sys
import zipfile


def send(host, port, glb, metadata, timeout=130):
    json.loads(metadata.read_text(encoding="utf-8-sig"))
    with io.BytesIO() as data:
        with zipfile.ZipFile(data, "w", compression=zipfile.ZIP_STORED) as archive:
            archive.write(glb, "model.glb")
            archive.write(metadata, "metadata.json")
        connection = http.client.HTTPConnection(host, port, timeout=timeout)
        try:
            connection.request("POST", "/design", data.getvalue(), {"Content-Type": "application/zip"})
            response = connection.getresponse()
            result = json.loads(response.read().decode("utf-8"))
            print(json.dumps(result, indent=2))
            return 0 if response.status == 200 and result.get("success") is True else 1
        finally:
            connection.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--glb", required=True, type=Path)
    parser.add_argument("--metadata", required=True, type=Path)
    parser.add_argument("--quest", default="127.0.0.1", help="Receiver IP (default: local Unity Editor)")
    parser.add_argument("--port", type=int, default=8085)
    args = parser.parse_args()
    try:
        sys.exit(send(args.quest, args.port, args.glb, args.metadata))
    except (OSError, ValueError, http.client.HTTPException) as error:
        print(f"Design transfer failed: {error}", file=sys.stderr)
        sys.exit(1)
