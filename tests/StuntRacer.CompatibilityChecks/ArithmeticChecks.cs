using StuntRacer.Core.Compatibility.Physics;

namespace StuntRacer.CompatibilityChecks;

internal static class ArithmeticChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("integer / signed and unsigned 16/32-bit wrapping", Wrapping);
        yield return new("integer / carry differs from signed overflow", Carry);
        yield return new("integer / widening products and truncating division", MultiplyAndDivide);
        yield return new("integer / word division preserves destination on traps/overflow", WordDivision);
        yield return new("integer / literal shifts do not inherit C# count masking", Shifts);
        yield return new("angle / wrapping, shortest arcs and explicit display sign", Angles);
    }

    private static void Wrapping()
    {
        Check.Equal((short)-1, AmigaIntegerMath.WrapInt16(65535), "signed word all bits");
        Check.Equal((short)0, AmigaIntegerMath.WrapInt16(65536), "signed word wrap");
        Check.Equal((ushort)65535, AmigaIntegerMath.WrapUInt16(-1), "unsigned word underflow");
        Check.Equal(-1, AmigaIntegerMath.WrapInt32(4294967295), "signed long all bits");
        Check.Equal(0, AmigaIntegerMath.WrapInt32(4294967296), "signed long wrap");
        Check.Equal(uint.MaxValue, AmigaIntegerMath.WrapUInt32(-1), "unsigned long underflow");
        Check.Equal(short.MinValue, AmigaIntegerMath.AddInt16(short.MaxValue, 1), "signed word add");
        Check.Equal((ushort)0, AmigaIntegerMath.AddUInt16(ushort.MaxValue, 1), "unsigned word add");
        Check.Equal(int.MinValue, AmigaIntegerMath.AddInt32(int.MaxValue, 1), "signed long add");
        Check.Equal(0u, AmigaIntegerMath.AddUInt32(uint.MaxValue, 1), "unsigned long add");
        Check.Equal(short.MaxValue, AmigaIntegerMath.SubtractInt16(short.MinValue, 1), "signed word subtract");
        Check.Equal(ushort.MaxValue, AmigaIntegerMath.SubtractUInt16(0, 1), "unsigned word subtract");
        Check.Equal(int.MaxValue, AmigaIntegerMath.SubtractInt32(int.MinValue, 1), "signed long subtract");
        Check.Equal(uint.MaxValue, AmigaIntegerMath.SubtractUInt32(0, 1), "unsigned long subtract");
    }

    private static void Carry()
    {
        var signed16 = AmigaIntegerMath.AddWithCarry16(0x7fff, 0, true);
        Check.Equal((ushort)0x8000, signed16.Value, "word incoming carry result");
        Check.Require(!signed16.Carry && signed16.SignedOverflow, "word signed overflow confused with unsigned carry");
        var unsigned16 = AmigaIntegerMath.AddWithCarry16(0xffff, 0, true);
        Check.Equal((ushort)0, unsigned16.Value, "word outgoing carry result");
        Check.Require(unsigned16.Carry && !unsigned16.SignedOverflow, "word unsigned carry confused with signed overflow");
        var both16 = AmigaIntegerMath.AddWithCarry16(0x8000, 0x8000, false);
        Check.Require(both16.Value == 0 && both16.Carry && both16.SignedOverflow, "word simultaneous carry/overflow");
        var signed32 = AmigaIntegerMath.AddWithCarry32(0x7fffffff, 0, true);
        Check.Require(signed32.Value == 0x80000000 && !signed32.Carry && signed32.SignedOverflow,
            "long signed overflow");
        var unsigned32 = AmigaIntegerMath.AddWithCarry32(uint.MaxValue, 0, true);
        Check.Require(unsigned32.Value == 0 && unsigned32.Carry && !unsigned32.SignedOverflow,
            "long unsigned carry");
        var neither = AmigaIntegerMath.AddWithCarry32(12, 30, false);
        Check.Require(neither.Value == 42 && !neither.Carry && !neither.SignedOverflow, "unexpected arithmetic flags");
    }

    private static void MultiplyAndDivide()
    {
        Check.Equal(1073741824, AmigaIntegerMath.MultiplyInt16(short.MinValue, short.MinValue), "full signed product");
        Check.Equal(-1073709056, AmigaIntegerMath.MultiplyInt16(short.MinValue, short.MaxValue), "negative signed product");
        Check.Equal(4294836225u, AmigaIntegerMath.MultiplyUInt16(ushort.MaxValue, ushort.MaxValue), "full unsigned product");
        Check.Equal(-2, AmigaIntegerMath.MultiplyInt32Wrapping(int.MaxValue, 2), "signed product wrap");
        Check.Equal(0xfffffffeu, AmigaIntegerMath.MultiplyUInt32Wrapping(uint.MaxValue, 2), "unsigned product wrap");
        Check.Equal(-1, AmigaIntegerMath.DivideInt32Truncating(-3, 2), "negative division truncates toward zero");
        Check.Equal(-1, AmigaIntegerMath.DivideInt32Truncating(3, -2), "negative divisor");
        Check.Equal(1, AmigaIntegerMath.DivideInt32Truncating(-3, -2), "two negative operands");
        Check.Equal(2147483647u, AmigaIntegerMath.DivideUInt32(uint.MaxValue, 2), "unsigned division");
        Check.Throws<OverflowException>(() => AmigaIntegerMath.DivideInt32Truncating(int.MinValue, -1), "signed divide overflow");
        Check.Throws<DivideByZeroException>(() => AmigaIntegerMath.DivideInt32Truncating(7, 0), "signed zero divisor");
        Check.Throws<DivideByZeroException>(() => AmigaIntegerMath.DivideUInt32(7, 0), "unsigned zero divisor");
    }

    private static void WordDivision()
    {
        var signed = AmigaIntegerMath.DivideSignedWord(-7, 3);
        Check.Equal(AmigaDivisionStatus.Success, signed.Status, "DIVS status");
        Check.Equal<int?>(-2, signed.Quotient, "DIVS quotient");
        Check.Equal<int?>(-1, signed.Remainder, "DIVS remainder retains dividend sign");
        Check.Equal(0xfffffffeu, signed.Destination, "DIVS packed negative remainder/quotient");
        var negativeDivisor = AmigaIntegerMath.DivideSignedWord(-7, -3);
        Check.Equal(0xffff0002u, negativeDivisor.Destination, "DIVS negative divisor packing");
        var boundary = AmigaIntegerMath.DivideSignedWord(short.MinValue, 1);
        Check.Equal(0x00008000u, boundary.Destination, "DIVS minimum quotient is valid");
        var unsigned = AmigaIntegerMath.DivideUnsignedWord(123456, 10);
        Check.Equal(AmigaDivisionStatus.Success, unsigned.Status, "DIVU status");
        Check.Equal<int?>(12345, unsigned.Quotient, "DIVU quotient");
        Check.Equal<int?>(6, unsigned.Remainder, "DIVU remainder");
        Check.Equal(0x00063039u, unsigned.Destination, "DIVU packed result");
        Check.Equal(65535u, AmigaIntegerMath.DivideUnsignedWord(65535, 1).Destination, "DIVU maximum valid quotient");

        Error(AmigaIntegerMath.DivideSignedWord(-32769, 1), AmigaDivisionStatus.QuotientOverflow,
            unchecked((uint)-32769), "DIVS word overflow");
        Error(AmigaIntegerMath.DivideSignedWord(int.MinValue, -1), AmigaDivisionStatus.QuotientOverflow,
            0x80000000, "DIVS wide overflow");
        Error(AmigaIntegerMath.DivideUnsignedWord(65536, 1), AmigaDivisionStatus.QuotientOverflow, 65536, "DIVU overflow");
        Error(AmigaIntegerMath.DivideSignedWord(-7, 0), AmigaDivisionStatus.DivideByZero, unchecked((uint)-7), "DIVS trap");
        Error(AmigaIntegerMath.DivideUnsignedWord(7, 0), AmigaDivisionStatus.DivideByZero, 7, "DIVU trap");

        static void Error(AmigaWordDivisionResult result, AmigaDivisionStatus status, uint destination, string context)
        {
            Check.Equal(status, result.Status, context);
            Check.Equal(destination, result.Destination, $"{context} preserves destination");
            Check.Require(result.Quotient is null && result.Remainder is null, $"{context} emitted truncated values");
        }
    }

    private static void Shifts()
    {
        Check.Equal((short)-2, AmigaIntegerMath.ArithmeticShiftRight16(-3, 1), "negative word ASR rounds down");
        Check.Equal(-2, AmigaIntegerMath.ArithmeticShiftRight32(-3, 1), "negative long ASR rounds down");
        Check.Equal((ushort)0x7fff, AmigaIntegerMath.LogicalShiftRight16(0xffff, 1), "word logical shift zero fills");
        Check.Equal(0x7fffffffu, AmigaIntegerMath.LogicalShiftRight32(uint.MaxValue, 1), "long logical shift zero fills");
        Check.Equal((ushort)2, AmigaIntegerMath.ShiftLeft16(0x8001, 1), "word left shift narrows");
        Check.Equal(2u, AmigaIntegerMath.ShiftLeft32(0x80000001, 1), "long left shift narrows");
        foreach (var count in new[] { 32, 63, 64, 1000 })
        {
            Check.Equal((short)-1, AmigaIntegerMath.ArithmeticShiftRight16(-3, count), $"word ASR {count}");
            Check.Equal(-1, AmigaIntegerMath.ArithmeticShiftRight32(-3, count), $"long ASR {count}");
            Check.Equal(0, AmigaIntegerMath.ArithmeticShiftRight32(3, count), $"positive ASR {count}");
            Check.Equal((ushort)0, AmigaIntegerMath.LogicalShiftRight16(0xffff, count), $"word LSR {count}");
            Check.Equal(0u, AmigaIntegerMath.LogicalShiftRight32(uint.MaxValue, count), $"long LSR {count}");
            Check.Equal((ushort)0, AmigaIntegerMath.ShiftLeft16(1, count), $"word LSL {count}");
            Check.Equal(0u, AmigaIntegerMath.ShiftLeft32(1, count), $"long LSL {count}");
        }
        Check.Equal((short)-1, AmigaIntegerMath.ArithmeticShiftRight16(-3, 16), "word sign at exact width");
        Check.Equal((ushort)0, AmigaIntegerMath.ShiftLeft16(1, 16), "word left at exact width");
        Check.Equal(-3, AmigaIntegerMath.ArithmeticShiftRight32(-3, 0), "zero shift preserves value");
        Check.Equal(0, AmigaIntegerMath.M68000RegisterShiftCount(64), "register count modulo 64");
        Check.Equal(63, AmigaIntegerMath.M68000RegisterShiftCount(uint.MaxValue), "register count is not modulo 32");
        Action[] invalid =
        [
            () => AmigaIntegerMath.ArithmeticShiftRight16(-1, -1),
            () => AmigaIntegerMath.ArithmeticShiftRight32(-1, -1),
            () => AmigaIntegerMath.LogicalShiftRight16(1, -1),
            () => AmigaIntegerMath.LogicalShiftRight32(1, -1),
            () => AmigaIntegerMath.ShiftLeft16(1, -1),
            () => AmigaIntegerMath.ShiftLeft32(1, -1)
        ];
        foreach (var action in invalid)
            Check.Throws<ArgumentOutOfRangeException>(action, "negative literal shift");
    }

    private static void Angles()
    {
        Check.Equal((ushort)65535, AmigaAngle.Wrap(-1), "negative angle wraps");
        Check.Equal((ushort)1, AmigaAngle.Wrap(65537), "angle over one turn");
        Check.Equal((short)2, AmigaAngle.ShortestDelta(65535, 1), "forward wrap delta");
        Check.Equal((short)-2, AmigaAngle.ShortestDelta(1, 65535), "reverse wrap delta");
        Check.Equal(short.MinValue, AmigaAngle.ShortestDelta(0, 32768), "positive half-turn tie");
        Check.Equal(short.MinValue, AmigaAngle.ShortestDelta(32768, 0), "negative half-turn tie");
        Check.Near(Math.PI / 2, AmigaAngle.ToDisplayRadians(16384, 1), "positive source angle projects explicitly");
        Check.Near(-Math.PI / 2, AmigaAngle.ToDisplayRadians(16384, -1), "negative display sign");
        Check.Near(Math.Tau, AmigaAngle.InterpolateDisplayRadians(65535, 1, 0.5, 1), "short arc across wrap");
        Check.Near(-Math.Tau, AmigaAngle.InterpolateDisplayRadians(65535, 1, 0.5, -1), "short arc with opposite sign");
        Check.Near(65535 * Math.Tau / 65536, AmigaAngle.InterpolateDisplayRadians(65535, 1, 0, 1), "alpha zero");
        Check.Near(65537 * Math.Tau / 65536, AmigaAngle.InterpolateDisplayRadians(65535, 1, 1, 1), "unwrapped alpha one");
        foreach (var alpha in new[] { -0.01, 1.01, double.NaN, double.PositiveInfinity })
            Check.Throws<ArgumentOutOfRangeException>(() => AmigaAngle.InterpolateDisplayRadians(0, 1, alpha, 1),
                $"invalid display alpha {alpha}");
        foreach (var sign in new[] { 0, 2, -2 })
        {
            Check.Throws<ArgumentOutOfRangeException>(() => AmigaAngle.ToDisplayRadians(0, sign), "implicit angle sign");
            Check.Throws<ArgumentOutOfRangeException>(() => AmigaAngle.InterpolateDisplayRadians(0, 1, 0.5, sign),
                "implicit interpolation sign");
        }
    }
}
