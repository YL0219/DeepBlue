using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aleph.Tests;

/// <summary>
/// Phase 10 boundary tests. These verify the sector boundary rules
/// declared in <c>Aleph/Architecture/PROJECT_DOCTRINE.md</c> by
/// reflecting over the public constructors of the three sector
/// implementations.
///
/// Rules under test:
///   * Aether must not require AppDbContext.
///   * Aether must not require TradingService.
///   * Arbiter must not require TradingService.
///   * Aether must not directly depend on Axiom concrete service types
///     (only the IAxiom contract is allowed, and only for the Python
///     dispatch compromise documented in CELL_RULES.md).
///   * Program.cs must delegate sector registrations to the four
///     bootstrapper extension methods rather than inlining AddXxx calls.
/// </summary>
public class DependencyBoundaryTests
{
    [Fact]
    public void Aether_constructor_does_not_require_AppDbContext()
    {
        var forbidden = typeof(AppDbContext);
        AssertNoConstructorParameterOfType(typeof(Aether), forbidden);
        AssertNoConstructorParameterOfType(typeof(Aether), typeof(IDbContextFactory<AppDbContext>));
    }

    [Fact]
    public void Aether_constructor_does_not_require_TradingService()
    {
        AssertNoConstructorParameterOfType(typeof(Aether), typeof(TradingService));
    }

    [Fact]
    public void Arbiter_constructor_does_not_require_TradingService()
    {
        AssertNoConstructorParameterOfType(typeof(Arbiter), typeof(TradingService));
    }

    [Fact]
    public void Arbiter_constructor_does_not_require_AppDbContext()
    {
        AssertNoConstructorParameterOfType(typeof(Arbiter), typeof(AppDbContext));
        AssertNoConstructorParameterOfType(typeof(Arbiter), typeof(IDbContextFactory<AppDbContext>));
    }

    /// <summary>
    /// Aether may depend on the <see cref="IAxiom"/> contract (documented
    /// compromise for Python dispatch). It must NOT depend on Axiom's
    /// concrete implementation type, nor on any internal Axiom service
    /// that bypasses the public contract.
    /// </summary>
    [Fact]
    public void Aether_does_not_depend_on_Axiom_concrete_types()
    {
        var aetherCtor = SingleConstructor(typeof(Aether));
        var paramTypes = aetherCtor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(typeof(Axiom), paramTypes);
        Assert.DoesNotContain(typeof(PythonDispatcherService), paramTypes);
        Assert.DoesNotContain(typeof(McpToolInvoker), paramTypes);
        Assert.DoesNotContain(typeof(McpToolSchemaAdapter), paramTypes);
        Assert.DoesNotContain(typeof(TradingService), paramTypes);
    }

    [Fact]
    public void Program_delegates_sector_registration_to_bootstrappers()
    {
        var programSource = LoadProgramSource();

        Assert.Contains("AddCirculation(", programSource);
        Assert.Contains("AddAxiom(", programSource);
        Assert.Contains("AddAether(", programSource);
        Assert.Contains("AddArbiter(", programSource);

        // Sanity: Program.cs no longer inlines the heavy sector registrations.
        Assert.DoesNotContain("AddDbContextFactory<AppDbContext>", programSource);
        Assert.DoesNotContain("AddScoped<TradingService>", programSource);
        Assert.DoesNotContain("AddHostedService<HeartbeatService>", programSource);
        Assert.DoesNotContain("AddHostedService<LiverService>", programSource);
        Assert.DoesNotContain("AddHostedService<MlCortexService>", programSource);
        Assert.DoesNotContain("AddHostedService<SleepCycleService>", programSource);
    }

    [Fact]
    public void All_four_bootstrappers_exist_and_are_public_static()
    {
        AssertBootstrapperShape("Aleph.CirculationBootstrapper", "AddCirculation");
        AssertBootstrapperShape("Aleph.AxiomBootstrapper", "AddAxiom");
        AssertBootstrapperShape("Aleph.ArbiterBootstrapper", "AddArbiter");
        AssertBootstrapperShape("Aleph.AetherBootstrapper", "AddAether");
    }

    // ──────────────────────── helpers ────────────────────────

    private static ConstructorInfo SingleConstructor(Type t)
    {
        var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.Single(ctors);
        return ctors[0];
    }

    private static void AssertNoConstructorParameterOfType(Type targetType, Type forbidden)
    {
        var ctor = SingleConstructor(targetType);
        var match = ctor.GetParameters().FirstOrDefault(p => p.ParameterType == forbidden);
        Assert.True(
            match is null,
            $"{targetType.Name} constructor must not require {forbidden.Name}; found parameter '{match?.Name}'.");
    }

    private static void AssertBootstrapperShape(string fullTypeName, string methodName)
    {
        var asm = typeof(Aether).Assembly;
        var type = asm.GetType(fullTypeName);
        Assert.NotNull(type);
        Assert.True(type!.IsAbstract && type.IsSealed, $"{fullTypeName} must be a static class.");

        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
    }

    private static string LoadProgramSource()
    {
        // Walk up from the test bin directory to the repo root, then read Program.cs.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = System.IO.Path.Combine(dir, "Aleph", "Program.cs");
            if (System.IO.File.Exists(candidate))
            {
                return System.IO.File.ReadAllText(candidate);
            }
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        throw new System.IO.FileNotFoundException(
            "Could not locate Aleph/Program.cs by walking up from AppContext.BaseDirectory.");
    }
}
