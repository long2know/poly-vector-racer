import { lookAt, multiply, perspective } from './engine/math.js';

const near = .12;
const limit = 6;

export function projectLandmarks(anchors, camera, aspect, width, height) {
  const far = camera.far ?? 2100;
  const m = multiply(perspective(camera.fov, aspect, near, far), lookAt(camera.eye, camera.target, camera.up));
  const projected = [];
  for (const { name, position: [x, y, z] } of anchors) {
    const depth = m[3] * x + m[7] * y + m[11] * z + m[15];
    if (!(depth >= near && depth <= far)) continue;
    const horizontal = (m[0] * x + m[4] * y + m[8] * z + m[12]) / depth;
    const vertical = (m[1] * x + m[5] * y + m[9] * z + m[13]) / depth;
    if (!(Math.abs(horizontal) < 1 && Math.abs(vertical) < 1)) continue;
    projected.push({ name, x: (horizontal + 1) * width / 2, y: (1 - vertical) * height / 2, depth });
  }
  return projected.sort((a, b) => a.depth - b.depth);
}

export class FlightLandmarkLabels {
  enabled = false;
  entries = new Map();
  visible = [];

  constructor(layer, canvas) {
    this.layer = layer;
    this.canvas = canvas;
  }

  clear(reset = false) {
    this.layer.hidden = true;
    for (const name of this.visible) this.entries.get(name).node.hidden = true;
    this.visible = [];
    if (reset) {
      this.layer.replaceChildren();
      this.entries.clear();
    }
  }

  update(anchors, camera) {
    if (!anchors.length) {
      this.clear();
      return;
    }
    const canvas = this.canvas;
    const rect = canvas.getBoundingClientRect();
    const projected = projectLandmarks(anchors, camera, canvas.width / canvas.height, rect.width, rect.height);
    if (!projected.length) {
      this.clear();
      return;
    }
    this.layer.hidden = false;
    const layer = this.layer.getBoundingClientRect();
    const offsetX = rect.left - layer.left, offsetY = rect.top - layer.top;
    const occupied = [];
    const shown = new Set();
    for (const point of projected) {
      if (shown.has(point.name)) continue;
      const { node, width, height } = this.entry(point.name);
      const x = point.x + offsetX, y = point.y + offsetY - 5;
      const bounds = { left: x - width / 2, right: x + width / 2, top: y - height, bottom: y };
      if (bounds.left < 8 || bounds.right > layer.width - 8 || bounds.top < 110 ||
          bounds.bottom > layer.height - 22 || occupied.some(other =>
            bounds.left < other.right + 8 && bounds.right > other.left - 8 &&
            bounds.top < other.bottom + 5 && bounds.bottom > other.top - 5)) continue;
      occupied.push(bounds);
      shown.add(point.name);
      node.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px) translate(-50%, -100%)`;
      node.hidden = false;
      if (shown.size === limit) break;
    }
    for (const name of this.visible) {
      if (!shown.has(name)) this.entries.get(name).node.hidden = true;
    }
    this.visible = [...shown];
    if (!shown.size) this.layer.hidden = true;
  }

  entry(name) {
    let result = this.entries.get(name);
    if (result) return result;
    const node = document.createElement('span');
    node.className = 'flight-landmark-label';
    node.textContent = name;
    this.layer.append(node);
    result = { node, width: node.offsetWidth, height: node.offsetHeight };
    node.hidden = true;
    this.entries.set(name, result);
    return result;
  }
}
