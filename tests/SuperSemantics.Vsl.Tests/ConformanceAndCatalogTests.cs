using SuperSemantics.Vsl.Catalog;
using SuperSemantics.Vsl.Conformance;

namespace SuperSemantics.Vsl.Tests;

public class ConformanceTests
{
    [Fact]
    public async Task The_reference_adapter_is_conformant()
    {
        Assert.Empty(await ConformanceSuite.RunAsync(new PlainReferenceAdapter()));
    }

    private sealed class NeverDenies : IVslAdapter
    {
        public CompiledGate CompilePreNode(PreNode preNode) => _ => Task.CompletedTask;
        public CompiledGate CompileInvariant(Invariant invariant) => _ => Task.CompletedTask;
    }

    private sealed class GenericInvariantDenial : IVslAdapter
    {
        private readonly PlainReferenceAdapter _inner = new();
        public CompiledGate CompilePreNode(PreNode preNode) => _inner.CompilePreNode(preNode);
        public CompiledGate CompileInvariant(Invariant invariant) => async input =>
        {
            if (!await invariant.Rule(input))
            {
                throw new AutomationDeniedException("denied", "x");
            }
        };
    }

    private sealed class Sticky : IVslAdapter
    {
        private readonly PlainReferenceAdapter _inner = new();
        public CompiledGate CompilePreNode(PreNode preNode)
        {
            var denied = false;
            return async input =>
            {
                if (denied) throw new AutomationDeniedException("still denied", "x");
                try
                {
                    await _inner.CompilePreNode(preNode)(input);
                }
                catch (AutomationDeniedException)
                {
                    denied = true;
                    throw;
                }
            };
        }

        public CompiledGate CompileInvariant(Invariant invariant) => _inner.CompileInvariant(invariant);
    }

    private sealed class ReturnsNull : IVslAdapter
    {
        public CompiledGate CompilePreNode(PreNode preNode) => null!;
        public CompiledGate CompileInvariant(Invariant invariant) => null!;
    }

    [Fact]
    public async Task An_adapter_that_never_denies_fails_checks_1_3_and_5()
    {
        var failures = await ConformanceSuite.RunAsync(new NeverDenies());
        Assert.Contains(failures, f => f.StartsWith("Check 1", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Check 3", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Check 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invariant_denial_must_be_the_specific_type()
    {
        var failures = await ConformanceSuite.RunAsync(new GenericInvariantDenial());
        Assert.Single(failures);
        Assert.StartsWith("Check 3", failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_leaking_between_calls_is_caught()
    {
        var failures = await ConformanceSuite.RunAsync(new Sticky());
        Assert.Contains(failures, f => f.Contains("third call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_null_gate_stops_the_suite()
    {
        await Assert.ThrowsAsync<ConformanceException>(() => ConformanceSuite.RunAsync(new ReturnsNull()));
    }

    [Fact]
    public async Task An_invariant_violation_names_its_terminal_state()
    {
        var halt = new TerminalState("halted", "No further automated refunds.");
        var invariant = new Invariant("never-refund-twice", "d", _ => Task.FromResult(false), new AssuranceBasis(true, F2Modification.None), onViolation: halt);
        var gate = new PlainReferenceAdapter().CompileInvariant(invariant);
        var ex = await Assert.ThrowsAsync<InvariantViolationException>(() => gate(null));
        Assert.Equal("halted", ex.TerminalStateName);
        Assert.Equal("never-refund-twice", ex.InvariantName);
    }
}

public class CatalogTests
{
    [Fact]
    public void Counts_come_from_the_data_not_the_metadata()
    {
        Assert.Equal(45, DriftCatalog.LoadDriftClasses().Count);
        Assert.Equal(10, DriftCatalog.LoadStabilisationOperators().Count);
        Assert.Equal(46, DriftCatalog.LoadLegionPatterns().Count);
        Assert.Equal(15, DriftCatalog.LoadDriftClasses().Values.Count(dc => dc.Category == "external"));
    }

    [Fact]
    public void Only_seven_heuristics_carry_real_confidence()
    {
        var validated = DriftCatalog.OnlyValidatedHeuristics();
        Assert.Equal(7, validated.Values.Sum(list => list.Count));
        Assert.Equal(3, validated["DC-E13"].Count);
        Assert.Equal(2, validated["DC-I11"].Count);
        Assert.Equal(2, validated["CLUSTER-HANDOVER"].Count);
        var total = DriftCatalog.LoadLegionPatterns().Values.Sum(e => e.Legions.Sum(l => l.Heuristics.Count));
        Assert.Equal(113, total);
    }

    [Fact]
    public void Validate_surfaces_the_three_known_findings()
    {
        var findings = DriftCatalog.Validate();
        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, f => f.Contains("claims 36", StringComparison.Ordinal) && f.Contains("is 45", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Contains("['CLUSTER-HANDOVER']", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.StartsWith("10 drift classes have zero Legion", StringComparison.Ordinal));
    }

    [Fact]
    public void Compound_primary_so_strings_split_into_codes()
    {
        var compound = DriftCatalog.LoadDriftClasses().Values.First(dc => dc.PrimarySo.Contains(',', StringComparison.Ordinal));
        Assert.True(compound.PrimarySoCodes.Count > 1);
        Assert.All(compound.PrimarySoCodes, code => Assert.StartsWith("SO-", code, StringComparison.Ordinal));
    }
}
