"""Archive the application bundle with the file modes macOS needs.

The bundle can be assembled from a machine that is not a Mac, and Windows has
no execute bit to preserve. Writing the archive here rather than with tar means
the executables inside it are marked executable whatever built them, so the
copy that comes out on the Mac runs without anyone having to chmod it first.
"""
import argparse
import pathlib
import tarfile

# The first four bytes of a Mach-O binary, thin or fat, either byte order.
MACH_O_MAGIC = {
    b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe",
    b"\xfe\xed\xfa\xcf", b"\xfe\xed\xfa\xce",
    b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca",
}


def is_executable(path: pathlib.Path) -> bool:
    if path.is_symlink() or not path.is_file():
        return False
    if path.suffix in {".dylib", ".so", ".sh"}:
        return True
    # Anything in a bin directory is there to be run.
    if path.parent.name == "bin":
        return True
    try:
        with path.open("rb") as handle:
            return handle.read(4) in MACH_O_MAGIC
    except OSError:
        return False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", help="directory to archive")
    parser.add_argument("output", help="tar.gz to write")
    parser.add_argument("--name", help="top-level name inside the archive (default: the source's own)")
    arguments = parser.parse_args()

    source = pathlib.Path(arguments.source).resolve()
    if not source.is_dir():
        raise SystemExit(f"Not a directory: {source}")
    top = arguments.name or source.name
    output = pathlib.Path(arguments.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    executables = 0

    def prepare(info: tarfile.TarInfo) -> tarfile.TarInfo:
        nonlocal executables
        # A reproducible archive: nothing about the machine that built it is
        # worth carrying into the one that unpacks it.
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        info.mtime = 0
        if info.isdir():
            info.mode = 0o755
        elif info.issym():
            info.mode = 0o777
        else:
            relative = pathlib.Path(info.name).relative_to(top)
            if is_executable(source / relative):
                info.mode = 0o755
                executables += 1
            else:
                info.mode = 0o644
        return info

    with tarfile.open(output, "w:gz", compresslevel=6) as archive:
        archive.add(source, arcname=top, filter=prepare)

    size = output.stat().st_size
    print(f"Wrote {output} ({size / (1024 * 1024):.0f} MB, {executables} executables)")


if __name__ == "__main__":
    main()
