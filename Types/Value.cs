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

        public static Value FromLiteralToken(string token, bool wasQuoted)
        {
            if (wasQuoted)
                return new(ValueKind.String, token);

            if (TryParseBool(token, out bool b))
                return new(ValueKind.Bool, b);

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                return new(ValueKind.Int, i);

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return new(ValueKind.Double, d);

            return new(ValueKind.String, token);
        }
    }
}
