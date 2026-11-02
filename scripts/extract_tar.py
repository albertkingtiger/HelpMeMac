"""Extract a tar.gz, turning links into copies where the filesystem cannot hold them.

The standalone Python runtime is a macOS archive full of symlinks: bin/python3
points at bin/python3.11, and so on. Windows cannot create those from an
ordinary shell, and tar gives up part way through. Copying the target in place
of the link produces a tree that behaves the same on the Mac, at the cost of a
few duplicated files.
"""
import argparse
import pathlib
import shutil
import tarfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("archive")
    parser.add_argument("destination")
    arguments = parser.parse_args()

    destination = pathlib.Path(arguments.destination).resolve()
    destination.mkdir(parents=True, exist_ok=True)

    links = []
    copied = 0
    with tarfile.open(arguments.archive, "r:gz") as archive:
        members = archive.getmembers()
        # Links are held back: whatever they point at has to exist first, and
        # an archive may list the link before its target.
        plain = [m for m in members if not (m.issym() or m.islnk())]
        for member in plain:
            target = (destination / member.name).resolve()
            if not str(target).startswith(str(destination)):
                raise SystemExit(f"Refusing to extract outside the destination: {member.name}")
        archive.extractall(destination, members=plain)
        links = [m for m in members if m.issym() or m.islnk()]

    for member in links:
        path = destination / member.name
        if member.issym():
            # A symlink's target is relative to the directory it sits in.
            source = (path.parent / member.linkname).resolve()
        else:
            source = (destination / member.linkname).resolve()
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists() or path.is_symlink():
            continue
        try:
            path.symlink_to(member.linkname)
            continue
        except (OSError, NotImplementedError):
            pass
        if source.is_dir():
            shutil.copytree(source, path, dirs_exist_ok=True)
        elif source.exists():
            shutil.copy2(source, path)
        else:
            # A link to something the archive does not carry is not worth
            # failing the build over; it was not going to resolve anyway.
            continue
        copied += 1

    print(f"Extracted {arguments.archive} to {destination} ({copied} links copied)")


if __name__ == "__main__":
    main()
