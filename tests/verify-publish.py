"""Publish twice from an isolated checkout copy and reject secrets/unexpected artifacts."""
import hashlib
import json
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
LEGAL_FILES = ("LICENSE", "THIRD-PARTY-NOTICES.txt")


def verify(stage, marker):
    output = stage / "publish"
    files = {str(p.relative_to(output)) for p in output.rglob("*") if p.is_file()}
    expected = {"StockSentinel.Web", "StockSentinel.Web.pdb", "StockSentinel.Web.staticwebassets.endpoints.json",
                "libe_sqlite3.so", "appsettings.json", "appsettings.example.json"}
    for name in LEGAL_FILES:
        expected.add(name)
        assert (output / name).read_bytes() == (stage / name).read_bytes(), name
    packs = json.loads(subprocess.check_output([
        "dotnet", "msbuild", "StockSentinel.Web.csproj", "-nologo", "-target:ResolveFrameworkReferences",
        "-p:RuntimeIdentifier=linux-arm", "-p:SelfContained=true", "-getItem:ResolvedRuntimePack"
    ], cwd=stage))["Items"]["ResolvedRuntimePack"]
    assert {pack["FrameworkName"] for pack in packs} == {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}
    for pack in packs:
        directory = Path(pack["PackageDirectory"])
        for name in ("LICENSE.txt", "THIRD-PARTY-NOTICES.txt"):
            source, = (p for p in directory.iterdir() if p.name.lower() == name.lower())
            relative = "legal/" + pack["FrameworkName"] + "/" + name
            expected.add(relative)
            assert (output / relative).read_bytes() == source.read_bytes(), relative
    for source in (stage / "wwwroot").rglob("*"):
        if source.is_file():
            name = str(source.relative_to(stage))
            expected.add(name)
            if source.suffix in (".js", ".css", ".html", ".svg", ".webmanifest"):
                expected.update({name + ".br", name + ".gz"})
            assert hashlib.sha256(source.read_bytes()).digest() == hashlib.sha256((output / name).read_bytes()).digest(), name
    assert files == expected, {"unexpected": sorted(files - expected), "missing": sorted(expected - files)}
    assert (output / "appsettings.json").read_bytes() == (stage / "appsettings.example.json").read_bytes()
    for tree in (stage / "bin", output):
        for path in tree.rglob("*"):
            if not path.is_file():
                continue
            assert "data" not in path.relative_to(tree).parts, path
            with path.open("rb") as stream:
                tail = b""
                while chunk := stream.read(1024 * 1024):
                    block = tail + chunk
                    assert marker not in block, path
                    tail = block[-len(marker):]
    with (output / "StockSentinel.Web").open("rb") as stream:
        header = stream.read(20)
    assert header[:4] == b"\x7fELF" and header[4] == 1 and int.from_bytes(header[18:20], "little") == 40, "Expected 32-bit ARM ELF"
    return files


def main():
    with tempfile.TemporaryDirectory(prefix="stocksentinel-publish-check-") as directory:
        stage = Path(directory)
        tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).split(b"\0")
        # Explicit legal inputs also work before newly added files have been staged.
        tracked += [name.encode() for name in LEGAL_FILES if name.encode() not in tracked]
        for name in tracked:
            if not name:
                continue
            path = Path(name.decode())
            if path.parts[0] in ("data", "bin", "obj", "publish"):
                continue
            target = stage / path
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / path, target)
        shutil.copy2(stage / "appsettings.example.json", stage / "appsettings.json")
        marker = ("STOCKSENTINEL_SYNTHETIC_SECRET_" + secrets.token_hex(16)).encode()
        for name in ("data/push-vapid.json", "data/push-subscriptions.json", "data/sentinel.db", "data/sentinel.db-wal",
                     "data/sentinel.db-shm", "data/nested/private.key", "data/nested/settings.config",
                     "publish/publish/data/private.json", "tests/stale/bin/check.runtimeconfig.json"):
            path = stage / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(json.dumps({"privateKey": marker.decode()}).encode())
        # The stale publish fixture must not remain in the output being checked. Keep
        # it excluded as an input, then use a fresh output directory for the first run.
        evaluated = subprocess.check_output(["dotnet", "msbuild", "StockSentinel.Web.csproj", "-nologo", "-getItem:Content,None"], cwd=stage)
        items = json.loads(evaluated)["Items"]
        assert all(not item["Identity"].startswith(("data/", "publish/", "tests/")) for group in items.values() for item in group)
        shutil.rmtree(stage / "publish")
        previous = None
        for run in range(2):
            command = ["dotnet", "publish", "StockSentinel.Web.csproj", "-c", "Release", "-r", "linux-arm",
                       "--self-contained", "true", "-p:PublishSingleFile=true", "-o", str(stage / "publish")]
            subprocess.run(command, cwd=stage, check=True)
            files = verify(stage, marker)
            assert previous is None or files == previous, "Repeated publish changed the file list"
            previous = files
            print(f"PASS publish {run + 1}: {len(files)} expected files; no runtime secrets or recursive output", flush=True)


if __name__ == "__main__":
    main()
