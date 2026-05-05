namespace EasyShell.Types
{
    public sealed class Variable
    {
        public string Name { get; }
        public ValueKind DeclaredKind { get; private set; }
        public Value Value { get; private set; }

        public Variable(string name, ValueKind kind, Value value)
        {
            Name = name;
            DeclaredKind = kind;
            Value = Coerce(kind, value);
        }

        public void Set(Value value) => Value = Coerce(DeclaredKind, value);

        public static Value Coerce(ValueKind kind, Value v)
        {
            return kind switch
            {
                ValueKind.String => new(ValueKind.String, v.AsString()),
                ValueKind.Int => new(ValueKind.Int, v.AsInt()),
                ValueKind.Bool => new(ValueKind.Bool, v.AsBool()),
                ValueKind.Double => new(ValueKind.Double, v.AsDouble()),
                ValueKind.Handle => v.Kind == ValueKind.Handle ? v : new(ValueKind.Handle, v.Data), // allow wrapping
                ValueKind.Null => Value.Null,
                _ => v
            };
        }
    }
}
