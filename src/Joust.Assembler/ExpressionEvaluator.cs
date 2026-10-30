namespace Joust.Assembler;

/// <summary>
/// Evaluates operand expressions using the Macro Assembler {AS} operator set and ranking
/// (lower rank binds tighter). Notably, AS gives the bitwise operators (&amp;, |, !) higher
/// precedence than * and /, and the shifts higher still.
/// </summary>
internal sealed class ExpressionEvaluator
{
    private static readonly (string Op, int Rank)[] BinaryOps =
    [
        ("<<", 3), (">>", 3), ("><", 4),
        ("<=", 14), (">=", 14), ("<>", 14), ("!=", 14), ("==", 14),
        ("&&", 11), ("||", 12), ("!!", 13),
        ("&", 5), ("|", 6), ("!", 7), ("^", 8),
        ("*", 9), ("/", 9),
        ("+", 10), ("-", 10),
        ("<", 14), (">", 14), ("=", 14),
    ];

    private const int MaxRank = 14;

    private readonly Func<string, long?> _lookup;
    private readonly Func<long> _pc;
    private string _s = "";
    private int _p;

    public ExpressionEvaluator(Func<string, long?> lookup, Func<long> pc)
    {
        _lookup = lookup;
        _pc = pc;
    }

    /// <summary>True when the last evaluation referenced a symbol with no known value.</summary>
    public bool Unknown { get; private set; }

    /// <summary>Names of symbols that had no known value in the last evaluation.</summary>
    public List<string> UnknownSymbols { get; } = [];

    public long Evaluate(string text)
    {
        _s = text;
        _p = 0;
        Unknown = false;
        UnknownSymbols.Clear();
        long v = ParseBinary(MaxRank);
        SkipWs();
        if (_p < _s.Length)
            throw new AsmException($"unexpected '{_s[_p..]}' in expression '{text}'");
        return v;
    }

    private long ParseBinary(int rank)
    {
        if (rank <= 2)
            return ParseUnary();
        long left = ParseBinary(rank - 1);
        while (true)
        {
            SkipWs();
            string? op = PeekBinary(rank);
            if (op == null)
                return left;
            _p += op.Length;
            long right = ParseBinary(rank - 1);
            left = Apply(op, left, right);
        }
    }

    private string? PeekBinary(int rank)
    {
        foreach (var (op, r) in BinaryOps)
        {
            if (string.CompareOrdinal(_s, _p, op, 0, op.Length) != 0)
                continue;
            // The first (longest) operator matching here is the real token; it only
            // applies if it belongs to the rank being parsed.
            return r == rank ? op : null;
        }
        return null;
    }

    private static long Apply(string op, long a, long b) => op switch
    {
        "<<" => a << (int)b,
        ">>" => (long)((ulong)a >> (int)b),
        "><" => throw new AsmException("bit mirror operator not supported"),
        "&" => a & b,
        "|" => a | b,
        "!" => a ^ b,
        "^" => (long)Math.Pow(a, b),
        "*" => a * b,
        "/" => b == 0 ? throw new AsmException("division by zero") : a / b,
        "+" => a + b,
        "-" => a - b,
        "&&" => (a != 0 && b != 0) ? 1 : 0,
        "||" => (a != 0 || b != 0) ? 1 : 0,
        "!!" => ((a != 0) ^ (b != 0)) ? 1 : 0,
        "=" or "==" => a == b ? 1 : 0,
        "<>" or "!=" => a != b ? 1 : 0,
        "<" => a < b ? 1 : 0,
        ">" => a > b ? 1 : 0,
        "<=" => a <= b ? 1 : 0,
        ">=" => a >= b ? 1 : 0,
        _ => throw new AsmException($"unknown operator {op}"),
    };

    private long ParseUnary()
    {
        SkipWs();
        if (_p >= _s.Length)
            throw new AsmException($"missing operand in expression '{_s}'");
        if (Match("~~"))
            return ParseUnary() == 0 ? 1 : 0;
        if (Match("~"))
            return ~ParseUnary();
        if (Match("-"))
            return -ParseUnary();
        if (Match("+"))
            return ParseUnary();
        return ParsePrimary();
    }

    private long ParsePrimary()
    {
        SkipWs();
        char c = _s[_p];
        if (c == '(')
        {
            _p++;
            long v = ParseBinary(MaxRank);
            SkipWs();
            if (!Match(")"))
                throw new AsmException($"missing ')' in '{_s}'");
            return v;
        }
        if (c == '*')
        {
            _p++;
            return _pc();
        }
        if (c == '$')
            return ParseDigits(_p + 1, 16);
        if (c == '%')
            return ParseDigits(_p + 1, 2);
        if (c == '@')
            return ParseDigits(_p + 1, 8);
        if (c == '\'' || c == '"')
            return ParseCharConstant(c);
        if (char.IsDigit(c))
            return ParseNumber();
        if (IsIdentStart(c))
        {
            int start = _p;
            while (_p < _s.Length && IsIdentPart(_s[_p]))
                _p++;
            string name = _s[start.._p];
            long? value = _lookup(name);
            if (value is null)
            {
                Unknown = true;
                UnknownSymbols.Add(name);
                return 0;
            }
            return value.Value;
        }
        throw new AsmException($"unexpected '{c}' in expression '{_s}'");
    }

    private long ParseDigits(int start, int radix)
    {
        int p = start;
        long v = 0;
        while (p < _s.Length && DigitValue(_s[p]) is int d && d < radix)
        {
            v = v * radix + d;
            p++;
        }
        if (p == start)
            throw new AsmException($"bad number in '{_s}'");
        _p = p;
        return v;
    }

    private long ParseNumber()
    {
        // Decimal, or Intel-style hex with an H suffix (e.g. 0FFH).
        int p = _p;
        while (p < _s.Length && DigitValue(_s[p]) is int d && d < 16)
            p++;
        if (p < _s.Length && char.ToUpperInvariant(_s[p]) == 'H')
        {
            long v = Convert.ToInt64(_s[_p..p], 16);
            _p = p + 1;
            return v;
        }
        if (_p + 1 < _s.Length && _s[_p] == '0' && char.ToUpperInvariant(_s[_p + 1]) == 'X')
            return ParseDigits(_p + 2, 16);
        return ParseDigits(_p, 10);
    }

    private long ParseCharConstant(char quote)
    {
        int p = _p + 1;
        long v = 0;
        int count = 0;
        while (p < _s.Length && _s[p] != quote)
        {
            v = (v << 8) | (byte)_s[p];
            p++;
            count++;
        }
        if (p >= _s.Length || count == 0)
            throw new AsmException($"bad character constant in '{_s}'");
        _p = p + 1;
        return v;
    }

    private static int? DigitValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        c = char.ToUpperInvariant(c);
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return null;
    }

    private bool Match(string s)
    {
        if (string.CompareOrdinal(_s, _p, s, 0, s.Length) != 0)
            return false;
        _p += s.Length;
        return true;
    }

    private void SkipWs()
    {
        while (_p < _s.Length && char.IsWhiteSpace(_s[_p]))
            _p++;
    }

    internal static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '.';

    internal static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
}

public sealed class AsmException(string message) : Exception(message);
