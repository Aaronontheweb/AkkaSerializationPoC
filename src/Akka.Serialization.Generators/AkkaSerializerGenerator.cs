using Microsoft.CodeAnalysis;

namespace Akka.Serialization.Generators;

[Generator]
public class AkkaSerializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Phase 5 will implement the full generator
        // For now, this is just a scaffold to verify the project structure works
    }
}
