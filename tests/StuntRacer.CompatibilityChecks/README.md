# Synthetic native compatibility checks

Package-free .NET 10 console checks against the real Core readers, converter,
exact-edge track factory, integer helpers, trace verifier and compiled importer CLI.
Run from repository root:

```sh
bash scripts/dotnet.sh restore tests/StuntRacer.CompatibilityChecks --source tests/StuntRacer.CompatibilityChecks
bash scripts/dotnet.sh run --project tests/StuntRacer.CompatibilityChecks --no-restore
bash scripts/dotnet.sh run --project tests/StuntRacer.Checks --no-restore
```

Restore uses only the local test directory as a package source. No originals,
network access or new packages are needed. Fixtures contain at most four synthetic
layout sections, independently authored decoded geometry and two synthetic raw ticks.
CLI inputs/outputs live under ignored `artifacts/amiga-compat/synthetic-checks-*`;
cleanup deletes only explicitly owned files and directories.

Checks inspect exact emitted coordinates/provenance and mutate an actual converted
JSON artifact to prove its coordinate assertion fails. CLI arguments use
`ProcessStartInfo.ArgumentList`, never shell command interpolation or evaluation.
The existing eight authored checks are separate and unchanged.

Passing checks do not qualify original dynamics, native packed-height extraction,
stock timing, an independent executable oracle, rights or hardware performance.
Observed filesystem permission bits are reported separately: mounts such as
`fuseblk` may expose `0777` despite a requested `0600`; this suite does not certify
filesystem privacy.
