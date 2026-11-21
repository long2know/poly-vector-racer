"""Source windows and detail patches for the World Flight catalog."""

BASE_ELEVATION = {
    "pnw": ((-125.3, 45.4, -119.6, 49.2), (3500, 3500)),
    "seattle": ((-122.57, 47.43, -122.08, 47.81), (2100, 2300)),
}

DETAIL_ELEVATION = {
    "rainier": ((-122.19, 46.63, -121.40, 47.16), (2450, 2150)),
    "san-francisco": ((-122.64, 37.68, -122.30, 37.92), (1450, 1150)),
    "yosemite": ((-120.13, 37.49, -119.10, 38.16), (2400, 2350)),
    "grand-canyon": ((-113.10, 35.65, -111.65, 36.63), (3050, 3000)),
}
YOSEMITE_DEM = "https://prd-tnm.s3.amazonaws.com/StagedProducts/Elevation/1/TIFF/current/n38w120/USGS_1_n38w120.tif"

# Each box is snapped to full groups of four child tiles. Higher levels can
# focus on landmarks instead of loading the empty space between detail patches.
PACIFIC_NORTHWEST = {
    3: [(-125.15, 45.55, -119.75, 49.05)],
    4: [(-125.15, 45.55, -119.75, 49.05)],
    5: [(-125.04, 45.7, -119.93, 49.02)],
    6: [(-124.55, 46.14, -120.97, 49.0)],
    7: [(-122.53, 47.455, -122.12, 47.78), (-122.14, 46.69, -121.47, 47.10)],
    8: [(-122.53, 47.455, -122.12, 47.78), (-122.01, 46.74, -121.58, 46.99)],
}
WESTERN_DETAIL = {
    "san-francisco": (-122.595, 37.715, -122.345, 37.885),
    "yosemite": (-120.00, 37.56, -119.23, 38.09),
    "grand-canyon": (-112.96, 35.755, -111.78, 36.535),
}
WESTERN_CORES = {
    "san-francisco": WESTERN_DETAIL["san-francisco"],
    "yosemite": (-119.81, 37.63, -119.41, 37.88),
    "grand-canyon": (-112.47, 35.96, -111.95, 36.28),
}

OSM_EXTRACTS = {
    "seattle": {
        "osm-center": "-122.348,47.603,-122.327,47.614",
        "osm-north": "-122.355,47.614,-122.334,47.626",
        "osm-south": "-122.344,47.591,-122.318,47.603",
    },
    "san-francisco": {
        "osm-sf-market": "-122.417,37.779,-122.393,37.788",
        "osm-sf-waterfront": "-122.414,37.788,-122.386,37.800",
        "osm-sf-north": "-122.420,37.800,-122.392,37.810",
    },
}

# Separate map windows keep each API response below the OSM map node limit.
# Only the road layer uses these: do not expand either city's building dataset.
SEATTLE_ROAD_LONGITUDES = (-122.379, -122.355, -122.335, -122.313, -122.287)
SEATTLE_ROAD_LATITUDES = (47.566, 47.587, 47.603, 47.614, 47.627, 47.648, 47.662)
SEATTLE_ROAD_BOUNDS = (-122.379, 47.566, -122.287, 47.662)
SEATTLE_LOCAL_BOUNDS = (-122.365, 47.579, -122.304, 47.649)
SEATTLE_ROAD_EXTRACTS = {
    f"osm-seattle-roads-{x}-{z}": f"{west:.3f},{south:.3f},{east:.3f},{north:.3f}"
    for z, (south, north) in enumerate(zip(SEATTLE_ROAD_LATITUDES, SEATTLE_ROAD_LATITUDES[1:]))
    for x, (west, east) in enumerate(zip(SEATTLE_ROAD_LONGITUDES, SEATTLE_ROAD_LONGITUDES[1:]))
}
ROAD_SPLIT_LIMIT = 3


def seattle_road_inputs(directory):
    def files(name, depth):
        path = directory / f"{name}.xml"
        if path.is_file():
            return [path]
        if depth == ROAD_SPLIT_LIMIT or not any(directory.glob(f"{name}-*.xml")):
            raise FileNotFoundError(f"Missing Seattle road map input: {path}")
        return files(f"{name}-a", depth + 1) + files(f"{name}-b", depth + 1)

    return [path for name in SEATTLE_ROAD_EXTRACTS for path in files(name, 0)]
