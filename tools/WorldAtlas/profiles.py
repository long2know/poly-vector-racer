"""Stock nonoverlapping terrain areas and independently licensed OSM overlays."""

from dataclasses import dataclass

from sources import Source, cop_source, osm_source
from terrain import groups_for, plan_boxes

# These eight exact L0-column ranges cover the world in 45-degree intervals.
LONGITUDE_BANDS = {
    "north-pacific": ("North Pacific • Alaska and the Aleutians", -76, -57),
    "western-americas": ("Western Americas • Pacific coast and Rockies", -57, -38),
    "eastern-americas": ("Eastern Americas • Atlantic coast and Andes", -38, -19),
    "western-atlantic": ("Western Atlantic • Brazil and Greenland", -19, 0),
    "europe-africa": ("Europe and Africa • Greenwich to 45°E", 0, 19),
    "western-asia": ("Western Asia • Arabia to 90°E", 19, 38),
    "eastern-asia": ("Eastern Asia and Australia • 90°E to 135°E", 38, 57),
    "western-pacific": ("Western Pacific • Japan to New Zealand", 57, 76),
}


@dataclass(frozen=True)
class Pilot:
    name: str
    terrain: tuple
    feature_bounds: tuple
    extract: str
    copernicus: tuple = ()


PILOTS = {
    "puget-sound": Pilot(
        "Puget Sound • Seattle, I-90 and the Cascades",
        (((-123.05, 46.75, -120.94, 48.02), 6), ((-122.45, 47.47, -122.21, 47.76), 8),
         ((-121.7, 47.33, -121.31, 47.56), 7)),
        (-122.63, 47.37, -121.32, 47.80), "washington", ("N47_00_W122_00",)),
    "panama-canal": Pilot(
        "Panama Canal • Pacific and Caribbean approaches",
        (((-80.22, 8.72, -79.29, 9.50), 6), ((-79.70, 8.91, -79.47, 9.10), 8)),
        (-80.16, 8.76, -79.34, 9.46), "panama", ("N08_00_W080_00", "N09_00_W080_00")),
    "machu-picchu": Pilot(
        "Machu Picchu • Sacred Valley and Cusco",
        (((-73.05, -13.68, -71.74, -12.93), 7), ((-72.64, -13.27, -72.43, -13.09), 8)),
        (-73.03, -13.66, -71.78, -12.95), "peru", ("S14_00_W073_00", "S14_00_W072_00")),
    "rio-de-janeiro": Pilot(
        "Rio de Janeiro • Guanabara Bay and Sugarloaf",
        (((-43.86, -23.12, -43.02, -22.68), 7), ((-43.32, -23.05, -43.11, -22.82), 8)),
        (-43.83, -23.10, -43.04, -22.70), "sudeste", ("S23_00_W044_00",)),
}

# These drop-in refinements retain the exact bounds of the original atlas builds.
# Their supporting L1/L2 tiles are identical to the longitude bands, not carved out.
REFINEMENTS = {
    "alps-detail": ("Alps: Mont Blanc and Matterhorn", ("N45_00_E006_00", "N45_00_E007_00"), (
        (1, 4, 38, 8, 40), (2, 10, 76, 14, 78), (3, 22, 154, 28, 156), (4, 44, 308, 54, 312),
        (5, 88, 616, 108, 624), (6, 176, 1232, 216, 1246), (7, 352, 2466, 430, 2490),
        (8, 718, 4940, 764, 4968), (8, 808, 4952, 846, 4974),
    )),
    "cape-town-detail": ("Cape Town: Table Mountain and Peninsula", ("S34_00_E018_00",), (
        (1, 14, -30, 16, -28), (2, 30, -58, 32, -56), (3, 60, -116, 64, -114), (4, 122, -230, 128, -228),
        (5, 246, -460, 254, -456), (6, 492, -920, 506, -912), (7, 986, -1840, 1010, -1824),
        (8, 1972, -3680, 2020, -3648),
    )),
    "himalayas-detail": ("Himalayas: Everest and Khumbu", ("N27_00_E086_00",), (
        (1, 72, 22, 74, 24), (2, 146, 46, 148, 48), (3, 292, 92, 294, 96), (4, 584, 186, 588, 190),
        (5, 1168, 372, 1176, 380), (6, 2338, 746, 2352, 758), (7, 4676, 1494, 4704, 1516),
        (8, 9368, 3004, 9404, 3028),
    )),
    "japan-kanto-detail": ("Japan: Fuji, Tokyo and Sagami Bay", ("N35_00_E138_00", "N35_00_E139_00"), (
        (1, 116, 28, 120, 32), (2, 232, 58, 238, 62), (3, 466, 118, 474, 122), (4, 934, 236, 946, 244),
        (5, 1870, 474, 1892, 486), (6, 3740, 948, 3784, 970), (7, 7480, 1896, 7566, 1938),
        (8, 14972, 3808, 15012, 3842), (8, 15080, 3838, 15130, 3872),
    )),
    "kilimanjaro-detail": ("Kilimanjaro: Summit and Moshi", ("S04_00_E037_00",), (
        (1, 30, -4, 32, -2), (2, 62, -6, 64, -4), (3, 124, -12, 128, -10), (4, 250, -24, 256, -20),
        (5, 500, -48, 510, -40), (6, 1002, -96, 1018, -80), (7, 2006, -192, 2036, -162),
        (8, 4024, -352, 4058, -324),
    )),
    "nile-giza-detail": ("Egypt: Cairo, Giza and the Nile", ("N29_00_E031_00", "N30_00_E031_00"), (
        (1, 24, 24, 28, 26), (2, 50, 50, 54, 52), (3, 102, 100, 108, 104), (4, 206, 200, 214, 206),
        (5, 414, 400, 428, 410), (6, 830, 802, 856, 818), (7, 1662, 1604, 1712, 1636),
        (8, 3350, 3228, 3398, 3264),
    )),
    "patagonia-detail": ("Patagonia: Torres del Paine", ("S51_00_W074_00",), (
        (1, -64, -44, -60, -42), (2, -126, -88, -122, -84), (3, -250, -174, -244, -170),
        (4, -498, -346, -490, -342), (5, -996, -692, -980, -684), (6, -1990, -1382, -1962, -1370),
        (7, -3978, -2762, -3926, -2740), (8, -7928, -5514, -7892, -5488),
    )),
    "sydney-detail": ("Sydney: Harbour and Coast", ("S34_00_E151_00",), (
        (1, 126, -30, 128, -28), (2, 254, -58, 256, -56), (3, 510, -116, 512, -112), (4, 1020, -230, 1024, -226),
        (5, 2040, -460, 2048, -454), (6, 4080, -920, 4094, -908), (7, 8160, -1838, 8186, -1818),
        (8, 16322, -3674, 16370, -3638),
    )),
}


def pilot_roots(pilot):
    return set().union(*(groups_for(box, 1) for box, level in pilot.terrain))


def stock(pack_id, raw, inputs):
    if pack_id in REFINEMENTS:
        name, coords, rectangles = REFINEMENTS[pack_id]
        plan = {}
        for level, w, s, e, n in rectangles:
            plan.setdefault(level, set()).update((x, z) for x in range(w, e, 2) for z in range(s, n, 2))
        dem = [cop_source(inputs, coord, bucket="https://copernicus-dem-30m.s3.amazonaws.com", release="")
               for coord in coords]
        return name, plan, None, None, dem
    all_roots = set().union(*(pilot_roots(pilot) for pilot in PILOTS.values()))
    if pack_id in LONGITUDE_BANDS:
        name, w, e = LONGITUDE_BANDS[pack_id]
        roots = [((w, -38, e, 38), 2)]
        return name, plan_boxes([], roots=roots, exclude_roots=all_roots), None, None, []
    if pack_id in PILOTS:
        pilot = PILOTS[pack_id]
        dem = []
        if pack_id == "puget-sound":
            from json import loads
            source_path = raw / "sources.json"
            if source_path.is_file():
                urls = loads(source_path.read_text())
                for name in ("pnw-3dep.tif", "seattle-3dep.tif", "rainier-3dep.tif"):
                    if name in urls and (raw / name).is_file():
                        dem.append(Source("usgs-3dep", raw / name, urls[name]))
        dem.extend(cop_source(inputs, coord) for coord in pilot.copernicus)
        return pilot.name, plan_boxes(pilot.terrain, include_roots=pilot_roots(pilot)), None, None, dem
    if pack_id.endswith("-osm") and pack_id[:-4] in PILOTS:
        pilot = PILOTS[pack_id[:-4]]
        return f"{pilot.name} • OpenStreetMap", {}, osm_source(inputs, pilot.extract), pilot.feature_bounds, []
    return None
