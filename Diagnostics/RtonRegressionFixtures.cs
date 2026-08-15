using ZombiesVsPlants2.SaveEditor.Rton;

namespace ZombiesVsPlants2.SaveEditor.Diagnostics;

internal static class RtonRegressionFixtures
{
    public static void Run()
    {
        VerifyBinaryWireShape();
        VerifyAsciiPromotion();
        VerifyPropertyKeyEditing();
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

    private static void VerifyPropertyKeyEditing()
    {
        byte[] directFixture = WrapRootPayload(
            0x81, 0x04, (byte)'n', (byte)'a', (byte)'m', (byte)'e',
            0x21,
            0xFF);
        RtonDocument directDocument = RtonCodec.Decode(directFixture);
        Require(!directDocument.Root.RenameProperty(0, "name"), "Renaming a property to its exact current name must be a no-op.");
        Require(
            directFixture.AsSpan().SequenceEqual(RtonCodec.Encode(directDocument)),
            "A no-op property rename must preserve the original bytes.");

        Require(directDocument.Root.RenameProperty(0, "na\u00EFve"), "A changed property key must report a modification.");
        Require(directDocument.Root.Properties[0].Key.TypeCode == 0x82, "A non-ASCII direct key must promote to UTF-8.");
        byte[] editedBytes = RtonCodec.Encode(directDocument);
        RtonDocument editedDocument = RtonCodec.Decode(editedBytes);
        Require(editedDocument.Root.FindProperty("na\u00EFve") is not null, "An edited property key must survive encoding and decoding.");
        Require(
            editedBytes.AsSpan().SequenceEqual(RtonCodec.Encode(editedDocument)),
            "An edited property key must stabilize after encoding.");

        RtonDocument pooledValueDocument = RtonCodec.Decode(WrapRootPayload(
            0x90, 0x05, (byte)'a', (byte)'l', (byte)'p', (byte)'h', (byte)'a',
            0x90, 0x05, (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e',
            0x91, 0x00,
            0x91, 0x01,
            0x91, 0x01,
            0x91, 0x00,
            0xFF));
        Require(
            pooledValueDocument.Root.RenameProperty(0, "\u00E1lpha"),
            "A pooled-value key rename must report a modification.");
        Require(
            pooledValueDocument.Root.Properties[0].Key.TypeCode == 0x92,
            "A non-ASCII pooled-value key must promote to UTF-8.");
        byte[] rebuiltPoolBytes = RtonCodec.Encode(pooledValueDocument);
        RtonDocument rebuiltPoolDocument = RtonCodec.Decode(rebuiltPoolBytes);
        Require(
            rebuiltPoolDocument.Root.Properties[0].Key.Text == "\u00E1lpha"
                && rebuiltPoolDocument.Root.Properties[0].Value.AsString() == "value"
                && rebuiltPoolDocument.Root.Properties[1].Key.Text == "alpha"
                && rebuiltPoolDocument.Root.Properties[1].Value.AsString() == "value"
                && rebuiltPoolDocument.Root.Properties[2].Key.Text == "value"
                && rebuiltPoolDocument.Root.Properties[2].Value.AsString() == "alpha",
            "Rebuilding a pool after a declaration rename must preserve all downstream keys and values.");
        Require(
            rebuiltPoolBytes.AsSpan().SequenceEqual(RtonCodec.Encode(rebuiltPoolDocument)),
            "A promoted pooled-value key must survive encoding and decoding.");

        RtonDocument restoredDocument = RtonCodec.Decode(directFixture);
        Require(
            restoredDocument.Root.FindProperty("name") is not null
                && directFixture.AsSpan().SequenceEqual(RtonCodec.Encode(restoredDocument)),
            "Restoring a pre-edit byte snapshot must provide byte-exact property-key undo.");

        byte[] pooledReferenceFixture = WrapRootPayload(
            0x81, 0x04, (byte)'s', (byte)'e', (byte)'e', (byte)'d',
            0x90, 0x05, (byte)'a', (byte)'l', (byte)'p', (byte)'h', (byte)'a',
            0x91, 0x00,
            0x91, 0x00,
            0x81, 0x01, (byte)'z',
            0x91, 0x00,
            0xFF);
        RtonDocument pooledReferenceDocument = RtonCodec.Decode(pooledReferenceFixture);
        Require(
            pooledReferenceDocument.Root.RenameProperty(1, "caf\u00E9"),
            "A pooled-reference key rename must report a modification.");
        Require(
            pooledReferenceDocument.Root.Properties[1].Key.TypeCode == 0x93,
            "A non-ASCII pooled-reference key must promote to a UTF-8 pool reference.");
        RtonDocument decodedPooledReference = RtonCodec.Decode(RtonCodec.Encode(pooledReferenceDocument));
        Require(
            decodedPooledReference.Root.FindProperty("caf\u00E9")?.Value.AsString() == "alpha"
                && decodedPooledReference.Root.FindValue("seed")?.AsString() == "alpha"
                && decodedPooledReference.Root.FindValue("z")?.AsString() == "alpha",
            "A promoted pooled-reference key must preserve neighboring and downstream pool references.");

        byte[] duplicateFixture = WrapRootPayload(
            0x81, 0x01, (byte)'a', 0x21,
            0x81, 0x01, (byte)'b', 0x21,
            0xFF);
        RtonDocument duplicateDocument = RtonCodec.Decode(duplicateFixture);
        RequireThrows<InvalidOperationException>(
            () => duplicateDocument.Root.RenameProperty(1, "a"),
            "A property must not be renamed to an exact duplicate key.");
        Require(
            duplicateDocument.Root.Properties[1].Key.Text == "b",
            "A rejected duplicate key must not mutate the object.");
        Require(
            duplicateDocument.Root.RenameProperty(1, "A"),
            "Duplicate-key validation must remain ordinal and case-sensitive.");
        Require(duplicateDocument.Root.RenameProperty(1, string.Empty), "An empty property key must remain valid.");
        RtonDocument emptyKeyDocument = RtonCodec.Decode(RtonCodec.Encode(duplicateDocument));
        Require(emptyKeyDocument.Root.Properties[1].Key.Text.Length == 0, "An empty property key must round-trip.");
        Require(
            emptyKeyDocument.Root.RenameProperty(1, "line\nbreak"),
            "A property key containing a control character must remain editable.");
        Require(
            RtonCodec.Decode(RtonCodec.Encode(emptyKeyDocument)).Root.Properties[1].Key.Text == "line\nbreak",
            "A property key containing a control character must round-trip.");

        RtonDocument specialKeyDocument = RtonCodec.Decode(WrapRootPayload(0x84, 0x21, 0xFF));
        Require(
            !specialKeyDocument.Root.RenameProperty(0, "RTID(0)"),
            "An unchanged special key must remain a no-op.");
        RequireThrows<InvalidOperationException>(
            () => specialKeyDocument.Root.RenameProperty(0, "renamed"),
            "A special property key type must remain read-only.");

        RtonDocument binaryKeyDocument = RtonCodec.Decode(WrapRootPayload(
            0x87, 0x00,
            0x06, (byte)'0', (byte)'1', (byte)'0', (byte)'2', (byte)'0', (byte)'3',
            0x03,
            0x21,
            0xFF));
        RequireThrows<InvalidOperationException>(
            () => binaryKeyDocument.Root.RenameProperty(0, "renamed"),
            "A Binary property key type must remain read-only.");

        RequireThrows<ArgumentOutOfRangeException>(
            () => directDocument.Root.RenameProperty(-1, "invalid"),
            "A negative property index must be rejected.");
        RequireThrows<ArgumentOutOfRangeException>(
            () => directDocument.Root.RenameProperty(directDocument.Root.Properties.Count, "invalid"),
            "A property index equal to the property count must be rejected.");
        RequireThrows<ArgumentNullException>(
            () => directDocument.Root.RenameProperty(0, null!),
            "A null property name must be rejected.");
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

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidDataException(message);
    }
}
