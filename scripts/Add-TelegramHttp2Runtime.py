"""Add the exact reviewed HTTP/2 wheels to a staged Telegram runtime."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
from urllib.parse import urlsplit
from urllib.request import urlopen
from zipfile import ZipFile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", required=True, type=Path)
    parser.add_argument("--cache", required=True, type=Path)
    args = parser.parse_args()
    runtime = args.runtime.resolve()
    packages = runtime / "Lib" / "site-packages"
    lock_path = runtime / "runtime.lock.json"
    lock = json.loads(lock_path.read_text(encoding="utf-8-sig"))
    records = {item["file"]: {"file": item["file"], "sha256": item["sha256"]} for item in lock["wheels"]}
    wheels = json.loads(Path(__file__).with_name("TelegramHttp2Wheels.json").read_text())
    args.cache.mkdir(parents=True, exist_ok=True)
    for item in wheels:
        name = item["file"]
        url = urlsplit(item["url"])
        if Path(name).name != name or not name.endswith(".whl"):
            raise ValueError("Unsafe wheel filename")
        if url.scheme != "https" or url.netloc != "files.pythonhosted.org":
            raise ValueError("Unexpected wheel host")
        wheel = args.cache / name
        if not wheel.exists():
            with urlopen(item["url"], timeout=60) as response:
                data = response.read(16 * 1024 * 1024 + 1)
            if len(data) > 16 * 1024 * 1024:
                raise ValueError("Wheel size limit exceeded")
            wheel.write_bytes(data)
        if hashlib.sha256(wheel.read_bytes()).hexdigest() != item["sha256"]:
            raise ValueError("Reviewed wheel SHA256 mismatch: " + name)
        with ZipFile(wheel) as archive:
            for member in archive.infolist():
                path = PurePosixPath(member.filename)
                if path.is_absolute() or ".." in path.parts or "\\" in member.filename or ":" in member.filename:
                    raise ValueError("Unsafe wheel entry")
            archive.extractall(packages)
        records[name] = {"file": name, "sha256": item["sha256"]}
    lock["wheels"] = [records[name] for name in sorted(records)]
    lock_path.write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8")
    print("Reviewed Telegram HTTP/2 dependency wheels installed:", len(wheels))


if __name__ == "__main__":
    main()
