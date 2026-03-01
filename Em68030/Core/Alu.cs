namespace Em68030.Core;

public static class Alu
{
    public static (byte result, byte ccr) AddByte(byte a, byte b, byte ccr, bool withExtend = false)
    {
        int x = withExtend && (ccr & 0x10) != 0 ? 1 : 0;
        int result = a + b + x;
        byte r = (byte)result;
        byte newCcr = 0;

        if (result > 0xFF) newCcr |= 0x11; // C and X
        if (r == 0) newCcr |= 0x04; // Z
        if ((r & 0x80) != 0) newCcr |= 0x08; // N
        if (((a ^ r) & (b ^ r) & 0x80) != 0) newCcr |= 0x02; // V

        return (r, newCcr);
    }

    public static (ushort result, byte ccr) AddWord(ushort a, ushort b, byte ccr, bool withExtend = false)
    {
        int x = withExtend && (ccr & 0x10) != 0 ? 1 : 0;
        int result = a + b + x;
        ushort r = (ushort)result;
        byte newCcr = 0;

        if (result > 0xFFFF) newCcr |= 0x11;
        if (r == 0) newCcr |= 0x04;
        if ((r & 0x8000) != 0) newCcr |= 0x08;
        if (((a ^ r) & (b ^ r) & 0x8000) != 0) newCcr |= 0x02;

        return (r, newCcr);
    }

    public static (uint result, byte ccr) AddLong(uint a, uint b, byte ccr, bool withExtend = false)
    {
        int x = withExtend && (ccr & 0x10) != 0 ? 1 : 0;
        long result = (long)a + b + x;
        uint r = (uint)result;
        byte newCcr = 0;

        if (result > 0xFFFFFFFF) newCcr |= 0x11;
        if (r == 0) newCcr |= 0x04;
        if ((r & 0x80000000) != 0) newCcr |= 0x08;
        if (((a ^ r) & (b ^ r) & 0x80000000) != 0) newCcr |= 0x02;

        return (r, newCcr);
    }

    public static (byte result, byte ccr) SubByte(byte a, byte b, byte ccr, bool withExtend = false)
    {
        int x = withExtend && (ccr & 0x10) != 0 ? 1 : 0;
        int result = a - b - x;
        byte r = (byte)result;
        byte newCcr = 0;

        if (result < 0) newCcr |= 0x11;
        if (r == 0) newCcr |= 0x04;
        if ((r & 0x80) != 0) newCcr |= 0x08;
        if (((a ^ b) & (a ^ r) & 0x80) != 0) newCcr |= 0x02;

        return (r, newCcr);
    }

    public static (ushort result, byte ccr) SubWord(ushort a, ushort b, byte ccr, bool withExtend = false)
    {
        int x = withExtend && (ccr & 0x10) != 0 ? 1 : 0;
        int result = a - b - x;
        ushort r = (ushort)result;
        byte newCcr = 0;

        if (result < 0) newCcr |= 0x11;
        if (r == 0) newCcr |= 0x04;
        if ((r & 0x8000) != 0) newCcr |= 0x08;
        if (((a ^ b) & (a ^ r) & 0x8000) != 0) newCcr |= 0x02;

        return (r, newCcr);
    }

    public static (uint result, byte ccr) SubLong(uint a, uint b, byte ccr, bool withExtend = false)
    {
        int x = withExtend && (ccr & 0x10) != 0 ? 1 : 0;
        long result = (long)a - b - x;
        uint r = (uint)result;
        byte newCcr = 0;

        if (result < 0) newCcr |= 0x11;
        if (r == 0) newCcr |= 0x04;
        if ((r & 0x80000000) != 0) newCcr |= 0x08;
        if (((a ^ b) & (a ^ r) & 0x80000000) != 0) newCcr |= 0x02;

        return (r, newCcr);
    }

    public static byte SetNZFlags(byte value)
    {
        byte ccr = 0;
        if (value == 0) ccr |= 0x04;
        if ((value & 0x80) != 0) ccr |= 0x08;
        return ccr;
    }

    public static byte SetNZFlags(ushort value)
    {
        byte ccr = 0;
        if (value == 0) ccr |= 0x04;
        if ((value & 0x8000) != 0) ccr |= 0x08;
        return ccr;
    }

    public static byte SetNZFlags(uint value)
    {
        byte ccr = 0;
        if (value == 0) ccr |= 0x04;
        if ((value & 0x80000000) != 0) ccr |= 0x08;
        return ccr;
    }

    public static (uint result, byte ccr) MulSigned(int a, int b)
    {
        long result = (long)a * b;
        uint r = (uint)(int)result;
        byte ccr = SetNZFlags(r);
        if (result > int.MaxValue || result < int.MinValue) ccr |= 0x02; // V
        return (r, ccr);
    }

    public static (uint result, byte ccr) MulUnsigned(uint a, uint b)
    {
        ulong result = (ulong)a * b;
        uint r = (uint)result;
        byte ccr = SetNZFlags(r);
        if (result > 0xFFFFFFFF) ccr |= 0x02;
        return (r, ccr);
    }

    public static (uint quotient, uint remainder, byte ccr, bool overflow) DivSigned(int dividend, int divisor)
    {
        if (divisor == 0)
            return (0, 0, 0, true);

        long q = (long)dividend / divisor;
        long rem = (long)dividend % divisor;

        if (q > short.MaxValue || q < short.MinValue)
            return (0, 0, 0x02, true);

        uint quotient = (uint)(ushort)(short)q;
        uint remainder = (uint)(ushort)(short)rem;
        byte ccr = SetNZFlags((ushort)q);
        return (quotient, remainder, ccr, false);
    }

    public static (uint quotient, uint remainder, byte ccr, bool overflow) DivUnsigned(uint dividend, uint divisor)
    {
        if (divisor == 0)
            return (0, 0, 0, true);

        uint q = dividend / (ushort)divisor;
        uint rem = dividend % (ushort)divisor;

        if (q > 0xFFFF)
            return (0, 0, 0x02, true);

        byte ccr = SetNZFlags((ushort)q);
        return (q & 0xFFFF, rem & 0xFFFF, ccr, false);
    }

    public static (byte result, byte ccr) ShiftLeft(byte value, int count, byte ccr)
    {
        byte r = value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)((r >> 7) & 1);
            r <<= 1;
        }
        byte newCcr = SetNZFlags(r);
        if (count > 0 && c != 0) newCcr |= 0x11;
        if (((value ^ r) & 0x80) != 0 && count > 0) newCcr |= 0x02;
        return (r, newCcr);
    }

    public static (ushort result, byte ccr) ShiftLeft(ushort value, int count, byte ccr)
    {
        ushort r = value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)((r >> 15) & 1);
            r <<= 1;
        }
        byte newCcr = SetNZFlags(r);
        if (count > 0 && c != 0) newCcr |= 0x11;
        if (((value ^ r) & 0x8000) != 0 && count > 0) newCcr |= 0x02;
        return (r, newCcr);
    }

    public static (uint result, byte ccr) ShiftLeft(uint value, int count, byte ccr)
    {
        uint r = value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)((r >> 31) & 1);
            r <<= 1;
        }
        byte newCcr = SetNZFlags(r);
        if (count > 0 && c != 0) newCcr |= 0x11;
        if (((value ^ r) & 0x80000000) != 0 && count > 0) newCcr |= 0x02;
        return (r, newCcr);
    }

    public static (byte result, byte ccr) ArithShiftRight(byte value, int count)
    {
        int r = (sbyte)value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)(r & 1);
            r >>= 1;
        }
        byte result = (byte)r;
        byte newCcr = SetNZFlags(result);
        if (count > 0 && c != 0) newCcr |= 0x11;
        return (result, newCcr);
    }

    public static (ushort result, byte ccr) ArithShiftRight(ushort value, int count)
    {
        int r = (short)value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)(r & 1);
            r >>= 1;
        }
        ushort result = (ushort)(short)r;
        byte newCcr = SetNZFlags(result);
        if (count > 0 && c != 0) newCcr |= 0x11;
        return (result, newCcr);
    }

    public static (uint result, byte ccr) ArithShiftRight(uint value, int count)
    {
        int r = (int)value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)(r & 1);
            r >>= 1;
        }
        uint result = (uint)r;
        byte newCcr = SetNZFlags(result);
        if (count > 0 && c != 0) newCcr |= 0x11;
        return (result, newCcr);
    }

    public static (byte result, byte ccr) LogicalShiftRight(byte value, int count)
    {
        byte r = value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)(r & 1);
            r >>= 1;
        }
        byte newCcr = SetNZFlags(r);
        if (count > 0 && c != 0) newCcr |= 0x11;
        return (r, newCcr);
    }

    public static (ushort result, byte ccr) LogicalShiftRight(ushort value, int count)
    {
        ushort r = value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)(r & 1);
            r >>= 1;
        }
        byte newCcr = SetNZFlags(r);
        if (count > 0 && c != 0) newCcr |= 0x11;
        return (r, newCcr);
    }

    public static (uint result, byte ccr) LogicalShiftRight(uint value, int count)
    {
        uint r = value;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)(r & 1);
            r >>= 1;
        }
        byte newCcr = SetNZFlags(r);
        if (count > 0 && c != 0) newCcr |= 0x11;
        return (r, newCcr);
    }

    public static (uint result, byte ccr) RotateLeft(uint value, int count, int size)
    {
        uint mask = size switch { 1 => 0xFF, 2 => 0xFFFF, _ => 0xFFFFFFFF };
        int bits = size * 8;
        uint r = value & mask;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)((r >> (bits - 1)) & 1);
            r = ((r << 1) | c) & mask;
        }
        byte newCcr = size switch
        {
            1 => SetNZFlags((byte)r),
            2 => SetNZFlags((ushort)r),
            _ => SetNZFlags(r)
        };
        if (count > 0 && c != 0) newCcr |= 0x01;
        return (r, newCcr);
    }

    public static (uint result, byte ccr) RotateRight(uint value, int count, int size)
    {
        uint mask = size switch { 1 => 0xFF, 2 => 0xFFFF, _ => 0xFFFFFFFF };
        int bits = size * 8;
        uint r = value & mask;
        byte c = 0;
        for (int i = 0; i < count; i++)
        {
            c = (byte)(r & 1);
            r = (r >> 1) | ((uint)c << (bits - 1));
            r &= mask;
        }
        byte newCcr = size switch
        {
            1 => SetNZFlags((byte)r),
            2 => SetNZFlags((ushort)r),
            _ => SetNZFlags(r)
        };
        if (count > 0 && c != 0) newCcr |= 0x01;
        return (r, newCcr);
    }

    // ROXL - Rotate Left through eXtend
    public static (uint result, byte ccr) RotateLeftX(uint value, int count, int size, byte ccr)
    {
        uint mask = size switch { 1 => 0xFF, 2 => 0xFFFF, _ => 0xFFFFFFFF };
        int bits = size * 8;
        uint r = value & mask;
        int x = (ccr & 0x10) != 0 ? 1 : 0;
        byte c = (byte)x;
        for (int i = 0; i < count; i++)
        {
            c = (byte)((r >> (bits - 1)) & 1);
            r = ((r << 1) | (uint)x) & mask;
            x = c;
        }
        byte newCcr = size switch
        {
            1 => SetNZFlags((byte)r),
            2 => SetNZFlags((ushort)r),
            _ => SetNZFlags(r)
        };
        if (count > 0)
        {
            if (c != 0) newCcr |= 0x11;
        }
        else
        {
            newCcr |= (byte)(ccr & 0x10);
            if ((ccr & 0x10) != 0) newCcr |= 0x01;
        }
        return (r, newCcr);
    }

    // ROXR - Rotate Right through eXtend
    public static (uint result, byte ccr) RotateRightX(uint value, int count, int size, byte ccr)
    {
        uint mask = size switch { 1 => 0xFF, 2 => 0xFFFF, _ => 0xFFFFFFFF };
        int bits = size * 8;
        uint r = value & mask;
        int x = (ccr & 0x10) != 0 ? 1 : 0;
        byte c = (byte)x;
        for (int i = 0; i < count; i++)
        {
            c = (byte)(r & 1);
            r = (r >> 1) | ((uint)x << (bits - 1));
            r &= mask;
            x = c;
        }
        byte newCcr = size switch
        {
            1 => SetNZFlags((byte)r),
            2 => SetNZFlags((ushort)r),
            _ => SetNZFlags(r)
        };
        if (count > 0)
        {
            if (c != 0) newCcr |= 0x11;
        }
        else
        {
            newCcr |= (byte)(ccr & 0x10);
            if ((ccr & 0x10) != 0) newCcr |= 0x01;
        }
        return (r, newCcr);
    }

    // ABCD - Add BCD with extend
    public static (byte result, byte ccr) AddBcd(byte src, byte dst, byte ccr)
    {
        int x = (ccr & 0x10) != 0 ? 1 : 0;
        int lo = (dst & 0x0F) + (src & 0x0F) + x;
        int carry = 0;
        if (lo > 9) { lo -= 10; carry = 1; }
        int hi = ((dst >> 4) & 0x0F) + ((src >> 4) & 0x0F) + carry;
        carry = 0;
        if (hi > 9) { hi -= 10; carry = 1; }
        byte result = (byte)(((hi & 0x0F) << 4) | (lo & 0x0F));
        byte newCcr = 0;
        if (carry != 0) newCcr |= 0x11; // C and X
        if (result == 0) newCcr |= (byte)(ccr & 0x04); // Z unchanged if zero
        if ((result & 0x80) != 0) newCcr |= 0x08; // N (undefined per spec)
        return (result, newCcr);
    }

    // SBCD - Subtract BCD with extend
    public static (byte result, byte ccr) SubBcd(byte src, byte dst, byte ccr)
    {
        int x = (ccr & 0x10) != 0 ? 1 : 0;
        int lo = (dst & 0x0F) - (src & 0x0F) - x;
        int borrow = 0;
        if (lo < 0) { lo += 10; borrow = 1; }
        int hi = ((dst >> 4) & 0x0F) - ((src >> 4) & 0x0F) - borrow;
        borrow = 0;
        if (hi < 0) { hi += 10; borrow = 1; }
        byte result = (byte)(((hi & 0x0F) << 4) | (lo & 0x0F));
        byte newCcr = 0;
        if (borrow != 0) newCcr |= 0x11; // C and X
        if (result == 0) newCcr |= (byte)(ccr & 0x04); // Z unchanged if zero
        if ((result & 0x80) != 0) newCcr |= 0x08; // N (undefined per spec)
        return (result, newCcr);
    }

    // MULS.L / MULU.L - 32-bit multiply (68020+)
    public static (uint resultLo, uint resultHi, byte ccr) MulSignedLong(int a, int b)
    {
        long result = (long)a * b;
        uint lo = (uint)(result & 0xFFFFFFFF);
        uint hi = (uint)((ulong)result >> 32);
        byte flags = SetNZFlags(lo);
        return (lo, hi, flags);
    }

    public static (uint resultLo, uint resultHi, byte ccr) MulUnsignedLong(uint a, uint b)
    {
        ulong result = (ulong)a * b;
        uint lo = (uint)(result & 0xFFFFFFFF);
        uint hi = (uint)(result >> 32);
        byte flags = SetNZFlags(lo);
        return (lo, hi, flags);
    }

    // DIVS.L / DIVU.L - 32-bit division (68020+)
    public static (uint quotient, uint remainder, byte ccr, bool overflow) DivSignedLong(long dividend, int divisor)
    {
        if (divisor == 0) return (0, 0, 0, true);
        long q = dividend / divisor;
        long rem = dividend % divisor;
        if (q > int.MaxValue || q < int.MinValue)
            return (0, 0, 0x02, true);
        uint quotient = (uint)(int)q;
        uint remainder = (uint)(int)rem;
        byte flags = SetNZFlags(quotient);
        return (quotient, remainder, flags, false);
    }

    public static (uint quotient, uint remainder, byte ccr, bool overflow) DivUnsignedLong(ulong dividend, uint divisor)
    {
        if (divisor == 0) return (0, 0, 0, true);
        ulong q = dividend / divisor;
        ulong rem = dividend % divisor;
        if (q > 0xFFFFFFFF)
            return (0, 0, 0x02, true);
        uint quotient = (uint)q;
        uint remainder = (uint)rem;
        byte flags = SetNZFlags(quotient);
        return (quotient, remainder, flags, false);
    }
}
