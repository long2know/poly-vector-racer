namespace StuntRacer.Core.Compatibility.Physics;

public enum AmigaDivisionStatus
{
    Success,
    QuotientOverflow,
    DivideByZero
}

/// <summary>
/// Word-division result, not a condition-code-register emulator. On overflow or a
/// divide-by-zero trap the supplied destination is retained and both values are null.
/// </summary>
public readonly record struct AmigaWordDivisionResult(
    AmigaDivisionStatus Status, uint Destination, int? Quotient, int? Remainder);

public readonly record struct AmigaAddition16(ushort Value, bool Carry, bool SignedOverflow);
public readonly record struct AmigaAddition32(uint Value, bool Carry, bool SignedOverflow);

/// <summary>
/// Independently authored fixed-width arithmetic, not vehicle dynamics or proof of
/// original instruction ordering. No operation implicitly carries flags into another.
/// </summary>
/// <remarks>
/// CPU facts checked against Motorola's M68000 Family Programmer's Reference Manual
/// (1992), ASL/ASR, LSL/LSR, MULS.W/MULU.W and DIVS.W/DIVU.W. Generic 32-bit
/// multiplication/division below is not a claim that the base 68000 has long forms.
/// </remarks>
public static class AmigaIntegerMath
{
    public static short WrapInt16(long value) => unchecked((short)value);
    public static ushort WrapUInt16(long value) => unchecked((ushort)value);
    public static int WrapInt32(long value) => unchecked((int)value);
    public static uint WrapUInt32(long value) => unchecked((uint)value);

    public static short AddInt16(short left, short right) => unchecked((short)(left + right));
    public static ushort AddUInt16(ushort left, ushort right) => unchecked((ushort)(left + right));
    public static int AddInt32(int left, int right) => unchecked(left + right);
    public static uint AddUInt32(uint left, uint right) => unchecked(left + right);

    public static short SubtractInt16(short left, short right) => unchecked((short)(left - right));
    public static ushort SubtractUInt16(ushort left, ushort right) => unchecked((ushort)(left - right));
    public static int SubtractInt32(int left, int right) => unchecked(left - right);
    public static uint SubtractUInt32(uint left, uint right) => unchecked(left - right);

    /// <summary>Full signed 16-by-16 product; no intermediate narrowing.</summary>
    public static int MultiplyInt16(short left, short right) => left * right;

    /// <summary>Full unsigned 16-by-16 product; widening precedes multiplication.</summary>
    public static uint MultiplyUInt16(ushort left, ushort right) => (uint)left * right;

    public static int MultiplyInt32Wrapping(int left, int right) =>
        unchecked((int)((long)left * right));

    public static uint MultiplyUInt32Wrapping(uint left, uint right) =>
        unchecked((uint)((ulong)left * right));

    /// <summary>
    /// Generic signed division truncating toward zero. Zero throws DivideByZeroException;
    /// int.MinValue / -1 throws OverflowException, never wrapping or saturating.
    /// </summary>
    public static int DivideInt32Truncating(int dividend, int divisor)
    {
        if (divisor == 0)
            throw new DivideByZeroException();
        var quotient = (long)dividend / divisor;
        if (quotient is < int.MinValue or > int.MaxValue)
            throw new OverflowException("The quotient does not fit a signed 32-bit integer.");
        return (int)quotient;
    }

    public static uint DivideUInt32(uint dividend, uint divisor)
    {
        if (divisor == 0)
            throw new DivideByZeroException();
        return dividend / divisor;
    }

    /// <summary>
    /// DIVS.W value semantics: signed long dividend, signed word divisor, signed word
    /// quotient and remainder. The remainder has the dividend's sign, unless zero.
    /// </summary>
    public static AmigaWordDivisionResult DivideSignedWord(int dividend, short divisor)
    {
        var original = unchecked((uint)dividend);
        if (divisor == 0)
            return new(AmigaDivisionStatus.DivideByZero, original, null, null);

        var quotient = (long)dividend / divisor;
        if (quotient is < short.MinValue or > short.MaxValue)
            return new(AmigaDivisionStatus.QuotientOverflow, original, null, null);

        var remainder = (int)((long)dividend % divisor);
        var destination = ((uint)unchecked((ushort)remainder) << 16) |
                          unchecked((ushort)quotient);
        return new(AmigaDivisionStatus.Success, destination, (int)quotient, remainder);
    }

    /// <summary>DIVU.W value semantics; errors do not write a truncated quotient.</summary>
    public static AmigaWordDivisionResult DivideUnsignedWord(uint dividend, ushort divisor)
    {
        if (divisor == 0)
            return new(AmigaDivisionStatus.DivideByZero, dividend, null, null);

        var quotient = dividend / divisor;
        if (quotient > ushort.MaxValue)
            return new(AmigaDivisionStatus.QuotientOverflow, dividend, null, null);

        var remainder = dividend % divisor;
        return new(AmigaDivisionStatus.Success, (remainder << 16) | quotient,
            (int)quotient, (int)remainder);
    }

    /// <summary>Mathematical carry and signed overflow; not ADDX's sticky Z flag.</summary>
    public static AmigaAddition16 AddWithCarry16(ushort left, ushort right, bool carryIn)
    {
        var sum = (uint)left + right + (carryIn ? 1u : 0u);
        var signedSum = (int)unchecked((short)left) + unchecked((short)right) +
                        (carryIn ? 1 : 0);
        return new(unchecked((ushort)sum), sum > ushort.MaxValue,
            signedSum is < short.MinValue or > short.MaxValue);
    }

    public static AmigaAddition32 AddWithCarry32(uint left, uint right, bool carryIn)
    {
        var sum = (ulong)left + right + (carryIn ? 1u : 0u);
        var signedSum = (long)unchecked((int)left) + unchecked((int)right) +
                        (carryIn ? 1 : 0);
        return new(unchecked((uint)sum), sum > uint.MaxValue,
            signedSum is < int.MinValue or > int.MaxValue);
    }

    /// <summary>
    /// Literal nonnegative shift count, without C#'s modulo-32 masking.
    /// Arithmetic right shifts round negative values downward, unlike division.
    /// </summary>
    public static short ArithmeticShiftRight16(short value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count >= 16 ? (short)(value < 0 ? -1 : 0) : (short)(value >> count);
    }

    public static int ArithmeticShiftRight32(int value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count >= 32 ? (value < 0 ? -1 : 0) : value >> count;
    }

    public static ushort LogicalShiftRight16(ushort value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count >= 16 ? (ushort)0 : (ushort)(value >> count);
    }

    public static uint LogicalShiftRight32(uint value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count >= 32 ? 0u : value >> count;
    }

    public static ushort ShiftLeft16(ushort value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count >= 16 ? (ushort)0 : unchecked((ushort)(value << count));
    }

    public static uint ShiftLeft32(uint value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count >= 32 ? 0u : unchecked(value << count);
    }

    /// <summary>
    /// Explicit base-68000 data-register shift-count decoding, modulo 64.
    /// Apply only when that addressing form is actually intended; literal helpers do not.
    /// </summary>
    public static int M68000RegisterShiftCount(uint countRegister) => (int)(countRegister & 63);
}
