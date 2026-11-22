import assert from 'node:assert/strict';
import test from 'node:test';
import { atlasRouteForDeparture, atlasRouteDeparture, formatAtlasPlayEta }
  from '../../src/StuntRacer.Browser/wwwroot/atlas-flight-plan.js';
import { atlasGrid } from '../../src/StuntRacer.Browser/wwwroot/world-atlas.js';

test('scenic departure pairs are short east/northeast coastal flights, not voyage reroutes', () => {
  const expected = [
    ['scenic-california-coast', 'california-coast', 39_000, 41_000, 9],
    ['scenic-oahu-south-shore', 'oahu-south-shore', 48_000, 50_000, 87],
  ];
  for (const [preset, id, min, max, bearing] of expected) {
    const route = atlasRouteForDeparture(preset);
    assert.equal(route?.id, id);
    assert.equal(route.scenic, true);
    const departure = atlasRouteDeparture(route);
    const distance = Math.hypot(route.destination.lonDeg - departure.lon, route.destination.latDeg - departure.lat) *
      atlasGrid.halfWidth / 180;
    assert.ok(distance > min && distance < max, `${id}: ${distance} projected metres`);
    assert.equal(Math.round(departure.heading * 180 / Math.PI), bearing);
  }
  const pacific = atlasRouteForDeparture('voyage-pacific');
  assert.ok(atlasRouteDeparture(pacific).heading > 0, 'Tokyo still departs eastward across the date line');
  assert.equal(atlasRouteForDeparture('custom'), undefined, 'Custom coordinates are not linked to a route');
});

test('only an active GPS route shows a real-play estimate or an unavailable state', () => {
  const route = (playEtaSeconds, status = 'cruise') => ({ mode: 'route', status, playEtaSeconds });
  assert.equal(formatAtlasPlayEta(null), '');
  assert.equal(formatAtlasPlayEta({ ...route(88), mode: 'hold' }), '');
  assert.equal(formatAtlasPlayEta(route(88, 'off')), '');
  assert.equal(formatAtlasPlayEta(route(0, 'arrived')), '');
  for (const value of [undefined, 0, -1, NaN, Infinity])
    assert.equal(formatAtlasPlayEta(route(value, 'acquiring')), 'Play ETA unavailable');
  assert.equal(formatAtlasPlayEta(route(0.5, 'acquiring')), 'Play ETA <15 s');
  assert.equal(formatAtlasPlayEta(route(41, 'acquiring')), 'Play ETA ~45 s', 'A stable 1× approach can show ETA');
  assert.equal(formatAtlasPlayEta(route(60)), 'Play ETA ~1 min');
  assert.equal(formatAtlasPlayEta(route(70)), 'Play ETA ~1 min 15 s');
  assert.equal(formatAtlasPlayEta(route(121)), 'Play ETA ~2 min');
  assert.equal(formatAtlasPlayEta(route(3600)), 'Play ETA ~1 h');
  assert.equal(formatAtlasPlayEta(route(4300)), 'Play ETA ~1 h 12 min');
});
