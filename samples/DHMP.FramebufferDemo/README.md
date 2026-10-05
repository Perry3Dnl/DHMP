# DHMP Framebuffer Demo

This sample visualizes a state-oriented display profile built on DHMP fixed-record Latest processing semantics.

The browser generates a 160x90 logical framebuffer and sends only pixels whose RGB value changed. The ASP.NET backend converts every changed pixel to a fixed 16-byte application record and processes that record through DhmpServer configured for DhmpProcessingMode.Latest. The receiver canvas changes only when an update comes back; otherwise its previous pixel remains on screen.

This is deliberately a semantics demo, not a raw-network benchmark. Browser traffic uses HTTPS/WebSocket because browsers cannot emit DHMP raw IPv6 Next Header traffic. DHMP processing happens in the deployed backend.

Record layout:

- bytes 0-1: X
- bytes 2-3: Y
- bytes 4-11: per-pixel generation
- bytes 12-14: RGB
- byte 15: reserved

Run locally with:

    dotnet run --project samples/DHMP.FramebufferDemo/DHMP.FramebufferDemo.csproj

Deployment is intentionally manual through .github/workflows/deploy-framebuffer-demo.yml. The workflow expects DEPLOY_HOST, DEPLOY_USER, DEPLOY_PASSWORD, optional DEPLOY_PORT, and the same shared Docker network/Caddy installation already used by the other Perry3D deployments.
