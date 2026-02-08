# Implementation Phases

## Phase 1: Core Runtime + Flat Messages

### Goal
Build the foundational abstractions and prove they work with simple flat messages.

### Deliverables

1. **Core interfaces** (`src/Akka.Serialization.V2/`):
   - `ICodecWriter` — write-side wire format abstraction
   - `ICodecReader` — read-side wire format abstraction
   - `ICodecProvider` — factory creating writer/reader instances
   - `SerializerV2` — abstract base class with `Write(ICodecWriter)` / `Read(ICodecReader)` / `Manifest` / `Identifier`
   - `SerializerRegistry` — maps `(SerializerId, Type)` → `SerializerV2`

2. **MessagePack codec** (`src/Akka.Serialization.MessagePack/`):
   - `MessagePackCodecProvider` — creates MessagePack-based writer/reader
   - `MessagePackCodecWriter` — uses `MessagePackWriter` ref struct per operation, writes to shared `IBufferWriter<byte>`
   - `MessagePackCodecReader` — uses `MessagePackReader` ref struct per operation from `ReadOnlyMemory<byte>`

3. **Sample messages**:
   ```csharp
   public sealed record UserCreated(string UserId, string Email, DateTime CreatedAt);
   public sealed record UserUpdated(string UserId, string? NewEmail, string? NewName, DateTime UpdatedAt);
   public sealed record OrderPlaced(Guid OrderId, string CustomerId, decimal Amount, DateTimeOffset PlacedAt);
   ```

4. **Hand-written serializers** for each message type with:
   - Manifest routing (switch expression)
   - Field-by-field `Write` / `Read`
   - `SkipField` loops for forward compat

5. **Tests**:
   - Flat message round-trip (all types)
   - Nullable field handling
   - Version tolerance: V1 reader skips V2's extra fields via `SkipField()`
   - Forward compat: V2 reader handles missing V1 fields with defaults

### Acceptance Criteria
- `dotnet test` passes all flat message tests
- `dotnet build` compiles cleanly with no warnings

### Commit Points
- After project structure creation
- After core interfaces compile
- After MessagePack codec compiles
- After flat message tests pass

---

## Phase 2: Envelopes + Nested Composition

### Goal
Prove zero-copy nested serialization and backwards compatibility with legacy serializers.

### Deliverables

1. **Envelope messages**:
   ```csharp
   public sealed record RemoteEnvelope(string RecipientPath, string SenderPath, int InnerSerializerId, string InnerManifest, object Message);
   public sealed record DDataEnvelope(string Key, long Version, int InnerSerializerId, string InnerManifest, object Data);
   ```

2. **Envelope serializers** demonstrating zero-copy nesting:
   - `RemoteEnvelopeSerializer` — writes envelope fields, then calls `innerSerializer.Write(writer, message)` on the SAME writer
   - `DDataEnvelopeSerializer` — same pattern
   - Composable: Remote wrapping DData wrapping UserCreated, all to one buffer

3. **`SerializerV2Adapter`** — wraps legacy `Serializer`/`SerializerWithStringManifest`:
   - `Write` calls `_legacy.ToBinary()`, writes as binary blob
   - `Read` reads blob, calls `_legacy.FromBinary()`

4. **Akka.NET type codecs**:
   - `ActorRefCodec` — serializes IActorRef as path string
   - Messages with IActorRef fields (`SubscribeToEvents`, `ForwardMessage`)
   - Tests require real ActorSystem via Akka.Hosting.TestKit

5. **Tests**:
   - Single-layer envelope round-trip
   - Double-nested (Remote → DData → user message) round-trip
   - Triple-nested with tracing header
   - Single-buffer verification (all layers write to one ArrayBufferWriter)
   - Legacy adapter round-trip
   - Legacy message inside V2 envelope
   - Mixed registry: V2 + adapted V1 serializers coexist
   - ActorRef round-trip with real ActorSystem

### Acceptance Criteria
- All envelope and backwards compat tests pass
- Nested serialization uses single buffer (verified in test)

### Commit Points
- After envelope serializers compile
- After single-layer envelope tests pass
- After nested envelope tests pass
- After backwards compat tests pass
- After ActorRef codec tests pass

---

## Phase 3: Benchmarks

### Goal
Quantify the mechanical improvement of V2 over legacy serialization by comparing against a byte-for-byte identical baseline.

### Deliverables

1. **Legacy baseline serializers** — hand-rolled serializers that produce **byte-for-byte identical MessagePack output** using the old pattern:
   - `MemoryStream` → `MessagePackSerializer.Serialize(stream, obj)` → `stream.ToArray()` → `byte[]`
   - For envelopes: serialize inner → `byte[]` → embed as binary field in outer → serialize outer → `byte[]`

2. **BenchmarkDotNet scenarios** (`benchmarks/Akka.Serialization.Benchmarks/`):

   | Benchmark | What it measures |
   |-----------|-----------------|
   | `FlatMessage_Serialize_Legacy` | MessagePack → MemoryStream → ToArray() |
   | `FlatMessage_Serialize_V2` | SerializerV2 → ArrayBufferWriter |
   | `FlatMessage_Deserialize_Legacy` | MessagePack from byte[] |
   | `FlatMessage_Deserialize_V2` | SerializerV2 Read from Memory |
   | `Envelope_1Layer_Legacy` | Inner → byte[] → embed in outer → byte[] |
   | `Envelope_1Layer_V2` | Single buffer, both layers write directly |
   | `Envelope_3Layer_Legacy` | Triple byte[] allocation + copy |
   | `Envelope_3Layer_V2` | Single buffer, all 3 layers write directly |

3. All benchmarks use `[MemoryDiagnoser]` for GC/allocation tracking.

### Expected Outcomes
- Flat messages: modest improvement in allocations
- 1-layer envelope: ~2x allocation reduction
- 3-layer envelope: ~3-4x allocation reduction, meaningful throughput improvement

### Acceptance Criteria
- Benchmarks run successfully
- V2 shows measurable allocation reduction for nested envelopes
- Results are captured and documented

### Commit Points
- After baseline serializers compile
- After benchmarks run successfully
- After results are captured

---

## Phase 4: Source Generation Design

### Goal
Design the attribute system and source generator output format.

### Deliverables

1. **Attribute definitions** (`src/Akka.Serialization.V2/`):
   ```csharp
   [AkkaSerializerModule(SerializerId = 5001)]
   public partial class UserMessagesSerializer;

   [AkkaSerializable(Manifest = "user-created-v1")]
   public sealed record UserCreated(
       [property: AkkaField(0)] string UserId,
       [property: AkkaField(1)] string Email,
       [property: AkkaField(2)] DateTime CreatedAt);
   ```

2. **Design decisions documented**:
   - Message grouping: `[AkkaSerializerModule]` on partial class, types auto-associate within assembly
   - Field ordering: explicit `[AkkaField(n)]` indices (protobuf-style)
   - Manifest strings: explicit `[AkkaSerializable(Manifest = "...")]`
   - Version tolerance: generator validates extend-only (higher indices + nullable for new fields)

3. **Expected generated code** (written by hand first as reference):
   - Per-type Write/Read methods
   - Manifest routing switch
   - SerializerV2 subclass on the partial module class

### Acceptance Criteria
- Attribute types compile
- Hand-written "expected generated code" matches what the generator will produce
- Annotated message types pass same tests as Phase 1/2 hand-written serializers

### Commit Points
- After attribute definitions
- After expected generated code reference

---

## Phase 5: Source Generator Implementation

### Goal
Build a Roslyn incremental source generator that produces serializers passing the same tests as hand-written ones.

### Deliverables

1. **Generator project** (`src/Akka.Serialization.Generators/`):
   - Roslyn `IIncrementalGenerator`
   - Finds `[AkkaSerializable]` types in compilation
   - Groups by assembly / `[AkkaSerializerModule]`
   - Generates Write/Read methods per type
   - Generates Manifest routing + SerializerV2 implementation on module class

2. **Test variants**:
   - `Generated_UserCreated_RoundTrip` — uses source-generated serializer
   - `Generated_Envelope_RoundTrip` — generated serializers in envelope context
   - Both produce **identical output bytes** to hand-written serializers

3. **Validation diagnostics**:
   - Warning if `[AkkaField]` indices are not sequential
   - Error if field index conflicts within a type
   - Warning if non-nullable field added at end (should be nullable for forward compat)

### Acceptance Criteria
- Source-generated serializers pass exact same test suite as hand-written
- Generated and hand-written serializers produce byte-identical output
- Full benchmark suite: hand-written vs generated vs legacy baseline

### Commit Points
- After generator scaffold compiles
- After first generated serializer compiles
- After generated serializer passes round-trip test
- After all generated tests pass
- After final benchmarks
