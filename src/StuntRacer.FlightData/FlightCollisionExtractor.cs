namespace StuntRacer.FlightData;

internal static class FlightCollisionExtractor
{
    internal static IReadOnlyList<FlightModelCollision> Extract(FlightImage image, IReadOnlyList<FlightModel> models)
    {
        var result = new List<FlightModelCollision>();
        foreach (var model in models)
        {
            if (model.Kind != "normal-model-program" || model.Levels.Count == 0) continue;
            // The first header word is also the footprint offset. A drawing block's
            // "part id" alone is not evidence that the referenced bytes form a footprint.
            int address = model.ModelBase + (model.Levels[0].PartId & 0xFFF);
            if (!TryFootprint(image, address, out FlightCollisionBox? outer, out var nodes)) continue;
            var segment = ReferenceLayout.Segments.Single(s => address >= s.FlatAddress && address < s.FlatAddress + s.ByteCount);
            uint ram = checked(segment.RamAddress + (uint)(address - segment.FlatAddress));
            int bias = (int)(ram >> 16) >> 2;
            int auxiliary = 0;
            ushort referenceCount = 0;
            var referencePlanes = new List<FlightCollisionPlane>();
            // Aux is for the painter reorder, NOT the footprint chain or a replacement for it.
            if (model.Id >= 16 && (model.Levels[0].Flags & 0x10) != 0 && model.AuxiliaryAddress != 0)
            {
                auxiliary = model.AuxiliaryAddress;
                referenceCount = image.ReadUInt16(auxiliary);
                if (referenceCount >= 8) throw new FlightDataException("collision.reference-count", "Too many original reorder reference planes.", auxiliary);
                int planeAddress = FlightSceneExtractor.Address(image.ReadUInt32(auxiliary + 6));
                for (int i = 0; i <= referenceCount; i++) referencePlanes.Add(Plane(image, planeAddress + 12 * i));
            }
            result.Add(new FlightModelCollision(model.Id, address, ram, bias, outer!, nodes!,
                auxiliary, referenceCount, referencePlanes.AsReadOnly()));
        }
        // Full image and executable pins are verified before this decoder is called.
        // No disappearing malformed footprint can quietly turn into a no-collision world.
        // The upstream permissive recognizer also accepts effect model 34's odd address
        // and inverted bounds as a footprint candidate. It is not a
        // static landmark and is intentionally not promoted to a collision volume here.
        if (result.Count != 13 || result.Count(r => r.ReorderPlanes.Count > 0) != 3 ||
            result.Any(r => r.ReferenceLandmarkZBias != 48))
            throw new FlightDataException("collision.inventory", $"Original footprint/reference-plane inventory differs: {result.Count} valid static model footprints; expected 13.");
        return result.AsReadOnly();
    }

    // Not every normal model contains footprint data. This recognizer never catches parser
    // exceptions or supplies empty boxes: invalid candidates are absent, and the known source
    // inventory plus full image pin makes unexpected absence a hard error above.
    internal static bool TryFootprint(FlightImage image, int address, out FlightCollisionBox? outer,
        out IReadOnlyList<FlightCollisionNode>? nodes)
    {
        outer = null; nodes = null;
        if ((address & 1) != 0 || !image.Contains(address, 10)) return false;
        var box = Box(image, address);
        if (!ValidBox(box)) return false;
        int next = address + 10, slotBytes = 14;
        var found = new List<FlightCollisionNode>();
        var seen = new HashSet<int>();
        while (true)
        {
            if (!seen.Add(next) || found.Count == 4 || (next & 1) != 0 || !image.Contains(next, 16)) return false;
            var nodeBox = Box(image, next);
            if (!ValidBox(nodeBox)) return false;
            uint pointer = image.ReadUInt32(next + 10);
            ushort count = image.ReadUInt16(next + 14);
            int storedPlanes = Math.Max((int)count, 1);
            if (count > 8 || !image.Contains(next + 16, 12 * storedPlanes)) return false;
            slotBytes += 14 + 6 * storedPlanes;
            if (slotBytes > 256) return false;
            var planes = new List<FlightCollisionPlane>();
            for (int k = 0; k < storedPlanes; k++) planes.Add(Plane(image, next + 16 + 12 * k));
            found.Add(new FlightCollisionNode(next, nodeBox, pointer, count, planes.AsReadOnly()));
            if ((pointer & 0x80000000) != 0) break;
            if (pointer > int.MaxValue) return false;
            next = (int)pointer;
        }
        outer = box; nodes = found.AsReadOnly(); return true;
    }
    private static bool ValidBox(FlightCollisionBox box) => box.MinimumX < box.MaximumX && box.MinimumZ < box.MaximumZ;
    private static FlightCollisionBox Box(FlightImage image, int p) => new(image.ReadInt16(p), image.ReadInt16(p + 2),
        image.ReadInt16(p + 4), image.ReadInt16(p + 6), image.ReadInt16(p + 8));
    private static FlightCollisionPlane Plane(FlightImage image, int p) => new(p, image.ReadInt16(p), image.ReadInt16(p + 2),
        image.ReadInt16(p + 4), image.ReadInt16(p + 6), image.ReadInt16(p + 8), image.ReadInt16(p + 10));
}
