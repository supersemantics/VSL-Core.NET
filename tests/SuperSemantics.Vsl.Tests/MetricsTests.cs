namespace SuperSemantics.Vsl.Tests;

public class MetricsTests
{
    [Theory]
    [InlineData(false, F2Modification.Full, AssuranceLevel.Low)]
    [InlineData(false, F2Modification.Partial, AssuranceLevel.Low)]
    [InlineData(false, F2Modification.Indirect, AssuranceLevel.Low)]
    [InlineData(false, F2Modification.None, AssuranceLevel.Low)]
    [InlineData(true, F2Modification.Full, AssuranceLevel.High)]
    [InlineData(true, F2Modification.Partial, AssuranceLevel.Medium)]
    [InlineData(true, F2Modification.Indirect, AssuranceLevel.Medium)]
    [InlineData(true, F2Modification.None, AssuranceLevel.Low)]
    public void Assurance_level_is_derived_from_f1_and_f2(bool f1, F2Modification f2, AssuranceLevel expected)
    {
        Assert.Equal(expected, new AssuranceBasis(f1, f2).DerivedLevel);
    }

    [Fact]
    public void An_undefined_f2_value_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AssuranceBasis(true, (F2Modification)42));
    }

    [Fact]
    public void Exactly_on_the_bar_does_not_pass()
    {
        Assert.False(new GammaEstimate(1.1).Sufficient(1.1));
        Assert.True(new GammaEstimate(1.1000001).Sufficient(1.1));
    }

    [Fact]
    public void A_larger_declared_error_raises_the_bar_a_smaller_one_does_not()
    {
        Assert.Equal(1.1, new GammaEstimate(2, deltaEstimationError: 0.1).RequiredGammaHat(1.1));
        Assert.Equal(1.2, new GammaEstimate(2, deltaEstimationError: 0.2).RequiredGammaHat(1.1));
        Assert.False(new GammaEstimate(1.2, deltaEstimationError: 0.2).Sufficient(1.1));
    }

    [Fact]
    public void Every_pass_means_robust_gamma_above_one()
    {
        var random = new Random(7);
        for (var i = 0; i < 10_000; i++)
        {
            var estimate = new GammaEstimate(random.NextDouble() * 3, random.NextDouble() * 0.5, measuredAt: 0);
            var threshold = random.NextDouble() < 0.5 ? Thresholds.RobustGammaDefault : Thresholds.RobustGammaHighStakes;
            if (estimate.Sufficient(threshold))
            {
                Assert.True(estimate.RobustGamma() > 1);
            }
        }
    }

    [Fact]
    public void Ges_and_eef_follow_the_specification()
    {
        Assert.Equal(0.75, Ges.Compute(0.25, 1.0).Score, 12);
        Assert.True(Ges.Compute(0.9, 1.0).TriggersSpecificationUpdate(0.5));
        Assert.Throws<ArgumentException>(() => Ges.Compute(1, 0));
        Assert.Equal(EefZone.Insufficient, Eef.Compute(0.5, 1).Zone(new GammaEstimate(0.9)));
        Assert.Equal(EefZone.Optimal, Eef.Compute(0.5, 1).Zone(new GammaEstimate(2)));
        Assert.Equal(EefZone.OverGovernance, Eef.Compute(2, 1).Zone(new GammaEstimate(2)));
        Assert.Equal(6.0, Delta.FromGamma(2, 3).Value);
    }

    [Fact]
    public void Constructs_are_built_without_an_assurance_basis()
    {
        var preNode = new PreNode("p", "d", _ => Task.FromResult(new GammaEstimate(2)), gammaThreshold: 1.2);
        var invariant = new Invariant("i", "d", _ => Task.FromResult(true));
        var state = new AllowedState("a", "d", "refund under cap");
        Assert.Equal(1.2, preNode.GammaThreshold);
        Assert.True(invariant.CannotBeBypassed);
        Assert.Equal("refund under cap", state.Measurement);
        Assert.Throws<ArgumentException>(() =>
            new PreNode("p", "d", _ => Task.FromResult(new GammaEstimate(2)), fallback: new Fallback(MaxRetries: -1)));
    }

    [Fact]
    public void A_construct_without_a_basis_still_has_one_for_older_adapters_and_reports_no_level()
    {
        var preNode = new PreNode("p", "d", _ => Task.FromResult(new GammaEstimate(2)));
        Assert.Equal(F2Modification.None, preNode.AssuranceBasis.F2Modification);
        Assert.NotEqual(new AssuranceBasis(true, F2Modification.None), preNode.AssuranceBasis);
#pragma warning disable CS0618 // the deprecated members still behave as documented
        Assert.Null(preNode.AssuranceLevel);
        Assert.Null(new Invariant("i", "d", _ => Task.FromResult(true)).AssuranceLevel);
        Assert.Null(new AllowedState("a", "d", "m").AssuranceLevel);
#pragma warning restore CS0618
    }

    [Fact]
    public void The_obsolete_constructors_still_work_and_keep_the_basis_they_were_given()
    {
#pragma warning disable CS0618
        var basis = new AssuranceBasis(true, F2Modification.Partial);
        var preNode = new PreNode("p", "d", _ => Task.FromResult(new GammaEstimate(2)), basis);
        var invariant = new Invariant("i", "d", _ => Task.FromResult(true), basis);
        var state = new AllowedState("a", "d", basis, "m");
        Assert.Same(basis, preNode.AssuranceBasis);
        Assert.Equal(AssuranceLevel.Medium, preNode.AssuranceLevel);
        Assert.Equal(AssuranceLevel.Medium, invariant.AssuranceLevel);
        Assert.Equal(AssuranceLevel.Medium, state.AssuranceLevel);
        Assert.Throws<ArgumentNullException>(() => new PreNode("p", "d", _ => Task.FromResult(new GammaEstimate(2)), (AssuranceBasis)null!));
#pragma warning restore CS0618
    }

    [Fact]
    public void The_constructors_that_take_a_basis_are_marked_obsolete()
    {
        foreach (var type in new[] { typeof(PreNode), typeof(Invariant), typeof(AllowedState) })
        {
            var withBasis = type.GetConstructors().Single(c => c.GetParameters().Any(p => p.ParameterType == typeof(AssuranceBasis)));
            var obsolete = (ObsoleteAttribute?)Attribute.GetCustomAttribute(withBasis, typeof(ObsoleteAttribute));
            Assert.Equal(AssuranceDeprecation.Basis, obsolete?.Message);
            Assert.False(obsolete!.IsError);
        }
    }
}

public class GovernanceTests
{
    private static readonly GovernanceAuthority Board = new("safety-board", "oncall@example.com");

    [Fact]
    public void Re_enablement_returns_a_new_instance_and_its_authorisation_together()
    {
        var old = Instance.New();
        var (fresh, transition) = Governance.RequestReEnablement(old, Board, "jane.doe", new Evidence("root cause", "add heartbeat invariant"));
        Assert.Equal(old.InstanceId, fresh.PredecessorInstanceId);
        Assert.Equal(1, fresh.Generation);
        Assert.NotEqual(old.IdentityKey, fresh.IdentityKey);
        Assert.Equal("jane.doe", transition.AuthorisedBy);
        Assert.Equal(64, transition.AuthorisationEvidenceHash.Length);
    }

    [Fact]
    public void The_bare_re_enable_mechanism_is_not_public()
    {
        var method = typeof(Instance).GetMethod("ReEnable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.Null(method);
    }

    [Fact]
    public void Evidence_and_approver_must_be_real()
    {
        Assert.Throws<ArgumentException>(() => new Evidence(" ", "proposal"));
        Assert.Throws<ArgumentException>(() => new Evidence("cause", ""));
        Assert.Throws<ArgumentException>(() => HumanAuthorisedTransition.Authorise(Board, "  ", new Evidence("a", "b")));
    }

    [Fact]
    public void A_specification_activates_only_with_a_binding_from_verified_proofing()
    {
        var spec = new Specification("refunds", "1.0", Board);
        Assert.Throws<InvalidOperationException>(() => spec.Activate());
        Assert.Throws<ArgumentException>(() => new Binding(new Proofing(false, "registry"), "board"));
        var active = spec.WithBinding(new Binding(new Proofing(true, "registry"), "board")).Activate();
        Assert.NotNull(active.ActivatedAt);
        Assert.Throws<InvalidOperationException>(() => active.Activate());
    }

    [Fact]
    public void Cluster_must_cover_its_constituents()
    {
        var a = new IdentityKey("a");
        var b = new IdentityKey("b");
        var key = ClusterKey.FromConstituents([a]);
        Assert.Throws<ArgumentException>(() => new Cluster("c", key, [a, b]));
        var cluster = new Cluster("c", key, [a]).AddConstituent(b);
        Assert.Equal(ClusterKey.FromConstituents([b, a]).Value, cluster.ClusterKey.Value);
    }

    [Fact]
    public void Invariant_registry_refuses_duplicates()
    {
        var name = $"unique-{Guid.NewGuid():N}";
        InvariantRegistry.Register(new Invariant(name, "d", _ => Task.FromResult(true)));
        Assert.Throws<ArgumentException>(() =>
            InvariantRegistry.Register(new Invariant(name, "d", _ => Task.FromResult(true))));
        Assert.Contains(InvariantRegistry.Registered(), i => i.Name == name);
    }
}
