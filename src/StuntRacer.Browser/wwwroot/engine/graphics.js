import { cross, identity, lookAt, multiply, normalize, orthographic, perspective, scale, sub } from './math.js';

const vertex = `#version 300 es
precision highp float;
layout(location=0) in vec3 position;
layout(location=1) in vec3 normal;
layout(location=2) in vec3 color;
layout(location=3) in float material;
uniform mat4 model, viewProjection, lightProjection;
out vec3 worldPosition, worldNormal, surfaceColor;
out vec4 shadowPosition;
flat out float surfaceMaterial;
#ifdef FLIGHT_DEPTH
out float viewDepth;
#endif
void main() {
  vec4 world = model * vec4(position,1.0);
  worldPosition=world.xyz; worldNormal=mat3(model)*normal;
  surfaceColor=color; surfaceMaterial=material;
  shadowPosition=lightProjection*world;
  gl_Position=viewProjection*world;
#ifdef FLIGHT_DEPTH
  viewDepth=gl_Position.w;
#endif
}`;

const fragment = `#version 300 es
precision highp float;
in vec3 worldPosition, worldNormal, surfaceColor;
in vec4 shadowPosition;
flat in float surfaceMaterial;
uniform vec3 eye, sun;
uniform sampler2D shadowMap;
uniform float shadowTexel, opacity, fogDistance, worldTime;
uniform bool softShadows;
out vec4 pixel;
#ifdef FLIGHT_DEPTH
in float viewDepth;
uniform float logDepthScale;
#endif
float hash(vec2 p) {
  uvec2 q=uvec2(ivec2(p));
  uint h=q.x*1664525u+q.y*1013904223u;
  h=(h^(h>>16u))*2246822519u;
  h=(h^(h>>13u))*3266489917u;
  return float((h^(h>>16u))&0x00ffffffu)/16777215.0;
}
float shadow(vec3 n) {
  vec3 p=shadowPosition.xyz/shadowPosition.w*.5+.5;
  if(p.x<0.0||p.y<0.0||p.x>1.0||p.y>1.0||p.z>1.0) return 1.0;
  float bias=max(.0004,.0015*(1.0-max(dot(n,sun),0.0))), result=0.0;
  if(!softShadows) return p.z-bias<=texture(shadowMap,p.xy).r?1.0:.34;
  for(int x=-1;x<=1;x++) for(int y=-1;y<=1;y++)
    result+=p.z-bias<=texture(shadowMap,p.xy+vec2(x,y)*shadowTexel).r?1.0:.0;
  return .34+.66*result/9.0;
}
void main() {
#ifdef FLIGHT_DEPTH
  gl_FragDepth=log2(1.0+viewDepth)*logDepthScale;
#endif
  vec3 n=normalize(worldNormal), base=surfaceColor;
  bool water=surfaceMaterial>4.5;
  if(water) {
    float a=worldPosition.x*.021+worldTime*.7, b=worldPosition.z*.017-worldTime*.5;
    n=normalize(n+vec3(cos(a)*.045,0.0,sin(b)*.045));
    base*=.94+.045*sin(a+b);
  }
  if(surfaceMaterial>.5&&surfaceMaterial<1.5) {
    float grain=hash(floor(worldPosition.xz*31.0));
    float detail=1.0-smoothstep(.025,.12,length(fwidth(worldPosition.xz)));
    base*=.98+(grain-.5)*.12*detail;
    base+=.004*sin(worldPosition.x*.7)*sin(worldPosition.z*.63);
  }
  if(surfaceMaterial>2.5&&surfaceMaterial<3.5) {
    float patches=sin(worldPosition.x*.019)*sin(worldPosition.z*.024);
    base*=.93+.12*patches+.04*hash(floor(worldPosition.xz*.7));
  }
  vec3 view=normalize(eye-worldPosition);
  float diffuse=max(dot(n,sun),0.0), visibility=shadow(n);
  vec3 ambient=mix(vec3(.21,.24,.25),vec3(.48,.57,.62),n.y*.5+.5);
  vec3 lit=base*(ambient+vec3(1.0,.88,.71)*diffuse*visibility*1.02);
  float metallic=water?.65:step(1.5,surfaceMaterial)*(1.0-step(2.5,surfaceMaterial));
  float spec=pow(max(dot(n,normalize(sun+view)),0.0),mix(22.0,88.0,metallic));
  lit+=vec3(1.0,.89,.76)*spec*mix(.09,.5,metallic)*visibility;
  if(surfaceMaterial>3.5&&surfaceMaterial<4.5) lit=base*1.8;
  float fog=1.0-exp(-pow(length(eye-worldPosition)/fogDistance,1.6));
  lit=mix(lit,vec3(.65,.74,.76),fog);
  lit=lit*(2.51*lit+.03)/(lit*(2.43*lit+.59)+.14);
  pixel=vec4(pow(clamp(lit,0.0,1.0),vec3(.9)),opacity);
}`;

const shadowVertex = `#version 300 es
layout(location=0) in vec3 position;
uniform mat4 model, lightProjection;
void main(){ gl_Position=lightProjection*model*vec4(position,1.0); }`;
const shadowFragment = `#version 300 es
precision highp float;
void main(){}`;
const skyVertex = `#version 300 es
out vec2 uv;
void main(){
  vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);
  uv=p*2.0-1.0; gl_Position=vec4(uv,1.0,1.0);
}`;
const skyFragment = `#version 300 es
precision highp float;
in vec2 uv;
uniform vec3 forward, right, up, sun;
uniform float aspect, lens, time;
out vec4 pixel;
float hash(vec2 p){return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}
float noise(vec2 p){
  vec2 i=floor(p),f=fract(p); f=f*f*(3.0-2.0*f);
  return mix(mix(hash(i),hash(i+vec2(1,0)),f.x),
    mix(hash(i+vec2(0,1)),hash(i+vec2(1,1)),f.x),f.y);
}
void main(){
  vec3 ray=normalize(forward+right*uv.x*aspect*lens+up*uv.y*lens);
  float h=pow(max(ray.y,0.0),.55);
  vec3 col=mix(vec3(.75,.82,.81),vec3(.24,.50,.66),h);
  float glow=pow(max(dot(ray,sun),0.0),12.0);
  col+=vec3(.26,.17,.065)*glow;
  col=mix(col,vec3(1.0,.95,.77),smoothstep(.9994,.9997,dot(ray,sun)));
  if(ray.y>.025){
    vec2 p=ray.xz/(ray.y+.12)*1.5+vec2(time*.004,0.0);
    float cloud=noise(p)*.57+noise(p*2.03)*.28+noise(p*4.1)*.15;
    float amount=smoothstep(.57,.74,cloud)*smoothstep(.02,.2,ray.y)*.65;
    col=mix(col,vec3(.93,.94,.9),amount);
  }
  pixel=vec4(col,1.0);
}`;

function program(gl, vs, fs, names, define) {
  const shaders = [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]].map(([type, source]) => {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, define ? source.replace('\n', `\n#define ${define}\n`) : source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const error = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader);
      throw new Error(`WebGL shader compilation failed: ${error}`);
    }
    return shader;
  });
  const handle = gl.createProgram();
  for (const shader of shaders) gl.attachShader(handle, shader);
  gl.linkProgram(handle);
  for (const shader of shaders) gl.deleteShader(shader);
  if (!gl.getProgramParameter(handle, gl.LINK_STATUS)) {
    const error = gl.getProgramInfoLog(handle);
    gl.deleteProgram(handle);
    throw new Error(`WebGL shader linking failed: ${error}`);
  }
  return { handle, uniforms: Object.fromEntries(names.map(name => [name, gl.getUniformLocation(handle, name)])) };
}

export class Geometry {
  vertices = [];

  triangle(a, b, c, color, material = 0) {
    const normal = normalize(cross(sub(b, a), sub(c, a)));
    for (const point of [a, b, c]) this.vertices.push(...point, ...normal, ...color, material);
  }

  quad(a, b, c, d, color, material = 0) {
    this.triangle(a, b, c, color, material);
    this.triangle(a, c, d, color, material);
  }
}

export class Graphics {
  meshes = new Set();
  completedFrames = 0;
  submittedFrames = 0;
  skippedFrames = 0;
  gpuMilliseconds = null;
  pending = [];
  query = null;
  quality = 'high';
  sun = normalize([-0.5, 0.83, -0.3]);

  constructor(canvas) {
    this.canvas = canvas;
    const gl = canvas.getContext('webgl2', { alpha: false, antialias: true, powerPreference: 'high-performance' });
    if (!gl) throw new Error('WebGL2 is required. Enable hardware acceleration or use a WebGL2-capable browser.');
    this.gl = gl;
    const uniforms = ['model', 'viewProjection', 'lightProjection', 'eye', 'sun', 'shadowMap', 'shadowTexel',
      'softShadows', 'opacity', 'fogDistance', 'worldTime', 'logDepthScale'];
    this.scene = program(gl, vertex, fragment, uniforms);
    this.flightScene = program(gl, vertex, fragment, uniforms, 'FLIGHT_DEPTH');
    this.depth = program(gl, shadowVertex, shadowFragment, ['model', 'lightProjection']);
    this.sky = program(gl, skyVertex, skyFragment, ['forward', 'right', 'up', 'sun', 'aspect', 'lens', 'time']);
    this.emptyVao = gl.createVertexArray();
    this.timer = gl.getExtension('EXT_disjoint_timer_query_webgl2');
    const debug = gl.getExtension('WEBGL_debug_renderer_info');
    this.device = debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER);
    this.createShadow(2048);
  }

  createShadow(size) {
    const gl = this.gl;
    if (this.shadowTexture) gl.deleteTexture(this.shadowTexture);
    if (this.shadowBuffer) gl.deleteFramebuffer(this.shadowBuffer);
    this.shadowSize = Math.min(size, gl.getParameter(gl.MAX_TEXTURE_SIZE));
    this.shadowTexture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, this.shadowTexture);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.DEPTH_COMPONENT24, this.shadowSize, this.shadowSize,
      0, gl.DEPTH_COMPONENT, gl.UNSIGNED_INT, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    this.shadowBuffer = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.shadowBuffer);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, this.shadowTexture, 0);
    gl.drawBuffers([gl.NONE]);
    gl.readBuffer(gl.NONE);
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) {
      throw new Error('The GPU could not create the shadow framebuffer.');
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  }

  setQuality(quality) {
    if (quality !== 'high' && quality !== 'balanced') throw new Error(`Unknown graphics quality: ${quality}`);
    this.quality = quality;
    this.createShadow(quality === 'high' ? 2048 : 1024);
  }

  get pendingFrames() {
    return this.pending.length;
  }

  mesh(geometry) {
    const gl = this.gl, vao = gl.createVertexArray(), buffer = gl.createBuffer();
    gl.bindVertexArray(vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(geometry.vertices), gl.STATIC_DRAW);
    for (const [location, count, offset] of [[0, 3, 0], [1, 3, 12], [2, 3, 24], [3, 1, 36]]) {
      gl.enableVertexAttribArray(location);
      gl.vertexAttribPointer(location, count, gl.FLOAT, false, 40, offset);
    }
    const mesh = { vao, buffer, count: geometry.vertices.length / 10 };
    this.meshes.add(mesh);
    return mesh;
  }

  releaseMesh(mesh) {
    if (!this.meshes.delete(mesh)) return;
    this.gl.deleteBuffer(mesh.buffer);
    this.gl.deleteVertexArray(mesh.vao);
  }

  receipt() {
    const gl = this.gl;
    if (gl.isContextLost()) throw new Error('The WebGL context was lost. Reload to recreate GPU resources.');
    while (this.pending.length) {
      const status = gl.clientWaitSync(this.pending[0], 0, 0);
      if (status === gl.WAIT_FAILED) {
        throw new Error(gl.isContextLost()
          ? 'The WebGL context was lost. Reload to recreate GPU resources.'
          : 'The GPU stopped completing rendering work.');
      }
      if (status === gl.TIMEOUT_EXPIRED) break;
      if (status !== gl.ALREADY_SIGNALED && status !== gl.CONDITION_SATISFIED) {
        throw new Error('The GPU returned an invalid rendering completion status.');
      }
      gl.deleteSync(this.pending.shift());
      this.completedFrames++;
    }
    if (this.query && gl.getQueryParameter(this.query, gl.QUERY_RESULT_AVAILABLE)) {
      if (!gl.getParameter(this.timer.GPU_DISJOINT_EXT)) {
        this.gpuMilliseconds = gl.getQueryParameter(this.query, gl.QUERY_RESULT) / 1e6;
      }
      gl.deleteQuery(this.query);
      this.query = null;
    }
    // Keep at most two submissions in flight. One delayed fence receipt must
    // not cost a whole display frame, but a slow GPU must not grow a stale queue.
    return this.pending.length < 2;
  }

  render(draws, camera, seconds) {
    if (!this.receipt()) {
      this.skippedFrames++;
      return false;
    }
    const gl = this.gl, rect = this.canvas.getBoundingClientRect();
    const dpr = Math.min(globalThis.devicePixelRatio || 1, this.quality === 'high' ? 1.75 : 1);
    const width = Math.max(1, Math.round(rect.width * dpr)), height = Math.max(1, Math.round(rect.height * dpr));
    if (this.canvas.width !== width || this.canvas.height !== height) {
      this.canvas.width = width;
      this.canvas.height = height;
    }
    if (this.timer && !this.query) {
      this.query = gl.createQuery();
      gl.beginQuery(this.timer.TIME_ELAPSED_EXT, this.query);
      this.activeQuery = true;
    }
    const center = camera.target.map(value => Math.round(value * 8) / 8);
    const lightEye = center.map((v, i) => v + this.sun[i] * 260);
    const light = multiply(orthographic(210, 1, 650), lookAt(lightEye, center));
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(true);
    gl.disable(gl.BLEND);
    gl.disable(gl.CULL_FACE);
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.shadowBuffer);
    gl.viewport(0, 0, this.shadowSize, this.shadowSize);
    gl.clear(gl.DEPTH_BUFFER_BIT);
    gl.useProgram(this.depth.handle);
    gl.uniformMatrix4fv(this.depth.uniforms.lightProjection, false, light);
    for (const draw of draws) {
      if (draw.shadow === false) continue;
      gl.uniformMatrix4fv(this.depth.uniforms.model, false, draw.model ?? identity());
      gl.bindVertexArray(draw.mesh.vao);
      gl.drawArrays(gl.TRIANGLES, 0, draw.mesh.count);
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, width, height);
    gl.clear(gl.DEPTH_BUFFER_BIT);
    gl.disable(gl.DEPTH_TEST);
    gl.depthMask(false);
    const view = lookAt(camera.eye, camera.target, camera.up);
    const forward = [view[2], view[6], view[10]];
    const right = [view[0], view[4], view[8]];
    const up = [view[1], view[5], view[9]];
    gl.useProgram(this.sky.handle);
    gl.bindVertexArray(this.emptyVao);
    for (const [name, value] of Object.entries({ forward, right, up, sun: this.sun })) {
      gl.uniform3fv(this.sky.uniforms[name], value);
    }
    gl.uniform1f(this.sky.uniforms.aspect, width / height);
    gl.uniform1f(this.sky.uniforms.lens, Math.tan(camera.fov / 2));
    gl.uniform1f(this.sky.uniforms.time, seconds);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(true);
    // Keep racing's early-depth path; flight needs precision across its much larger visible world.
    const scene = camera.logarithmicDepth ? this.flightScene : this.scene;
    gl.useProgram(scene.handle);
    const u = scene.uniforms, far = camera.far ?? 2100;
    gl.uniformMatrix4fv(u.viewProjection, false, multiply(
      perspective(camera.fov, width / height, 0.12, far), view));
    gl.uniform1f(u.logDepthScale, 1 / Math.log2(1 + far));
    gl.uniformMatrix4fv(u.lightProjection, false, light);
    gl.uniform3fv(u.eye, camera.eye);
    gl.uniform3fv(u.sun, this.sun);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.shadowTexture);
    gl.uniform1i(u.shadowMap, 0);
    gl.uniform1f(u.shadowTexel, 1 / this.shadowSize);
    gl.uniform1i(u.softShadows, this.quality === 'high' ? 1 : 0);
    gl.uniform1f(u.fogDistance, camera.fogDistance ?? 920);
    gl.uniform1f(u.worldTime, seconds);
    gl.uniform1f(u.opacity, 1);
    for (const transparent of [false, true]) {
      if (transparent) {
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
        gl.depthMask(false);
      }
      for (const draw of draws) {
        const opacity = draw.opacity ?? 1;
        if ((opacity < 1) !== transparent) continue;
        gl.uniform1f(u.opacity, opacity);
        gl.uniformMatrix4fv(u.model, false, draw.model ?? identity());
        gl.bindVertexArray(draw.mesh.vao);
        gl.drawArrays(gl.TRIANGLES, 0, draw.mesh.count);
      }
    }
    gl.depthMask(true);
    gl.disable(gl.BLEND);
    gl.uniform1f(u.opacity, 1);
    if (this.activeQuery) {
      gl.endQuery(this.timer.TIME_ELAPSED_EXT);
      this.activeQuery = false;
    }
    const fence = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
    if (!fence) throw new Error(gl.isContextLost()
      ? 'The WebGL context was lost. Reload to recreate GPU resources.'
      : 'The GPU could not track rendering completion.');
    this.pending.push(fence);
    gl.flush();
    this.submittedFrames++;
    return true;
  }

  dispose() {
    const gl = this.gl;
    for (const fence of this.pending) gl.deleteSync(fence);
    this.pending.length = 0;
    if (this.query) gl.deleteQuery(this.query);
    this.query = null;
    for (const mesh of this.meshes) this.releaseMesh(mesh);
    for (const p of [this.scene, this.flightScene, this.depth, this.sky]) gl.deleteProgram(p.handle);
    gl.deleteTexture(this.shadowTexture);
    gl.deleteFramebuffer(this.shadowBuffer);
    gl.deleteVertexArray(this.emptyVao);
  }
}
