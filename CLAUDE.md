# Akka.NET SerializerV2 PoC

## Project Purpose

Proof of concept for Akka.NET's next-generation serialization system. Builds a codec-based serializer externally (no Akka.NET core modifications needed) that solves:

1. **Allocation-heavy APIs** — current `ToBinary()` returns `byte[]`, every call allocates. V2 uses `IBufferWriter<byte>`.
2. **Nested envelope explosion** — DData messages go through 3-4 serialization layers (Remote → DData → user data → tracing), each allocating a new `byte[]`. V2 writes all layers to a single shared buffer.
3. **Tedious boilerplate** — `SerializerWithStringManifest` requires triple-duplication of every message type. V2 adds source generation (Phase 4-5).

Related issues: [akkadotnet/akka.net#7465](https://github.com/akkadotnet/akka.net/issues/7465), [#3740](https://github.com/akkadotnet/akka.net/issues/3740), [#6060](https://github.com/akkadotnet/akka.net/issues/6060)

## Architecture

Three-layer codec design:

1. **`ICodecWriter`/`ICodecReader`** — wire format abstraction (BeginObject, WriteInt32, ReadString, etc.)
2. **`ICodecProvider`** — factory that creates writers/readers. Default implementation uses MessagePack-CSharp.
3. **`SerializerV2`** — base class with `Write(ICodecWriter, object)` / `Read(ICodecReader, string manifest)`. Uses `(SerializerId, Manifest, Payload)` wire contract for backwards compatibility.

**Zero-copy nesting:** Envelope and inner message serializers share the same `ICodecWriter`/`ICodecReader` instance. The inner serializer's `BeginObject(N)` becomes a nested MessagePack array in the parent's array. Single buffer, no intermediate allocations.

See `docs/ARCHITECTURE.md` for detailed design.

## Build Commands

```bash
# Build everything
dotnet build SampleSln.slnx

# Run tests
dotnet test tests/Akka.Serialization.V2.Tests/

# Run benchmarks
dotnet run -c Release --project benchmarks/Akka.Serialization.Benchmarks/
```

## Project Structure

```
src/
  Akka.Serialization.V2/           # Core abstractions (ICodecWriter, ICodecReader, SerializerV2, SerializerRegistry)
  Akka.Serialization.MessagePack/  # MessagePack ICodecProvider implementation
  Akka.Serialization.Generators/   # Roslyn source generator (Phase 4-5)
  Akka.Console/                    # Original sample app (untouched)
tests/
  Akka.Serialization.V2.Tests/     # xUnit tests
benchmarks/
  Akka.Serialization.Benchmarks/   # BenchmarkDotNet benchmarks
```

## Implementation Phases

See `docs/PHASES.md` for detailed phase breakdown.

- **Phase 1**: Core abstractions + MessagePack codec + flat message serializers + tests
- **Phase 2**: Envelope serializers (nested zero-copy) + backwards compat adapter + Akka.NET type codecs + tests
- **Phase 3**: Baseline benchmarks (hand-rolled legacy producing byte-identical output) + V2 benchmarks
- **Phase 4**: Source generation design (attributes, field ordering, manifest generation)
- **Phase 5**: Roslyn incremental source generator — generated serializers pass same tests as hand-written

## Key Design Decisions

- **MessagePack-CSharp** as default codec — `MessagePackWriter` takes `IBufferWriter<byte>`, supports streaming writes
- **Array format with integer keys** — fields written in index order, extend-only friendly
- **`ICodecProvider` abstraction** — wraps MessagePack but allows future format swaps (Protobuf, custom binary)
- **`SerializerV2Adapter`** — wraps legacy `Serializer`/`SerializerWithStringManifest` for backwards compat
- **Explicit field indices** (`[AkkaField(n)]`) and manifest strings — protobuf-style stability guarantees

## Development Guidelines

- **Commit frequently** — after each meaningful milestone (project setup, each interface, each test suite passing, each benchmark)
- **Tests first** — write tests alongside each serializer implementation
- **No Akka.NET core changes** — this is an external PoC. The current Akka.NET serializer APIs require `byte[]`; changing that is separate work.
- Target `net9.0`, use central package management (`Directory.Packages.props`)
- Never use `--no-build` when running tests
