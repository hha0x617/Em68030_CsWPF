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

using Em68030.IO;
using Xunit;

namespace Em68030.Tests.IoTests;

public class KeyMappingTests
{
    // ============================================================================
    // Escape and function keys
    // ============================================================================

    [Fact]
    public void Escape_MapsToKeyEsc()
    {
        Assert.Equal((ushort)1, KeyMapping.WindowsVkToLinuxKey(0x1B));   // VK_ESCAPE -> KEY_ESC
    }

    [Fact]
    public void F1_MapsToKeyF1()
    {
        Assert.Equal((ushort)59, KeyMapping.WindowsVkToLinuxKey(0x70));  // VK_F1 -> KEY_F1
    }

    [Fact]
    public void F10_MapsToKeyF10()
    {
        Assert.Equal((ushort)68, KeyMapping.WindowsVkToLinuxKey(0x79));  // VK_F10 -> KEY_F10
    }

    [Fact]
    public void F11_MapsToKeyF11()
    {
        Assert.Equal((ushort)87, KeyMapping.WindowsVkToLinuxKey(0x7A));  // VK_F11 -> KEY_F11
    }

    [Fact]
    public void F12_MapsToKeyF12()
    {
        Assert.Equal((ushort)88, KeyMapping.WindowsVkToLinuxKey(0x7B));  // VK_F12 -> KEY_F12
    }

    [Fact]
    public void AllFunctionKeys_AreMapped()
    {
        // F1(0x70) through F12(0x7B)
        for (int vk = 0x70; vk <= 0x7B; vk++)
        {
            Assert.NotEqual((ushort)0, KeyMapping.WindowsVkToLinuxKey(vk));
        }
    }

    // ============================================================================
    // Number row
    // ============================================================================

    [Fact]
    public void Key1_MapsToKey1()
    {
        Assert.Equal((ushort)2, KeyMapping.WindowsVkToLinuxKey(0x31));   // '1' -> KEY_1
    }

    [Fact]
    public void Key0_MapsToKey0()
    {
        Assert.Equal((ushort)11, KeyMapping.WindowsVkToLinuxKey(0x30));  // '0' -> KEY_0
    }

    [Fact]
    public void AllDigits_AreMapped()
    {
        for (int vk = 0x30; vk <= 0x39; vk++)
        {
            Assert.NotEqual((ushort)0, KeyMapping.WindowsVkToLinuxKey(vk));
        }
    }

    // ============================================================================
    // Letter keys
    // ============================================================================

    [Fact]
    public void A_MapsToKeyA()
    {
        Assert.Equal((ushort)30, KeyMapping.WindowsVkToLinuxKey(0x41));  // 'A' -> KEY_A
    }

    [Fact]
    public void Z_MapsToKeyZ()
    {
        Assert.Equal((ushort)44, KeyMapping.WindowsVkToLinuxKey(0x5A));  // 'Z' -> KEY_Z
    }

    [Fact]
    public void AllLetters_AreMapped()
    {
        for (int vk = 0x41; vk <= 0x5A; vk++)
        {
            Assert.NotEqual((ushort)0, KeyMapping.WindowsVkToLinuxKey(vk));
        }
    }

    [Fact]
    public void AllLetters_AreUnique()
    {
        var codes = new HashSet<ushort>();
        for (int vk = 0x41; vk <= 0x5A; vk++)
        {
            ushort code = KeyMapping.WindowsVkToLinuxKey(vk);
            Assert.True(codes.Add(code), $"Duplicate Linux key code {code} for VK 0x{vk:X}");
        }
    }

    // ============================================================================
    // Special keys
    // ============================================================================

    [Fact]
    public void Backspace_MapsToKeyBackspace()
    {
        Assert.Equal((ushort)14, KeyMapping.WindowsVkToLinuxKey(0x08));  // VK_BACK -> KEY_BACKSPACE
    }

    [Fact]
    public void Tab_MapsToKeyTab()
    {
        Assert.Equal((ushort)15, KeyMapping.WindowsVkToLinuxKey(0x09));  // VK_TAB -> KEY_TAB
    }

    [Fact]
    public void Enter_MapsToKeyEnter()
    {
        Assert.Equal((ushort)28, KeyMapping.WindowsVkToLinuxKey(0x0D));  // VK_RETURN -> KEY_ENTER
    }

    [Fact]
    public void Space_MapsToKeySpace()
    {
        Assert.Equal((ushort)57, KeyMapping.WindowsVkToLinuxKey(0x20));  // VK_SPACE -> KEY_SPACE
    }

    // ============================================================================
    // Modifier keys
    // ============================================================================

    [Fact]
    public void Shift_MapsToLeftShift()
    {
        Assert.Equal((ushort)42, KeyMapping.WindowsVkToLinuxKey(0x10));  // VK_SHIFT -> KEY_LEFTSHIFT
    }

    [Fact]
    public void LShift_MapsToLeftShift()
    {
        Assert.Equal((ushort)42, KeyMapping.WindowsVkToLinuxKey(0xA0));  // VK_LSHIFT -> KEY_LEFTSHIFT
    }

    [Fact]
    public void RShift_MapsToRightShift()
    {
        Assert.Equal((ushort)54, KeyMapping.WindowsVkToLinuxKey(0xA1));  // VK_RSHIFT -> KEY_RIGHTSHIFT
    }

    [Fact]
    public void Control_MapsToLeftCtrl()
    {
        Assert.Equal((ushort)29, KeyMapping.WindowsVkToLinuxKey(0x11));  // VK_CONTROL -> KEY_LEFTCTRL
    }

    [Fact]
    public void LControl_MapsToLeftCtrl()
    {
        Assert.Equal((ushort)29, KeyMapping.WindowsVkToLinuxKey(0xA2));  // VK_LCONTROL -> KEY_LEFTCTRL
    }

    [Fact]
    public void RControl_MapsToRightCtrl()
    {
        Assert.Equal((ushort)97, KeyMapping.WindowsVkToLinuxKey(0xA3));  // VK_RCONTROL -> KEY_RIGHTCTRL
    }

    [Fact]
    public void Alt_MapsToLeftAlt()
    {
        Assert.Equal((ushort)56, KeyMapping.WindowsVkToLinuxKey(0x12));  // VK_MENU -> KEY_LEFTALT
    }

    [Fact]
    public void LAlt_MapsToLeftAlt()
    {
        Assert.Equal((ushort)56, KeyMapping.WindowsVkToLinuxKey(0xA4));  // VK_LMENU -> KEY_LEFTALT
    }

    [Fact]
    public void RAlt_MapsToRightAlt()
    {
        Assert.Equal((ushort)100, KeyMapping.WindowsVkToLinuxKey(0xA5)); // VK_RMENU -> KEY_RIGHTALT
    }

    [Fact]
    public void CapsLock_MapsToKeyCapslock()
    {
        Assert.Equal((ushort)58, KeyMapping.WindowsVkToLinuxKey(0x14));  // VK_CAPITAL -> KEY_CAPSLOCK
    }

    // ============================================================================
    // Punctuation
    // ============================================================================

    [Fact]
    public void Minus_MapsToKeyMinus()
    {
        Assert.Equal((ushort)12, KeyMapping.WindowsVkToLinuxKey(0xBD));  // VK_OEM_MINUS -> KEY_MINUS
    }

    [Fact]
    public void Equal_MapsToKeyEqual()
    {
        Assert.Equal((ushort)13, KeyMapping.WindowsVkToLinuxKey(0xBB));  // VK_OEM_PLUS -> KEY_EQUAL
    }

    [Fact]
    public void LeftBrace_MapsToKeyLeftBrace()
    {
        Assert.Equal((ushort)26, KeyMapping.WindowsVkToLinuxKey(0xDB));  // VK_OEM_4 -> KEY_LEFTBRACE
    }

    [Fact]
    public void RightBrace_MapsToKeyRightBrace()
    {
        Assert.Equal((ushort)27, KeyMapping.WindowsVkToLinuxKey(0xDD));  // VK_OEM_6 -> KEY_RIGHTBRACE
    }

    [Fact]
    public void Backslash_MapsToKeyBackslash()
    {
        Assert.Equal((ushort)43, KeyMapping.WindowsVkToLinuxKey(0xDC));  // VK_OEM_5 -> KEY_BACKSLASH
    }

    [Fact]
    public void Semicolon_MapsToKeySemicolon()
    {
        Assert.Equal((ushort)39, KeyMapping.WindowsVkToLinuxKey(0xBA));  // VK_OEM_1 -> KEY_SEMICOLON
    }

    [Fact]
    public void Apostrophe_MapsToKeyApostrophe()
    {
        Assert.Equal((ushort)40, KeyMapping.WindowsVkToLinuxKey(0xDE));  // VK_OEM_7 -> KEY_APOSTROPHE
    }

    [Fact]
    public void Grave_MapsToKeyGrave()
    {
        Assert.Equal((ushort)41, KeyMapping.WindowsVkToLinuxKey(0xC0));  // VK_OEM_3 -> KEY_GRAVE
    }

    [Fact]
    public void Comma_MapsToKeyComma()
    {
        Assert.Equal((ushort)51, KeyMapping.WindowsVkToLinuxKey(0xBC));  // VK_OEM_COMMA -> KEY_COMMA
    }

    [Fact]
    public void Period_MapsToKeyDot()
    {
        Assert.Equal((ushort)52, KeyMapping.WindowsVkToLinuxKey(0xBE));  // VK_OEM_PERIOD -> KEY_DOT
    }

    [Fact]
    public void Slash_MapsToKeySlash()
    {
        Assert.Equal((ushort)53, KeyMapping.WindowsVkToLinuxKey(0xBF));  // VK_OEM_2 -> KEY_SLASH
    }

    // ============================================================================
    // Navigation
    // ============================================================================

    [Fact]
    public void Insert_MapsToKeyInsert()
    {
        Assert.Equal((ushort)110, KeyMapping.WindowsVkToLinuxKey(0x2D)); // VK_INSERT -> KEY_INSERT
    }

    [Fact]
    public void Delete_MapsToKeyDelete()
    {
        Assert.Equal((ushort)111, KeyMapping.WindowsVkToLinuxKey(0x2E)); // VK_DELETE -> KEY_DELETE
    }

    [Fact]
    public void Home_MapsToKeyHome()
    {
        Assert.Equal((ushort)102, KeyMapping.WindowsVkToLinuxKey(0x24)); // VK_HOME -> KEY_HOME
    }

    [Fact]
    public void End_MapsToKeyEnd()
    {
        Assert.Equal((ushort)107, KeyMapping.WindowsVkToLinuxKey(0x23)); // VK_END -> KEY_END
    }

    [Fact]
    public void PageUp_MapsToKeyPageUp()
    {
        Assert.Equal((ushort)104, KeyMapping.WindowsVkToLinuxKey(0x21)); // VK_PRIOR -> KEY_PAGEUP
    }

    [Fact]
    public void PageDown_MapsToKeyPageDown()
    {
        Assert.Equal((ushort)109, KeyMapping.WindowsVkToLinuxKey(0x22)); // VK_NEXT -> KEY_PAGEDOWN
    }

    [Fact]
    public void ArrowUp_MapsToKeyUp()
    {
        Assert.Equal((ushort)103, KeyMapping.WindowsVkToLinuxKey(0x26)); // VK_UP -> KEY_UP
    }

    [Fact]
    public void ArrowDown_MapsToKeyDown()
    {
        Assert.Equal((ushort)108, KeyMapping.WindowsVkToLinuxKey(0x28)); // VK_DOWN -> KEY_DOWN
    }

    [Fact]
    public void ArrowLeft_MapsToKeyLeft()
    {
        Assert.Equal((ushort)105, KeyMapping.WindowsVkToLinuxKey(0x25)); // VK_LEFT -> KEY_LEFT
    }

    [Fact]
    public void ArrowRight_MapsToKeyRight()
    {
        Assert.Equal((ushort)106, KeyMapping.WindowsVkToLinuxKey(0x27)); // VK_RIGHT -> KEY_RIGHT
    }

    // ============================================================================
    // Numpad
    // ============================================================================

    [Fact]
    public void Numpad0_MapsToKeyKP0()
    {
        Assert.Equal((ushort)82, KeyMapping.WindowsVkToLinuxKey(0x60));  // VK_NUMPAD0 -> KEY_KP0
    }

    [Fact]
    public void Numpad9_MapsToKeyKP9()
    {
        Assert.Equal((ushort)73, KeyMapping.WindowsVkToLinuxKey(0x69));  // VK_NUMPAD9 -> KEY_KP9
    }

    [Fact]
    public void NumpadMultiply_MapsToKeyKPAsterisk()
    {
        Assert.Equal((ushort)55, KeyMapping.WindowsVkToLinuxKey(0x6A));  // VK_MULTIPLY -> KEY_KPASTERISK
    }

    [Fact]
    public void NumpadPlus_MapsToKeyKPPlus()
    {
        Assert.Equal((ushort)78, KeyMapping.WindowsVkToLinuxKey(0x6B));  // VK_ADD -> KEY_KPPLUS
    }

    [Fact]
    public void NumpadMinus_MapsToKeyKPMinus()
    {
        Assert.Equal((ushort)74, KeyMapping.WindowsVkToLinuxKey(0x6D));  // VK_SUBTRACT -> KEY_KPMINUS
    }

    [Fact]
    public void NumpadDot_MapsToKeyKPDot()
    {
        Assert.Equal((ushort)83, KeyMapping.WindowsVkToLinuxKey(0x6E));  // VK_DECIMAL -> KEY_KPDOT
    }

    [Fact]
    public void NumpadSlash_MapsToKeyKPSlash()
    {
        Assert.Equal((ushort)98, KeyMapping.WindowsVkToLinuxKey(0x6F));  // VK_DIVIDE -> KEY_KPSLASH
    }

    // ============================================================================
    // Misc
    // ============================================================================

    [Fact]
    public void NumLock_MapsToKeyNumlock()
    {
        Assert.Equal((ushort)69, KeyMapping.WindowsVkToLinuxKey(0x90));  // VK_NUMLOCK -> KEY_NUMLOCK
    }

    [Fact]
    public void ScrollLock_MapsToKeyScrollLock()
    {
        Assert.Equal((ushort)70, KeyMapping.WindowsVkToLinuxKey(0x91));  // VK_SCROLL -> KEY_SCROLLLOCK
    }

    [Fact]
    public void Pause_MapsToKeyPause()
    {
        Assert.Equal((ushort)119, KeyMapping.WindowsVkToLinuxKey(0x13)); // VK_PAUSE -> KEY_PAUSE
    }

    [Fact]
    public void PrintScreen_MapsToKeySysRq()
    {
        Assert.Equal((ushort)99, KeyMapping.WindowsVkToLinuxKey(0x2C));  // VK_SNAPSHOT -> KEY_SYSRQ
    }

    // ============================================================================
    // Unmapped keys
    // ============================================================================

    [Fact]
    public void UnmappedKey_ReturnsZero()
    {
        Assert.Equal((ushort)0, KeyMapping.WindowsVkToLinuxKey(0x00));
        Assert.Equal((ushort)0, KeyMapping.WindowsVkToLinuxKey(0xFF));
        Assert.Equal((ushort)0, KeyMapping.WindowsVkToLinuxKey(0x5B));   // VK_LWIN
    }

    // ============================================================================
    // CharToLinuxKey tests
    // ============================================================================

    [Fact]
    public void CharToLinuxKey_LowercaseA_NoShift()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('a');
        Assert.Equal((ushort)30, code);
        Assert.False(shift);
    }

    [Fact]
    public void CharToLinuxKey_UppercaseA_WithShift()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('A');
        Assert.Equal((ushort)30, code);
        Assert.True(shift);
    }

    [Fact]
    public void CharToLinuxKey_LowercaseZ()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('z');
        Assert.Equal((ushort)44, code);
        Assert.False(shift);
    }

    [Fact]
    public void CharToLinuxKey_Digit0()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('0');
        Assert.Equal((ushort)11, code);
        Assert.False(shift);
    }

    [Fact]
    public void CharToLinuxKey_Space()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey(' ');
        Assert.Equal((ushort)57, code);
        Assert.False(shift);
    }

    [Fact]
    public void CharToLinuxKey_Enter()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('\n');
        Assert.Equal((ushort)28, code);
        Assert.False(shift);
    }

    [Fact]
    public void CharToLinuxKey_Tab()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('\t');
        Assert.Equal((ushort)15, code);
        Assert.False(shift);
    }

    [Fact]
    public void CharToLinuxKey_Slash_NoShift()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('/');
        Assert.Equal((ushort)53, code);
        Assert.False(shift);
    }

    [Fact]
    public void CharToLinuxKey_QuestionMark_WithShift()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('?');
        Assert.Equal((ushort)53, code);
        Assert.True(shift);
    }

    [Fact]
    public void CharToLinuxKey_ExclamationMark_Shift1()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('!');
        Assert.Equal((ushort)2, code);
        Assert.True(shift);
    }

    [Fact]
    public void CharToLinuxKey_Tilde_ShiftGrave()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('~');
        Assert.Equal((ushort)41, code);
        Assert.True(shift);
    }

    [Fact]
    public void CharToLinuxKey_Pipe_ShiftBackslash()
    {
        var (code, shift) = KeyMapping.CharToLinuxKey('|');
        Assert.Equal((ushort)43, code);
        Assert.True(shift);
    }

    [Fact]
    public void CharToLinuxKey_UnmappedChar_ReturnsZero()
    {
        var (code, _) = KeyMapping.CharToLinuxKey('\x01');
        Assert.Equal((ushort)0, code);
    }

    [Fact]
    public void CharToLinuxKey_AllLowercase_AreMapped()
    {
        for (char ch = 'a'; ch <= 'z'; ch++)
        {
            var (code, shift) = KeyMapping.CharToLinuxKey(ch);
            Assert.NotEqual((ushort)0, code);
            Assert.False(shift);
        }
    }

    [Fact]
    public void CharToLinuxKey_AllDigits_AreMapped()
    {
        for (char ch = '0'; ch <= '9'; ch++)
        {
            var (code, shift) = KeyMapping.CharToLinuxKey(ch);
            Assert.NotEqual((ushort)0, code);
            Assert.False(shift);
        }
    }
}
