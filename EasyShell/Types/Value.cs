using System;
using System.Globalization;

namespace EasyShell.Types
{
    public readonly record struct Value(ValueKind Kind, object? Data)
    {
        public static Value Null => new(ValueKind.Null, null);

        public string AsString() =>
            Kind switch
            {
                ValueKind.Null => "",
                ValueKind.String => (string)Data!,
                ValueKind.Int => ((int)Data!).ToString(CultureInfo.InvariantCulture),
                ValueKind.Bool => ((bool)Data!) ? "TRUE" : "FALSE",
                ValueKind.Double => ((double)Data!).ToString(CultureInfo.InvariantCulture),
                ValueKind.Handle => Data?.ToString() ?? "",
                _ => Data?.ToString() ?? ""
            };

        public bool AsBool()
        {
            return Kind switch
            {
                ValueKind.Bool => (bool)Data!,
                ValueKind.Int => (int)Data! != 0,
                ValueKind.Double => Math.Abs((double)Data!) > double.Epsilon,
                ValueKind.String => TryParseBool((string)Data!, out bool b) ? b : !string.IsNullOrEmpty((string)Data!),
                ValueKind.Handle => Data is not null,
                ValueKind.Null => false,
                _ => false
            };
        }

        public double AsDouble()
        {
            return Kind switch
            {
                ValueKind.Double => (double)Data!,
                ValueKind.Int => (int)Data!,
                ValueKind.Bool => (bool)Data! ? 1.0 : 0.0,
                ValueKind.String => double.TryParse((string)Data!, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0,
                _ => 0.0
            };
        }

        public int AsInt()
        {
            return Kind switch
            {
                ValueKind.Int => (int)Data!,
                ValueKind.Double => (int)(double)Data!,
                ValueKind.Bool => (bool)Data! ? 1 : 0,
                ValueKind.String => int.TryParse((string)Data!, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0,
                _ => 0
            };
        }

        public object? AsHandle() => Kind == ValueKind.Handle ? Data : null;

        public static bool TryParseBool(string s, out bool b)
        {
            if (s.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || s.Equals("YES", StringComparison.OrdinalIgnoreCase) || s.Equals("1"))
            {
                b = true; return true;
            }
            if (s.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || s.Equals("NO", StringComparison.OrdinalIgnoreCase) || s.Equals("0"))
            {
                b = false; return true;
            }
            b = false;
            return bool.TryParse(s, out b);
        }

        /// <summary>
        /// What an unquoted word in a script means.
        ///
        /// <para>Numbers are tried before booleans, and the order is the whole point.
        /// <see cref="TryParseBool"/> accepts "1" and "0" - which is right when something already
        /// known to be a condition has to be read as one - but asking it first made the literal
        /// <c>0</c> a BOOL, so <c>$i = 0</c> declared a boolean and the obvious counter never
        /// worked: <c>$i = (+ $i 1)</c> coerced 1.0 back to TRUE, and a surrounding
        /// <c>WHILE (&lt; $i 3)</c> compared "TRUE" against "3" as text and never ran a single
        /// time, in silence.</para>
        ///
        /// <para>Nothing is lost by preferring the number. A condition written as
        /// <c>(== $Flag 1)</c> still holds, because comparison falls back to comparing both sides
        /// as booleans when they are not both numeric, and <c>IF 1</c> is still true, because a
        /// non-zero INT is truthy. TRUE/FALSE/YES/NO are unaffected - they were never numbers.</para>
        /// </summary>
        public static Value FromLiteralToken(string token, bool wasQuoted)
        {
            if (wasQuoted)
                return new(ValueKind.String, token);

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                return new(ValueKind.Int, i);

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return new(ValueKind.Double, d);

            if (TryParseBool(token, out bool b))
                return new(ValueKind.Bool, b);

            return new(ValueKind.String, token);
        }
    }
}
