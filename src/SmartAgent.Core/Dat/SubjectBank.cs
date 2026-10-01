using System.Collections.Concurrent;

namespace SmartAgent.Core.Dat;

/// <summary>
/// Per-subject formula bank: one '.dat' file per subject (math, physics,
/// chemistry, ...) loaded into an in-memory hash dictionary. Upserts append a
/// formula when the name does not exist in that subject and update in place
/// when it does. Evaluation is thread-safe and goes through
/// <see cref="FormulaEvaluator"/>.
/// </summary>
public sealed class SubjectBank(string rootDirectory)
{
    /// <summary>Directory holding one '.dat' file per subject.</summary>
    public string RootDirectory { get; } = rootDirectory;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SubjectFormula>> _subjects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileLock = new();
    private bool _seeded;

    public IReadOnlyList<string> Subjects
    {
        get { EnsureSeeded(); return _subjects.Keys.OrderBy(k => k).ToList(); }
    }

    public IReadOnlyList<SubjectFormula> List(string subject)
    {
        EnsureSeeded();
        return BankFor(subject).Values.OrderBy(f => f.FormulaName).ToList();
    }

    public SubjectFormula? Find(string subject, string formulaName)
    {
        EnsureSeeded();
        return BankFor(subject).TryGetValue(formulaName, out var f) ? f : null;
    }

    /// <summary>Appends the formula when its name does not exist in the subject, updates in place otherwise.</summary>
    public (SubjectFormula Formula, string Action) Upsert(string subject, SubjectFormula formula)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentException.ThrowIfNullOrWhiteSpace(formula.FormulaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(formula.Expression);

        lock (_fileLock)
        {
            EnsureSeeded();
            var bank = BankFor(subject);
            var action = bank.ContainsKey(formula.FormulaName) ? "updated" : "added";
            bank[formula.FormulaName] = formula;
            PersistUnlocked(subject);
            return (formula, action);
        }
    }

    /// <summary>Evaluates a formula from the subject bank with the given variable values.</summary>
    public FormulaEvaluation Evaluate(string subject, string formulaName, IReadOnlyDictionary<string, double> variables)
    {
        EnsureSeeded();
        var bank = BankFor(subject);
        if (!bank.TryGetValue(formulaName, out var formula))
            throw new KeyNotFoundException($"Formula '{formulaName}' not found in subject '{subject}'");

        var declared = DeclaredVariables(formula);
        foreach (var name in declared)
            if (!variables.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Missing value for variable '{name}'");

        var value = FormulaEvaluator.Evaluate(formula.Expression, variables);
        return new FormulaEvaluation(formula, value, variables);
    }

    public static IReadOnlyList<string> DeclaredVariables(SubjectFormula formula) =>
        formula.Variables.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split(':', 2)[0].Trim())
            .ToList();

    private ConcurrentDictionary<string, SubjectFormula> BankFor(string subject)
    {
        EnsureSeeded();
        return _subjects.GetOrAdd(subject.Trim(),
            _ => new ConcurrentDictionary<string, SubjectFormula>(StringComparer.OrdinalIgnoreCase));
    }

    private string FilePath(string subject) =>
        Path.Combine(rootDirectory, $"{subject.Trim().ToLowerInvariant()}.dat");

    private void EnsureSeeded()
    {
        if (_seeded) return;
        lock (_fileLock)
        {
            if (_seeded) return;
            Directory.CreateDirectory(rootDirectory);
            foreach (var (subject, seed) in DefaultSeeds())
            {
                if (File.Exists(FilePath(subject))) continue;
                var bank = _subjects.GetOrAdd(subject,
                    _ => new ConcurrentDictionary<string, SubjectFormula>(StringComparer.OrdinalIgnoreCase));
                foreach (var f in seed) bank[f.FormulaName] = f;
                PersistUnlocked(subject);
            }
            foreach (var file in Directory.GetFiles(rootDirectory, "*.dat"))
            {
                // the OO classification catalog shares the root but is not a subject
                if (string.Equals(Path.GetFileName(file), "classbank.dat", StringComparison.OrdinalIgnoreCase))
                    continue;
                var subject = Path.GetFileNameWithoutExtension(file);
                var bank = _subjects.GetOrAdd(subject,
                    _ => new ConcurrentDictionary<string, SubjectFormula>(StringComparer.OrdinalIgnoreCase));
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                    try
                    {
                        var f = SubjectFormula.Parse(line.AsSpan());
                        bank[f.FormulaName] = f;
                    }
                    catch (FormatException) { /* header or malformed row */ }
                }
            }
            _seeded = true;
        }
    }

    private void PersistUnlocked(string subject)
    {
        Directory.CreateDirectory(rootDirectory);
        if (!_subjects.TryGetValue(subject, out var bank)) return;
        File.WriteAllLines(FilePath(subject),
            new[] { "# " + string.Join(",", SubjectFormula.Header) }
                .Concat(bank.Values.OrderBy(f => f.FormulaName).Select(f => f.ToRow())));
    }

    /// <summary>Default formula columns seeded on first run for core subjects.</summary>
    public static IReadOnlyList<(string Subject, List<SubjectFormula> Formulas)> DefaultSeeds() =>
        new (string Subject, List<SubjectFormula> Formulas)[]
    {
        ("math", new List<SubjectFormula>
        {
            new() { FormulaName = "PythagoreanHypotenuse", Expression = "sqrt(a^2+b^2)", Variables = "a:Double;b:Double", Units = "Length", Category = "geometry" },
            new() { FormulaName = "QuadraticRootPositive", Expression = "(-b+sqrt(b^2-4*a*c))/(2*a)", Variables = "a:Double;b:Double;c:Double", Units = "Dimensionless", Category = "algebra" },
            new() { FormulaName = "SphereVolume", Expression = "4/3*pi*r^3", Variables = "r:Double", Units = "Volume", Category = "geometry" },
            new() { FormulaName = "CompoundInterest", Expression = "P*(1+r/n)^(n*t)", Variables = "P:Double;r:Double;n:Double;t:Double", Units = "Currency", Category = "finance" },
            new() { FormulaName = "ArithmeticMean", Expression = "(a+b+c+d)/4", Variables = "a:Double;b:Double;c:Double;d:Double", Units = "Dimensionless", Category = "statistics" },
            new() { FormulaName = "StandardDeviationSample4", Expression = "sqrt(((a-m)^2+(b-m)^2+(c-m)^2+(d-m)^2)/3)", Variables = "a:Double;b:Double;c:Double;d:Double;m:Double", Units = "Dimensionless", Category = "statistics" }
        }),
        ("physics", new List<SubjectFormula>
        {
            new() { FormulaName = "KineticEnergy", Expression = "0.5*m*v^2", Variables = "m:Double;v:Double", Units = "Joules", Category = "mechanics" },
            new() { FormulaName = "NetForce", Expression = "m*a", Variables = "m:Double;a:Double", Units = "Newtons", Category = "mechanics" },
            new() { FormulaName = "ElectricalPower", Expression = "V*I", Variables = "V:Double;I:Double", Units = "Watts", Category = "electricity" },
            new() { FormulaName = "GravitationalWeight", Expression = "m*9.80665", Variables = "m:Double", Units = "Newtons", Category = "mechanics" },
            new() { FormulaName = "Momentum", Expression = "m*v", Variables = "m:Double;v:Double", Units = "kg*m/s", Category = "mechanics" },
            new() { FormulaName = "FreeFallDistance", Expression = "0.5*9.80665*t^2", Variables = "t:Double", Units = "Meters", Category = "kinematics" }
        }),
        ("chemistry", new List<SubjectFormula>
        {
            new() { FormulaName = "MolesFromMass", Expression = "mass/molarMass", Variables = "mass:Double;molarMass:Double", Units = "Moles", Category = "stoichiometry" },
            new() { FormulaName = "IdealGasPressure", Expression = "n*0.082057366080960*T/V", Variables = "n:Double;T:Double;V:Double", Units = "Atmospheres", Category = "gasLaws" },
            new() { FormulaName = "Molarity", Expression = "moles/volumeLiters", Variables = "moles:Double;volumeLiters:Double", Units = "mol/L", Category = "solutions" },
            new() { FormulaName = "DilutionC2", Expression = "C1*V1/V2", Variables = "C1:Double;V1:Double;V2:Double", Units = "mol/L", Category = "solutions" },
            new() { FormulaName = "PercentYield", Expression = "actual/theoretical*100", Variables = "actual:Double;theoretical:Double", Units = "Percent", Category = "stoichiometry" }
        })
    };
}

public sealed record FormulaEvaluation(SubjectFormula Formula, double Value, IReadOnlyDictionary<string, double> Inputs)
{
    public string Units => Formula.Units;
}
