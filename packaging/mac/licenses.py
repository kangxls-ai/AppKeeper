"""Collect verbatim dependency notices shipped in restored NuGet packages."""
import json
import pathlib
import sys

assets = json.loads(pathlib.Path("src/AppKeeper.Mac/obj/project.assets.json").read_text())
roots = [pathlib.Path(p) for p in assets["packageFolders"]]
output = pathlib.Path(sys.argv[1])
output.mkdir(parents=True, exist_ok=True)
with (output / "DEPENDENCY-LICENSES.txt").open("w", encoding="utf-8") as result:
    result.write(pathlib.Path("packaging/mac/Avalonia-LICENSE.txt").read_text(encoding="utf-8"))
    for name, library in assets["libraries"].items():
        if library.get("type") != "package":
            continue
        for relative in library.get("files", []):
            path = pathlib.PurePosixPath(relative)
            if path.suffix.lower() not in (".txt", ".md") or not any(term in path.name.lower() for term in ("license", "licence", "notice", "copyright")):
                continue
            for root in roots:
                source = root / library["path"] / relative
                if source.is_file():
                    result.write("\n\n---\n" + name + " / " + relative + "\n\n")
                    result.write(source.read_text(encoding="utf-8-sig"))
                    break
