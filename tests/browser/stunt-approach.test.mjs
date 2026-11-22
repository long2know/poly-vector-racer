import assert from 'node:assert/strict';
import test from 'node:test';
import { RaceScene } from '../../src/StuntRacer.Browser/wwwroot/scene.js';
import { add, cross, dot, lerp3, lookAt, normalize, scale, sub, transform } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';

const xyz = ({ x, y, z }) => [x, y, z];
const graphics = { mesh: geometry => geometry };
const modes = ['stunts', 'hard-driving'];

// Canonical straight/loop/runout rows from StuntParkCourse and HardDrivingSpatialCourse.
// Omit the rest of each circuit and deliberately use display names that cannot match the legacy aliases.
function loopTrack(mode) {
  const hard = mode === 'hard-driving', width = hard ? 14 : 11;
  const [x, y, z, radius, stretch, rows] = hard ? [-180, 8, 0, 22, 14, 768] : [-70, 12, 20, 18, 18, 640];
  const points = [], edges = [];
  let s = 0;
  function append(y, z, width, road = true) {
    if (points.length) s += Math.hypot(y - points.at(-1).y, z - points.at(-1).z);
    points.push({ s, x, y, z, width, heading: 0, bank: 0, road });
    edges.push({ left: { x: x - width / 2, y, z }, right: { x: x + width / 2, y, z } });
  }
  function line(z, width) {
    const p = points.at(-1), count = Math.ceil((z - p.z) / .7);
    for (let i = 1; i <= count; i++) append(y, p.z + (z - p.z) * i / count, p.width + (width - p.width) * i / count);
  }
  append(y, hard ? -300 : -185, hard ? 16 : width);
  if (hard) line(-80, 16);
  line(z, width);
  const loopStart = s;
  for (let i = 1; i <= rows; i++) {
    const t = i / rows, angle = t * 2 * Math.PI;
    append(y + radius * (1 - Math.cos(angle)), z + radius * Math.sin(angle) + stretch * t, width);
  }
  const loopEnd = s;
  if (hard) { line(34, 16); line(155, 16); }
  else { line(53, width); line(140, width); }
  append(y, points.at(-1).z + 1, width, false);
  return { name: `${mode} metadata-only title`, width, isSpatial: true, length: s + 400,
    points, edges, course: { mode, loopStart, loopEnd } };
}

function section(track, station) {
  const i = track.points.findIndex(point => point.s > station) - 1;
  const { points, edges } = track, a = points[i], b = points[i + 1];
  const t = (station - a.s) / (b.s - a.s);
  const left = lerp3(xyz(edges[i].left), xyz(edges[i + 1].left), t);
  const right = lerp3(xyz(edges[i].right), xyz(edges[i + 1].right), t);
  const across = sub(right, left);
  const upAt = j => normalize(cross(sub(xyz(points[j + 1]), xyz(points[Math.max(0, j - 1)])), across));
  const up = normalize(lerp3(upAt(i), upAt(i + 1), t));
  return { left, right, up, center: lerp3(left, right, .5), pitch: Math.atan2(-up[2], up[1]) };
}

function driveState(track, station, speed = 54, phase = 'racing') {
  const { center, up, pitch } = section(track, station), [x, y, z] = add(center, up);
  return { phase, progress: station / track.length, impact: 0,
    player: { x, y, z, yaw: 0, pitch, roll: 0, steer: 0, speed } };
}

function project(camera, point) {
  const [x, y, z] = transform(lookAt(camera.eye, camera.target, camera.up), point);
  const tan = Math.tan(camera.fov / 2);
  return [x / (z * tan * 1440 / 857), y / (z * tan), z];
}

function strips(track) {
  return track.edges.slice(0, -1).flatMap((edge, i) => {
    if (!track.points[i + 1].road) return [];
    const [a, b, c, d] = [edge.left, edge.right, track.edges[i + 1].left, track.edges[i + 1].right].map(xyz);
    return [[a, c, b], [b, c, d]];
  });
}

function crosses(road, from, to) {
  const ray = sub(to, from);
  return road.some(([a, b, c]) => {
    const ab = sub(b, a), ac = sub(c, a), p = cross(ray, ac), determinant = dot(ab, p);
    if (Math.abs(determinant) < 1e-9) return false;
    const t = sub(from, a), u = dot(t, p) / determinant;
    if (u < 0 || u > 1) return false;
    const q = cross(t, ab), v = dot(ray, q) / determinant, distance = dot(ac, q) / determinant;
    return v >= 0 && u + v <= 1 && distance > 1e-5 && distance < .99999;
  });
}

test('stock Stunts and Hard Drivin chase frames reveal the ring and keep the scored exit clear', () => {
  for (const mode of modes) {
    const track = loopTrack(mode), road = strips(track);
    const scene = new RaceScene(graphics, track);
    const plain = new RaceScene(graphics, { ...track, course: undefined });
    const { loopStart: start, loopEnd: end } = track.course, span = end - start;
    const dt = 1 / 60;
    const ready = driveState(track, 0, 0, 'ready');
    assert.deepEqual(scene.update(ready, dt).camera, plain.update(ready, dt).camera, `${mode} garage orbit`);
    assert.deepEqual(scene.update(driveState(track, 0, 0), dt).camera,
      plain.update(driveState(track, 0, 0), dt).camera, `${mode} unmodified runway start`);
    for (const station of [start - 22, end + 2]) {
      const cockpit = driveState(track, station);
      assert.deepEqual(scene.update(cockpit, dt, 'cockpit').camera, plain.update(cockpit, dt, 'cockpit').camera,
        `${mode} unchanged cockpit anchor and forward orientation`);
    }

    for (const speed of [38, 54]) {
      scene.update(ready, dt);
      plain.update(ready, dt);
      let previous, previousTurn = 0, revealed = false, toeOcclusion = 0, returnOcclusion = 0;
      let controlObscuredExit = false, runoutRejoined = false;
      const checkProjection = camera => {
        const positions = [section(track, start + span * .25), section(track, start + span * .75)];
        return positions.map(({ left, right }) => [left, right].map(edge => project(camera, edge)[0]).sort((a, b) => a - b));
      };
      for (let distance = -165; distance < span + 54; distance += speed * dt) {
        const state = driveState(track, start + distance, speed);
        const camera = scene.update(state, dt).camera, control = plain.update(state, dt).camera;
        const player = xyz(state.player);
        const forward = [0, Math.sin(state.player.pitch), Math.cos(state.player.pitch)];
        assert.ok(dot(sub(player, camera.eye), forward) > 5, `${mode}: eye trailed the car at ${distance.toFixed(1)} m`);
        assert.equal(camera.logarithmicDepth, true);
        if (previous) {
          const direction = frame => normalize(sub(frame.target, frame.eye));
          const turn = Math.acos(Math.min(1, dot(direction(previous), direction(camera))));
          assert.ok(turn < .084 && Math.abs(turn - previousTurn) < .014,
            `${mode} at ${speed} m/s, ${distance.toFixed(1)} m: sightline turned ${turn.toFixed(3)} rad`);
          previousTurn = turn;
          assert.ok(Math.abs(camera.fov - previous.fov) < .012, `${mode}: no lens snap`);
          assert.ok(dot(previous.up, camera.up) > .995, `${mode}: no abrupt roll in the loop`);
          assert.ok(!crosses(road, previous.eye, camera.eye), `${mode}: camera crossed the ring near ${distance.toFixed(1)} m`);
        }
        if (distance > -25) {
          const roof = add(player, scale(camera.up, .35));
          if (crosses(road, camera.eye, roof)) {
            if (distance > span - 11 && distance < span) returnOcclusion++;
            else {
              assert.ok(distance > 0 && distance < 10,
                `${mode}: road obscured the car ${(distance - span).toFixed(1)} m from the exit`);
              toeOcclusion++;
            }
          }
          if (distance >= span && distance < span + 12) {
            controlObscuredExit ||= crosses(road, control.eye, roof);
            const roadAhead = add(section(track, start + distance + 8).center, [0, .25, 0]);
            assert.ok(!crosses(road, camera.eye, roadAhead), `${mode}: scored runout sightline must also clear the strip`);
            const [x, y, depth] = project(camera, roof);
            assert.ok(depth > 0 && Math.abs(x) < .42 && Math.abs(y) < .5, `${mode}: car stays in the scored runout frame`);
          }
        }
        if (!revealed && distance >= -22) {
          const gap = ranges => Math.max(ranges[0][0], ranges[1][0]) - Math.min(ranges[0][1], ranges[1][1]);
          const opening = gap(checkProjection(camera));
          assert.ok(opening > .023, `${mode} at ${speed} m/s: projected opening was ${opening.toFixed(3)}`);
          assert.ok(gap(checkProjection(control)) < 0, `${mode}: centered view overlaps the same edges`);
          const peak = section(track, start + span / 2).center;
          const [px, py, pd] = project(camera, peak), [cx, cy, cd] = project(camera, player);
          assert.ok(pd > 0 && cd > 0 && Math.abs(px) < .55 && py > .2 && py < .94
            && Math.abs(cx) < .42 && cy > -.65 && cy < .08, `${mode}: crown and car share the forward frame`);
          const focus = lerp3(section(track, start).center, peak, .5);
          const aim = frame => dot(normalize(sub(frame.target, frame.eye)), normalize(sub(focus, frame.eye)));
          assert.ok(aim(camera) > aim(control) + .06, `${mode}: chase actually looks toward the ring`);
          revealed = true;
        }
        if (distance >= span + 52) {
          assert.ok(Math.hypot(...sub(camera.eye, control.eye)) < .15, `${mode}: normal chase resumes down the runout`);
          runoutRejoined = true;
        }
        previous = camera;
      }
      assert.ok(revealed && controlObscuredExit && runoutRejoined);
      assert.ok(toeOcclusion <= 3, `${mode}: only the physically intersecting toe may briefly mask the car`);
      assert.ok(returnOcclusion <= 6, `${mode}: descending through the crossed toe may briefly mask the roof, got ${returnOcclusion} frames`);
    }
  }
});

test('stunt loop trim and arrows follow canonical front faces over both slopes and the inverted crown', () => {
  for (const mode of modes) {
    const track = loopTrack(mode), scene = new RaceScene(graphics, track);
    assert.ok(scene.courseScenery, `${mode} alternate display names must not suppress scenery`);
    const { loopStart: start, loopEnd: end } = track.course;
    const vertices = [];
    for (let i = 0; i < scene.road.vertices.length; i += 10) vertices.push(scene.road.vertices.slice(i, i + 10));
    assert.deepEqual(vertices.filter(v => v[9] === 1).map(v => v.slice(0, 3)), strips(track).flat(),
      `${mode}: paint and structure must not move the canonical road triangles`);

    for (const fraction of [.25, .5, .75]) {
      const row = track.points.find(point => point.s >= start + (end - start) * fraction);
      const { left, right, up } = section(track, row.s), outward = normalize(sub(right, left));
      for (const [edge, side] of [[left, -1], [right, 1]]) {
        const at = (v, distance) => Math.hypot(...sub(v, add(edge, scale(up, distance)))) < .006;
        assert.ok(vertices.some(v => v[9] === 4 && at(v, .025) && dot(v.slice(3, 6), up) > .997),
          `${mode}: both edge stripes face the driven surface at ${fraction}`);
        assert.ok(vertices.some(v => v[9] === 0 && at(v, -.75) && dot(v.slice(3, 6), outward) * side > .997),
          `${mode}: structural sides belong below the road and face outward at ${fraction}`);
      }
    }

    const entry = section(track, start).center, exit = section(track, end).center;
    const arrows = (from, to) => vertices.filter(v => v[9] === 4 && v[1] > entry[1] && v[1] < entry[1] + .1
      && Math.abs(v[0] - entry[0]) < track.width * .34 && v[2] > from && v[2] < to).sort((a, b) => a[2] - b[2]);
    const entryArrows = arrows(entry[2] - 44, entry[2] - 10), exitArrows = arrows(exit[2] + 1, exit[2] + 33);
    assert.ok(entryArrows.length > 20 && exitArrows.length > 20, `${mode}: both flat approaches carry paint`);
    assert.notDeepEqual(entryArrows[0].slice(6, 9), exitArrows[0].slice(6, 9), `${mode}: entry and runout are distinct`);
    const first = entryArrows.filter(v => v[2] < entryArrows[0][2] + 5);
    assert.ok(Math.abs(first[0][0] - entry[0]) > Math.abs(first.at(-1)[0] - entry[0]) * 3,
      `${mode}: entry chevron narrows toward the loop, not back toward the player`);
  }
});
