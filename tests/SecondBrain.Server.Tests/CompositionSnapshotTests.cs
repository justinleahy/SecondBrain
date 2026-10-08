using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecondBrain.Server.Composition;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Pins the daemon's DI composition so refactors can change it only deliberately.</summary>
public sealed class CompositionSnapshotTests
{
    private static readonly Type[] PinnedServiceTypes = [typeof(TimeProvider), typeof(IDataProtectionProvider), typeof(IHostedService), typeof(IStartupFilter)];

    [Fact]
    public void CompositionMatchesGolden()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(global::Program).Assembly.GetName().Name,
        });
        var before = new HashSet<ServiceDescriptor>(builder.Services, ReferenceEqualityComparer.Instance);

        // Program.cs order. The provider is never built; AddKeyRing sets the private umask at registration.
        builder.Services.AddConfiguration();
        builder.Services.AddKeyRing();
        builder.Services.AddStorage();
        builder.Services.AddDomain();
        builder.Services.AddProviders();
        builder.Services.AddPrivacy();
        builder.Services.AddHttpHost();
        builder.Services.AddAuth();
        builder.Services.AddLimits();

        var actual = new StringBuilder();
        foreach (var descriptor in builder.Services)
        {
            if (before.Contains(descriptor) || !Kept(descriptor)) continue;
            actual.Append(Render(descriptor)).Append('\n');
        }
        GoldenFile.AssertMatches("composition.txt", actual.ToString());
    }

    private static bool Kept(ServiceDescriptor descriptor)
    {
        if (PinnedServiceTypes.Contains(descriptor.ServiceType)) return true;
        return Types(descriptor).Any(IsSecondBrainType);
    }

    private static IEnumerable<Type> Types(ServiceDescriptor descriptor)
    {
        yield return descriptor.ServiceType;
        var implementation = descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;
        if (implementation is not null) yield return implementation;
        var instance = descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance;
        if (instance is not null) yield return instance.GetType();
    }

    private static bool IsSecondBrainType(Type type)
    {
        var assembly = type.Assembly.GetName().Name;
        if (assembly is not null && (assembly.StartsWith("SecondBrain.", StringComparison.Ordinal) || assembly == "brain")) return true;
        return type.IsGenericType && type.GetGenericArguments().Any(IsSecondBrainType);
    }

    private static string Render(ServiceDescriptor descriptor)
    {
        var line = new StringBuilder();
        line.Append(descriptor.Lifetime).Append(' ').Append(Name(descriptor.ServiceType));
        if (descriptor.IsKeyedService) line.Append('[').Append(descriptor.ServiceKey).Append(']');
        line.Append(" -> ");
        var implementation = descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;
        var instance = descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance;
        if (implementation is not null) line.Append(Name(implementation));
        else if (instance is not null) line.Append("instance:").Append(Name(instance.GetType()));
        else line.Append("factory");
        return line.ToString();
    }

    private static string Name(Type type)
    {
        if (!type.IsGenericType) return type.Name;
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        var arguments = type.IsGenericTypeDefinition
            ? new string(',', type.GetGenericArguments().Length - 1)
            : string.Join(", ", type.GetGenericArguments().Select(Name));
        return name + "<" + arguments + ">";
    }
}
