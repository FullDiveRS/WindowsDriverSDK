namespace WindowsDriver.Sdk.Hid;

/// <summary>USB HID usage page IDs (HID 1.11 spec, table of major usage pages).</summary>
public enum HidUsagePage : ushort
{
    GenericDesktop = 0x01,
    Simulation = 0x02,
    Button = 0x09,
    Ordinal = 0x0A,
    Telephony = 0x0B,
    Consumer = 0x0C,
    Digitizer = 0x0D,
    Sensor = 0x20,
    VendorDefinedStart = 0xFF00,
}

/// <summary>Generic Desktop page usages used by joysticks/gamepads and mouse-like devices.</summary>
public enum GenericDesktopUsage : ushort
{
    Pointer = 0x01,
    Mouse = 0x02,
    Joystick = 0x04,
    GamePad = 0x05,
    Keyboard = 0x06,
    Keypad = 0x07,
    SystemControl = 0x80,
    X = 0x30,
    Y = 0x31,
    Z = 0x32,
    Rx = 0x33,
    Ry = 0x34,
    Rz = 0x35,
    Slider = 0x36,
    Dial = 0x37,
    Wheel = 0x38,
    HatSwitch = 0x39,
}

/// <summary>HID collection types.</summary>
public enum HidCollectionType : byte
{
    Physical = 0x00,
    Application = 0x01,
    Logical = 0x02,
    Report = 0x03,
    NamedArray = 0x04,
    UsageSwitch = 0x05,
    UsageModifier = 0x06,
}

/// <summary>Input/Output/Feature main item flags ([Data/Const][Array/Variable][Absolute/Relative]...).</summary>
[Flags]
public enum HidMainItemFlags
{
    Data = 0x0000,
    Const = 0x0001,
    Array = 0x0000,
    Variable = 0x0002,
    Absolute = 0x0000,
    Relative = 0x0004,
    NoWrap = 0x0000,
    Wrap = 0x0008,
    Linear = 0x0000,
    NonLinear = 0x0010,
    PreferredState = 0x0000,
    NoPreferredState = 0x0020,
    NoNullPosition = 0x0000,
    NullState = 0x0040,
    NonVolatile = 0x0000,
    Volatile = 0x0080,
}
