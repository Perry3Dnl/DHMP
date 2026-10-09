# Host your own Demo Arena

## Small standalone server for the first connection

The arena simulation is plain C#, so this example can also run in a small .NET 10 server process. The included `DHMP.UnityArenaHost~` directory contains its source, project and Dockerfile. This has the same simulation and record schema as the Unity server build. The trailing `~` keeps .NET-host code out of Unity's script importer.

From an unpacked package root:

```sh
dotnet build 'Samples~/DemoArena/Hosting/DHMP.UnityArenaHost~/DHMP.UnityArenaHost.csproj' -c Debug
dotnet 'Samples~/DemoArena/Hosting/DHMP.UnityArenaHost~/bin/Debug/net10.0/DHMP.UnityArenaHost.dll' serve --bind ::1 --experimental-plaintext --development
```

In another terminal, use the same DLL with `connect --bind ::1 --server ::1 --experimental-plaintext --development`. Both processes need raw-socket permission. The client prints `connection_verified` only after a guest join and an authoritative state reply acknowledging input, then disconnects. This verifies a real DHMP connection below Unity; it does not launch a Unity client.

After importing the sample into a Unity project, its host folder has moved. Pass `-p:DhmpPackageRoot=<absolute-path-to-the-installed-com.dhmp.networking-package>` to `dotnet build` so the host can find the exact shared runtime source.

Docker alternative, from the package root:

```sh
docker build --build-arg CONFIGURATION=Debug -t dhmp-arena-lab -f 'Samples~/DemoArena/Hosting/DHMP.UnityArenaHost~/Dockerfile' .
docker run --rm --network host --cap-drop ALL --cap-add NET_RAW --security-opt no-new-privileges --read-only --tmpfs /tmp dhmp-arena-lab serve --bind ::1 --experimental-plaintext --development
```

For a remote controlled test, replace the bind address with the server's native IPv6 and add `--allow-client <your-client-source-IPv6>`. Configure that client source address in the Unity settings asset. Every permitted source must be explicitly listed; source filtering does not encrypt or authenticate the connection. Release builds refuse `--development` and require `DHMP_APPLICATION_ID`, `DHMP_LICENSE_KEY` and `DHMP_LICENSE_PUBLIC_KEY` instead. The public verification key is base64 SubjectPublicKeyInfo.

## Unity Dedicated Server build

Build the imported scene with **DHMP > Build Demo > Linux Server (Development)**. Install Unity's Linux Dedicated Server build module first. Copy the entire output directory, including the executable, its data directory and shared libraries, to the host.

For a controlled development test:

1. Assign a native IPv6 address to the host and configure `DHMP_LOCAL_IPV6` to that exact address.
2. Enable the two experiment/plaintext options in the settings asset before building. Keep Development licensing for this development build.
3. Grant raw-socket permission to this dedicated executable only, or run under the included service configuration with `CAP_NET_RAW`. Do not grant capabilities to a general-purpose system interpreter.
4. Configure the host and upstream firewall for experimental IPv6 protocol numbers 253 and 254 between the intended test machines. These are IP protocol numbers, not port numbers. Verify the end-to-end path with actual client connections.
5. Start the server with `DHMP_LOCAL_IPV6=<your-address> bash start-server.sh`. Run the script from the output directory or set `DHMP_SERVER_BINARY` to the executable path.
6. On the client, set a valid local IPv6 address in `DhmpDemoSettings.asset` and enter the server's IPv6 address on the demo connection screen.

For a same-host Linux experiment, `::1` may be used for one client and one server. More clients need distinct source IPv6 addresses. A duplicate source address cannot open another arena session while the old session remains active; disconnect or wait for its ten-second idle timeout.

The `.service.example` file is a template, not an installer. Choose the paths and service account yourself. Its environment file supplies `DHMP_LOCAL_IPV6` and, for a licensed release build, `DHMP_APPLICATION_ID` and `DHMP_LICENSE_KEY`. Keep the environment file readable only by the administrator/service account. The public verification key is configured in the settings asset; no signing key belongs on the game server.

The maximum player setting defaults to 16 and is capped at 32 in this preview. This is an admission bound, not measured capacity. Server load, player simulation and network delivery need independent measurements.

## Publisher's demonstration endpoint

Build and run this same example, then put its verified IPv6 endpoint in the sample's `demoServerIpv6` field before packaging for customers. An empty field intentionally disables that button instead of pointing at a fictional service. The current work does not provide a live arena server, account service or customer hosting.

The current sample uses explicitly opted-in plaintext experiments. Before operating it as a public service, integrate the existing DHMS V2 protection, establish peer admission/abuse controls, validate client platform support and test multiple independent Internet paths. A successful local socket test does not establish those properties.
