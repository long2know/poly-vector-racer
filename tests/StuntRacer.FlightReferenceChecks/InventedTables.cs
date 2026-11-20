using StuntRacer.FlightReference;

namespace StuntRacer.FlightReferenceChecks;

// TEST DATA ONLY. Deliberately invented responses/gust/sink; mathematical
// sine and hypotenuse generation. NO claim of original stock-byte identity.
// In particular the sine WINDOW is generated as invented neighbouring words,
// not extracted executable bytes. Both implementations receive these SAME words.
internal static class InventedTables
{
    internal static FlightReferenceTables Make(int variant = 0)
    {
        // Set 1 deliberately stresses word overflow; it is not an aircraft tune.
        var player = Enumerable.Range(0, 62).Select(i => (short)(variant == 0 ? i * 31 + i * i / 11 : Math.Min(32767, i * 1700))).ToArray();
        var ai = Enumerable.Range(0, 64).Select(i => (short)(variant == 0 ? i * 47 + i * i / 7 : Math.Min(32767, i * 2100))).ToArray();
        var gust = Enumerable.Range(0, 31).Select(i => (short)(variant == 0 ? 400 - i * 11 : 30000 - i * 500)).ToArray();
        var sink = Enumerable.Range(0, 31).Select(i => (short)(650 - i * 19 + variant * 13)).ToArray();
        var sine = Enumerable.Range(FlightReferenceTables.FirstGustSineIndex, FlightReferenceTables.GustSineWordCount)
            .Select(i => (short)Math.Round(Math.Sin(i * Math.PI / 1800) * 16384, MidpointRounding.AwayFromZero)).ToArray();
        if (variant != 0)
            for (var i = FlightReferenceTables.FirstGustSineIndex; i <= FlightReferenceTables.LastGustSineIndex; i++)
                if (i is < 0 or > 900)
                    sine[i - FlightReferenceTables.FirstGustSineIndex] = unchecked((short)(i * 73 + 12345));
        var length = Enumerable.Range(0, 257)
            .Select(i => (short)Math.Round(Math.Sqrt(1 + i * (double)i / 65536) * 16384, MidpointRounding.AwayFromZero)).ToArray();
        return new(player, ai, gust, sink, sine, length);
    }

    internal static FlightReferenceTables With(
        FlightReferenceTables t, short[]? player = null, short[]? ai = null,
        short[]? gust = null, short[]? sink = null, short[]? sine = null, short[]? length = null)
        => new(player ?? t.PlayerResponse.ToArray(), ai ?? t.AiResponse.ToArray(),
            gust ?? t.Gust.ToArray(), sink ?? t.Sink.ToArray(),
            sine ?? t.GustSineWords.ToArray(), length ?? t.VectorLength.ToArray());
}
