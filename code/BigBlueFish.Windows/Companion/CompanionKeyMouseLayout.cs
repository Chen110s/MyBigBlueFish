using System.Collections.Generic;

namespace BigBlueFish.Windows;

// The two keyboards of 键鼠陪伴, from KuroCat (MIT, github.com/qianyiai/KuroCat, keyboardLayout.ts): a 65% board
// (15 x 5 key units) and a full-size 108-key board (22.7 x 6.25). Row and column are in key units; a key lists every
// virtual-key code that lights it (on the 65% board the F-keys light the number row, as on KuroCat).
internal sealed class CompanionKey
{
    public readonly string Id, Label, Bubble;
    public readonly double Row, RowSpan, UStart, UWidth;
    public readonly char Accent; // 'o' orange, 'm' modifier, 'n' plain
    public readonly int[] Vks;

    public CompanionKey(string id, string label, string bubble, double row, double rowSpan, double uStart, double uWidth, char accent, int[] vks)
    {
        Id = id; Label = label; Bubble = bubble; Row = row; RowSpan = rowSpan; UStart = uStart; UWidth = uWidth; Accent = accent; Vks = vks;
    }
}

internal sealed class CompanionKeyboardLayout
{
    public const int NumpadEnter = 0x10d; // KuroCat's own code for the numpad Enter (Enter lights it here)
    private const double Nav = 15.35, Num = 18.7;

    public readonly string Id;
    public readonly double TotalUnits, TotalRows;
    public readonly CompanionKey[] Keys;
    private readonly Dictionary<int, CompanionKey> byVk = new();

    private CompanionKeyboardLayout(string id, double units, double rows, CompanionKey[] keys)
    {
        Id = id; TotalUnits = units; TotalRows = rows; Keys = keys;
        foreach (var key in keys)
            foreach (int vk in key.Vks) byVk[vk] = key;
    }

    public bool Full => Id == "108";

    public CompanionKey? Find(int vk)
    {
        if (byVk.TryGetValue(vk, out var key)) return key;
        if (!Full && vk >= 0x70 && vk <= 0x87) return Keys[System.Math.Min(12, System.Math.Max(1, vk - 0x6f))];
        return null;
    }

    private static CompanionKey K(string id, string label, string bubble, double row, double span, double u, double w, char accent, params int[] vks)
        => new(id, label, bubble, row, span, u, w, accent, vks);

    public static readonly CompanionKeyboardLayout Compact = new("65", 15, 5,
    [
        K("esc", "ESC", "Esc", 0, 1, 0, 1, 'o', 0x1b, 0xc0),
        K("1", "1", "1", 0, 1, 1, 1, 'n', 0x31, 0x61, 0x70),
        K("2", "2", "2", 0, 1, 2, 1, 'n', 0x32, 0x62, 0x71),
        K("3", "3", "3", 0, 1, 3, 1, 'n', 0x33, 0x63, 0x72),
        K("4", "4", "4", 0, 1, 4, 1, 'n', 0x34, 0x64, 0x73),
        K("5", "5", "5", 0, 1, 5, 1, 'n', 0x35, 0x65, 0x74),
        K("6", "6", "6", 0, 1, 6, 1, 'n', 0x36, 0x66, 0x75),
        K("7", "7", "7", 0, 1, 7, 1, 'n', 0x37, 0x67, 0x76),
        K("8", "8", "8", 0, 1, 8, 1, 'n', 0x38, 0x68, 0x77),
        K("9", "9", "9", 0, 1, 9, 1, 'n', 0x39, 0x69, 0x78),
        K("0", "0", "0", 0, 1, 10, 1, 'n', 0x30, 0x60, 0x79),
        K("minus", "-", "-", 0, 1, 11, 1, 'n', 0xbd, 0x6d, 0x7a),
        K("equal", "=", "=", 0, 1, 12, 1, 'n', 0xbb, 0x6b, 0x7b),
        K("bs", "⌫", "⌫", 0, 1, 13, 2, 'm', 0x08, 0x2e),
        K("tab", "↹", "Tab", 1, 1, 0, 1.5, 'm', 0x09),
        K("q", "Q", "Q", 1, 1, 1.5, 1, 'n', 0x51),
        K("w", "W", "W", 1, 1, 2.5, 1, 'n', 0x57),
        K("e", "E", "E", 1, 1, 3.5, 1, 'n', 0x45),
        K("r", "R", "R", 1, 1, 4.5, 1, 'n', 0x52),
        K("t", "T", "T", 1, 1, 5.5, 1, 'n', 0x54),
        K("y", "Y", "Y", 1, 1, 6.5, 1, 'n', 0x59),
        K("u", "U", "U", 1, 1, 7.5, 1, 'n', 0x55),
        K("i", "I", "I", 1, 1, 8.5, 1, 'n', 0x49),
        K("o", "O", "O", 1, 1, 9.5, 1, 'n', 0x4f),
        K("p", "P", "P", 1, 1, 10.5, 1, 'n', 0x50),
        K("lb", "[", "[", 1, 1, 11.5, 1, 'n', 0xdb),
        K("rb", "]", "]", 1, 1, 12.5, 1, 'n', 0xdd),
        K("bslash", "\\", "\\", 1, 1, 13.5, 1.5, 'm', 0xdc, 0x2d, 0x24),
        K("caps", "⇪", "Caps", 2, 1, 0, 1.75, 'm', 0x14),
        K("a", "A", "A", 2, 1, 1.75, 1, 'n', 0x41),
        K("s", "S", "S", 2, 1, 2.75, 1, 'n', 0x53),
        K("d", "D", "D", 2, 1, 3.75, 1, 'n', 0x44),
        K("f", "F", "F", 2, 1, 4.75, 1, 'n', 0x46),
        K("g", "G", "G", 2, 1, 5.75, 1, 'n', 0x47),
        K("h", "H", "H", 2, 1, 6.75, 1, 'n', 0x48),
        K("j", "J", "J", 2, 1, 7.75, 1, 'n', 0x4a),
        K("k", "K", "K", 2, 1, 8.75, 1, 'n', 0x4b),
        K("l", "L", "L", 2, 1, 9.75, 1, 'n', 0x4c),
        K("semi", ";", ";", 2, 1, 10.75, 1, 'n', 0xba),
        K("quote", "'", "'", 2, 1, 11.75, 1, 'n', 0xde),
        K("enter", "↵", "↵", 2, 1, 12.75, 2.25, 'm', 0x0d, NumpadEnter),
        K("lshift", "⇧", "⇧", 3, 1, 0, 2.25, 'm', 0xa0, 0x10),
        K("z", "Z", "Z", 3, 1, 2.25, 1, 'n', 0x5a),
        K("x", "X", "X", 3, 1, 3.25, 1, 'n', 0x58),
        K("c", "C", "C", 3, 1, 4.25, 1, 'n', 0x43),
        K("v", "V", "V", 3, 1, 5.25, 1, 'n', 0x56),
        K("b", "B", "B", 3, 1, 6.25, 1, 'n', 0x42),
        K("n", "N", "N", 3, 1, 7.25, 1, 'n', 0x4e),
        K("m", "M", "M", 3, 1, 8.25, 1, 'n', 0x4d),
        K("comma", ",", ",", 3, 1, 9.25, 1, 'n', 0xbc),
        K("dot", ".", ".", 3, 1, 10.25, 1, 'n', 0xbe, 0x6e),
        K("slash", "/", "/", 3, 1, 11.25, 1, 'n', 0xbf, 0x6f),
        K("rshift", "⇧", "⇧", 3, 1, 12.25, 2.75, 'o', 0xa1),
        K("lctrl", "Ctrl", "Ctrl", 4, 1, 0, 1.25, 'm', 0xa2, 0x11),
        K("lwin", "Win", "Win", 4, 1, 1.25, 1.25, 'm', 0x5b, 0x5c),
        K("lalt", "Alt", "Alt", 4, 1, 2.5, 1.25, 'm', 0xa4, 0x12),
        K("space", "␣", "␣", 4, 1, 3.75, 6.25, 'n', 0x20),
        K("ralt", "Alt", "Alt", 4, 1, 10, 1, 'm', 0xa5, 0xa3),
        K("left", "←", "←", 4, 1, 11, 1, 'm', 0x25, 0x23),
        K("down", "↓", "↓", 4, 1, 12, 1, 'm', 0x28, 0x22),
        K("up", "↑", "↑", 4, 1, 13, 1, 'm', 0x26, 0x21),
        K("right", "→", "→", 4, 1, 14, 1, 'm', 0x27),
    ]);

    public static readonly CompanionKeyboardLayout FullSize = new("108", 22.7, 6.25,
    [
        K("esc", "Esc", "Esc", 0, 1, 0, 1, 'o', 0x1b),
        K("f1", "F1", "F1", 0, 1, 2, 1, 'n', 0x70),
        K("f2", "F2", "F2", 0, 1, 3, 1, 'n', 0x71),
        K("f3", "F3", "F3", 0, 1, 4, 1, 'n', 0x72),
        K("f4", "F4", "F4", 0, 1, 5, 1, 'n', 0x73),
        K("f5", "F5", "F5", 0, 1, 6.5, 1, 'm', 0x74),
        K("f6", "F6", "F6", 0, 1, 7.5, 1, 'm', 0x75),
        K("f7", "F7", "F7", 0, 1, 8.5, 1, 'm', 0x76),
        K("f8", "F8", "F8", 0, 1, 9.5, 1, 'm', 0x77),
        K("f9", "F9", "F9", 0, 1, 11, 1, 'n', 0x78),
        K("f10", "F10", "F10", 0, 1, 12, 1, 'n', 0x79),
        K("f11", "F11", "F11", 0, 1, 13, 1, 'n', 0x7a),
        K("f12", "F12", "F12", 0, 1, 14, 1, 'n', 0x7b),
        K("prt", "Prt", "PrtSc", 0, 1, Nav, 1, 'm', 0x2c),
        K("scr", "Scr", "ScrLk", 0, 1, Nav + 1, 1, 'm', 0x91),
        K("pau", "Pau", "Pause", 0, 1, Nav + 2, 1, 'm', 0x13),
        K("m_mute", "M1", "Mute", 0, 1, Num, 1, 'm', 0xad),
        K("m_vdown", "M2", "Vol-", 0, 1, Num + 1, 1, 'm', 0xae),
        K("m_vup", "M3", "Vol+", 0, 1, Num + 2, 1, 'm', 0xaf),
        K("m_calc", "M4", "Calc", 0, 1, Num + 3, 1, 'm', 0xb7, 0xb3),
        K("grave", "`", "`", 1.25, 1, 0, 1, 'n', 0xc0),
        K("1", "1", "1", 1.25, 1, 1, 1, 'n', 0x31),
        K("2", "2", "2", 1.25, 1, 2, 1, 'n', 0x32),
        K("3", "3", "3", 1.25, 1, 3, 1, 'n', 0x33),
        K("4", "4", "4", 1.25, 1, 4, 1, 'n', 0x34),
        K("5", "5", "5", 1.25, 1, 5, 1, 'n', 0x35),
        K("6", "6", "6", 1.25, 1, 6, 1, 'n', 0x36),
        K("7", "7", "7", 1.25, 1, 7, 1, 'n', 0x37),
        K("8", "8", "8", 1.25, 1, 8, 1, 'n', 0x38),
        K("9", "9", "9", 1.25, 1, 9, 1, 'n', 0x39),
        K("0", "0", "0", 1.25, 1, 10, 1, 'n', 0x30),
        K("minus", "-", "-", 1.25, 1, 11, 1, 'n', 0xbd),
        K("equal", "=", "=", 1.25, 1, 12, 1, 'n', 0xbb),
        K("bs", "⌫", "⌫", 1.25, 1, 13, 2, 'm', 0x08),
        K("ins", "Ins", "Ins", 1.25, 1, Nav, 1, 'm', 0x2d),
        K("home", "Hm", "Home", 1.25, 1, Nav + 1, 1, 'm', 0x24),
        K("pgup", "PU", "PgUp", 1.25, 1, Nav + 2, 1, 'm', 0x21),
        K("numlk", "Num", "Num", 1.25, 1, Num, 1, 'm', 0x90),
        K("np_div", "/", "Num/", 1.25, 1, Num + 1, 1, 'm', 0x6f),
        K("np_mul", "*", "Num*", 1.25, 1, Num + 2, 1, 'm', 0x6a),
        K("np_sub", "-", "Num-", 1.25, 1, Num + 3, 1, 'm', 0x6d),
        K("tab", "↹", "Tab", 2.25, 1, 0, 1.5, 'm', 0x09),
        K("q", "Q", "Q", 2.25, 1, 1.5, 1, 'n', 0x51),
        K("w", "W", "W", 2.25, 1, 2.5, 1, 'n', 0x57),
        K("e", "E", "E", 2.25, 1, 3.5, 1, 'n', 0x45),
        K("r", "R", "R", 2.25, 1, 4.5, 1, 'n', 0x52),
        K("t", "T", "T", 2.25, 1, 5.5, 1, 'n', 0x54),
        K("y", "Y", "Y", 2.25, 1, 6.5, 1, 'n', 0x59),
        K("u", "U", "U", 2.25, 1, 7.5, 1, 'n', 0x55),
        K("i", "I", "I", 2.25, 1, 8.5, 1, 'n', 0x49),
        K("o", "O", "O", 2.25, 1, 9.5, 1, 'n', 0x4f),
        K("p", "P", "P", 2.25, 1, 10.5, 1, 'n', 0x50),
        K("lb", "[", "[", 2.25, 1, 11.5, 1, 'n', 0xdb),
        K("rb", "]", "]", 2.25, 1, 12.5, 1, 'n', 0xdd),
        K("bslash", "\\", "\\", 2.25, 1, 13.5, 1.5, 'm', 0xdc),
        K("del", "Del", "Del", 2.25, 1, Nav, 1, 'm', 0x2e),
        K("end", "End", "End", 2.25, 1, Nav + 1, 1, 'm', 0x23),
        K("pgdn", "PD", "PgDn", 2.25, 1, Nav + 2, 1, 'm', 0x22),
        K("np_7", "7", "Num7", 2.25, 1, Num, 1, 'n', 0x67),
        K("np_8", "8", "Num8", 2.25, 1, Num + 1, 1, 'n', 0x68),
        K("np_9", "9", "Num9", 2.25, 1, Num + 2, 1, 'n', 0x69),
        K("np_add", "+", "Num+", 2.25, 2, Num + 3, 1, 'm', 0x6b),
        K("caps", "⇪", "Caps", 3.25, 1, 0, 1.75, 'm', 0x14),
        K("a", "A", "A", 3.25, 1, 1.75, 1, 'n', 0x41),
        K("s", "S", "S", 3.25, 1, 2.75, 1, 'n', 0x53),
        K("d", "D", "D", 3.25, 1, 3.75, 1, 'n', 0x44),
        K("f", "F", "F", 3.25, 1, 4.75, 1, 'n', 0x46),
        K("g", "G", "G", 3.25, 1, 5.75, 1, 'n', 0x47),
        K("h", "H", "H", 3.25, 1, 6.75, 1, 'n', 0x48),
        K("j", "J", "J", 3.25, 1, 7.75, 1, 'n', 0x4a),
        K("k", "K", "K", 3.25, 1, 8.75, 1, 'n', 0x4b),
        K("l", "L", "L", 3.25, 1, 9.75, 1, 'n', 0x4c),
        K("semi", ";", ";", 3.25, 1, 10.75, 1, 'n', 0xba),
        K("quote", "'", "'", 3.25, 1, 11.75, 1, 'n', 0xde),
        K("enter", "↵", "↵", 3.25, 1, 12.75, 2.25, 'm', 0x0d),
        K("np_4", "4", "Num4", 3.25, 1, Num, 1, 'n', 0x64),
        K("np_5", "5", "Num5", 3.25, 1, Num + 1, 1, 'n', 0x65),
        K("np_6", "6", "Num6", 3.25, 1, Num + 2, 1, 'n', 0x66),
        K("lshift", "⇧", "⇧", 4.25, 1, 0, 2.25, 'm', 0xa0, 0x10),
        K("z", "Z", "Z", 4.25, 1, 2.25, 1, 'n', 0x5a),
        K("x", "X", "X", 4.25, 1, 3.25, 1, 'n', 0x58),
        K("c", "C", "C", 4.25, 1, 4.25, 1, 'n', 0x43),
        K("v", "V", "V", 4.25, 1, 5.25, 1, 'n', 0x56),
        K("b", "B", "B", 4.25, 1, 6.25, 1, 'n', 0x42),
        K("n", "N", "N", 4.25, 1, 7.25, 1, 'n', 0x4e),
        K("m", "M", "M", 4.25, 1, 8.25, 1, 'n', 0x4d),
        K("comma", ",", ",", 4.25, 1, 9.25, 1, 'n', 0xbc),
        K("dot", ".", ".", 4.25, 1, 10.25, 1, 'n', 0xbe),
        K("slash", "/", "/", 4.25, 1, 11.25, 1, 'n', 0xbf),
        K("rshift", "⇧", "⇧", 4.25, 1, 12.25, 2.75, 'o', 0xa1),
        K("up", "↑", "↑", 4.25, 1, Nav + 1, 1, 'm', 0x26),
        K("np_1", "1", "Num1", 4.25, 1, Num, 1, 'n', 0x61),
        K("np_2", "2", "Num2", 4.25, 1, Num + 1, 1, 'n', 0x62),
        K("np_3", "3", "Num3", 4.25, 1, Num + 2, 1, 'n', 0x63),
        K("np_ent", "↵", "Num↵", 4.25, 2, Num + 3, 1, 'o', NumpadEnter),
        K("lctrl", "Ctrl", "Ctrl", 5.25, 1, 0, 1.25, 'm', 0xa2, 0x11),
        K("lwin", "Win", "Win", 5.25, 1, 1.25, 1.25, 'm', 0x5b),
        K("lalt", "Alt", "Alt", 5.25, 1, 2.5, 1.25, 'm', 0xa4, 0x12),
        K("space", "␣", "␣", 5.25, 1, 3.75, 6.25, 'n', 0x20),
        K("ralt", "Alt", "Alt", 5.25, 1, 10, 1.25, 'm', 0xa5),
        K("rwin", "Win", "Win", 5.25, 1, 11.25, 1.25, 'm', 0x5c),
        K("menu", "▤", "Menu", 5.25, 1, 12.5, 1.25, 'm', 0x5d),
        K("rctrl", "Ctrl", "Ctrl", 5.25, 1, 13.75, 1.25, 'm', 0xa3),
        K("left", "←", "←", 5.25, 1, Nav, 1, 'm', 0x25),
        K("down", "↓", "↓", 5.25, 1, Nav + 1, 1, 'm', 0x28),
        K("right", "→", "→", 5.25, 1, Nav + 2, 1, 'm', 0x27),
        K("np_0", "0", "Num0", 5.25, 1, Num, 2, 'n', 0x60),
        K("np_dot", ".", "Num.", 5.25, 1, Num + 2, 1, 'n', 0x6e),
    ]);

    public static CompanionKeyboardLayout For(string id) => id == "108" ? FullSize : Compact;
}
