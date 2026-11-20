### 2026-11-20: Poly Vector Racer repository and deployment identity

**By:** Copilot integration, based on the user's requested repository/image name and hosting path.

**What:** Ship the authored browser game and its durable Squad decisions, agent history and verification notes as Poly Vector Racer. GitHub repository: `long2know/poly-vector-racer`; registry: `ghcr.io/long2know/poly-vector-racer:latest` plus `sha-<commit>`. The deployment sample uses `BASE_URL=/poly-vector-racer` behind `games.long2know.com`, with a loopback-only upstream. Existing mounts remain configurable, and no public server configuration is changed by a release.

**Why:** The first game grew into stunt racing, open-city driving, original/reference modes and World Flight. C# projects retain their stable namespaces, and the container image contains only the authored runtime. Optional World Flight tiles and original reference inputs remain separate; the reproducible terrain pipeline and its licensing notes are in `tools/WorldFlight/README.md`. The latest local geography verification covers 38,744 terrain tiles with detailed San Francisco, Yosemite, Grand Canyon and Mount Rainier. World Flight has real Mach telemetry, chase HUD controls and a rebuilt flight cockpit; the Amiga flight rules remain unchanged.
