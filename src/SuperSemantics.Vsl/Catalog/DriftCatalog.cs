using System.Text.Json;

namespace SuperSemantics.Vsl.Catalog;

/// <summary>A Drift Class (Paper 5, "Toward a Basis Representation of Drift Forensics").</summary>
public sealed record DriftClass(
    string Code,
    string Name,
    string Category,
    string Tier,
    string OperationalDefinition,
    IReadOnlyList<string> PrimaryLegions,
    string PrimarySo,
    string? Contraindications = null,
    string? Notes = null,
    string References = "")
{
    /// <summary>The individual SO codes in a possibly compound <see cref="PrimarySo"/>
    /// (for example "SO-7, SO-8" or "SO-9 (external oversight)").</summary>
    public IReadOnlyList<string> PrimarySoCodes =>
        PrimarySo.Split(',')
            .Select(part => part.Trim())
            .Select(part => part.Contains('(') ? part[..part.IndexOf('(')].Trim() : part)
            .Where(part => part.Length > 0)
            .ToArray();
}

/// <summary>A Stabilisation Operator.</summary>
public sealed record StabilisationOperator(string Code, string Name, string ProposedFunction, IReadOnlyList<string> PrimaryDcTargets, IReadOnlyList<string> ContraindicatedOn);

/// <summary>A compound drift mode.</summary>
public sealed record CompoundDriftMode(string Code, string Name, IReadOnlyList<string> Components, string Mechanism, string Confidence);

/// <summary>A boundary case between Drift Classes.</summary>
public sealed record BoundaryCase(string Key, IReadOnlyList<string> Classes, string DistinguishingTest, string MisdiagnosisRisk, string WhyItMatters);

/// <summary>A critical contraindication.</summary>
public sealed record CriticalContraindication(string Key, string Prohibition, string PredictedFailureState, string Description, string ObservableSignature);

/// <summary>A case study.</summary>
public sealed record CaseStudy(string Key, string PrimaryDc, IReadOnlyList<string> SecondaryDc, string CompoundMode, string ProposedSo, string Confidence, string Reference);

/// <summary>One Legion detection heuristic.</summary>
public sealed record LegionHeuristic(string PatternType, IReadOnlyList<string> Patterns, string Confidence, string Description);

/// <summary>A Legion and its heuristics.</summary>
public sealed record Legion(string Code, string Name, IReadOnlyList<LegionHeuristic> Heuristics);

/// <summary>A top-level entry of the Legion patterns file.</summary>
public sealed record LegionEntry(string Code, string Name, string Tier, string Category, IReadOnlyList<Legion> Legions);

/// <summary>
/// The Drift Class / Stabilisation Operator / Legion catalog, the same data vsl-core ships.
/// </summary>
/// <remarks>
/// Not a finished, validated detection system: 106 of the 113 Legion heuristics are self-labelled
/// SPECULATIVE. Use <see cref="OnlyValidatedHeuristics"/> for the seven that carry real confidence.
/// <see cref="Validate"/> surfaces the data's known quality findings; they are never silently fixed.
/// </remarks>
public static class DriftCatalog
{
    /// <summary>The five categories, in file order.</summary>
    public static readonly IReadOnlyList<string> Categories = ["external", "internal", "systemic", "linguistic", "authority"];

    private static readonly Lazy<JsonDocument> RawDriftClasses = new(() => Read("drift_classes.json"));
    private static readonly Lazy<JsonDocument> RawLegionPatterns = new(() => Read("legion_patterns.json"));
    private static readonly Lazy<OrderedDictionary<string, DriftClass>> DriftClasses = new(LoadDriftClassesCore);
    private static readonly Lazy<OrderedDictionary<string, StabilisationOperator>> Operators = new(LoadOperatorsCore);
    private static readonly Lazy<OrderedDictionary<string, LegionEntry>> Legions = new(LoadLegionPatternsCore);

    /// <summary>All Drift Classes, keyed by code. 45 in the data (not the 36 its metadata claims).</summary>
    public static IReadOnlyDictionary<string, DriftClass> LoadDriftClasses() => DriftClasses.Value;

    /// <summary>One Drift Class by code.</summary>
    public static DriftClass GetDriftClass(string code) =>
        DriftClasses.Value.TryGetValue(code, out var dc) ? dc : throw new KeyNotFoundException($"No Drift Class with code '{code}'.");

    /// <summary>The Drift Classes of one tier (A, B, C or D).</summary>
    public static IReadOnlyList<DriftClass> ClassesByTier(string tier) => DriftClasses.Value.Values.Where(dc => dc.Tier == tier).ToArray();

    /// <summary>The 10 Stabilisation Operators: SO-1 to SO-9 plus SO-8b.</summary>
    public static IReadOnlyDictionary<string, StabilisationOperator> LoadStabilisationOperators() => Operators.Value;

    /// <summary>One Stabilisation Operator by code.</summary>
    public static StabilisationOperator GetStabilisationOperator(string code) =>
        Operators.Value.TryGetValue(code, out var so) ? so : throw new KeyNotFoundException($"No Stabilisation Operator with code '{code}'.");

    /// <summary>Compound drift modes.</summary>
    public static IReadOnlyDictionary<string, CompoundDriftMode> LoadCompoundDriftModes() =>
        Section(RawDriftClasses.Value, "compound_drift_modes", (code, d) =>
            new CompoundDriftMode(code, Str(d, "name"), Strings(d, "components"), Str(d, "mechanism"), Str(d, "confidence")));

    /// <summary>Boundary cases.</summary>
    public static IReadOnlyDictionary<string, BoundaryCase> LoadBoundaryCases() =>
        Section(RawDriftClasses.Value, "boundary_cases", (key, d) =>
            new BoundaryCase(key, Strings(d, "classes"), Str(d, "distinguishing_test"), Str(d, "misdiagnosis_risk"), Str(d, "why_it_matters")));

    /// <summary>Critical contraindications.</summary>
    public static IReadOnlyDictionary<string, CriticalContraindication> LoadCriticalContraindications() =>
        Section(RawDriftClasses.Value, "critical_contraindications", (key, d) =>
            new CriticalContraindication(key, Str(d, "prohibition"), Str(d, "predicted_failure_state"), Str(d, "description"), Str(d, "observable_signature")));

    /// <summary>Case studies.</summary>
    public static IReadOnlyDictionary<string, CaseStudy> LoadCaseStudies() =>
        Section(RawDriftClasses.Value, "case_studies", (key, d) =>
            new CaseStudy(key, Str(d, "primary_dc"), Strings(d, "secondary_dc"), Str(d, "compound_mode"), Str(d, "proposed_so"), Str(d, "confidence"), Str(d, "reference")));

    /// <summary>Required monitoring frequency, label to Drift Class codes.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadMonitoringFrequency()
    {
        var result = new OrderedDictionary<string, IReadOnlyList<string>>();
        if (RawDriftClasses.Value.RootElement.TryGetProperty("monitoring_frequency_by_dc", out var section))
        {
            foreach (var p in section.EnumerateObject())
            {
                result[p.Name] = p.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
            }
        }

        return result;
    }

    /// <summary>Tier letter to its definition.</summary>
    public static IReadOnlyDictionary<string, string> TierDefinitions()
    {
        var result = new OrderedDictionary<string, string>();
        foreach (var p in RawDriftClasses.Value.RootElement.GetProperty("tier_definitions").EnumerateObject())
        {
            result[p.Name] = p.Value.GetString() ?? "";
        }

        return result;
    }

    /// <summary>46 entries: the 45 Drift Class codes plus the orphan CLUSTER-HANDOVER.</summary>
    public static IReadOnlyDictionary<string, LegionEntry> LoadLegionPatterns() => Legions.Value;

    /// <summary>
    /// Only the heuristics with HIGH or MEDIUM confidence (7 of 113), keyed by top-level code, each with
    /// the Legion code that produced it.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<(string LegionCode, LegionHeuristic Heuristic)>> OnlyValidatedHeuristics()
    {
        var result = new OrderedDictionary<string, IReadOnlyList<(string, LegionHeuristic)>>();
        foreach (var (code, entry) in Legions.Value)
        {
            var matches = entry.Legions
                .SelectMany(legion => legion.Heuristics.Select(h => (legion.Code, h)))
                .Where(pair => pair.h.Confidence is "HIGH" or "MEDIUM")
                .ToArray();
            if (matches.Length > 0) result[code] = matches;
        }

        return result;
    }

    /// <summary>
    /// The data's quality findings, computed from the data at call time and never fixed: the stale
    /// class count, the orphan CLUSTER-HANDOVER entry, Drift Classes with no heuristics, and any
    /// name/tier/category or Stabilisation Operator code mismatches.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var findings = new List<string>();
        var root = RawDriftClasses.Value.RootElement;
        var classes = DriftClasses.Value;
        var realCount = classes.Count;
        int? claimed = root.TryGetProperty("metadata", out var metadata)
                       && metadata.TryGetProperty("total_drift_classes", out var total)
                       && total.ValueKind == JsonValueKind.Number
            ? total.GetInt32()
            : null;
        if (claimed != realCount)
        {
            var sections = root.GetProperty("drift_classes");
            var breakdown = string.Join(", ", Categories.Select(c =>
                $"{c}={(sections.TryGetProperty(c, out var s) ? s.EnumerateObject().Count() : 0)}"));
            findings.Add(
                $"metadata.total_drift_classes claims {claimed?.ToString() ?? "None"} but the real computed count across all five "
                + $"categories ({breakdown}) is {realCount}. Do not trust the metadata field.");
        }

        var legions = Legions.Value;
        var orphans = legions.Keys.Except(classes.Keys).Order(StringComparer.Ordinal).ToArray();
        if (orphans.Length > 0)
        {
            findings.Add(
                $"legion_patterns.json contains {PyList(orphans)} with no corresponding entry in drift_classes.json. Treat as a real "
                + "finding worth promoting to a formal DC in a future spec revision, not a data error to silently drop.");
        }

        var empty = legions.Where(p => p.Value.Legions.Count == 0).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        if (empty.Length > 0)
        {
            findings.Add(
                $"{empty.Length} drift classes have zero Legion detection heuristics defined at all: {string.Join(", ", empty)}. "
                + "This is a coverage gap, not a data error -- these are predominantly tier B/C/D (theoretical/hypothesised/limit) "
                + "classes where no detection heuristic has been proposed yet. OnlyValidatedHeuristics() naturally excludes them too, "
                + "since there is nothing to filter.");
        }

        var mismatches = legions.Keys.Intersect(classes.Keys).Order(StringComparer.Ordinal)
            .Where(code => (classes[code].Name, classes[code].Tier, classes[code].Category) != (legions[code].Name, legions[code].Tier, legions[code].Category))
            .ToArray();
        if (mismatches.Length > 0)
        {
            findings.Add($"name/tier/category mismatch between drift_classes.json and legion_patterns.json for: {string.Join(", ", mismatches)}.");
        }

        var soCodes = Operators.Value.Keys.Order(StringComparer.Ordinal).ToArray();
        if (soCodes.Length > 0 && !soCodes.All(c => c.StartsWith("SO-", StringComparison.Ordinal)))
        {
            findings.Add($"Unexpected Stabilisation Operator code format among: {PyList(soCodes)}.");
        }
        else if (soCodes.Length != 10 || !soCodes.Contains("SO-8b"))
        {
            findings.Add(
                "Expected 10 Stabilisation Operators (SO-1..SO-9 plus the lettered variant SO-8b, not a clean SO-1..SO-10 sequence); "
                + $"found {soCodes.Length}: {PyList(soCodes)}.");
        }

        return findings;
    }

    private static JsonDocument Read(string fileName)
    {
        var name = $"SuperSemantics.Vsl.Catalog.{fileName}";
        using var stream = typeof(DriftCatalog).Assembly.GetManifestResourceStream(name)
                           ?? throw new CatalogValidationException($"Catalog resource '{name}' is missing from the assembly.");
        try
        {
            return JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new CatalogValidationException($"Catalog file '{fileName}' is not valid JSON.", ex);
        }
    }

    private static OrderedDictionary<string, DriftClass> LoadDriftClassesCore()
    {
        var result = new OrderedDictionary<string, DriftClass>();
        var sections = RawDriftClasses.Value.RootElement.GetProperty("drift_classes");
        foreach (var category in Categories)
        {
            if (!sections.TryGetProperty(category, out var section)) continue;
            foreach (var p in section.EnumerateObject())
            {
                var d = p.Value;
                result[p.Name] = new DriftClass(
                    OptStr(d, "code") ?? p.Name,
                    Str(d, "name"),
                    Str(d, "category"),
                    Str(d, "tier"),
                    Str(d, "operational_definition"),
                    Strings(d, "primary_legions"),
                    Str(d, "primary_so"),
                    OptStr(d, "contraindications"),
                    OptStr(d, "notes"),
                    OptStr(d, "references") ?? "");
            }
        }

        return result;
    }

    private static OrderedDictionary<string, StabilisationOperator> LoadOperatorsCore()
    {
        var result = new OrderedDictionary<string, StabilisationOperator>();
        foreach (var p in RawDriftClasses.Value.RootElement.GetProperty("stabilisation_operators").EnumerateObject())
        {
            var d = p.Value;
            result[p.Name] = new StabilisationOperator(
                OptStr(d, "code") ?? p.Name, Str(d, "name"), Str(d, "proposed_function"), Strings(d, "primary_dc_targets"), Strings(d, "contraindicated_on"));
        }

        return result;
    }

    private static OrderedDictionary<string, LegionEntry> LoadLegionPatternsCore()
    {
        var result = new OrderedDictionary<string, LegionEntry>();
        foreach (var p in RawLegionPatterns.Value.RootElement.EnumerateObject())
        {
            var d = p.Value;
            var legions = new List<Legion>();
            if (d.TryGetProperty("legions", out var legionSection))
            {
                foreach (var l in legionSection.EnumerateObject())
                {
                    var heuristics = l.Value.TryGetProperty("heuristics", out var hs)
                        ? hs.EnumerateArray().Select(h => new LegionHeuristic(Str(h, "pattern_type"), Strings(h, "patterns"), Str(h, "confidence"), Str(h, "description"))).ToArray()
                        : [];
                    legions.Add(new Legion(l.Name, Str(l.Value, "name"), heuristics));
                }
            }

            result[p.Name] = new LegionEntry(p.Name, Str(d, "name"), Str(d, "tier"), Str(d, "category"), legions);
        }

        return result;
    }

    private static OrderedDictionary<string, T> Section<T>(JsonDocument doc, string name, Func<string, JsonElement, T> build)
    {
        var result = new OrderedDictionary<string, T>();
        if (doc.RootElement.TryGetProperty(name, out var section))
        {
            foreach (var p in section.EnumerateObject())
            {
                result[p.Name] = build(p.Name, p.Value);
            }
        }

        return result;
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new CatalogValidationException($"Catalog entry is missing required key '{key}'.");

    private static string? OptStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
            : [];

    private static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
}
