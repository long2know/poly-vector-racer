export const atlasRoutes = [
  { id: 'atlantic', departureId: 'voyage-atlantic', name: 'Atlantic / New York to London',
    origin: { name: 'New York / JFK', lon: -73.7781, lat: 40.6413 },
    destinationName: 'London / Heathrow (Atlantic)', destination: { lonDeg: -0.4543, latDeg: 51.47 } },
  { id: 'pacific', departureId: 'voyage-pacific', name: 'Pacific / Tokyo to San Francisco',
    origin: { name: 'Tokyo / Haneda', lon: 139.7798, lat: 35.5494 },
    destinationName: 'San Francisco / SFO (Pacific)', destination: { lonDeg: -122.379, latDeg: 37.6213 } },
  { id: 'california-coast', departureId: 'scenic-california-coast', scenic: true,
    name: 'California Coast / Half Moon Bay to Golden Gate',
    origin: { name: 'California Coast / Half Moon Bay', lon: -122.535, lat: 37.465 },
    destinationName: 'Golden Gate / California Coast', destination: { lonDeg: -122.4783, latDeg: 37.8199 } },
  { id: 'oahu-south-shore', departureId: 'scenic-oahu-south-shore', scenic: true,
    name: 'Oʻahu South Shore / Kalaeloa to Makapuʻu',
    origin: { name: 'Oʻahu South Shore / Kalaeloa', lon: -158.09, lat: 21.286 },
    destinationName: 'Makapuʻu / Oʻahu South Shore', destination: { lonDeg: -157.6494, latDeg: 21.3108 } },
];

export const atlasRouteForDeparture = preset => atlasRoutes.find(route => route.departureId === preset);

export function atlasRouteDeparture(route) {
  const { lon, lat } = route.origin;
  const { lonDeg, latDeg } = route.destination;
  // The Atlas projection uses the same number of metres per degree on both axes.
  const deltaLon = (lonDeg - lon + 540) % 360 - 180;
  return { id: route.departureId, ...route.origin, heading: Math.atan2(deltaLon, latDeg - lat) };
}

export function formatAtlasPlayEta(pilot) {
  if (pilot?.mode !== 'route' || pilot.status === 'off' || pilot.status === 'arrived') return '';
  const seconds = pilot.playEtaSeconds;
  if (!Number.isFinite(seconds) || seconds <= 0) return 'Play ETA unavailable';
  if (seconds < 15) return 'Play ETA <15 s';
  const quantum = seconds < 300 ? 15 : 60;
  const rounded = Math.round(seconds / quantum) * quantum;
  const hours = Math.floor(rounded / 3600), minutes = Math.floor(rounded / 60) % 60, rest = rounded % 60;
  return `Play ETA ~${[hours && `${hours} h`, minutes && `${minutes} min`, rest && `${rest} s`].filter(Boolean).join(' ')}`;
}
