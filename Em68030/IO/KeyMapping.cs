// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Em68030.IO;

/// <summary>
/// Maps a Windows virtual-key code (VK_*) to a Linux input event code (KEY_*).
/// Returns 0 if the key is not mapped.
/// </summary>
public static class KeyMapping
{
    public static ushort WindowsVkToLinuxKey(int vk)
    {
        switch (vk)
        {
            // Row 0: Escape + Function keys
            case 0x1B: return 1;   // VK_ESCAPE -> KEY_ESC
            case 0x70: return 59;  // VK_F1     -> KEY_F1
            case 0x71: return 60;  // VK_F2
            case 0x72: return 61;  // VK_F3
            case 0x73: return 62;  // VK_F4
            case 0x74: return 63;  // VK_F5
            case 0x75: return 64;  // VK_F6
            case 0x76: return 65;  // VK_F7
            case 0x77: return 66;  // VK_F8
            case 0x78: return 67;  // VK_F9
            case 0x79: return 68;  // VK_F10
            case 0x7A: return 87;  // VK_F11
            case 0x7B: return 88;  // VK_F12

            // Row 1: Number row ('0'-'9' = 0x30-0x39)
            case 0x31: return 2;   // '1' -> KEY_1
            case 0x32: return 3;
            case 0x33: return 4;
            case 0x34: return 5;
            case 0x35: return 6;
            case 0x36: return 7;
            case 0x37: return 8;
            case 0x38: return 9;
            case 0x39: return 10;
            case 0x30: return 11;  // '0' -> KEY_0

            // Letter keys ('A'-'Z' = 0x41-0x5A)
            case 0x41: return 30;  // A -> KEY_A
            case 0x42: return 48;  // B -> KEY_B
            case 0x43: return 46;  // C -> KEY_C
            case 0x44: return 32;  // D -> KEY_D
            case 0x45: return 18;  // E -> KEY_E
            case 0x46: return 33;  // F -> KEY_F
            case 0x47: return 34;  // G -> KEY_G
            case 0x48: return 35;  // H -> KEY_H
            case 0x49: return 23;  // I -> KEY_I
            case 0x4A: return 36;  // J -> KEY_J
            case 0x4B: return 37;  // K -> KEY_K
            case 0x4C: return 38;  // L -> KEY_L
            case 0x4D: return 50;  // M -> KEY_M
            case 0x4E: return 49;  // N -> KEY_N
            case 0x4F: return 24;  // O -> KEY_O
            case 0x50: return 25;  // P -> KEY_P
            case 0x51: return 16;  // Q -> KEY_Q
            case 0x52: return 19;  // R -> KEY_R
            case 0x53: return 31;  // S -> KEY_S
            case 0x54: return 20;  // T -> KEY_T
            case 0x55: return 22;  // U -> KEY_U
            case 0x56: return 47;  // V -> KEY_V
            case 0x57: return 17;  // W -> KEY_W
            case 0x58: return 45;  // X -> KEY_X
            case 0x59: return 21;  // Y -> KEY_Y
            case 0x5A: return 44;  // Z -> KEY_Z

            // Special keys
            case 0x08: return 14;  // VK_BACK      -> KEY_BACKSPACE
            case 0x09: return 15;  // VK_TAB       -> KEY_TAB
            case 0x0D: return 28;  // VK_RETURN    -> KEY_ENTER
            case 0x20: return 57;  // VK_SPACE     -> KEY_SPACE

            // Modifier keys
            case 0x10: return 42;  // VK_SHIFT     -> KEY_LEFTSHIFT
            case 0xA0: return 42;  // VK_LSHIFT    -> KEY_LEFTSHIFT
            case 0xA1: return 54;  // VK_RSHIFT    -> KEY_RIGHTSHIFT
            case 0x11: return 29;  // VK_CONTROL   -> KEY_LEFTCTRL
            case 0xA2: return 29;  // VK_LCONTROL  -> KEY_LEFTCTRL
            case 0xA3: return 97;  // VK_RCONTROL  -> KEY_RIGHTCTRL
            case 0x12: return 56;  // VK_MENU      -> KEY_LEFTALT
            case 0xA4: return 56;  // VK_LMENU     -> KEY_LEFTALT
            case 0xA5: return 100; // VK_RMENU     -> KEY_RIGHTALT
            case 0x14: return 58;  // VK_CAPITAL   -> KEY_CAPSLOCK

            // Punctuation (US layout)
            case 0xBD: return 12;  // VK_OEM_MINUS  -> KEY_MINUS
            case 0xBB: return 13;  // VK_OEM_PLUS   -> KEY_EQUAL
            case 0xDB: return 26;  // VK_OEM_4 ([)  -> KEY_LEFTBRACE
            case 0xDD: return 27;  // VK_OEM_6 (])  -> KEY_RIGHTBRACE
            case 0xDC: return 43;  // VK_OEM_5 (\)  -> KEY_BACKSLASH
            case 0xBA: return 39;  // VK_OEM_1 (;)  -> KEY_SEMICOLON
            case 0xDE: return 40;  // VK_OEM_7 (')  -> KEY_APOSTROPHE
            case 0xC0: return 41;  // VK_OEM_3 (`)  -> KEY_GRAVE
            case 0xBC: return 51;  // VK_OEM_COMMA  -> KEY_COMMA
            case 0xBE: return 52;  // VK_OEM_PERIOD -> KEY_DOT
            case 0xBF: return 53;  // VK_OEM_2 (/)  -> KEY_SLASH

            // Navigation
            case 0x2D: return 110; // VK_INSERT   -> KEY_INSERT
            case 0x2E: return 111; // VK_DELETE   -> KEY_DELETE
            case 0x24: return 102; // VK_HOME     -> KEY_HOME
            case 0x23: return 107; // VK_END      -> KEY_END
            case 0x21: return 104; // VK_PRIOR    -> KEY_PAGEUP
            case 0x22: return 109; // VK_NEXT     -> KEY_PAGEDOWN
            case 0x26: return 103; // VK_UP       -> KEY_UP
            case 0x28: return 108; // VK_DOWN     -> KEY_DOWN
            case 0x25: return 105; // VK_LEFT     -> KEY_LEFT
            case 0x27: return 106; // VK_RIGHT    -> KEY_RIGHT

            // Numpad
            case 0x60: return 82;  // VK_NUMPAD0  -> KEY_KP0
            case 0x61: return 79;  // VK_NUMPAD1
            case 0x62: return 80;  // VK_NUMPAD2
            case 0x63: return 81;  // VK_NUMPAD3
            case 0x64: return 75;  // VK_NUMPAD4
            case 0x65: return 76;  // VK_NUMPAD5
            case 0x66: return 77;  // VK_NUMPAD6
            case 0x67: return 71;  // VK_NUMPAD7
            case 0x68: return 72;  // VK_NUMPAD8
            case 0x69: return 73;  // VK_NUMPAD9  -> KEY_KP9
            case 0x6A: return 55;  // VK_MULTIPLY -> KEY_KPASTERISK
            case 0x6B: return 78;  // VK_ADD      -> KEY_KPPLUS
            case 0x6D: return 74;  // VK_SUBTRACT -> KEY_KPMINUS
            case 0x6E: return 83;  // VK_DECIMAL  -> KEY_KPDOT
            case 0x6F: return 98;  // VK_DIVIDE   -> KEY_KPSLASH
            case 0x90: return 69;  // VK_NUMLOCK  -> KEY_NUMLOCK

            // Misc
            case 0x91: return 70;  // VK_SCROLL   -> KEY_SCROLLLOCK
            case 0x13: return 119; // VK_PAUSE    -> KEY_PAUSE
            case 0x2C: return 99;  // VK_SNAPSHOT -> KEY_SYSRQ (PrintScreen)

            default: return 0;
        }
    }

    /// <summary>
    /// Maps an ASCII character to a Linux KEY_* code and whether Shift is needed.
    /// Returns (keyCode, needShift). keyCode==0 means unmapped.
    /// US keyboard layout assumed.
    /// </summary>
    public static (ushort keyCode, bool needShift) CharToLinuxKey(char ch)
    {
        // Letters
        if (ch >= 'a' && ch <= 'z')
        {
            ReadOnlySpan<ushort> map = [30,48,46,32,18,33,34,35,23,36,37,38,50,49,24,25,16,19,31,20,22,47,17,45,21,44];
            return (map[ch - 'a'], false);
        }
        if (ch >= 'A' && ch <= 'Z')
        {
            ReadOnlySpan<ushort> map = [30,48,46,32,18,33,34,35,23,36,37,38,50,49,24,25,16,19,31,20,22,47,17,45,21,44];
            return (map[ch - 'A'], true);
        }

        return ch switch
        {
            '0' => (11, false),
            '1' => (2, false),
            '2' => (3, false),
            '3' => (4, false),
            '4' => (5, false),
            '5' => (6, false),
            '6' => (7, false),
            '7' => (8, false),
            '8' => (9, false),
            '9' => (10, false),

            // Unshifted punctuation
            '-' => (12, false),
            '=' => (13, false),
            '[' => (26, false),
            ']' => (27, false),
            '\\' => (43, false),
            ';' => (39, false),
            '\'' => (40, false),
            '`' => (41, false),
            ',' => (51, false),
            '.' => (52, false),
            '/' => (53, false),

            // Shifted punctuation
            '!' => (2, true),
            '@' => (3, true),
            '#' => (4, true),
            '$' => (5, true),
            '%' => (6, true),
            '^' => (7, true),
            '&' => (8, true),
            '*' => (9, true),
            '(' => (10, true),
            ')' => (11, true),
            '_' => (12, true),
            '+' => (13, true),
            '{' => (26, true),
            '}' => (27, true),
            '|' => (43, true),
            ':' => (39, true),
            '"' => (40, true),
            '~' => (41, true),
            '<' => (51, true),
            '>' => (52, true),
            '?' => (53, true),

            // Whitespace
            ' ' => (57, false),
            '\n' => (28, false),
            '\r' => (28, false),
            '\t' => (15, false),

            _ => (0, false)
        };
    }
}
