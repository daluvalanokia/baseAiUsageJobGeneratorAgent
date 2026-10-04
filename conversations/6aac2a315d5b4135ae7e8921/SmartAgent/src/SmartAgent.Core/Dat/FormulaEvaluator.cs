namespace SmartAgent.Core.Dat;

/// <summary>
/// Small, dependency-free expression evaluator for subject formula '.dat'
/// files. Grammar (recursive descent, case-insensitive):
///   expr   := term (('+'|'-') term)*
///   term   := factor (('*'|'/'|'%') factor)*
///   factor := unary ('^' factor)?              (right-associative)
///   unary  := ('-'|'+') unary | primary
///   primary:= number | constant | name '(' args ')' | '(' expr ')'
/// Functions: sqrt abs sin cos tan asin acos atan log ln exp floor ceiling round min max
/// Constants: pi, e. Variables resolve from the caller-supplied dictionary.
/// </summary>
public static class FormulaEvaluator
{
    public static double Evaluate(string expression, IReadOnlyDictionary<string, double> variables)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("Expression is required");
        var tokens = new Tokenizer(expression);
        var value = ParseExpression(tokens, variables);
        if (tokens.HasMore) throw new FormatException($"Unexpected trailing input at position {tokens.Position}");
        return value;
    }

    private static double ParseExpression(Tokenizer t, IReadOnlyDictionary<string, double> v)
    {
        var value = ParseTerm(t, v);
        while (t.PeekIs("+") || t.PeekIs("-"))
        {
            if (t.NextIs("+")) value += ParseTerm(t, v);
            else { t.NextIs("-"); value -= ParseTerm(t, v); }
        }
        return value;
    }

    private static double ParseTerm(Tokenizer t, IReadOnlyDictionary<string, double> v)
    {
        var value = ParseFactor(t, v);
        while (t.PeekIs("*") || t.PeekIs("/") || t.PeekIs("%"))
        {
            if (t.NextIs("*")) value *= ParseFactor(t, v);
            else if (t.NextIs("/")) value /= ParseFactor(t, v);
            else { t.NextIs("%"); value %= ParseFactor(t, v); }
        }
        return value;
    }

    private static double ParseFactor(Tokenizer t, IReadOnlyDictionary<string, double> v)
    {
        var value = ParseUnary(t, v);
        if (t.NextIs("^")) value = Math.Pow(value, ParseFactor(t, v));
        return value;
    }

    private static double ParseUnary(Tokenizer t, IReadOnlyDictionary<string, double> v) =>
        t.NextIs("-") ? -ParseUnary(t, v) : t.NextIs("+") ? ParseUnary(t, v) : ParsePrimary(t, v);

    private static double ParsePrimary(Tokenizer t, IReadOnlyDictionary<string, double> v)
    {
        if (t.NextIs("("))
        {
            var value = ParseExpression(t, v);
            if (!t.NextIs(")")) throw new FormatException($"Missing ')' at position {t.Position}");
            return value;
        }

        if (t.TryNextNumber(out var number)) return number;

        var name = t.NextName();
        if (name.Length == 0) throw new FormatException($"Expected value at position {t.Position}");
        var lower = name.ToLowerInvariant();

        if (t.NextIs("("))
        {
            var args = new List<double>();
            if (!t.PeekIs(")"))
            {
                args.Add(ParseExpression(t, v));
                while (t.NextIs(",")) args.Add(ParseExpression(t, v));
            }
            if (!t.NextIs(")")) throw new FormatException($"Missing ')' after {name} at position {t.Position}");
            return ApplyFunction(lower, args, t.Position);
        }

        if (lower == "pi") return Math.PI;
        if (lower == "e") return Math.E;
        if (v.TryGetValue(name, out var val)) return val;
        if (v.TryGetValue(lower, out var valLower)) return valLower;
        throw new KeyNotFoundException($"Unknown variable '{name}'");
    }

    private static double ApplyFunction(string name, IReadOnlyList<double> a, int pos) => name switch
    {
        "sqrt" => a.Count == 1 ? Math.Sqrt(a[0]) : Bad(name, pos),
        "abs" => a.Count == 1 ? Math.Abs(a[0]) : Bad(name, pos),
        "sin" => a.Count == 1 ? Math.Sin(a[0]) : Bad(name, pos),
        "cos" => a.Count == 1 ? Math.Cos(a[0]) : Bad(name, pos),
        "tan" => a.Count == 1 ? Math.Tan(a[0]) : Bad(name, pos),
        "asin" => a.Count == 1 ? Math.Asin(a[0]) : Bad(name, pos),
        "acos" => a.Count == 1 ? Math.Acos(a[0]) : Bad(name, pos),
        "atan" => a.Count == 1 ? Math.Atan(a[0]) : Bad(name, pos),
        "log" => a.Count == 1 ? Math.Log10(a[0]) : a.Count == 2 ? Math.Log(a[1], a[0]) : Bad(name, pos),
        "ln" => a.Count == 1 ? Math.Log(a[0]) : Bad(name, pos),
        "exp" => a.Count == 1 ? Math.Exp(a[0]) : Bad(name, pos),
        "floor" => a.Count == 1 ? Math.Floor(a[0]) : Bad(name, pos),
        "ceiling" or "ceil" => a.Count == 1 ? Math.Ceiling(a[0]) : Bad(name, pos),
        "round" => a.Count is 1 ? Math.Round(a[0]) : Math.Round(a[0], (int)a[1]),
        "min" => a.Count == 2 ? Math.Min(a[0], a[1]) : Bad(name, pos),
        "max" => a.Count == 2 ? Math.Max(a[0], a[1]) : Bad(name, pos),
        _ => throw new FormatException($"Unknown function '{name}' at position {pos}")
    };

    private static double Bad(string name, int pos) =>
        throw new FormatException($"Wrong number of arguments for '{name}' at position {pos}");

    private sealed class Tokenizer
    {
        private readonly string _s;
        private int _i;

        public Tokenizer(string s) { _s = s; Skip(); }
        public int Position => Math.Min(_i, _s.Length);

        private char Current => _i < _s.Length ? _s[_i] : '\0';

        private void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        public bool HasMore { get { Skip(); return _i < _s.Length; } }

        public bool PeekIs(string op)
        {
            Skip();
            return _i < _s.Length && op.Length == 1 && _s[_i] == op[0];
        }

        public bool NextIs(string op)
        {
            if (!PeekIs(op)) return false;
            _i++; Skip(); return true;
        }

        public bool TryNextNumber(out double value)
        {
            Skip();
            var start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (_i == start || !double.TryParse(_s[start.._i], out value)) { _i = start; value = 0; return false; }
            Skip(); return true;
        }

        public string NextName()
        {
            Skip();
            var start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            return _s[start.._i];
        }
    }
}
