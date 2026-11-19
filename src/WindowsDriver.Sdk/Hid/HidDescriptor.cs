using System.Buffers.Binary;

namespace WindowsDriver.Sdk.Hid;

/// <summary>
/// Standard HID class descriptors. The 9-byte HID descriptor announces the spec
/// version and the report descriptor length; descriptor type 0x21 = HID,
/// 0x22 = Report.
/// </summary>
public static class HidDescriptor
{
    public const byte HidDescriptorType = 0x21;
    public const byte ReportDescriptorType = 0x22;
    public const int HidDescriptorLength = 9;

    /// <summary>Builds the 9-byte HID class descriptor for the given report descriptor.</summary>
    public static byte[] Build(ReadOnlySpan<byte> reportDescriptor, ushort bcdHid = 0x0111, byte countryCode = 0)
    {
        // Layout (USB HID class descriptor): bLength, bDescriptorType, bcdHID[2],
        // bCountryCode, bNumDescriptors, bDescriptorType2, wDescriptorLength[2].
        var descriptor = new byte[HidDescriptorLength];
        descriptor[0] = HidDescriptorLength;
        descriptor[1] = HidDescriptorType;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(2), bcdHid);
        descriptor[4] = countryCode;
        descriptor[5] = 1; // bNumDescriptors
        descriptor[6] = ReportDescriptorType;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(7), (ushort)reportDescriptor.Length);
        return descriptor;
    }
}

/// <summary>One Input/Output/Feature field decoded from a descriptor.</summary>
public sealed record HidFieldSummary(
    HidMainItemKind Kind,
    byte ReportId,
    int BitWidth,
    int LogicalMinimum,
    int LogicalMaximum,
    int UsagePage,
    int? UsageMinimum,
    int? UsageMaximum);

public enum HidMainItemKind
{
    Input,
    Output,
    Feature,
}

/// <summary>Aggregated per-report-ID sizes.</summary>
public sealed record HidReportLayout(byte ReportId, int InputBits, int OutputBits, int FeatureBits)
{
    /// <summary>Full report size as exchanged on the wire (report-ID byte included when present).</summary>
    public int WireBytes(bool hasReportId) => (hasReportId ? 1 : 0) + (Math.Max(InputBits, Math.Max(OutputBits, FeatureBits)) + 7) / 8;
}

/// <summary>Result of <see cref="HidReportDescriptorParser.Parse"/>.</summary>
public sealed record HidDescriptorSummary
{
    public required IReadOnlyList<HidReportLayout> Reports { get; init; }
    public required IReadOnlyList<HidFieldSummary> Fields { get; init; }
    public int MaxCollectionDepth { get; init; }
    public bool UsesReportIds { get; init; }

    public HidReportLayout? Find(byte reportId) => Reports.FirstOrDefault(r => r.ReportId == reportId);
}

/// <summary>
/// Parses a HID report descriptor back into a structural summary. Used to
/// validate generated descriptors (and for tests). Implements the short-item
/// encoding exactly as <see cref="HidReportDescriptorBuilder"/> emits it.
/// </summary>
public static class HidReportDescriptorParser
{
    public static HidDescriptorSummary Parse(ReadOnlySpan<byte> descriptor)
    {
        var fields = new List<HidFieldSummary>();
        var reportBits = new Dictionary<byte, (int Input, int Output, int Feature)>();
        var globalStack = new Stack<GlobalState>();
        var globals = new GlobalState();
        var locals = new LocalState();
        var depth = 0;
        var maxDepth = 0;
        var i = 0;

        while (i < descriptor.Length)
        {
            var prefix = descriptor[i++];
            var tag = (prefix >> 4) & 0xF;
            var type = (prefix >> 2) & 0x3;
            var sizeCode = prefix & 0x3;

            if (tag == 0xF)
            {
                throw new InvalidDataException("Long items are not supported by this parser (offset " + (i - 1) + ").");
            }
            var dataSize = sizeCode switch { 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => throw new InvalidDataException("bad size") };
            if (i + dataSize > descriptor.Length)
            {
                throw new InvalidDataException("Truncated descriptor: item at offset " + (i - 1) + " needs " + dataSize + " bytes.");
            }
            var value = ReadValue(descriptor.Slice(i, dataSize));
            i += dataSize;

            switch (type)
            {
                case 0: // Main
                    switch (tag)
                    {
                        case 0x8: // Input
                            Accumulate(HidMainItemKind.Input, globals, locals, value, fields, reportBits);
                            locals.Reset();
                            break;
                        case 0x9: // Output
                            Accumulate(HidMainItemKind.Output, globals, locals, value, fields, reportBits);
                            locals.Reset();
                            break;
                        case 0xB: // Feature
                            Accumulate(HidMainItemKind.Feature, globals, locals, value, fields, reportBits);
                            locals.Reset();
                            break;
                        case 0xA: // Collection
                            depth++;
                            maxDepth = Math.Max(maxDepth, depth);
                            locals.Reset();
                            break;
                        case 0xC: // End collection
                            depth--;
                            if (depth < 0)
                            {
                                throw new InvalidDataException("Unmatched EndCollection in descriptor.");
                            }
                            break;
                    }
                    break;

                case 1: // Global
                    switch (tag)
                    {
                        case 0x0: globals.UsagePage = value; break;
                        case 0x1: globals.LogicalMin = value; break;
                        case 0x2: globals.LogicalMax = value; break;
                        case 0x3: globals.PhysicalMin = value; break;
                        case 0x4: globals.PhysicalMax = value; break;
                        case 0x5: break; // unit exponent — not tracked
                        case 0x6: break; // unit — not tracked
                        case 0x7: globals.ReportSize = (int)value; break;
                        case 0x8: globals.ReportId = (byte)value; break;
                        case 0x9: globals.ReportCount = (int)value; break;
                        case 0xA: globalStack.Push(globals.Clone()); break; // Push
                        case 0xB: globals = globalStack.Count > 0 ? globalStack.Pop() : throw new InvalidDataException("Pop without Push."); break;
                    }
                    break;

                case 2: // Local
                    switch (tag)
                    {
                        case 0x0: locals.Usages.Add((int)value); break;
                        case 0x1: locals.UsageMin = (int)value; break;
                        case 0x2: locals.UsageMax = (int)value; break;
                    }
                    break;
            }
        }

        if (depth != 0)
        {
            throw new InvalidDataException($"{depth} collection(s) left open at end of descriptor.");
        }
        if (globalStack.Count != 0)
        {
            throw new InvalidDataException("Push without matching Pop at end of descriptor.");
        }

        return new HidDescriptorSummary
        {
            Fields = fields,
            Reports = reportBits.Select(kvp => new HidReportLayout(kvp.Key, kvp.Value.Input, kvp.Value.Output, kvp.Value.Feature)).ToList(),
            MaxCollectionDepth = maxDepth,
            UsesReportIds = reportBits.Keys.Any(id => id != 0),
        };
    }

    private static void Accumulate(
        HidMainItemKind kind,
        GlobalState globals,
        LocalState locals,
        int flags,
        List<HidFieldSummary> fields,
        Dictionary<byte, (int, int, int)> reportBits)
    {
        var bitWidth = globals.ReportSize * globals.ReportCount;
        var isConst = (flags & 0x1) != 0;
        if (!isConst)
        {
            fields.Add(new HidFieldSummary(
                kind,
                globals.ReportId,
                bitWidth,
                globals.LogicalMin,
                globals.LogicalMax,
                globals.UsagePage,
                locals.UsageMin,
                locals.UsageMax));
        }
        var entry = reportBits.TryGetValue(globals.ReportId, out var bits) ? bits : (0, 0, 0);
        var updated = kind switch
        {
            HidMainItemKind.Input => (entry.Item1 + bitWidth, entry.Item2, entry.Item3),
            HidMainItemKind.Output => (entry.Item1, entry.Item2 + bitWidth, entry.Item3),
            _ => (entry.Item1, entry.Item2, entry.Item3 + bitWidth),
        };
        reportBits[globals.ReportId] = updated;
    }

    private static int ReadValue(ReadOnlySpan<byte> data)
    {
        // Signed, little-endian.
        return data.Length switch
        {
            0 => 0,
            1 => (sbyte)data[0],
            2 => (short)(data[0] | (data[1] << 8)),
            4 => data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24),
            _ => throw new InvalidDataException("Invalid item data size."),
        };
    }

    private sealed class GlobalState
    {
        public int UsagePage;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        public int ReportSize;
        public int ReportCount = 1;
        public byte ReportId;

        public GlobalState Clone() => (GlobalState)MemberwiseClone();
    }

    private sealed class LocalState
    {
        public List<int> Usages { get; } = [];
        public int? UsageMin;
        public int? UsageMax;

        public void Reset()
        {
            Usages.Clear();
            UsageMin = null;
            UsageMax = null;
        }
    }
}

/// <summary>Canonical descriptors shipped with the SDK.</summary>
public static class HidDescriptors
{
    /// <summary>
    /// 6-axis + 12-button gamepad: X/Y/Z + Rx/Ry/Rz as 16-bit axes, 12 buttons,
    /// input report 1. Produces a 15-byte input report (ID + 2 buttons/pad bytes
    /// + 12 axis bytes).
    /// </summary>
    public static byte[] GamepadSixAxisTwelveButtons()
    {
        return HidReportDescriptorBuilder.Create()
            .UsagePage(HidUsagePage.GenericDesktop)
            .Usage(GenericDesktopUsage.GamePad)
            .BeginCollection(HidCollectionType.Application)
            .ReportId(1)
            .Buttons(12)
            .Axis(GenericDesktopUsage.X, 16, short.MinValue, short.MaxValue)
            .Axis(GenericDesktopUsage.Y, 16, short.MinValue, short.MaxValue)
            .Axis(GenericDesktopUsage.Z, 16, short.MinValue, short.MaxValue)
            .Axis(GenericDesktopUsage.Rx, 16, short.MinValue, short.MaxValue)
            .Axis(GenericDesktopUsage.Ry, 16, short.MinValue, short.MaxValue)
            .Axis(GenericDesktopUsage.Rz, 16, short.MinValue, short.MaxValue)
            .EndCollection()
            .ToBytes();
    }
}
