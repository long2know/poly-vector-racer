namespace StuntRacer.FlightData;

public static class FlightScenePlacements
{
    /// <summary>
    /// Exact source item position. The low nibble selects a reversed 4x4 sub-cell, NOT
    /// height. C offsets use 256 position units (shift 8); A/B use 1024 (shift 10).
    /// Item-list ids were already converted to object-model ids by adding 16 in ingestion.
    /// This does not invent bindings for the source's runtime model slots 16..32.
    /// </summary>
    public static FlightTerrainPlacement Place(FlightTerrainTier tier, FlightTerrainCell cell, FlightTerrainItem item)
    {
        if (tier.PositionShift is not (22 or 24) || cell.X < 0 || cell.Z < 0 ||
            cell.X >= tier.GridSize || cell.Z >= tier.GridSize || item.Layer is < 0 or > 15 ||
            item.FineX is < 0 or > 4095 || item.FineZ is < 0 or > 4095 || item.ModelId is < 0 or >= 134)
            throw new FlightDataException("scene.placement", "Terrain item is outside the documented source coordinate bounds.");
        int shift = tier.PositionShift, fineShift = shift - 14;
        long x = ((long)cell.X << shift) + ((long)(3 - (item.Layer & 3)) << (shift - 2)) + ((long)item.FineX << fineShift);
        long z = ((long)cell.Z << shift) + ((long)(3 - (item.Layer >> 2)) << (shift - 2)) + ((long)item.FineZ << fineShift);
        return new FlightTerrainPlacement(tier.Name, cell.X, cell.Z, item.Layer, item.ModelId, x, 0, z,
            fineShift, item.ModelId is >= 16 and <= 32);
    }

    public static IEnumerable<FlightTerrainPlacement> Enumerate(FlightScene scene)
    {
        foreach (var tier in scene.TerrainTiers)
            foreach (var cell in tier.Cells)
            {
                if (cell.ListIndex < 0 || cell.ListIndex >= scene.TerrainLists.Count)
                    throw new FlightDataException("scene.list-index", "Terrain cell references a missing item list.");
                foreach (var item in scene.TerrainLists[cell.ListIndex].Items) yield return Place(tier, cell, item);
            }
    }
}
