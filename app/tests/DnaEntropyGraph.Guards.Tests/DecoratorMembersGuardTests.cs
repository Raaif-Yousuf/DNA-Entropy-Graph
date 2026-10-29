using System.Reflection;
using System.Runtime.CompilerServices;
using DnaEntropyGraph.App.Startup;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #909, wired-to-nothing: a decorator over an interface that has a default interface method (DIM) compiles without forwarding it and silently
/// inherits the default body instead of the inner implementation's override (feat/245 audit decorators; feat/109 <c>TryUpsertIfPhaseAsync</c>).
/// A decorator is a non-abstract class that implements interface I and takes an I in its constructor; DECISION: also a <c>Func&lt;I&gt;</c> or
/// <c>Lazy&lt;I&gt;</c> parameter, because a lazy or factory-built inner is still wrapped and still bypassed by an inherited default.
/// Every member of I and of I's base interfaces, DIM or not, must be implemented by a method declared on the class itself.
/// The per-branch guards from #245 and #109 are subsumed by this one and can be deleted when those branches land.
/// </summary>
public class DecoratorMembersGuardTests
{
    /// <summary>"Namespace.Class|Namespace.IInterface|Member" to why the class may inherit that member body. The design is that it stays empty; a stale entry (class gone or now compliant) fails.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal);

    internal sealed record Violation(Type Class, Type Interface, string Member)
    {
        public string Key => $"{Class.FullName}|{Interface.FullName}|{Member}";
        public override string ToString() => $"{Class.FullName} decorates {Interface.Name} but does not declare {Member} (it maps to the interface's own default body)";
    }

    /// <summary>
    /// The interfaces of <paramref name="cls"/> that it wraps: an implemented interface J for which some constructor parameter, or a generic argument of one
    /// (Func, Lazy, IEnumerable, Task, nested), is assignable to J. That covers a derived interface, a concrete inner type and a collection of inners.
    /// Known limit: an inner handed in by property or factory method is not chased. Open generic type definitions are skipped (a generic decorator is
    /// not enumerable closed from reflection; none exists in app/src today).
    /// </summary>
    internal static IEnumerable<Type> DecoratedInterfaces(Type cls)
    {
        if (cls.IsInterface || cls.IsEnum || cls.IsGenericTypeDefinition || cls.IsDefined(typeof(CompilerGeneratedAttribute), false)) yield break;
        var candidates = new HashSet<Type>();
        void Collect(Type t)
        {
            if (!candidates.Add(t) || !t.IsGenericType) return;
            foreach (var arg in t.GetGenericArguments()) Collect(arg);
        }
        foreach (var ctor in cls.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            foreach (var p in ctor.GetParameters()) Collect(p.ParameterType);
        foreach (var j in cls.GetInterfaces())
            if (candidates.Any(c => j.IsAssignableFrom(c))) yield return j;
    }

    internal static IEnumerable<Violation> FindViolations(IEnumerable<Type> types)
    {
        foreach (var cls in types)
            foreach (var iface in DecoratedInterfaces(cls))
            {
                var map = cls.GetInterfaceMap(iface);
                for (var i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.TargetMethods[i].DeclaringType!.IsInterface)
                        yield return new Violation(cls, iface, map.InterfaceMethods[i].Name);
                }
            }
    }

    /// <summary>The six app/src assemblies, named by a type in each so a dropped reference cannot silently shrink the scan.</summary>
    internal static IReadOnlyList<Assembly> AppSrcAssemblies() =>
    [
        typeof(ServiceRegistration).Assembly,
        typeof(DnaEntropyGraph.Core.Cloud.IComputeGateway).Assembly,
        typeof(DnaEntropyGraph.Presentation.ViewModels.ShellViewModel).Assembly,
        typeof(DnaEntropyGraph.Cloud.ResilientComputeGateway).Assembly,
        typeof(DnaEntropyGraph.Persistence.CostLedgerRepository).Assembly,
        typeof(DnaEntropyGraph.LocalEngine.LocalEngineInstaller).Assembly,
    ];

    private static IEnumerable<Type> AppTypes() => AppSrcAssemblies().SelectMany(a => a.GetTypes());
    // ---- planted types (test assembly only) ----
    public interface IProbe { Task<int> Plain(); Task<int> WithDefault() => Task.FromResult(-1); }
    public interface IProbeChild : IProbe { int Child { get; } }
    public interface IGen<T> { T Get(); T GetOr(T fallback) => fallback; }

    public sealed class InheritsDefault(IProbe inner) : IProbe { public Task<int> Plain() => inner.Plain(); }
    public sealed class Forwards(IProbe inner) : IProbe { public Task<int> Plain() => inner.Plain(); public Task<int> WithDefault() => inner.WithDefault(); }
    public sealed class ExplicitForwards(IProbe inner) : IProbe { Task<int> IProbe.Plain() => inner.Plain(); Task<int> IProbe.WithDefault() => inner.WithDefault(); }
    public sealed class NotADecorator(IProbe unrelated) { public IProbe Unrelated { get; } = unrelated; }
    public sealed class ChildMissesBase(IProbeChild inner) : IProbeChild
    {
        public int Child => inner.Child;
        public Task<int> Plain() => inner.Plain();
    }
    public sealed class LazyInherits(Func<IProbe> inner) : IProbe { public Task<int> Plain() => inner().Plain(); }
    public sealed class GenInherits(IGen<string> inner) : IGen<string> { public string Get() => inner.Get(); }
    public sealed class GenForwards(IGen<string> inner) : IGen<string> { public string Get() => inner.Get(); public string GetOr(string f) => inner.GetOr(f); }
    public class BaseForwards(IProbe inner) : IProbe { public virtual Task<int> Plain() => inner.Plain(); public virtual Task<int> WithDefault() => inner.WithDefault(); }
    public sealed class DerivedFromForwarder(IProbe inner) : BaseForwards(inner) { }

    public interface IProbeEx : IProbe { }
    public sealed class FakeProbe : IProbe { public Task<int> Plain() => Task.FromResult(1); public Task<int> WithDefault() => Task.FromResult(2); }
    public sealed class DerivedInterfaceParam(IProbeEx inner) : IProbe { public Task<int> Plain() => inner.Plain(); }
    public sealed class ConcreteParam(FakeProbe inner) : IProbe { public Task<int> Plain() => inner.Plain(); }
    public sealed class EnumerableParam(IEnumerable<IProbe> inners) : IProbe { public Task<int> Plain() => inners.First().Plain(); }
    public sealed class TaskParam(Func<string, Task<IProbe>> inner) : IProbe { public Task<int> Plain() => inner("x").Result.Plain(); }
    public abstract class AbstractInheritsDefault(IProbe inner) : IProbe { public Task<int> Plain() => inner.Plain(); }
    public readonly struct StructInheritsDefault(IProbe inner) : IProbe { public Task<int> Plain() => inner.Plain(); }
    public sealed class GenericDecorator<T>(IProbe inner) : IProbe { public Task<int> Plain() => inner.Plain(); }

    [Fact]
    public void A_ctor_parameter_assignable_to_the_interface_makes_a_decorator_whatever_its_shape()
    {
        foreach (var t in new[] { typeof(DerivedInterfaceParam), typeof(ConcreteParam), typeof(EnumerableParam), typeof(TaskParam), typeof(StructInheritsDefault) })
            FindViolations([t]).Select(x => x.Member).ShouldBe([nameof(IProbe.WithDefault)], t.Name);
    }

    [Fact]
    public void An_abstract_decorator_that_inherits_a_default_body_is_reported()
    {
        FindViolations([typeof(AbstractInheritsDefault)]).Select(x => x.Member).ShouldBe([nameof(IProbe.WithDefault)]);
    }

    [Fact]
    public void A_subclass_of_a_forwarding_base_is_clean_and_an_open_generic_does_not_throw()
    {
        FindViolations([typeof(DerivedFromForwarder), typeof(BaseForwards)]).ShouldBeEmpty();
        Should.NotThrow(() => FindViolations([typeof(GenericDecorator<>)]).ToList());
    }

    [Fact]
    public void The_scan_discovers_known_decorators_in_all_six_src_projects()
    {
        AppSrcAssemblies().Select(a => a.GetName().Name).Order().ShouldBe(
        [
            "DnaEntropyGraph.App", "DnaEntropyGraph.Cloud", "DnaEntropyGraph.Core",
            "DnaEntropyGraph.LocalEngine", "DnaEntropyGraph.Persistence", "DnaEntropyGraph.Presentation",
        ]);
        var found = AppTypes().Where(t => DecoratedInterfaces(t).Any()).Select(t => t.Name).ToHashSet();
        foreach (var known in new[] { "ResilientComputeGateway", "RecordingNavigator", "ReconcileOnReconnect", "CatalogPricingSource" })
            found.ShouldContain(known);
    }

    [Fact]
    public void A_decorator_inheriting_a_default_body_is_reported_by_class_interface_and_member()
    {
        var v = FindViolations([typeof(InheritsDefault)]).ToList();
        v.Count.ShouldBe(1);
        v[0].Class.ShouldBe(typeof(InheritsDefault));
        v[0].Interface.ShouldBe(typeof(IProbe));
        v[0].Member.ShouldBe(nameof(IProbe.WithDefault));
        v[0].ToString().ShouldContain("InheritsDefault");
        v[0].ToString().ShouldContain("IProbe");
        v[0].ToString().ShouldContain("WithDefault");
    }

    [Fact]
    public void A_decorator_that_declares_every_member_is_clean_including_explicit_implementations()
    {
        FindViolations([typeof(Forwards), typeof(ExplicitForwards)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_class_that_takes_the_interface_without_implementing_it_is_not_a_decorator()
    {
        DecoratedInterfaces(typeof(NotADecorator)).ShouldBeEmpty();
        FindViolations([typeof(NotADecorator)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_member_of_a_base_interface_is_checked_too()
    {
        var v = FindViolations([typeof(ChildMissesBase)]).Select(x => (x.Interface, x.Member)).ToList();
        v.ShouldBe([(typeof(IProbe), nameof(IProbe.WithDefault))]);
    }

    [Fact]
    public void A_Func_wrapped_inner_counts_as_a_decorator()
    {
        FindViolations([typeof(LazyInherits)]).Select(x => x.Member).ShouldBe([nameof(IProbe.WithDefault)]);
    }

    [Fact]
    public void Closed_generic_interfaces_are_checked()
    {
        FindViolations([typeof(GenInherits)]).Select(x => x.Member).ShouldBe(["GetOr"]);
        FindViolations([typeof(GenForwards)]).ShouldBeEmpty();
    }


    [Fact]
    public void Every_decorator_in_app_src_declares_every_member_of_its_interface()
    {
        var violations = FindViolations(AppTypes()).Where(v => !Allowed.ContainsKey(v.Key)).Select(v => v.ToString()).ToList();
        violations.ShouldBeEmpty("A decorator must declare (forward) every member of the interface it wraps, default-bodied ones included; add an override that calls the inner.\n" + string.Join("\n", violations));
    }

    [Fact]
    public void The_allowlist_has_a_reason_per_entry_and_no_stale_entries()
    {
        foreach (var (key, reason) in Allowed) reason.Trim().ShouldNotBeEmpty($"{key} needs a reason");
        var live = FindViolations(AppTypes()).Select(v => v.Key).ToHashSet();
        foreach (var key in Allowed.Keys)
            live.ShouldContain(key, $"stale allowlist entry {key}: the class is gone or now forwards every member; delete the entry");
    }
}
