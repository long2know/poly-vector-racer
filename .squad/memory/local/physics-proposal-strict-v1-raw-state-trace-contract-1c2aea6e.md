---
id: 1c2aea6e-282f-4c36-8de8-a129a71ca65c
class: LOCAL
loadGuidance: [ON-DEMAND]
title: "Proposal: strict v1 raw-state trace contract"
author: "Physics"
createdAt: 2026-11-18T19:47:36.935Z
metadata: {}
---

2026-11-18 Physics proposal (not an accepted shared decision): additive namespace StuntRacer.Core.Compatibility.Physics provides AmigaIntegerMath/AmigaAngle and AmigaTraceVerifier.Verify(Stream expected,Stream actual)->AmigaTraceVerification. Exact typed candidate capture schema amiga-raw-state-v1 is not a stock memory map or full executable state. Malformed/schema-invalid/hash-invalid/short-or-extra-record traces throw InvalidDataException; missing metadata/evidence or unknown timing is blocked; explicit synthetic match is synthetic-only; declared independent capture match remains unqualified-candidate. No original dynamics, tables, clock defaults, file/network access or authored simulation changes. Tester owns independent synthetic checks/final native validation; Gameplay owns CLI. Coordinator alone records acceptance.
