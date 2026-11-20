# Optional Classic/reference runtime checks

Package-free .NET 10 console checks. The default run needs no original data or
network and runs eight synthetic scalar/control/unit groups:

```sh
bash scripts/dotnet.sh restore tests/StuntRacer.SourceRuntimeChecks --source tests/StuntRacer.SourceRuntimeChecks
bash scripts/dotnet.sh run --project tests/StuntRacer.SourceRuntimeChecks --no-restore
```

Run the real externally extracted LittleRamp package and separately reviewed
component-oracle receipt explicitly:

```sh
bash scripts/dotnet.sh run --project tests/StuntRacer.SourceRuntimeChecks --no-restore -- \
  --package artifacts/amiga-source/runtime/LittleRamp.source-package.json \
  --component-oracle artifacts/amiga-source/oracle/receipt.json
```

Explicit missing files fail, never silently skip. No original layouts, geometry,
lookup arrays or traces are embedded in these tests. Real data stays in ignored
external artifacts. Real-package groups verify the identified 804-byte layout,
both reader paths, canonical/native seams and height units, exact first falling
tick, three contacts, landing, throttle/brake/right-positive steering, boost,
natural ramp flight, actual falls/recovery and raw-state cadence agreement at
30/60/120/144 Hz and jitter. No production position overrides are used.

The eight component-only groups do not claim a running-player result. The full
20-group invocation additionally includes ten real-package/session groups and two
oracle groups. The oracle compares ten bounded vectors from two reviewed
functions included from an unmodified private reference source. Its actual
`g++ 13.3.0`, `sizeof(long)=64` receipt is an LP64 nonoverflow comparison, not a
Windows-long32 or complete reference-player equivalence claim. Mutating a real
emitted oracle result proves its comparison gate detects a changed value.
Compiling/running additional upstream code requires a separate safety review.

The configured reference clock is 50 callbacks/6 callbacks per step (`0.12s`).
The candidate normalizes the source host's initially open gate to a first full
interval. Raw stored-height Q14, contact units and display Y `/4096` are distinct;
the 64-PC-unit body-origin lift and rear-wheel duplication are presentation only.
The source road callback uses canonical triangles, not the stock road sampler;
its per-segment heading is not original arc-section steering.

Classic creation is explicit. The responsive authored 120 Hz default is retained
and tested separately; package availability never certifies or selects Classic.
Passing these checks establishes functional reference-derived candidate evidence,
not stock Amiga parity, original executable/timer/road-sampler equivalence,
redistribution rights or hardware performance.
