using System.Globalization;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Expansion;

/// <summary>
/// Evaluates <c>$(( ... ))</c> and <c>(( ... ))</c>: C-like integer arithmetic over 64-bit
/// values, including assignment, increment/decrement and the ternary operator.
/// </summary>
public sealed class ArithmeticEvaluator
{
    private const int MaxIndirectionDepth = 16;

    private readonly ShellContext _context;
    private readonly string _text;
    private int _pos;
    private int _depth;

    private ArithmeticEvaluator(string text, ShellContext context)
    {
        _text = text;
        _context = context;
    }

    public static long Evaluate(string expression, ShellContext context)
    {
        var evaluator = new ArithmeticEvaluator(expression, context);
        evaluator.SkipSpace();
        if (evaluator.AtEnd) return 0;

        var value = evaluator.ParseComma();
        evaluator.SkipSpace();
        if (!evaluator.AtEnd) throw new ShellException($"{expression}: arithmetic syntax error near `{evaluator.Rest}'");
        return value;
    }

    private bool AtEnd => _pos >= _text.Length;

    private char Cur => _pos < _text.Length ? _text[_pos] : '\0';

    private string Rest => _text[_pos..];

    private void SkipSpace()
    {
        while (!AtEnd && char.IsWhiteSpace(Cur)) _pos++;
    }

    private bool Eat(string op)
    {
        SkipSpace();
        if (string.CompareOrdinal(_text, _pos, op, 0, op.Length) != 0) return false;

        // Do not let "<" swallow the "<" of "<<", or "=" the "=" of "==".
        var after = _pos + op.Length < _text.Length ? _text[_pos + op.Length] : '\0';
        switch (op)
        {
            case "<" or ">" when after == op[0] || after == '=': return false;
            case "=" when after == '=': return false;
            case "!" when after == '=': return false;
            case "&" or "|" when after == op[0] || after == '=': return false;
            case "*" when after is '*' or '=': return false;
            case "+" when after is '+' or '=': return false;
            case "-" when after is '-' or '=': return false;
            case "/" or "%" or "^" when after == '=': return false;
            case "<<" or ">>" when after == '=': return false;
        }

        _pos += op.Length;
        return true;
    }

    private bool Peek(string op)
    {
        var save = _pos;
        var ok = Eat(op);
        _pos = save;
        return ok;
    }

    // ------------------------------------------------------- precedence

    private long ParseComma()
    {
        var value = ParseAssignment();
        while (Eat(",")) value = ParseAssignment();
        return value;
    }

    private static readonly string[] CompoundAssignments =
        { "<<=", ">>=", "+=", "-=", "*=", "/=", "%=", "&=", "^=", "|=", "**=" };

    private long ParseAssignment()
    {
        var save = _pos;
        SkipSpace();
        var name = TryReadName();

        if (name is not null)
        {
            foreach (var op in CompoundAssignments)
            {
                if (!Eat(op)) continue;
                var rhs = ParseAssignment();
                var current = ReadVariable(name);
                var result = Apply(op[..^1], current, rhs);
                _context.SetValue(name, result.ToString(CultureInfo.InvariantCulture));
                return result;
            }

            if (Eat("="))
            {
                var value = ParseAssignment();
                _context.SetValue(name, value.ToString(CultureInfo.InvariantCulture));
                return value;
            }
        }

        _pos = save;
        return ParseTernary();
    }

    private long ParseTernary()
    {
        var condition = ParseLogicalOr();
        if (!Eat("?")) return condition;

        var whenTrue = ParseAssignment();
        SkipSpace();
        if (!Eat(":")) throw new ShellException("arithmetic syntax error: expected `:'");
        var whenFalse = ParseAssignment();
        return condition != 0 ? whenTrue : whenFalse;
    }

    private long ParseLogicalOr()
    {
        var left = ParseLogicalAnd();

        while (Eat("||"))
        {
            var right = ParseLogicalAnd();
            left = left != 0 || right != 0 ? 1 : 0;
        }

        return left;
    }

    private long ParseLogicalAnd()
    {
        var left = ParseBitOr();

        while (Eat("&&"))
        {
            var right = ParseBitOr();
            left = left != 0 && right != 0 ? 1 : 0;
        }

        return left;
    }

    private long ParseBitOr()
    {
        var left = ParseBitXor();
        while (Eat("|")) left |= ParseBitXor();
        return left;
    }

    private long ParseBitXor()
    {
        var left = ParseBitAnd();
        while (Eat("^")) left ^= ParseBitAnd();
        return left;
    }

    private long ParseBitAnd()
    {
        var left = ParseEquality();
        while (Eat("&")) left &= ParseEquality();
        return left;
    }

    private long ParseEquality()
    {
        var left = ParseRelational();

        while (true)
        {
            if (Eat("==")) left = left == ParseRelational() ? 1 : 0;
            else if (Eat("!=")) left = left != ParseRelational() ? 1 : 0;
            else return left;
        }
    }

    private long ParseRelational()
    {
        var left = ParseShift();

        while (true)
        {
            if (Eat("<=")) left = left <= ParseShift() ? 1 : 0;
            else if (Eat(">=")) left = left >= ParseShift() ? 1 : 0;
            else if (Eat("<")) left = left < ParseShift() ? 1 : 0;
            else if (Eat(">")) left = left > ParseShift() ? 1 : 0;
            else return left;
        }
    }

    private long ParseShift()
    {
        var left = ParseAdditive();

        while (true)
        {
            if (Eat("<<")) left <<= (int)ParseAdditive();
            else if (Eat(">>")) left >>= (int)ParseAdditive();
            else return left;
        }
    }

    private long ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (true)
        {
            if (Eat("+")) left += ParseMultiplicative();
            else if (Eat("-")) left -= ParseMultiplicative();
            else return left;
        }
    }

    private long ParseMultiplicative()
    {
        var left = ParsePower();

        while (true)
        {
            if (Eat("*")) left *= ParsePower();
            else if (Eat("/")) left = Divide(left, ParsePower());
            else if (Eat("%")) left = Modulo(left, ParsePower());
            else return left;
        }
    }

    private long ParsePower()
    {
        var left = ParseUnary();
        if (!Eat("**")) return left;

        var exponent = ParsePower(); // right associative
        return Power(left, exponent);
    }

    private long ParseUnary()
    {
        SkipSpace();

        if (Eat("++"))
        {
            var name = TryReadName() ?? throw new ShellException("arithmetic syntax error: `++' needs a variable");
            var value = ReadVariable(name) + 1;
            _context.SetValue(name, value.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        if (Eat("--"))
        {
            var name = TryReadName() ?? throw new ShellException("arithmetic syntax error: `--' needs a variable");
            var value = ReadVariable(name) - 1;
            _context.SetValue(name, value.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        if (Eat("!")) return ParseUnary() == 0 ? 1 : 0;
        if (Eat("~")) return ~ParseUnary();
        if (Eat("+")) return ParseUnary();
        if (Eat("-")) return -ParseUnary();

        return ParsePostfix();
    }

    private long ParsePostfix()
    {
        SkipSpace();

        if (Cur == '(')
        {
            _pos++;
            var inner = ParseComma();
            SkipSpace();
            if (Cur != ')') throw new ShellException("arithmetic syntax error: expected `)'");
            _pos++;
            return inner;
        }

        if (char.IsAsciiDigit(Cur)) return ReadNumber();

        var save = _pos;
        var name = TryReadName();

        if (name is null)
        {
            _pos = save;
            throw new ShellException($"arithmetic syntax error near `{Rest}'");
        }

        var subscript = TryReadSubscript();
        var value = subscript is null ? ReadVariable(name) : ReadArrayElement(name, subscript.Value);

        if (Eat("++"))
        {
            Assign(name, subscript, value + 1);
            return value;
        }

        if (Eat("--"))
        {
            Assign(name, subscript, value - 1);
            return value;
        }

        return value;
    }

    private void Assign(string name, int? subscript, long value)
    {
        if (subscript is null) _context.SetValue(name, value.ToString(CultureInfo.InvariantCulture));
        else _context.SetArrayElement(name, subscript.Value, value.ToString(CultureInfo.InvariantCulture));
    }

    private int? TryReadSubscript()
    {
        SkipSpace();
        if (Cur != '[') return null;

        var close = _text.IndexOf(']', _pos);
        if (close < 0) return null;

        var inner = _text[(_pos + 1)..close];
        _pos = close + 1;
        return (int)new ArithmeticEvaluator(inner, _context).ParseComma();
    }

    private long ReadArrayElement(string name, int index)
    {
        var variable = _context.Lookup(name);
        var list = variable?.AsList();
        if (list is null || index < 0 || index >= list.Count) return 0;
        return ToNumber(list[index]);
    }

    private string? TryReadName()
    {
        SkipSpace();
        if (AtEnd || !(char.IsLetter(Cur) || Cur == '_')) return null;

        var start = _pos;
        while (!AtEnd && (char.IsLetterOrDigit(Cur) || Cur == '_')) _pos++;
        return _text[start.._pos];
    }

    private long ReadNumber()
    {
        var start = _pos;

        if (Cur == '0' && _pos + 1 < _text.Length && (_text[_pos + 1] is 'x' or 'X'))
        {
            _pos += 2;
            var hexStart = _pos;
            while (!AtEnd && Uri.IsHexDigit(Cur)) _pos++;
            return Convert.ToInt64(_text[hexStart.._pos], 16);
        }

        while (!AtEnd && char.IsAsciiDigit(Cur)) _pos++;

        // base#digits, e.g. 2#1011: the '#' and the digits after it are part of the same literal.
        if (!AtEnd && Cur == '#')
        {
            _pos++;
            while (!AtEnd && char.IsLetterOrDigit(Cur)) _pos++;
        }

        var text = _text[start.._pos];

        // base#digits, e.g. 2#1011 or 16#ff
        var hash = text.IndexOf('#');
        if (hash > 0)
        {
            var radix = int.Parse(text[..hash], CultureInfo.InvariantCulture);
            if (radix is < 2 or > 36) throw new ShellException($"{text}: invalid arithmetic base");
            return ParseRadix(text[(hash + 1)..], radix);
        }

        if (text.Length > 1 && text[0] == '0' && text.All(c => c is >= '0' and <= '7'))
            return Convert.ToInt64(text, 8);

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ShellException($"{text}: value too great for base");

        return value;
    }

    private static long ParseRadix(string digits, int radix)
    {
        long value = 0;

        foreach (var c in digits)
        {
            int digit;
            if (char.IsAsciiDigit(c)) digit = c - '0';
            else if (char.IsAsciiLetterLower(c)) digit = c - 'a' + 10;
            else if (char.IsAsciiLetterUpper(c)) digit = c - 'A' + 10;
            else throw new ShellException($"{digits}: invalid number");

            if (digit >= radix) throw new ShellException($"{digits}: value too great for base");
            value = value * radix + digit;
        }

        return value;
    }

    private long ReadVariable(string name)
    {
        var raw = _context.GetValue(name);
        return raw is null ? 0 : ToNumber(raw);
    }

    /// <summary>A variable's value is itself an arithmetic expression, so `a=b; b=3; $((a))` is 3.</summary>
    private long ToNumber(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return 0;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct)) return direct;

        if (_depth >= MaxIndirectionDepth) return 0;

        _depth++;
        try
        {
            var nested = new ArithmeticEvaluator(raw, _context) { _depth = _depth };
            nested.SkipSpace();
            if (nested.AtEnd) return 0;
            return nested.ParseComma();
        }
        catch (ShellException)
        {
            return 0;
        }
        finally
        {
            _depth--;
        }
    }

    private static long Divide(long left, long right) =>
        right == 0 ? throw new ShellException("division by 0") : left / right;

    private static long Modulo(long left, long right) =>
        right == 0 ? throw new ShellException("division by 0") : left % right;

    private static long Power(long value, long exponent)
    {
        if (exponent < 0) return 0;

        long result = 1;
        while (exponent-- > 0) result *= value;
        return result;
    }

    private long Apply(string op, long left, long right) => op switch
    {
        "+" => left + right,
        "-" => left - right,
        "*" => left * right,
        "/" => Divide(left, right),
        "%" => Modulo(left, right),
        "<<" => left << (int)right,
        ">>" => left >> (int)right,
        "&" => left & right,
        "^" => left ^ right,
        "|" => left | right,
        "**" => Power(left, right),
        _ => throw new ShellException($"unsupported operator `{op}'"),
    };
}
