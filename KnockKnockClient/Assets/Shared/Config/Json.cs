using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KnockKnockArena.Shared.Config
{
    /// <summary>
    /// A small JSON reader, shared verbatim by the server and the reference client.
    ///
    /// WHY NOT A LIBRARY: the config files carry the collision geometry, and client
    /// prediction only matches the server if both sides turn the same text into the
    /// same float BITS. Two different JSON libraries — System.Text.Json on the server,
    /// something else under IL2CPP — are two chances to disagree in the last bit. One
    /// parser, compiled into both, cannot disagree with itself.
    ///
    /// It reads the subset the config files use: objects, arrays, strings, numbers,
    /// true/false/null. No comments, no trailing commas.
    /// </summary>
    public static class Json
    {
        public static JsonValue Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));

            int index = 0;
            JsonValue value = ReadValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length)
                throw Error(text, index, "trailing content after the top-level value");
            return value;
        }

        // ------------------------------------------------------------------ values

        private static JsonValue ReadValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length)
                throw Error(text, index, "unexpected end of input, expected a value");

            char c = text[index];
            switch (c)
            {
                case '{': return ReadObject(text, ref index);
                case '[': return ReadArray(text, ref index);
                case '"': return JsonValue.ForString(ReadString(text, ref index));
                case 't': ReadLiteral(text, ref index, "true"); return JsonValue.ForBool(true);
                case 'f': ReadLiteral(text, ref index, "false"); return JsonValue.ForBool(false);
                case 'n': ReadLiteral(text, ref index, "null"); return JsonValue.ForNull();
                default: return JsonValue.ForNumber(ReadNumberToken(text, ref index));
            }
        }

        private static JsonValue ReadObject(string text, ref int index)
        {
            index++; // '{'
            Dictionary<string, JsonValue> members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

            SkipWhitespace(text, ref index);
            if (Peek(text, index) == '}')
            {
                index++;
                return JsonValue.ForObject(members);
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (Peek(text, index) != '"')
                    throw Error(text, index, "expected a quoted member name");

                string name = ReadString(text, ref index);
                SkipWhitespace(text, ref index);
                if (Peek(text, index) != ':')
                    throw Error(text, index, $"expected ':' after member \"{name}\"");
                index++;

                members[name] = ReadValue(text, ref index);

                SkipWhitespace(text, ref index);
                char next = Peek(text, index);
                if (next == ',')
                {
                    index++;
                    continue;
                }
                if (next == '}')
                {
                    index++;
                    return JsonValue.ForObject(members);
                }
                throw Error(text, index, "expected ',' or '}'");
            }
        }

        private static JsonValue ReadArray(string text, ref int index)
        {
            index++; // '['
            List<JsonValue> items = new List<JsonValue>();

            SkipWhitespace(text, ref index);
            if (Peek(text, index) == ']')
            {
                index++;
                return JsonValue.ForArray(items);
            }

            while (true)
            {
                items.Add(ReadValue(text, ref index));

                SkipWhitespace(text, ref index);
                char next = Peek(text, index);
                if (next == ',')
                {
                    index++;
                    continue;
                }
                if (next == ']')
                {
                    index++;
                    return JsonValue.ForArray(items);
                }
                throw Error(text, index, "expected ',' or ']'");
            }
        }

        private static string ReadString(string text, ref int index)
        {
            index++; // opening quote
            StringBuilder builder = new StringBuilder();

            while (true)
            {
                if (index >= text.Length)
                    throw Error(text, index, "unterminated string");

                char c = text[index++];
                if (c == '"')
                    return builder.ToString();

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (index >= text.Length)
                    throw Error(text, index, "unterminated escape sequence");

                char escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length)
                            throw Error(text, index, "truncated \\u escape");
                        builder.Append((char)ushort.Parse(text.Substring(index, 4),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        throw Error(text, index - 1, $"unknown escape '\\{escape}'");
                }
            }
        }

        /// <summary>Grabs the raw number token; the conversion to float happens later,
        /// in <see cref="JsonValue.AsFloat"/>, where the exact algorithm lives.</summary>
        private static string ReadNumberToken(string text, ref int index)
        {
            int start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+'))
                index++;
            while (index < text.Length)
            {
                char c = text[index];
                bool partOfNumber = c >= '0' && c <= '9' ||
                                    c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-';
                if (!partOfNumber)
                    break;
                index++;
            }
            if (index == start)
                throw Error(text, index, $"expected a value, found '{text[index]}'");
            return text.Substring(start, index - start);
        }

        private static void ReadLiteral(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                throw Error(text, index, $"expected '{literal}'");
            index += literal.Length;
        }

        // ------------------------------------------------------------------ helpers

        private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                    return;
                index++;
            }
        }

        /// <summary>Errors name the line and column, because these files are meant to
        /// be hand-authored — "bad JSON" is not a useful thing to tell an author.</summary>
        private static JsonException Error(string text, int index, string message)
        {
            int line = 1;
            int column = 1;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
            return new JsonException($"line {line}, column {column}: {message}");
        }
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>One parsed JSON value. Accessors throw with the member name in the
    /// message rather than returning a default, so a mistyped config file fails loudly
    /// at load instead of quietly producing a map with no walls.</summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; private set; }

        private Dictionary<string, JsonValue>? _members;
        private List<JsonValue>? _items;
        private string? _text;
        private bool _bool;

        private JsonValue() { }

        internal static JsonValue ForNull() => new JsonValue { Kind = JsonKind.Null };
        internal static JsonValue ForBool(bool value) => new JsonValue { Kind = JsonKind.Bool, _bool = value };
        internal static JsonValue ForNumber(string token) => new JsonValue { Kind = JsonKind.Number, _text = token };
        internal static JsonValue ForString(string value) => new JsonValue { Kind = JsonKind.String, _text = value };
        internal static JsonValue ForArray(List<JsonValue> items) => new JsonValue { Kind = JsonKind.Array, _items = items };
        internal static JsonValue ForObject(Dictionary<string, JsonValue> members)
            => new JsonValue { Kind = JsonKind.Object, _members = members };

        /// <summary>A required member. Missing is an error — see the class remark.</summary>
        public JsonValue this[string name]
        {
            get
            {
                if (Kind != JsonKind.Object || _members == null)
                    throw new JsonException($"expected an object to read \"{name}\" from, found {Kind}");
                if (!_members.TryGetValue(name, out JsonValue value))
                    throw new JsonException($"missing required member \"{name}\"");
                return value;
            }
        }

        /// <summary>An optional member; null when absent.</summary>
        public JsonValue? Optional(string name)
        {
            if (Kind != JsonKind.Object || _members == null)
                return null;
            return _members.TryGetValue(name, out JsonValue value) ? value : null;
        }

        public bool Has(string name) => Kind == JsonKind.Object && _members != null && _members.ContainsKey(name);

        public IReadOnlyList<JsonValue> Items
        {
            get
            {
                if (Kind != JsonKind.Array || _items == null)
                    throw new JsonException($"expected an array, found {Kind}");
                return _items;
            }
        }

        public string AsString()
        {
            if (Kind != JsonKind.String || _text == null)
                throw new JsonException($"expected a string, found {Kind}");
            return _text;
        }

        public bool AsBool()
        {
            if (Kind != JsonKind.Bool)
                throw new JsonException($"expected true or false, found {Kind}");
            return _bool;
        }

        public int AsInt()
        {
            float value = AsFloat();
            int rounded = (int)value;
            if (rounded != value)
                throw new JsonException($"expected a whole number, found {_text}");
            return rounded;
        }

        /// <summary>
        /// The number as a float, by an algorithm that gives the SAME BITS on every
        /// runtime — which is the whole reason this reader exists.
        ///
        /// The digits are accumulated into an exact integer and scaled by an exact
        /// power of ten, so the value is produced by a SINGLE IEEE-754 operation on two
        /// exactly-representable doubles. IEEE-754 requires that operation to be
        /// correctly rounded, and correct rounding is the same everywhere — there is no
        /// room for the runtime to have an opinion. (This is the classic strtod fast
        /// path.) Numbers outside the range where that holds are rejected rather than
        /// silently computed a slower, less certain way.
        /// </summary>
        public float AsFloat()
        {
            if (Kind != JsonKind.Number || _text == null)
                throw new JsonException($"expected a number, found {Kind}");

            string token = _text;
            int index = 0;
            bool negative = false;
            if (index < token.Length && (token[index] == '-' || token[index] == '+'))
                negative = token[index++] == '-';

            ulong mantissa = 0;
            int significantDigits = 0;
            int exponent = 0;
            bool sawDigit = false;

            while (index < token.Length && token[index] >= '0' && token[index] <= '9')
            {
                sawDigit = true;
                if (significantDigits < MaxSignificantDigits)
                {
                    // Leading zeroes are not significant, so "0.5" spends no budget.
                    if (mantissa != 0 || token[index] != '0')
                    {
                        mantissa = mantissa * 10 + (ulong)(token[index] - '0');
                        significantDigits++;
                    }
                }
                else
                {
                    exponent++; // digits past the budget only shift the decimal point
                }
                index++;
            }

            if (index < token.Length && token[index] == '.')
            {
                index++;
                while (index < token.Length && token[index] >= '0' && token[index] <= '9')
                {
                    sawDigit = true;
                    if (significantDigits < MaxSignificantDigits)
                    {
                        if (mantissa != 0 || token[index] != '0')
                        {
                            mantissa = mantissa * 10 + (ulong)(token[index] - '0');
                            significantDigits++;
                        }
                        exponent--;
                    }
                    index++;
                }
            }

            if (!sawDigit)
                throw new JsonException($"\"{token}\" is not a number");

            if (index < token.Length && (token[index] == 'e' || token[index] == 'E'))
            {
                index++;
                bool negativeExponent = false;
                if (index < token.Length && (token[index] == '-' || token[index] == '+'))
                    negativeExponent = token[index++] == '-';

                int literal = 0;
                bool sawExponentDigit = false;
                while (index < token.Length && token[index] >= '0' && token[index] <= '9')
                {
                    sawExponentDigit = true;
                    literal = literal * 10 + (token[index] - '0');
                    if (literal > 1000)
                        throw new JsonException($"exponent in \"{token}\" is out of range");
                    index++;
                }
                if (!sawExponentDigit)
                    throw new JsonException($"\"{token}\" has an empty exponent");
                exponent += negativeExponent ? -literal : literal;
            }

            if (index != token.Length)
                throw new JsonException($"\"{token}\" is not a number");

            if (mantissa == 0)
                return negative ? -0f : 0f;

            if (exponent < -MaxExactPowerOfTen || exponent > MaxExactPowerOfTen)
                throw new JsonException(
                    $"\"{token}\" cannot be converted to the same bits on every runtime — " +
                    $"keep the decimal exponent within +/-{MaxExactPowerOfTen}");

            // Both operands are exact doubles, so this one operation is correctly
            // rounded, and the same everywhere.
            double scale = ExactPowersOfTen[exponent < 0 ? -exponent : exponent];
            double scaled = exponent < 0 ? mantissa / scale : mantissa * scale;

            float result = (float)scaled;
            return negative ? -result : result;
        }

        /// <summary>15 digits keeps the mantissa below 2^53, where every integer is an
        /// exact double. Float carries about 7 significant digits, so this is roomy.</summary>
        private const int MaxSignificantDigits = 15;

        /// <summary>10^0 through 10^22 are the powers of ten that are exactly
        /// representable as doubles.</summary>
        private const int MaxExactPowerOfTen = 22;

        private static readonly double[] ExactPowersOfTen =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
            1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
        };
    }
}
