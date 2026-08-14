using ZombiesVsPlants2.SaveEditor.Rton;

namespace ZombiesVsPlants2.SaveEditor.Diagnostics;

internal static class RtonRegressionFixtures
{
    public static void Run()
    {
        VerifyBinaryWireShape();
        VerifyAsciiPromotion();
        VerifyNumericRtidDisplay();
        VerifyExtendedBooleanPayload();
    }

    private static void VerifyBinaryWireShape()
    {
        byte[] fixture = WrapRootPayload(
            0x81, 0x01, (byte)'b',
            0x87, 0x00,
            0x06, (byte)'0', (byte)'1', (byte)'0', (byte)'2', (byte)'0', (byte)'3',
            0x03,
            0xFF);
        Require(RoundTripsExactly(fixture), "A standard Binary value must not consume bytes after its decoded-length field.");
    }

    private static void VerifyAsciiPromotion()
    {
        byte[] pooledFixture = WrapRootPayload(
            0x81, 0x01, (byte)'n',
            0x90, 0x03, (byte)'a', (byte)'b', (byte)'c',
            0xFF);
        RtonDocument document = RtonCodec.Decode(pooledFixture);
        RtonValue value = document.Root.FindValue("n")
            ?? throw new InvalidDataException("The ASCII promotion fixture is missing field 'n'.");
        value.SetString("Jos\u00E9");
        Require(value.TypeCode == 0x92, "A non-ASCII Latin character must promote a pooled string to UTF-8.");

        RtonValue decoded = RtonCodec.Decode(RtonCodec.Encode(document)).Root.FindValue("n")
            ?? throw new InvalidDataException("The decoded ASCII promotion fixture is missing field 'n'.");
        RtonStringToken token = (RtonStringToken)decoded.Data;
        Require(decoded.AsString() == "Jos\u00E9", "The promoted UTF-8 string must retain its text.");
        Require(token.DeclaredCharacterLength == 4, "The promoted UTF-8 string must contain four Unicode scalars.");

        // Preserve an unedited legacy single-byte token even though new edits use canonical UTF-8 tags.
        byte[] legacySingleByte = WrapRootPayload(0x81, 0x01, (byte)'x', 0x81, 0x01, 0xE9, 0xFF);
        Require(RoundTripsExactly(legacySingleByte), "An unedited legacy single-byte string must remain byte-identical.");
    }

    private static void VerifyNumericRtidDisplay()
    {
        byte[] fixture = WrapRootPayload(
            0x81, 0x01, (byte)'r',
            0x83, 0x01,
            0x0A, 0x0F,
            0xCD, 0xAB, 0x34, 0x12,
            0xFF);
        RtonValue value = RtonCodec.Decode(fixture).Root.FindValue("r")
            ?? throw new InvalidDataException("The numeric RTID fixture is missing field 'r'.");
        Require(value.ToDisplayString() == "RTID(15.10.1234abcd@)", "Numeric RTID components must use decimal, decimal, and hexadecimal formatting.");
        Require(RoundTripsExactly(fixture), "A numeric RTID must remain byte-identical.");
    }

    private static void VerifyExtendedBooleanPayload()
    {
        byte[] fixture = WrapRootPayload(0x81, 0x01, (byte)'b', 0xBC, 0x02, 0xFF);
        RtonDocument document = RtonCodec.Decode(fixture);
        RtonValue value = document.Root.FindValue("b")
            ?? throw new InvalidDataException("The extended Boolean fixture is missing field 'b'.");
        Require(value.Kind == RtonValueKind.Boolean && (bool)value.Data, "A non-zero extended Boolean payload must decode as true.");
        Require(RoundTripsExactly(fixture), "An unchanged extended Boolean payload must remain byte-identical.");

        value.SetBoolean(false);
        byte[] edited = RtonCodec.Encode(document);
        RtonValue decoded = RtonCodec.Decode(edited).Root.FindValue("b")
            ?? throw new InvalidDataException("The decoded extended Boolean fixture is missing field 'b'.");
        Require(!(bool)decoded.Data, "An edited extended Boolean must decode with its new value.");
        Require(edited.AsSpan().SequenceEqual(RtonCodec.Encode(RtonCodec.Decode(edited))), "An edited extended Boolean must stabilize after encoding.");
    }

    private static bool RoundTripsExactly(byte[] bytes) =>
        bytes.AsSpan().SequenceEqual(RtonCodec.Encode(RtonCodec.Decode(bytes)));

    private static byte[] WrapRootPayload(params byte[] rootPayload)
    {
        List<byte> bytes =
        [
            (byte)'R', (byte)'T', (byte)'O', (byte)'N',
            0x01, 0x00, 0x00, 0x00
        ];
        bytes.AddRange(rootPayload);
        bytes.AddRange([(byte)'D', (byte)'O', (byte)'N', (byte)'E']);
        return [.. bytes];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
