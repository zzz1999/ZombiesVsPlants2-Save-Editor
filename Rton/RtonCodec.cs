using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ZombiesVsPlants2.SaveEditor.Rton;

internal static class RtonCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public static RtonDocument Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Reader reader = new(bytes);
        return reader.Decode();
    }

    public static byte[] Encode(RtonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Writer writer = new();
        return writer.Encode(document);
    }

    private sealed class Reader
    {
        private const int MaximumNestingDepth = 128;
        private const int MaximumDecodedValues = 2_000_000;
        private const int MaximumObjectProperties = 1_000_000;
        private const int MaximumArrayItems = 1_000_000;
        private const int MaximumDeclaredArrayCapacity = 10_000_000;
        private readonly byte[] _bytes;
        private readonly List<string> _asciiPool = [];
        private readonly List<string> _utf8Pool = [];
        private readonly Dictionary<byte, int> _typeCounts = [];
        private int _offset;
        private int _objectCount;
        private int _arrayCount;
        private int _maximumDepth;
        private int _decodedValueCount;

        public Reader(byte[] bytes) => _bytes = bytes;

        public RtonDocument Decode()
        {
            string magic = ReadAscii(4);
            if (!string.Equals(magic, "RTON", StringComparison.Ordinal))
            {
                Fail($"Expected an RTON header, but found {FormatText(magic)}");
            }

            uint version = ReadUInt32();
            if (version != 1)
            {
                Fail($"RTON version {version} is not supported");
            }

            RtonObject root = ReadObject(1);
            string footer = ReadAscii(4);
            if (!string.Equals(footer, "DONE", StringComparison.Ordinal))
            {
                Fail($"Expected a DONE footer, but found {FormatText(footer)}");
            }

            if (_offset != _bytes.Length)
            {
                Fail($"The file contains {_bytes.Length - _offset} trailing bytes");
            }

            return new RtonDocument
            {
                Version = version,
                Root = root,
                Metadata = new RtonMetadata
                {
                    BytesConsumed = _offset,
                    ObjectCount = _objectCount,
                    ArrayCount = _arrayCount,
                    MaximumDepth = _maximumDepth,
                    AsciiPoolEntries = _asciiPool.Count,
                    Utf8PoolEntries = _utf8Pool.Count,
                    TypeCounts = new Dictionary<byte, int>(_typeCounts)
                }
            };
        }

        private RtonObject ReadObject(int depth)
        {
            EnsureDepth(depth);
            _objectCount++;
            _maximumDepth = Math.Max(_maximumDepth, depth);
            RtonObject result = new();
            int propertyCount = 0;

            while (true)
            {
                byte keyType = ReadByte();
                if (keyType == 0xFF)
                {
                    break;
                }

                propertyCount++;
                if (propertyCount > MaximumObjectProperties)
                {
                    Fail($"The object property count exceeds the safety limit of {MaximumObjectProperties:N0}");
                }

                NoteType(keyType);
                RtonStringToken key = ReadStringToken(keyType);
                byte valueType = ReadByte();
                RtonValue value = ReadValue(valueType, depth);
                result.Properties.Add(new RtonProperty { Key = key, Value = value });
            }

            return result;
        }

        private RtonArray ReadArray(int depth)
        {
            EnsureDepth(depth);
            _arrayCount++;
            _maximumDepth = Math.Max(_maximumDepth, depth);
            ExpectByte(0xFD);
            int declaredCapacity = ReadLength();
            if (declaredCapacity > MaximumDeclaredArrayCapacity)
            {
                Fail($"The declared array capacity of {declaredCapacity:N0} exceeds the safety limit of {MaximumDeclaredArrayCapacity:N0}");
            }

            // The encoded count is a capacity; a 0xFE terminator may end the array before that capacity.
            RtonArray result = new() { DeclaredCapacity = declaredCapacity };
            for (int index = 0; index < declaredCapacity; index++)
            {
                byte type = ReadByte();
                if (type == 0xFE)
                {
                    return result;
                }

                if (result.Items.Count >= MaximumArrayItems)
                {
                    Fail($"The array item count exceeds the safety limit of {MaximumArrayItems:N0}");
                }

                result.Items.Add(ReadValue(type, depth));
            }

            ExpectByte(0xFE);
            return result;
        }

        private RtonValue ReadValue(byte type, int parentDepth)
        {
            _decodedValueCount++;
            if (_decodedValueCount > MaximumDecodedValues)
            {
                Fail($"The decoded value count exceeds the safety limit of {MaximumDecodedValues:N0}");
            }

            NoteType(type);
            return type switch
            {
                0x00 => Value(type, RtonValueKind.Boolean, false),
                0x01 => Value(type, RtonValueKind.Boolean, true),
                0x02 => Value(type, RtonValueKind.Special, CreateSpecialToken(type, "*")),
                0x08 => Value(type, RtonValueKind.SignedInteger, (long)ReadSByte()),
                0x09 => Value(type, RtonValueKind.SignedInteger, 0L),
                0x0A => Value(type, RtonValueKind.UnsignedInteger, (ulong)ReadByte()),
                0x0B => Value(type, RtonValueKind.UnsignedInteger, 0UL),
                0x10 => Value(type, RtonValueKind.SignedInteger, (long)ReadInt16()),
                0x11 => Value(type, RtonValueKind.SignedInteger, 0L),
                0x12 => Value(type, RtonValueKind.UnsignedInteger, (ulong)ReadUInt16()),
                0x13 => Value(type, RtonValueKind.UnsignedInteger, 0UL),
                0x20 => Value(type, RtonValueKind.SignedInteger, (long)ReadInt32()),
                0x21 => Value(type, RtonValueKind.SignedInteger, 0L),
                0x22 => ReadSingleValue(type),
                0x23 => Value(type, RtonValueKind.FloatingPoint, 0D),
                0x24 => Value(type, RtonValueKind.SignedInteger, (long)ReadVarInt32()),
                0x25 => Value(type, RtonValueKind.SignedInteger, (long)ReadZigZag32()),
                0x26 => Value(type, RtonValueKind.UnsignedInteger, (ulong)ReadUInt32()),
                0x27 => Value(type, RtonValueKind.UnsignedInteger, 0UL),
                0x28 => Value(type, RtonValueKind.UnsignedInteger, (ulong)ReadVarUInt32()),
                0x40 => Value(type, RtonValueKind.SignedInteger, ReadInt64()),
                0x41 => Value(type, RtonValueKind.SignedInteger, 0L),
                0x42 => ReadDoubleValue(type),
                0x43 => Value(type, RtonValueKind.FloatingPoint, 0D),
                0x44 => Value(type, RtonValueKind.SignedInteger, ReadVarInt64()),
                0x45 => Value(type, RtonValueKind.SignedInteger, ReadZigZag64()),
                0x46 => Value(type, RtonValueKind.UnsignedInteger, ReadUInt64()),
                0x47 => Value(type, RtonValueKind.UnsignedInteger, 0UL),
                0x48 => Value(type, RtonValueKind.UnsignedInteger, ReadVarUInt64()),
                0x81 or 0x82 or 0x90 or 0x91 or 0x92 or 0x93 =>
                    Value(type, RtonValueKind.String, ReadStringToken(type)),
                0x83 or 0x84 or 0x87 =>
                    Value(type, RtonValueKind.Special, ReadStringToken(type)),
                0x85 => Value(type, RtonValueKind.Object, ReadObject(parentDepth + 1)),
                0x86 => Value(type, RtonValueKind.Array, ReadArray(parentDepth + 1)),
                >= 0xB0 and <= 0xBC => throw Error($"Compact RTON type 0x{type:X2} is not supported in standard version 1 files"),
                _ => throw Error($"Unknown RTON value type 0x{type:X2}")
            };
        }

        private RtonStringToken ReadStringToken(byte type)
        {
            return type switch
            {
                0x02 => CreateSpecialToken(type, "*"),
                0x81 => CreateTextToken(type, ReadLengthPrefixedLatin1()),
                0x82 => ReadUtf8TextToken(type),
                0x83 => ReadRawSpecialToken(type, ReadRtidText),
                0x84 => CreateSpecialToken(type, "RTID(0)"),
                0x87 => ReadRawSpecialToken(type, ReadBinaryText),
                0x90 => ReadNewPooledToken(type, _asciiPool, false),
                0x91 => ReadPooledReference(type, _asciiPool),
                0x92 => ReadNewPooledToken(type, _utf8Pool, true),
                0x93 => ReadPooledReference(type, _utf8Pool),
                _ => throw Error($"Unknown RTON string type 0x{type:X2}")
            };
        }

        private RtonStringToken ReadUtf8TextToken(byte type)
        {
            int characters = ReadLength();
            string text = ReadLengthPrefixedUtf8();
            return CreateTextToken(type, text, declaredCharacterLength: characters);
        }

        private RtonStringToken ReadNewPooledToken(byte type, List<string> pool, bool hasCharacterLength)
        {
            int? characters = hasCharacterLength ? ReadLength() : null;
            string text = hasCharacterLength ? ReadLengthPrefixedUtf8() : ReadLengthPrefixedLatin1();
            pool.Add(text);
            return CreateTextToken(type, text, declaredCharacterLength: characters);
        }

        private RtonStringToken ReadPooledReference(byte type, List<string> pool)
        {
            int index = ReadLength();
            if ((uint)index >= (uint)pool.Count)
            {
                Fail($"String pool index {index} is out of range for a pool containing {pool.Count} entries");
            }

            string text = pool[index];
            return new RtonStringToken
            {
                TypeCode = type,
                Text = text,
                OriginalText = text,
                ReferenceIndex = index
            };
        }

        private RtonStringToken ReadRawSpecialToken(byte type, Func<string> textReader)
        {
            int start = _offset;
            string text = textReader();
            // Preserve the complete RTID or binary payload so unedited values can be emitted byte-for-byte.
            byte[] payload = _bytes.AsSpan(start, _offset - start).ToArray();
            return new RtonStringToken
            {
                TypeCode = type,
                Text = text,
                OriginalText = text,
                RawPayload = payload
            };
        }

        private string ReadRtidText()
        {
            byte subtype = ReadByte();
            return subtype switch
            {
                0x00 => "RTID(0)",
                0x01 => ReadRtidNumeric(string.Empty),
                0x02 => ReadRtidNumeric(ReadPrefixedName()),
                0x03 => ReadRtidNamed(),
                _ => throw Error($"Unknown RTID subtype 0x{subtype:X2}")
            };
        }

        private string ReadPrefixedName()
        {
            _ = ReadLength();
            return ReadLengthPrefixedUtf8();
        }

        private string ReadRtidNumeric(string nameSpace)
        {
            ulong value2 = ReadVarUInt64();
            ulong value1 = ReadVarUInt64();
            uint hexadecimal = ReadUInt32();
            return $"RTID({value1:x}.{value2:x}.{hexadecimal:x8}@{nameSpace})";
        }

        private string ReadRtidNamed()
        {
            string nameSpace = ReadPrefixedName();
            _ = ReadLength();
            string name = ReadLengthPrefixedUtf8();
            return $"RTID({name}@{nameSpace})";
        }

        private string ReadBinaryText()
        {
            byte subtype = ReadByte();
            string encoded = ReadLengthPrefixedLatin1();
            int length = ReadLength();
            _ = ReadSpan(length);
            return $"$BINARY(\"{encoded}\", {length}; subtype={subtype})";
        }

        private RtonValue ReadSingleValue(byte type)
        {
            uint bits = ReadUInt32();
            // Keep raw IEEE-754 bits so NaN payloads and signed zero survive an unedited round trip.
            return new RtonValue
            {
                TypeCode = type,
                Kind = RtonValueKind.FloatingPoint,
                Data = (double)BitConverter.UInt32BitsToSingle(bits),
                OriginalFloatingPointBits = bits
            };
        }

        private RtonValue ReadDoubleValue(byte type)
        {
            ulong bits = ReadUInt64();
            return new RtonValue
            {
                TypeCode = type,
                Kind = RtonValueKind.FloatingPoint,
                Data = BitConverter.UInt64BitsToDouble(bits),
                OriginalFloatingPointBits = bits
            };
        }

        private static RtonValue Value(byte type, RtonValueKind kind, object data) =>
            new() { TypeCode = type, Kind = kind, Data = data };

        private static RtonStringToken CreateTextToken(byte type, string text, int? declaredCharacterLength = null) =>
            new()
            {
                TypeCode = type,
                Text = text,
                OriginalText = text,
                DeclaredCharacterLength = declaredCharacterLength
            };

        private static RtonStringToken CreateSpecialToken(byte type, string text) =>
            new() { TypeCode = type, Text = text, OriginalText = text };

        private void NoteType(byte type) => _typeCounts[type] = _typeCounts.GetValueOrDefault(type) + 1;

        private int ReadLength()
        {
            uint value = ReadVarUInt32();
            if (value > int.MaxValue)
            {
                Fail($"Length {value} exceeds the supported range");
            }

            return (int)value;
        }

        private string ReadLengthPrefixedLatin1() => Latin1.GetString(ReadSpan(ReadLength()));

        private string ReadLengthPrefixedUtf8() => ReadUtf8(ReadLength());

        private string ReadUtf8(int length)
        {
            ReadOnlySpan<byte> bytes = ReadSpan(length);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw Error("The string is not valid UTF-8", exception);
            }
        }

        private string ReadAscii(int length) => Encoding.ASCII.GetString(ReadSpan(length));

        private sbyte ReadSByte() => unchecked((sbyte)ReadByte());
        private short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(ReadSpan(2));
        private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadSpan(2));
        private int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadSpan(4));
        private uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadSpan(4));
        private long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadSpan(8));
        private ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadSpan(8));
        private float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());
        private double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

        private int ReadVarInt32() => unchecked((int)ReadVarUInt32());

        private uint ReadVarUInt32()
        {
            uint value = 0;
            // Reject overflow and non-canonical encodings before they can be normalized on save.
            for (int byteIndex = 0; byteIndex < 5; byteIndex++)
            {
                byte current = ReadByte();
                if (byteIndex == 4 && (current & 0xF0) != 0)
                {
                    throw Error("32-bit VarInt overflow");
                }

                int shift = byteIndex * 7;
                value |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    if (byteIndex > 0 && (current & 0x7F) == 0)
                    {
                        throw Error("The 32-bit VarInt is not canonically encoded");
                    }

                    return value;
                }
            }

            throw Error("The 32-bit VarInt is too long");
        }

        private long ReadVarInt64() => unchecked((long)ReadVarUInt64());

        private ulong ReadVarUInt64()
        {
            ulong value = 0;
            for (int byteIndex = 0; byteIndex < 10; byteIndex++)
            {
                byte current = ReadByte();
                if (byteIndex == 9 && (current & 0xFE) != 0)
                {
                    throw Error("64-bit VarInt overflow");
                }

                int shift = byteIndex * 7;
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    if (byteIndex > 0 && (current & 0x7F) == 0)
                    {
                        throw Error("The 64-bit VarInt is not canonically encoded");
                    }

                    return value;
                }
            }

            throw Error("The 64-bit VarInt is too long");
        }

        private int ReadZigZag32()
        {
            uint value = ReadVarUInt32();
            return unchecked((int)(value >> 1) ^ -((int)value & 1));
        }

        private long ReadZigZag64()
        {
            ulong value = ReadVarUInt64();
            return unchecked((long)(value >> 1) ^ -((long)value & 1));
        }

        private byte ReadByte()
        {
            EnsureAvailable(1);
            return _bytes[_offset++];
        }

        private ReadOnlySpan<byte> ReadSpan(int length)
        {
            if (length < 0)
            {
                Fail($"A read length cannot be negative: {length}");
            }

            EnsureAvailable(length);
            ReadOnlySpan<byte> result = _bytes.AsSpan(_offset, length);
            _offset += length;
            return result;
        }

        private void EnsureDepth(int depth)
        {
            if (depth > MaximumNestingDepth)
            {
                Fail($"The RTON nesting depth exceeds the safety limit of {MaximumNestingDepth}");
            }
        }

        private void ExpectByte(byte expected)
        {
            byte actual = ReadByte();
            if (actual != expected)
            {
                Fail($"Expected byte 0x{expected:X2}, but found 0x{actual:X2}");
            }
        }

        private void EnsureAvailable(int length)
        {
            if (length > _bytes.Length - _offset)
            {
                Fail($"Unexpected end of data: needed {length} bytes, but only {_bytes.Length - _offset} remain");
            }
        }

        private InvalidDataException Error(string message, Exception? innerException = null) =>
            new($"{message} (offset 0x{_offset:X} / {_offset})", innerException);

        private void Fail(string message) => throw Error(message);

        private static string FormatText(string text) =>
            '"' + string.Concat(text.Select(character => char.IsControl(character) ? $"\\u{(int)character:X4}" : character.ToString(CultureInfo.InvariantCulture))) + '"';
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        private readonly List<string> _asciiPool = [];
        private readonly List<string> _utf8Pool = [];

        public byte[] Encode(RtonDocument document)
        {
            if (document.Version != 1)
            {
                throw new InvalidDataException($"Writing RTON version {document.Version} is not supported.");
            }

            WriteAscii("RTON");
            WriteUInt32(document.Version);
            WriteObject(document.Root);
            WriteAscii("DONE");
            return _stream.ToArray();
        }

        private void WriteObject(RtonObject value)
        {
            foreach (RtonProperty property in value.Properties)
            {
                WriteStringToken(property.Key);
                WriteValue(property.Value);
            }

            WriteByte(0xFF);
        }

        private void WriteArray(RtonArray value)
        {
            WriteByte(0xFD);
            int declaredCapacity = Math.Max(value.DeclaredCapacity, value.Items.Count);
            WriteVarUInt32(checked((uint)declaredCapacity));
            foreach (RtonValue item in value.Items)
            {
                WriteValue(item);
            }

            WriteByte(0xFE);
        }

        private void WriteValue(RtonValue value)
        {
            if (value.Kind == RtonValueKind.String || value.Kind == RtonValueKind.Special)
            {
                if (value.Data is not RtonStringToken token)
                {
                    throw new InvalidDataException("The string or special value is missing its RTON string token.");
                }

                WriteStringToken(token);
                return;
            }

            WriteByte(value.TypeCode);
            switch (value.TypeCode)
            {
                case 0x00:
                case 0x01:
                    break;
                case 0x08:
                    WriteByte(unchecked((byte)checked((sbyte)(long)value.Data)));
                    break;
                case 0x09:
                case 0x0B:
                case 0x11:
                case 0x13:
                case 0x21:
                case 0x23:
                case 0x27:
                case 0x41:
                case 0x43:
                case 0x47:
                    break;
                case 0x0A:
                    WriteByte(checked((byte)(ulong)value.Data));
                    break;
                case 0x10:
                    WriteInt16(checked((short)(long)value.Data));
                    break;
                case 0x12:
                    WriteUInt16(checked((ushort)(ulong)value.Data));
                    break;
                case 0x20:
                    WriteInt32(checked((int)(long)value.Data));
                    break;
                case 0x22:
                    if (value.OriginalFloatingPointBits is ulong singleBits && singleBits <= uint.MaxValue)
                    {
                        WriteUInt32((uint)singleBits);
                    }
                    else
                    {
                        WriteSingle(checked((float)(double)value.Data));
                    }
                    break;
                case 0x24:
                    WriteVarUInt32(unchecked((uint)checked((int)(long)value.Data)));
                    break;
                case 0x25:
                    WriteZigZag32(checked((int)(long)value.Data));
                    break;
                case 0x26:
                    WriteUInt32(checked((uint)(ulong)value.Data));
                    break;
                case 0x28:
                    WriteVarUInt32(checked((uint)(ulong)value.Data));
                    break;
                case 0x40:
                    WriteInt64((long)value.Data);
                    break;
                case 0x42:
                    if (value.OriginalFloatingPointBits is ulong doubleBits)
                    {
                        WriteUInt64(doubleBits);
                    }
                    else
                    {
                        WriteDouble((double)value.Data);
                    }
                    break;
                case 0x44:
                    WriteVarUInt64(unchecked((ulong)(long)value.Data));
                    break;
                case 0x45:
                    WriteZigZag64((long)value.Data);
                    break;
                case 0x46:
                    WriteUInt64((ulong)value.Data);
                    break;
                case 0x48:
                    WriteVarUInt64((ulong)value.Data);
                    break;
                case 0x85:
                    WriteObject((RtonObject)value.Data);
                    break;
                case 0x86:
                    WriteArray((RtonArray)value.Data);
                    break;
                default:
                    throw new InvalidDataException($"RTON value type 0x{value.TypeCode:X2} cannot be written.");
            }
        }

        private void WriteStringToken(RtonStringToken token)
        {
            UpgradeStringTokenIfNeeded(token);
            switch (token.TypeCode)
            {
                case 0x02:
                case 0x84:
                    WriteByte(token.TypeCode);
                    return;
                case 0x81:
                    WriteByte(token.TypeCode);
                    WriteLengthPrefixedLatin1(token.Text);
                    return;
                case 0x82:
                    WriteByte(token.TypeCode);
                    WriteCharacterLength(token);
                    WriteLengthPrefixedUtf8(token.Text);
                    return;
                case 0x83:
                case 0x87:
                    if (token.RawPayload is null)
                    {
                        throw new InvalidDataException($"Special string type 0x{token.TypeCode:X2} is missing its raw payload.");
                    }

                    WriteByte(token.TypeCode);
                    _stream.Write(token.RawPayload);
                    return;
                case 0x90:
                    WriteByte(token.TypeCode);
                    WriteLengthPrefixedLatin1(token.Text);
                    _asciiPool.Add(token.Text);
                    return;
                case 0x91:
                    WritePooledReferenceOrValue(token, _asciiPool, 0x91, 0x90, false);
                    return;
                case 0x92:
                    WriteByte(token.TypeCode);
                    WriteCharacterLength(token);
                    WriteLengthPrefixedUtf8(token.Text);
                    _utf8Pool.Add(token.Text);
                    return;
                case 0x93:
                    WritePooledReferenceOrValue(token, _utf8Pool, 0x93, 0x92, true);
                    return;
                default:
                    throw new InvalidDataException($"RTON string type 0x{token.TypeCode:X2} cannot be written.");
            }
        }

        private void WritePooledReferenceOrValue(
            RtonStringToken token,
            List<string> pool,
            byte referenceType,
            byte valueType,
            bool hasCharacterLength)
        {
            int index = -1;
            if (token.ReferenceIndex is int originalIndex
                && (uint)originalIndex < (uint)pool.Count
                && string.Equals(pool[originalIndex], token.Text, StringComparison.Ordinal))
            {
                index = originalIndex;
            }
            else
            {
                index = pool.FindIndex(value => string.Equals(value, token.Text, StringComparison.Ordinal));
            }

            if (index >= 0)
            {
                WriteByte(referenceType);
                WriteVarUInt32(checked((uint)index));
                return;
            }

            WriteByte(valueType);
            if (hasCharacterLength)
            {
                WriteVarUInt32(checked((uint)GetUnicodeScalarCount(token.Text)));
            }

            if (hasCharacterLength)
            {
                WriteLengthPrefixedUtf8(token.Text);
            }
            else
            {
                WriteLengthPrefixedLatin1(token.Text);
            }
            pool.Add(token.Text);
        }

        private void WriteCharacterLength(RtonStringToken token)
        {
            int length = string.Equals(token.Text, token.OriginalText, StringComparison.Ordinal)
                ? token.DeclaredCharacterLength ?? GetUnicodeScalarCount(token.Text)
                : GetUnicodeScalarCount(token.Text);
            WriteVarUInt32(checked((uint)length));
        }

        private static void UpgradeStringTokenIfNeeded(RtonStringToken token)
        {
            if (IsLatin1(token.Text))
            {
                return;
            }

            token.TypeCode = token.TypeCode switch
            {
                0x81 => 0x82,
                0x90 => 0x92,
                0x91 => 0x93,
                _ => token.TypeCode
            };
        }

        private static bool IsLatin1(string value) => value.All(character => character <= '\u00FF');

        private static int GetUnicodeScalarCount(string value) => value.EnumerateRunes().Count();

        private void WriteLengthPrefixedLatin1(string value)
        {
            if (!IsLatin1(value))
            {
                throw new InvalidDataException("The Latin-1 RTON string contains characters that cannot be encoded.");
            }

            byte[] encoded = new byte[value.Length];
            for (int index = 0; index < value.Length; index++)
            {
                encoded[index] = checked((byte)value[index]);
            }

            WriteVarUInt32(checked((uint)encoded.Length));
            _stream.Write(encoded);
        }

        private void WriteLengthPrefixedUtf8(string value)
        {
            byte[] encoded = StrictUtf8.GetBytes(value);
            WriteVarUInt32(checked((uint)encoded.Length));
            _stream.Write(encoded);
        }

        private void WriteAscii(string value) => _stream.Write(Encoding.ASCII.GetBytes(value));
        private void WriteByte(byte value) => _stream.WriteByte(value);

        private void WriteInt16(short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        private void WriteUInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        private void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        private void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        private void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        private void WriteUInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        private void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));
        private void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

        private void WriteVarUInt32(uint value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        private void WriteVarUInt64(ulong value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        private void WriteZigZag32(int value) =>
            WriteVarUInt32(unchecked((uint)((value << 1) ^ (value >> 31))));

        private void WriteZigZag64(long value) =>
            WriteVarUInt64(unchecked((ulong)((value << 1) ^ (value >> 63))));
    }
}
