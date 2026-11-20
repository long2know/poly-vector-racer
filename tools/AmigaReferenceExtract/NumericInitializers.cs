using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AmigaReferenceExtract;

internal sealed record Initializer(long? Number = null, string? Symbol = null,
    string? SizeOf = null, IReadOnlyList<Initializer>? Items = null)
{
    internal long Integer() => Number ?? (Symbol switch
    {
        "FALSE" or "NULL" => 0,
        "TRUE" => 1,
        _ => throw new InvalidDataException("Expected a numeric literal or TRUE/FALSE/NULL, not an expression.")
    });

    internal IReadOnlyList<Initializer> Row(int count)
    {
        if (Items is null || Items.Count != count)
            throw new InvalidDataException($"Initializer row requires exactly {count} fields.");
        return Items;
    }
}

internal sealed class NumericInitializers
{
    private readonly string _text;
    private readonly Dictionary<string, IReadOnlyList<Initializer>> _arrays = new(StringComparer.Ordinal);

    internal NumericInitializers(string text) => _text = RemoveNonData(text);

    internal IReadOnlyList<Initializer> Array(string name, IReadOnlyDictionary<string, long>? dimensions = null)
    {
        if (_arrays.TryGetValue(name, out var cached))
            return cached;
        var matches = Regex.Matches(_text,
            $@"\b{Regex.Escape(name)}\s*\[(?<count>[^\]\r\n]*)\]\s*=\s*(?<start>\{{)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        if (matches.Count != 1)
            throw new InvalidDataException($"Expected one active array initializer for '{name}', found {matches.Count}.");
        var match = matches[0];
        var parser = new Parser(_text, match.Groups["start"].Index);
        var values = parser.Value(0).Items!;
        parser.Expect(';');
        var dimension = match.Groups["count"].Value.Trim();
        if (dimension.Length != 0 && Expression.Read(dimension, dimensions) != values.Count)
            throw new InvalidDataException($"Array '{name}' initializer count does not equal its declared dimension.");
        _arrays.Add(name, values);
        return values;
    }

    internal byte[] Bytes(string name, IReadOnlyDictionary<string, long>? dimensions = null) =>
        Array(name, dimensions).Select(value =>
        {
            var number = value.Integer();
            if (number is < 0 or > 255)
                throw new InvalidDataException($"Array '{name}' requires unsigned byte literals.");
            return (byte)number;
        }).ToArray();

    internal long Define(string name)
    {
        var expression = DefineText(name);
        return Expression.Read(expression, null);
    }

    internal double FloatingDefine(string name)
    {
        var text = DefineText(name).Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
            throw new InvalidDataException($"Define '{name}' must be a finite decimal literal.");
        return value;
    }

    private string DefineText(string name)
    {
        var matches = Regex.Matches(_text, $@"(?m)^\s*#\s*define\s+{Regex.Escape(name)}\s+(?<value>[^\r\n]+)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        if (matches.Count != 1)
            throw new InvalidDataException($"Expected one numeric object-like define '{name}'.");
        return matches[0].Groups["value"].Value;
    }

    private static string RemoveNonData(string text)
    {
        if (text.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Reference text exceeds the 4 MiB safety limit.");
        var output = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                output.Append('\n');
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new InvalidDataException("Reference text has an unterminated comment.");
                output.Append(' ');
                for (; i < end + 1; i++)
                    if (text[i] == '\n')
                        output.Append('\n');
            }
            else if (text[i] is '"' or '\'')
            {
                var quote = text[i];
                var closed = false;
                while (++i < text.Length)
                {
                    if (text[i] == '\\')
                        i++;
                    else if (text[i] == quote)
                    {
                        closed = true;
                        break;
                    }
                }
                if (!closed)
                    throw new InvalidDataException("Reference text has an unterminated quoted literal.");
                output.Append(" __QUOTED_LITERAL_NOT_NUMERIC__ ");
            }
            else
                output.Append(text[i]);
        }
        return output.ToString();
    }

    private sealed class Parser(string text, int start)
    {
        private int _position = start;

        internal Initializer Value(int depth)
        {
            if (depth > 8)
                throw new InvalidDataException("Initializer nesting exceeds eight levels.");
            Space();
            if (_position >= text.Length)
                throw new InvalidDataException("Truncated numeric initializer.");
            if (text[_position] == '{')
            {
                _position++;
                var values = new List<Initializer>();
                Space();
                if (Take('}'))
                    return new Initializer(Items: values);
                while (true)
                {
                    if (values.Count >= 100_000)
                        throw new InvalidDataException("Initializer exceeds its item limit.");
                    values.Add(Value(depth + 1));
                    if (Take('}'))
                        return new Initializer(Items: values);
                    Expect(',');
                    if (Take('}'))
                        return new Initializer(Items: values);
                }
            }
            if (char.IsAsciiLetter(text[_position]) || text[_position] == '_')
            {
                var begin = _position++;
                while (_position < text.Length &&
                       (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
                    _position++;
                var symbol = text[begin.._position];
                if (symbol != "sizeof")
                    return new Initializer(Symbol: symbol);
                Expect('(');
                var value = Value(depth + 1);
                if (value.Symbol is null || value.Items is not null)
                    throw new InvalidDataException("Only sizeof(named-byte-array) is supported.");
                Expect(')');
                return new Initializer(SizeOf: value.Symbol);
            }
            var token = _position;
            if (text[_position] is '-' or '+')
                _position++;
            while (_position < text.Length && char.IsAsciiLetterOrDigit(text[_position]))
                _position++;
            if (token == _position)
                throw new InvalidDataException("Initializer contains unsupported syntax; calls, casts and expressions are forbidden.");
            return new Initializer(Number: Number(text[token.._position]));
        }

        internal void Expect(char character)
        {
            if (!Take(character))
                throw new InvalidDataException($"Expected '{character}' in numeric initializer; executable expressions are unsupported.");
        }

        private bool Take(char character)
        {
            Space();
            if (_position >= text.Length || text[_position] != character)
                return false;
            _position++;
            return true;
        }

        private void Space()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
                _position++;
        }
    }

    private static long Number(string token)
    {
        var negative = token.StartsWith('-');
        var unsigned = token.TrimStart('+', '-');
        if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(unsigned.AsSpan(2), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var value) || value > long.MaxValue)
                throw new InvalidDataException("Hex initializer literal exceeds signed 64-bit bounds.");
            return negative ? -(long)value : (long)value;
        }
        if (unsigned.Length > 1 && unsigned[0] == '0')
            throw new InvalidDataException("Ambiguous octal-style numeric literals are unsupported.");
        if (!long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            throw new InvalidDataException("Unsupported numeric literal in initializer.");
        return number;
    }

    private sealed class Expression(string text, IReadOnlyDictionary<string, long>? names)
    {
        private int _position;

        internal static long Read(string text, IReadOnlyDictionary<string, long>? names)
        {
            if (text.Length > 256)
                throw new InvalidDataException("Numeric dimensions/defines exceed their 256-character safety bound.");
            var parser = new Expression(text, names);
            var result = parser.Sum();
            parser.Space();
            if (parser._position != text.Length)
                throw new InvalidDataException("Only integer literals, known dimensions, parentheses, +, -, * and / are supported.");
            return result;
        }

        private long Sum()
        {
            var result = Product();
            while (true)
            {
                if (Take('+'))
                    result = checked(result + Product());
                else if (Take('-'))
                    result = checked(result - Product());
                else
                    return result;
            }
        }

        private long Product()
        {
            var result = Atom();
            while (true)
            {
                if (Take('*'))
                    result = checked(result * Atom());
                else if (Take('/'))
                {
                    var divisor = Atom();
                    if (divisor == 0 || result % divisor != 0)
                        throw new InvalidDataException("Dimension division must be nonzero and exact.");
                    result /= divisor;
                }
                else
                    return result;
            }
        }

        private long Atom()
        {
            Space();
            if (Take('('))
            {
                var result = Sum();
                if (!Take(')'))
                    throw new InvalidDataException("Unbalanced numeric dimension parentheses.");
                return result;
            }
            var start = _position;
            if (_position < text.Length && text[_position] is '+' or '-')
                _position++;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
                _position++;
            var token = text[start.._position];
            if (token.Length == 0)
                throw new InvalidDataException("Missing numeric dimension operand.");
            return names is not null && names.TryGetValue(token, out var value) ? value : Number(token);
        }

        private bool Take(char character)
        {
            Space();
            if (_position >= text.Length || text[_position] != character)
                return false;
            _position++;
            return true;
        }

        private void Space()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
                _position++;
        }
    }
}
