# Raw arithmetic and trace comparison

Namespace: `StuntRacer.Core.Compatibility.Physics`.

This independently authored, package-free .NET 10 groundwork does **not** implement
original vehicle dynamics, a road-height sampler, lookup tables, a stock memory
map, or an executable oracle. The authored simulation is unchanged. No original
code, expressive source translation or original arrays are included.

## Entry points

```csharp
AmigaTraceVerification AmigaTraceVerifier.Verify(Stream expected, Stream actual);
AmigaTrace AmigaTraceReader.Read(Stream utf8JsonLines);
string AmigaTraceReader.ComputeInputSha256(IEnumerable<AmigaTraceTransition> transitions);
```

Streams are read from their current positions, never rewound, and left open. There
is no file/network access. `Read` requires a complete trace. `Verify` returns
`blocked` for an empty stream; malformed/unsupported data in either complete
stream throws `InvalidDataException`, even after an earlier numerical difference.
Do not pass the same stream instance as both arguments.

Results have string `Status` (`match`, `mismatch`, `blocked`), `long ComparedTicks`,
`long? FirstMismatchTick`, `Reason`, `Qualification`, `OracleBasis`, and nullable
string `FirstMismatchField`, `ExpectedValue`, `ActualValue`. Tick zero is the
initial state and does not count toward `ComparedTicks`. A differing transition
counts as inspected; a missing tail counts only the shared transitions.

- Exact synthetic matches are `match` / `synthetic-only`.
- An expected `same-implementation`, `remake`, `candidate-implementation` or
  `unknown` is blocked, not an independent original oracle.
- An exact match against declared `independent-executable` observations remains
  `unqualified-candidate`. Hashes and provenance declarations are identifiers,
  **not authenticated external evidence, source authority or a parity receipt**.
- Missing metadata/evidence or unknown timing blocks comparison. Incompatible
  baseline, tables, mapping or timing blocks comparison with field diagnostics.
- Raw state/input differences and different complete lengths are `mismatch`.
  Initial-state fields are named `initial.state.position.x`, etc.; transition
  fields are named `input.steer`, `after.position.x`, etc. A length difference
  names `transitions.length` and the first missing tick.

No result qualifies full original dynamics, hidden executable state, stock
instruction ordering, timing/overrun/race-timer behavior, or code/data reuse rights.

## JSONL v1

One UTF-8 JSON object per line, no BOM, comments, blank lines, duplicate
properties, unknown fields or trailing records. Property names are case-sensitive;
property order is immaterial. A final LF or CRLF is allowed, as is EOF immediately
after the last object. Each record is limited to 65,536 UTF-16 characters.
Version 1 supports 1..1,000,000 transitions.

The order is **header, initial tick 0, contiguous unique transition ticks 1..N,
EOF**. `transitionCount` declares N, so a truncated prefix cannot silently become
a complete trace. Header, input and every state field below are mandatory.
Metadata shown as nullable must still have its property present. Unknown facts
are `null` or explicit unknown timing, never an inferred stock/default clock.

Header shape (deliberately blocked unknown-evidence synthetic example):

```json
{"type":"header","schemaVersion":1,"stateSchema":"amiga-raw-state-v1","transitionCount":1,"baseline":{"executableSha256":null,"version":null,"region":null},"tablesSha256":null,"inputSha256":null,"stateMappingSha256":null,"timing":{"status":"unknown","numerator":null,"denominator":null,"evidenceSha256":null},"provenance":{"kind":"synthetic","producer":"independent-fixture-author","evidenceSha256":null}}
```

- Every SHA-256 is `null` or exactly 64 lowercase hexadecimal digits.
- `baseline.version` / `region` are nonblank strings or null. Executable identity
  is the **target baseline**, not a substitute remake's filename.
- `tablesSha256` identifies the exact consumed table bundle, including geometry
  or sine tables where applicable. No tables or generated substitutes ship here.
- `stateMappingSha256` identifies the separate capture/input mapping document:
  fields, source widths/offsets if established, signs, precision, flag meanings
  and capture point/order. The new schema does not invent these source facts.
- Known `timing` is `{"status":"known","numerator":P,"denominator":Q,"evidenceSha256":H}`:
  positive signed-64-bit integer P/Q seconds per logical transition, plus evidence
  hash. Equivalent rational fractions compare equally; timing evidence hashes
  must also match. Unknown timing requires both numbers null. This does not set
  wall pacing or a simulator's step. Neither 120 Hz, 60 Hz nor descendant 0.14 s
  is supplied as original timing.
- `provenance.kind` is exactly `independent-executable`,
  `candidate-implementation`, `same-implementation`, `remake`, `synthetic`, or
  `unknown`. `producer` is a nonblank identifier. `evidenceSha256` identifies
  the independently reviewed capture/independence record, not a self-referential
  hash of this header. Absence blocks comparison, including for fixture runs.

Record shapes below use `STATE` as a metasyntactic placeholder for the full state
object in the next example, not a literal JSON token:

```text
{"type":"initial","tick":0,"state":STATE}
{"type":"tick","tick":1,"input":{"steer":0,"throttle":0,"brake":0,"boost":false,"recover":false},"after":STATE}
```

Synthetic all-zero `STATE` shape (not an original initial condition):

```json
{"position":{"x":0,"y":0,"z":0},"angles":{"yaw":0,"pitch":0,"roll":0},"linearVelocity":{"x":0,"y":0,"z":0},"angularVelocity":{"yaw":0,"pitch":0,"roll":0},"contacts":{"frontLeft":{"roadHeight":0,"actualHeight":0,"compression":0,"previousCompression":0,"damage":0,"flags":0},"frontRight":{"roadHeight":0,"actualHeight":0,"compression":0,"previousCompression":0,"damage":0,"flags":0},"rear":{"roadHeight":0,"actualHeight":0,"compression":0,"previousCompression":0,"damage":0,"flags":0}},"resources":{"boostReserve":0,"boostSubunit":0,"damage":0,"damageLimit":0,"engineRevs":0},"road":{"section":0,"segment":0,"lateralPosition":0,"distanceIntoSection":0},"runtime":{"flags":0,"offTrackTicks":0,"recoveryTicks":0,"clockAccumulator":0,"damageCooldown":0}}
```

All numbers are integer JSON tokens, without a decimal point or exponent; strings,
booleans and out-of-range values are not coerced/truncated into integers.

| Required group | Explicit interchange widths |
|---|---|
| `position` | `x,y,z`: signed int32 |
| `angles` | `yaw,pitch,roll`: unsigned int16, wrapping full turn 65536 |
| `linearVelocity` | `x,y,z`: signed int16 |
| `angularVelocity` | `yaw,pitch,roll`: signed int16 |
| Each of `contacts.frontLeft,frontRight,rear` | `roadHeight,actualHeight`: int32; `compression,previousCompression`: int16; `damage,flags`: uint8 |
| `resources` | `boostReserve,boostSubunit,damage,damageLimit`: uint8; `engineRevs`: uint16 |
| `road` | `section,segment`: uint16; `lateralPosition`: int16; `distanceIntoSection`: int32 |
| `runtime` | `flags`: uint32; `offTrackTicks,recoveryTicks`: uint16; `clockAccumulator,damageCooldown`: uint8 |
| Transition `input` | `steer`: int16; `throttle,brake`: uint8; `boost,recover`: JSON booleans |

These are **candidate capture-envelope widths**, not claims that each stock
variable has that width or a known address. All raw observations compare exactly,
including prior contact compression and counters. Further hidden state requires
a new version rather than optional dictionary entries. `rear` is rear-central,
not two original rear springs. Positive input steer denotes right; stock input
encoding needs separate mapping evidence. Source position signs/precision remain
raw. Parent display convention remains LH +X right, +Y up, +Z forward; positive
yaw rotates +Z toward +X. Display conversion must be explicit, never fed back.

### Input hash

`inputSha256` is checked against the entire recorded input sequence. SHA-256 input:

1. UTF-8 bytes of `amiga-physics-input-v1` followed by one LF.
2. For each tick in order: signed int64 big-endian tick; signed int16 big-endian
   steer; uint8 throttle; uint8 brake; uint8 boost (0/1); uint8 recover (0/1).
   Each transition contributes exactly 14 bytes, with no separators.

This is a new canonical hash contract, not the hash of JSON spelling, an external
controller recording or state bytes. Readers recompute it; a wrong nonnull hash
is invalid data. Null is blocked, not replaced by the computed value. The two
traces must have equal typed inputs and lengths, so matching declarations alone
cannot bypass input comparison.

## Arithmetic

`AmigaIntegerMath` explicitly wraps signed/unsigned 16/32-bit add/subtract and
narrowing. `MultiplyInt16` / `MultiplyUInt16` widen to 32 bits; the named
`MultiplyInt32Wrapping` / `MultiplyUInt32Wrapping` keep the low 32 bits.
`DivideInt32Truncating` truncates toward zero and throws on zero or signed overflow;
`DivideUInt32` throws on zero.

`DivideSignedWord` / `DivideUnsignedWord` model the base-68000 word division's
value result: 32-bit dividend, 16-bit divisor, 16-bit quotient, remainder in the
high destination word. `AmigaWordDivisionResult` reports `Success`,
`QuotientOverflow` or `DivideByZero`. The latter represents a CPU trap, distinct
from quotient overflow; errors retain destination bits and null quotient/remainder.
No exception stack, cycles, undefined condition codes or full CCR are emulated.
`AddWithCarry16/32` return mathematical carry/signed overflow, not sticky-Z ADDX.

Shift helpers accept **literal nonnegative counts**, saturating to sign bits or
zero past the width rather than C#'s masked count. For a known data-register shift
instruction only, call `M68000RegisterShiftCount` explicitly (modulo 64). This is
not the immediate-count decoder or an instruction interpreter.

`AmigaAngle.Wrap`, `ShortestDelta` and display-only `ToDisplayRadians` /
`InterpolateDisplayRadians` never round display values back into state.
The exact half-turn tie is -32768; direction sign must be exactly +1 or -1.
Interpolation alpha must be finite in [0,1] and its angle is intentionally unwrapped.
There is no trig lookup substitute.

Independent synthetic boundary cases for consumers include:

| Operation | Exact expected value |
|---|---|
| `WrapInt16(32768)` / `WrapUInt16(-1)` | -32768 / 65535 |
| `WrapInt32(2147483648)` / `WrapUInt32(-1)` | -2147483648 / 4294967295 |
| `ArithmeticShiftRight32(-3,1)` / `DivideInt32Truncating(-3,2)` | -2 / -1 |
| `ArithmeticShiftRight32(-3,32)` / `LogicalShiftRight32(0xffffffff,32)` | -1 / 0 |
| `MultiplyUInt16(65535,65535)` | 4294836225 |
| `DivideSignedWord(-7,3)` | quotient -2, remainder -1, destination `0xfffffffe` |
| `DivideSignedWord(-2147483648,-1)` | quotient overflow; destination `0x80000000` unchanged |
| `AddWithCarry16(65535,0,true)` | value 0, carry true, signed overflow false |
| `ShortestDelta(65535,0)` / `ShortestDelta(0,32768)` | 1 / -32768 |

CPU facts were checked against Motorola's **M68000 Family Programmer's Reference
Manual (1992)**, sections ASL/ASR, LSL/LSR, MULS.W/MULU.W and DIVS.W/DIVU.W. Later
CPU32/68020 long forms in that manual do not authorize base-68000 assumptions.
Mathematical agreement establishes neither a vehicle routine nor its instruction
order. Original dynamics remain blocked pending source authority, exact table
data, stock road-sampler/order evidence, selected executable/version/region,
independent executable traces and measured timing/overrun/race-timer semantics.
