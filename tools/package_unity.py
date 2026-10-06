"""Create a deterministic Unity Package Manager archive, including the sold package's sample."""
from pathlib import Path
import gzip
import io
import json
import tarfile

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "Packages/com.dhmp.networking"


def main():
    manifest = json.loads((PACKAGE / "package.json").read_text())
    output = ROOT / "artifacts/unity" / f"{manifest['name']}-{manifest['version']}.tgz"
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as zipped:
            with tarfile.open(fileobj=zipped, mode="w|") as archive:
                for path in sorted(PACKAGE.rglob("*")):
                    if not path.is_file():
                        continue
                    relative = path.relative_to(PACKAGE)
                    if any(part in {"bin", "obj", "__pycache__"} for part in relative.parts):
                        continue
                    data = path.read_bytes()
                    entry = tarfile.TarInfo("package/" + relative.as_posix())
                    entry.size = len(data)
                    entry.mode = 0o755 if path.suffix == ".sh" else 0o644
                    entry.mtime = 0
                    archive.addfile(entry, io.BytesIO(data))
    print(output)


if __name__ == "__main__":
    main()
