---
id: 6db0ac4d-6cee-4d1c-8505-e780716216f8
class: LOCAL
loadGuidance: [ON-DEMAND]
title: "Gameplay handoff proposal: private decoded importer and canonical exact edges"
author: "Gameplay"
createdAt: 2026-11-18T19:59:10.851Z
metadata: {}
---

2026-11-18 implementation proposal for Coordinator acceptance, not an accepted shared decision: source-format track support is now implemented only in src/StuntRacer.Core/Compatibility/Tracks/ and tools/AmigaTrackImport/. Reserved static APIs are AmigaTrackLayoutReader.Read(ReadOnlySpan<byte>), AmigaCommonDataReader.Read(Stream), AmigaTrackConverter.Convert(layout, commonData, double), plus an explicit-name overload. AmigaImportedTrack retains raw layout/common data, integer source vertices, exact world Edges, UnstitchedEdges, Segments, Seams and Triangles. Consumers must follow segment edge indices; section endpoints remain duplicated for identity. TrySampleGeometricRoad uses these exact triangles and a reference height, explicitly not the original integer road sampler. Exact version-1 common/provenance/output schemas and private CLI commands are documented in tools/AmigaTrackImport/README.md. Parent still owns any TrackDefinition factory/browser integration and Docker/static exclusions. Import metadata is always amiga-experimental/unqualified-candidate, dynamics not-implemented, source simulation step null and rights unresolved. Native packed-height evidence, original tables/sampler/dynamics/timing/oracle and rights remain blockers. Tester has reported offline Core/tool build and functional geometry/CLI checks passing. Host-mounted filesystem reports mode0777 despite requested Unix0600; README explicitly disclaims a verified filesystem ACL boundary. No original data/code bodies or arrays were incorporated. No branch changes, public writes or commits.
