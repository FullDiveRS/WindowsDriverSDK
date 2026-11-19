using System.Buffers.Binary;

namespace WindowsDriver.Sdk.Hid;

/// <summary>
/// Builds real USB HID report descriptors by encoding HID short items. Every
/// emitted byte follows the HID 1.11 item encoding:
/// <code>
/// byte = (tag &lt;&lt; 4) | (type &lt;&lt; 2) | sizeCode   // sizeCode: 0=0B, 1=1B, 2=2B, 3=4B
/// type: 0 = Main, 1 = Global, 2 = Local
/// </code>
/// The descriptor can be parsed back with <see cref="HidReportDescriptorParser"/>
/// and fed straight to the Windows HID stack (HidCx) via a UMDF HID mini-driver.
/// </summary>
public sealed class HidReportDescriptorBuilder
{
    private readonly List<byte> _bytes = [];

    /// <summary>Static global state tracked so convenience helpers can restore context.</summary>
    private ushort _currentPage;

    public static HidReportDescriptorBuilder Create() => new();

    // ----- Global items -----

    public HidReportDescriptorBuilder UsagePage(HidUsagePage page)
    {
        _currentPage = (ushort)page;
        return Item(tag: 0x0, type: GlobalType, (int)page);
    }

    public HidReportDescriptorBuilder LogicalMinimum(int value) => Item(0x1, GlobalType, value);
    public HidReportDescriptorBuilder LogicalMaximum(int value) => Item(0x2, GlobalType, value);
    public HidReportDescriptorBuilder PhysicalMinimum(int value) => Item(0x3, GlobalType, value);
    public HidReportDescriptorBuilder PhysicalMaximum(int value) => Item(0x4, GlobalType, value);

    /// <summary>Unit exponent (signed nibble: −8..7, encoded as 4-bit two's complement).</summary>
    public HidReportDescriptorBuilder UnitExponent(int exponent)
    {
        if (exponent < -8 || exponent > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent), "Unit exponent must be in [-8, 7].");
        }
        return Item(0x5, GlobalType, exponent);
    }

    public HidReportDescriptorBuilder Unit(int value) => Item(0x6, GlobalType, value);
    public HidReportDescriptorBuilder ReportSize(int bits)
    {
        if (bits <= 0 || bits > 32) throw new ArgumentOutOfRangeException(nameof(bits), "Report size must be 1..32 bits.");
        return Item(0x7, GlobalType, bits);
    }
    public HidReportDescriptorBuilder ReportId(byte id)
    {
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id), "Report ID 0 is reserved (no-ID reports).");
        return Item(0x8, GlobalType, id);
    }
    public HidReportDescriptorBuilder ReportCount(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Report count must be positive.");
        return Item(0x9, GlobalType, count);
    }

    // ----- Local items -----

    public HidReportDescriptorBuilder Usage(ushort usage) => Item(0x0, LocalType, usage);
    public HidReportDescriptorBuilder Usage(GenericDesktopUsage usage) => Usage((ushort)usage);
    public HidReportDescriptorBuilder UsageMinimum(ushort usage) => Item(0x1, LocalType, usage);
    public HidReportDescriptorBuilder UsageMaximum(ushort usage) => Item(0x2, LocalType, usage);

    // ----- Main items -----

    public HidReportDescriptorBuilder Input(HidMainItemFlags flags = HidMainItemFlags.Data | HidMainItemFlags.Variable)
        => MainItem(0x8, (int)flags);

    public HidReportDescriptorBuilder Output(HidMainItemFlags flags = HidMainItemFlags.Data | HidMainItemFlags.Variable)
        => MainItem(0x9, (int)flags);

    public HidReportDescriptorBuilder Feature(HidMainItemFlags flags = HidMainItemFlags.Data | HidMainItemFlags.Variable)
        => MainItem(0xB, (int)flags);

    public HidReportDescriptorBuilder BeginCollection(HidCollectionType type) => MainItem(0xA, (int)type);

    public HidReportDescriptorBuilder EndCollection() => MainItem(0xC, 0);

    // ----- Convenience blocks -----

    /// <summary>
    /// Emits the standard button block: switches to the Button usage page, declares
    /// <paramref name="count"/> single-bit Data,Variable,Absolute fields with usages
    /// 1..count, pads to a byte boundary, and restores the previous usage page.
    /// </summary>
    public HidReportDescriptorBuilder Buttons(int count, HidMainItemFlags? kind = null)
    {
        var previousPage = _currentPage;
        UsagePage(HidUsagePage.Button);
        UsageMinimum(1);
        UsageMaximum((ushort)count);
        LogicalMinimum(0);
        LogicalMaximum(1);
        ReportSize(1);
        ReportCount(count);
        Input(kind ?? (HidMainItemFlags.Data | HidMainItemFlags.Variable));
        var paddingBits = (8 - (count % 8)) % 8;
        if (paddingBits > 0)
        {
            ReportSize(paddingBits);
            ReportCount(1);
            Input(HidMainItemFlags.Const | HidMainItemFlags.Absolute);
        }
        return UsagePage((HidUsagePage)previousPage);
    }

    /// <summary>Emits a single Data,Variable,Absolute axis field with explicit width and range.</summary>
    public HidReportDescriptorBuilder Axis(GenericDesktopUsage usage, int bits, int logicalMinimum, int logicalMaximum)
    {
        Usage(usage);
        LogicalMinimum(logicalMinimum);
        LogicalMaximum(logicalMaximum);
        ReportSize(bits);
        ReportCount(1);
        return Input();
    }

    /// <summary>Emits Const,Absolute padding bits (keeps report byte-aligned without consuming a usage).</summary>
    public HidReportDescriptorBuilder PaddingInput(int bits)
    {
        ReportSize(bits);
        ReportCount(1);
        return Input(HidMainItemFlags.Const | HidMainItemFlags.Absolute);
    }

    /// <summary>Returns the encoded descriptor bytes.</summary>
    public byte[] ToBytes()
    {
        if (_bytes.Count == 0 || _bytes[^1] != 0xC0)
        {
            throw new InvalidOperationException("Descriptor must end with an EndCollection item.");
        }
        return [.. _bytes];
    }

    public int ByteCount => _bytes.Count;

    // ----- Item encoding -----

    private const int MainType = 0, GlobalType = 1, LocalType = 2;

    private HidReportDescriptorBuilder Item(int tag, int type, int value)
    {
        var (sizeCode, encoded) = EncodeValue(value);
        _bytes.Add((byte)((tag << 4) | (type << 2) | sizeCode));
        _bytes.AddRange(encoded);
        return this;
    }

    private HidReportDescriptorBuilder MainItem(int tag, int value)
    {
        // Main item flags are unsigned bit fields; encode in the smallest unsigned size.
        var (sizeCode, encoded) = EncodeUnsigned((uint)value);
        _bytes.Add((byte)((tag << 4) | (MainType << 2) | sizeCode));
        _bytes.AddRange(encoded);
        return this;
    }

    private static (int SizeCode, byte[] Bytes) EncodeValue(int value)
    {
        // Signed, little-endian, smallest size that holds the value with sign extension.
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
        {
            return (1, [(byte)value]);
        }
        if (value >= short.MinValue && value <= short.MaxValue)
        {
            var buf = new byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(buf, (short)value);
            return (2, buf);
        }
        var buf4 = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf4, value);
        return (3, buf4);
    }

    private static (int SizeCode, byte[] Bytes) EncodeUnsigned(uint value)
    {
        if (value == 0)
        {
            return (0, Array.Empty<byte>()); // zero-value main items encode with no data bytes
        }
        if (value <= byte.MaxValue)
        {
            return (1, [(byte)value]);
        }
        if (value <= ushort.MaxValue)
        {
            var buf = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)value);
            return (2, buf);
        }
        var buf4 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf4, value);
        return (3, buf4);
    }
}
