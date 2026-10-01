#!/usr/bin/env python3
from __future__ import annotations

import argparse
import shutil
import subprocess
import tempfile
import textwrap
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_VERSION = "0.1.0-preview.local"

PACK_PROJECTS = [
    ROOT / "src/DHMP.Protocol/DHMP.Protocol.csproj",
    ROOT / "src/DHMP.Licensing/DHMP.Licensing.csproj",
    ROOT / "src/DHMP.Client/DHMP.Client.csproj",
    ROOT / "src/DHMP.Server/DHMP.Server.csproj",
    ROOT / "src/DHMP.Security/DHMP.Security.csproj",
    ROOT / "src/DHMP.RawIpv6/DHMP.RawIpv6.csproj",
    ROOT / "src/DHMP.AspNetCore/DHMP.AspNetCore.csproj",
]

PACKAGE_IDS = [
    "DHMP.Protocol",
    "DHMP.Licensing",
    "DHMP.Client",
    "DHMP.Server",
    "DHMP.Security",
    "DHMP.RawIpv6",
    "DHMP.AspNetCore",
]


def run(*args: str, cwd: Path | None = None) -> None:
    print("+", " ".join(args))
    subprocess.run(args, cwd=cwd, check=True)


def write_nuget_config(path: Path, feed: Path) -> None:
    escaped = (
        str(feed)
        .replace("&", "&amp;")
        .replace('"', "&quot;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )
    path.write_text(
        textwrap.dedent(
            f"""\
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="dhmp-local" value="{escaped}" />
              </packageSources>
            </configuration>
            """
        ),
        encoding="utf-8",
    )


def validate_package(package: Path, package_id: str, version: str) -> None:
    if not package.exists():
        raise RuntimeError(f"Missing package: {package}")

    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        if "README.md" not in names:
            raise RuntimeError(f"{package.name} does not contain README.md")

        nuspec_name = next((name for name in names if name.endswith(".nuspec")), None)
        if nuspec_name is None:
            raise RuntimeError(f"{package.name} has no .nuspec")

        root = ET.fromstring(archive.read(nuspec_name))
        ns = ""
        if root.tag.startswith("{"):
            ns = root.tag.split("}", 1)[0] + "}"

        metadata = root.find(f"{ns}metadata")
        if metadata is None:
            raise RuntimeError(f"{package.name} has no metadata section")

        def value(name: str) -> str:
            node = metadata.find(f"{ns}{name}")
            return (node.text or "").strip() if node is not None else ""

        if value("id") != package_id:
            raise RuntimeError(f"{package.name}: unexpected package id {value('id')!r}")
        if value("version") != version:
            raise RuntimeError(f"{package.name}: unexpected version {value('version')!r}")
        if not value("description"):
            raise RuntimeError(f"{package.name}: missing description")
        if value("readme") != "README.md":
            raise RuntimeError(f"{package.name}: PackageReadmeFile was not emitted")

        repository = metadata.find(f"{ns}repository")
        if repository is None or repository.attrib.get("url") != "https://github.com/Perry3Dnl/DHMP":
            raise RuntimeError(f"{package.name}: missing repository metadata")


def create_core_consumer(root: Path, feed: Path, version: str) -> None:
    project = root / "CoreConsumer"
    project.mkdir()
    write_nuget_config(project / "NuGet.Config", feed)

    (project / "CoreConsumer.csproj").write_text(
        textwrap.dedent(
            f"""\
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="DHMP.RawIpv6" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        ),
        encoding="utf-8",
    )

    (project / "Program.cs").write_text(
        textwrap.dedent(
            """\
            using System.Net;
            using DHMP.Protocol;
            using DHMP.RawIpv6;

            var wire = new DhmpWireContract(32);
            var options = DhmpRawIpv6Options.ForUnknownPath(
                IPAddress.IPv6Loopback,
                IPAddress.Parse("2001:db8::2"));

            Console.WriteLine($"{wire.RecordSize}:{options.MaximumPayloadBytes}");
            """
        ),
        encoding="utf-8",
    )

    run("dotnet", "restore", "--configfile", "NuGet.Config", cwd=project)
    run("dotnet", "build", "-c", "Release", "--no-restore", cwd=project)


def create_aspnet_consumer(root: Path, feed: Path, version: str) -> None:
    project = root / "AspNetConsumer"
    project.mkdir()
    write_nuget_config(project / "NuGet.Config", feed)

    (project / "AspNetConsumer.csproj").write_text(
        textwrap.dedent(
            f"""\
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="DHMP.AspNetCore" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        ),
        encoding="utf-8",
    )

    (project / "Program.cs").write_text(
        textwrap.dedent(
            """\
            using DHMP.AspNetCore;

            var builder = WebApplication.CreateBuilder(args);
            Console.WriteLine(typeof(DhmpServiceCollectionExtensions).FullName);
            """
        ),
        encoding="utf-8",
    )

    run("dotnet", "restore", "--configfile", "NuGet.Config", cwd=project)
    run("dotnet", "build", "-c", "Release", "--no-restore", cwd=project)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Pack DHMP to an isolated local feed and compile clean consumer projects. Never publishes packages."
    )
    parser.add_argument("--version", default=DEFAULT_VERSION)
    parser.add_argument("--keep", action="store_true", help="Keep the temporary workspace for inspection.")
    args = parser.parse_args()

    if shutil.which("dotnet") is None:
        raise RuntimeError("dotnet was not found on PATH")

    workspace = Path(tempfile.mkdtemp(prefix="dhmp-package-consumer-"))
    feed = workspace / "feed"
    feed.mkdir()

    try:
        for project in PACK_PROJECTS:
            run(
                "dotnet",
                "pack",
                str(project),
                "-c",
                "Release",
                "-o",
                str(feed),
                f"-p:PackageVersion={args.version}",
            )

        for package_id in PACKAGE_IDS:
            package = feed / f"{package_id}.{args.version}.nupkg"
            validate_package(package, package_id, args.version)

        create_core_consumer(workspace, feed, args.version)
        create_aspnet_consumer(workspace, feed, args.version)

        print(f"LOCAL_PACKAGE_CONSUMER_PASS version={args.version} packages={len(PACKAGE_IDS)}")
        print("No package was uploaded or published.")
        return 0
    finally:
        if args.keep:
            print(f"Workspace retained at {workspace}")
        else:
            shutil.rmtree(workspace, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
