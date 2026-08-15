using System.Globalization;
using System.Numerics;

namespace ZombiesVsPlants2.SaveEditor.Rton;

internal enum RtonValueKind
{
    Boolean,
    SignedInteger,
    UnsignedInteger,
    FloatingPoint,
    String,
    Object,
    Array,
    Special
}

internal sealed class RtonDocument
{
    public required uint Version { get; init; }
    public required RtonObject Root { get; init; }
    public required RtonMetadata Metadata { get; init; }
}

internal sealed class RtonMetadata
{
    public int BytesConsumed { get; init; }
    public int ObjectCount { get; init; }
    public int ArrayCount { get; init; }
    public int MaximumDepth { get; init; }
    public int AsciiPoolEntries { get; init; }
    public int Utf8PoolEntries { get; init; }
    public required IReadOnlyDictionary<byte, int> TypeCounts { get; init; }
}

internal sealed class RtonStringToken
{
    public required byte TypeCode { get; set; }
    public required string Text { get; set; }
    public int? ReferenceIndex { get; init; }
    public int? DeclaredCharacterLength { get; init; }
    public required string OriginalText { get; init; }
    public byte[]? RawPayload { get; init; }

    public bool IsEditable => TypeCode is 0x81 or 0x82 or 0x90 or 0x91 or 0x92 or 0x93;

    public bool SetText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.Equals(Text, value, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsEditable)
        {
            throw new InvalidOperationException("This special string type is read-only.");
        }

        Text = value;
        // Single-byte string tags are reserved for ASCII; edited non-ASCII text must use UTF-8 tags.
        if (!IsAscii(value))
        {
            TypeCode = TypeCode switch
            {
                0x81 => 0x82,
                0x90 => 0x92,
                0x91 => 0x93,
                _ => TypeCode
            };
        }

        return true;
    }

    private static bool IsAscii(string value) => value.All(character => character <= '\u007F');
}

internal sealed class RtonProperty
{
    public required RtonStringToken Key { get; init; }
    public required RtonValue Value { get; init; }
}

internal sealed class RtonObject
{
    public List<RtonProperty> Properties { get; } = [];

    public RtonProperty? FindProperty(string name) =>
        Properties.FirstOrDefault(property => string.Equals(property.Key.Text, name, StringComparison.Ordinal));

    public RtonValue? FindValue(string name) => FindProperty(name)?.Value;

    public bool RenameProperty(int propertyIndex, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        if ((uint)propertyIndex >= (uint)Properties.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(propertyIndex),
                propertyIndex,
                $"The property index must be between 0 and {Properties.Count - 1}.");
        }

        RtonStringToken key = Properties[propertyIndex].Key;
        if (string.Equals(key.Text, newName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!key.IsEditable)
        {
            throw new InvalidOperationException("This special property key type is read-only.");
        }

        for (int index = 0; index < Properties.Count; index++)
        {
            if (index != propertyIndex
                && string.Equals(Properties[index].Key.Text, newName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The object already contains a property named '{newName}'.");
            }
        }

        return key.SetText(newName);
    }
}

internal sealed class RtonArray
{
    public List<RtonValue> Items { get; } = [];
    public int DeclaredCapacity { get; set; }
}

internal sealed class RtonValue
{
    public required byte TypeCode { get; set; }
    public required RtonValueKind Kind { get; set; }
    public required object Data { get; set; }
    public byte? OriginalBooleanPayload { get; set; }
    public ulong? OriginalFloatingPointBits { get; set; }

    public bool IsEditable => Kind is RtonValueKind.Boolean
        or RtonValueKind.SignedInteger
        or RtonValueKind.UnsignedInteger
        or RtonValueKind.FloatingPoint
        || Kind == RtonValueKind.String && ((RtonStringToken)Data).IsEditable;

    public RtonObject AsObject() => Kind == RtonValueKind.Object
        ? (RtonObject)Data
        : throw new InvalidOperationException("The value is not an object.");

    public RtonArray AsArray() => Kind == RtonValueKind.Array
        ? (RtonArray)Data
        : throw new InvalidOperationException("The value is not an array.");

    public string AsString() => Kind == RtonValueKind.String
        ? ((RtonStringToken)Data).Text
        : throw new InvalidOperationException("The value is not a string.");

    public BigInteger AsInteger() => Kind switch
    {
        RtonValueKind.SignedInteger => new BigInteger((long)Data),
        RtonValueKind.UnsignedInteger => new BigInteger((ulong)Data),
        _ => throw new InvalidOperationException("The value is not an integer.")
    };

    public string ToDisplayString(int maximumLength = 80)
    {
        string text = Kind switch
        {
            RtonValueKind.Boolean => ((bool)Data).ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            RtonValueKind.SignedInteger => ((long)Data).ToString(CultureInfo.InvariantCulture),
            RtonValueKind.UnsignedInteger => ((ulong)Data).ToString(CultureInfo.InvariantCulture),
            RtonValueKind.FloatingPoint => ((double)Data).ToString("R", CultureInfo.InvariantCulture),
            RtonValueKind.String => ((RtonStringToken)Data).Text,
            RtonValueKind.Object => $"Object ({((RtonObject)Data).Properties.Count} properties)",
            RtonValueKind.Array => $"Array ({((RtonArray)Data).Items.Count} items)",
            _ => Data is RtonStringToken token ? token.Text : Data.ToString() ?? string.Empty
        };

        if (text.Length <= maximumLength)
        {
            return text;
        }

        return text[..Math.Max(0, maximumLength - 1)] + "…";
    }

    public void SetBoolean(bool value)
    {
        if (Kind != RtonValueKind.Boolean)
        {
            throw new InvalidOperationException("The field is not a Boolean value.");
        }

        bool previous = (bool)Data;
        Data = value;
        if (TypeCode is 0x00 or 0x01)
        {
            TypeCode = value ? (byte)0x01 : (byte)0x00;
        }
        else if (TypeCode == 0xBC && previous != value)
        {
            // A changed extended Boolean is written canonically; unchanged non-zero payloads remain byte-exact.
            OriginalBooleanPayload = null;
        }
    }

    public void SetString(string value)
    {
        if (Kind != RtonValueKind.String)
        {
            throw new InvalidOperationException("The field is not a string.");
        }

        RtonStringToken token = (RtonStringToken)Data;
        if (token.SetText(value))
        {
            TypeCode = token.TypeCode;
        }
    }

    public void SetFloatingPoint(double value)
    {
        if (Kind != RtonValueKind.FloatingPoint)
        {
            throw new InvalidOperationException("The field is not a floating-point value.");
        }

        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "NaN and infinity are not allowed.");
        }

        Data = value;
        OriginalFloatingPointBits = null;
        if (TypeCode == 0x22 && (double)(float)value != value)
        {
            TypeCode = 0x42;
        }
        else if (TypeCode == 0x23 && BitConverter.DoubleToInt64Bits(value) != 0)
        {
            TypeCode = (double)(float)value == value ? (byte)0x22 : (byte)0x42;
        }
        else if (TypeCode == 0x43 && BitConverter.DoubleToInt64Bits(value) != 0)
        {
            TypeCode = 0x42;
        }
    }

    public void SetInteger(BigInteger value)
    {
        if (Kind is not (RtonValueKind.SignedInteger or RtonValueKind.UnsignedInteger))
        {
            throw new InvalidOperationException("The field is not an integer.");
        }

        if (value < long.MinValue || value > ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "An RTON integer must be between Int64.MinValue and UInt64.MaxValue.");
        }

        if (Kind == RtonValueKind.UnsignedInteger && value >= 0 && TryPreserveUnsignedType(value))
        {
            return;
        }

        if (Kind == RtonValueKind.SignedInteger && value <= long.MaxValue && TryPreserveSignedType(value))
        {
            return;
        }

        if (Kind == RtonValueKind.UnsignedInteger && value >= 0)
        {
            Kind = RtonValueKind.UnsignedInteger;
            Data = (ulong)value;
            TypeCode = value <= uint.MaxValue ? (byte)0x28 : (byte)0x48;
            return;
        }

        if (value < 0)
        {
            Kind = RtonValueKind.SignedInteger;
            Data = (long)value;
            TypeCode = value >= int.MinValue ? (byte)0x25 : (byte)0x45;
        }
        else if (value <= int.MaxValue)
        {
            Kind = RtonValueKind.SignedInteger;
            Data = (long)value;
            TypeCode = value == 0 ? (byte)0x21 : (byte)0x24;
        }
        else if (value <= long.MaxValue)
        {
            Kind = RtonValueKind.SignedInteger;
            Data = (long)value;
            TypeCode = (byte)0x44;
        }
        else
        {
            Kind = RtonValueKind.UnsignedInteger;
            Data = (ulong)value;
            TypeCode = (byte)0x48;
        }
    }

    private bool TryPreserveSignedType(BigInteger value)
    {
        bool fits = TypeCode switch
        {
            0x08 or 0x09 => value >= sbyte.MinValue && value <= sbyte.MaxValue,
            0x10 or 0x11 => value >= short.MinValue && value <= short.MaxValue,
            0x20 or 0x21 or 0x24 or 0x25 => value >= int.MinValue && value <= int.MaxValue,
            0x40 or 0x41 or 0x44 or 0x45 => value >= long.MinValue && value <= long.MaxValue,
            _ => false
        };

        if (!fits)
        {
            return false;
        }

        long converted = (long)value;
        Data = converted;
        TypeCode = TypeCode switch
        {
            0x09 when converted != 0 => 0x08,
            0x11 when converted != 0 => 0x10,
            0x21 when converted > 0 => 0x24,
            0x21 when converted < 0 => 0x25,
            0x24 when converted < 0 => 0x25,
            0x41 when converted > 0 => 0x44,
            0x41 when converted < 0 => 0x45,
            0x44 when converted < 0 => 0x45,
            _ => TypeCode
        };
        return true;
    }

    private bool TryPreserveUnsignedType(BigInteger value)
    {
        bool fits = TypeCode switch
        {
            0x0A or 0x0B => value <= byte.MaxValue,
            0x12 or 0x13 => value <= ushort.MaxValue,
            0x26 or 0x27 or 0x28 => value <= uint.MaxValue,
            0x46 or 0x47 or 0x48 => value <= ulong.MaxValue,
            _ => false
        };

        if (!fits)
        {
            return false;
        }

        ulong converted = (ulong)value;
        Data = converted;
        TypeCode = TypeCode switch
        {
            0x0B when converted != 0 => 0x0A,
            0x13 when converted != 0 => 0x12,
            0x27 when converted != 0 => 0x28,
            0x47 when converted != 0 => 0x48,
            _ => TypeCode
        };
        return true;
    }
}
