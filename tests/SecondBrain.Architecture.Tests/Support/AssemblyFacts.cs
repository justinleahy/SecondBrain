using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SecondBrain.Architecture.Tests.Support;

/// <summary>
/// Facts read from an assembly's metadata on disk, without loading anything beyond the assembly itself.
/// </summary>
internal sealed class AssemblyFacts
{
    private static readonly ConcurrentDictionary<string, AssemblyFacts> Cache = new(StringComparer.Ordinal);

    private AssemblyFacts(string name, IReadOnlySet<string> assemblyReferences, IReadOnlySet<string> typeReferences, IReadOnlySet<string> memberReferences, IReadOnlyList<string> pinvokeMethods)
    {
        Name = name;
        AssemblyReferences = assemblyReferences;
        TypeReferences = typeReferences;
        MemberReferences = memberReferences;
        PinvokeMethods = pinvokeMethods;
    }

    public string Name { get; }

    /// <summary>Simple names of every referenced assembly.</summary>
    public IReadOnlySet<string> AssemblyReferences { get; }

    /// <summary>Every type reference as <c>Namespace.Name</c>; nested types as <c>Namespace.Outer+Inner</c>.</summary>
    public IReadOnlySet<string> TypeReferences { get; }

    /// <summary>Every member reference whose parent is a type reference, as <c>Namespace.Name::Member</c>.</summary>
    public IReadOnlySet<string> MemberReferences { get; }

    /// <summary>Every method definition carrying <see cref="MethodAttributes.PinvokeImpl"/>, as <c>Type::Method</c>.</summary>
    public IReadOnlyList<string> PinvokeMethods { get; }

    /// <summary>Simple names of referenced assemblies that belong to SecondBrain.</summary>
    public IReadOnlySet<string> SecondBrainReferences =>
        AssemblyReferences.Where(Layers.IsSecondBrainAssembly).ToHashSet(StringComparer.Ordinal);

    public static AssemblyFacts For(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var location = assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException($"Assembly {assembly.FullName} has no location on disk.");
        }

        return Cache.GetOrAdd(location, Read);
    }

    private static AssemblyFacts Read(string location)
    {
        using var stream = File.OpenRead(location);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var name = reader.GetString(reader.GetAssemblyDefinition().Name);

        var assemblyReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in reader.AssemblyReferences)
        {
            assemblyReferences.Add(reader.GetString(reader.GetAssemblyReference(handle).Name));
        }

        var typeReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeReferences)
        {
            typeReferences.Add(TypeReferenceName(reader, handle));
        }

        var memberReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                memberReferences.Add($"{TypeReferenceName(reader, (TypeReferenceHandle)member.Parent)}::{reader.GetString(member.Name)}");
            }
        }

        var pinvokeMethods = new List<string>();
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                pinvokeMethods.Add($"{TypeDefinitionName(reader, method.GetDeclaringType())}::{reader.GetString(method.Name)}");
            }
        }

        return new AssemblyFacts(name, assemblyReferences, typeReferences, memberReferences, pinvokeMethods);
    }

    private static string TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{TypeReferenceName(reader, (TypeReferenceHandle)type.ResolutionScope)}+{name}";
        }

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static string TypeDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return $"{TypeDefinitionName(reader, declaring)}+{name}";
        }

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }
}
