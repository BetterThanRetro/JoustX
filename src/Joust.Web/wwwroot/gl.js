// The picture in WebGL2: the classic frame, or the HD look drawn the way the desktop app's
// Shaders/Hd.fx does it (and Joust.Render.HdRenderer, the CPU reference): the light and its
// blur at the field's size, Scale4x as two Scale2x passes, one compose pass, and the particles.
// Internal targets keep image rows in order (row 0 = the top of the picture); only the final
// pass to the canvas turns it the right way up.

const W = 1168, H = 960, WIDE = 1558, LW = 292, LH = 240, LAVA_TOP = 196;

const VS = `#version 300 es
void main() {
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;

const HEAD = `#version 300 es
precision highp float;
precision highp int;
precision highp usampler2D;
`;

const SCALE2X = HEAD + `
uniform usampler2D src;
out uvec4 o;
uint f(ivec2 p, ivec2 size) { return texelFetch(src, clamp(p, ivec2(0), size - 1), 0).r; }
void main() {
    ivec2 size = textureSize(src, 0);
    ivec2 op = ivec2(gl_FragCoord.xy), s = op / 2, q = op - s * 2;
    uint p = f(s, size);
    uint a = f(s + ivec2(0, -1), size), b = f(s + ivec2(1, 0), size);
    uint c = f(s + ivec2(-1, 0), size), d = f(s + ivec2(0, 1), size);
    uint r;
    if (q.x == 0 && q.y == 0) r = (c == a && c != d && a != b) ? a : p;
    else if (q.x == 1 && q.y == 0) r = (a == b && a != c && b != d) ? b : p;
    else if (q.x == 0 && q.y == 1) r = (d == c && d != b && c != a) ? c : p;
    else r = (b == d && b != a && d != c) ? d : p;
    o = uvec4(r, 0u, 0u, 0u);
}`;

const LIGHT = HEAD + `
uniform usampler2D low;
uniform sampler2D palette;
uniform bool heat;
out vec4 o;
void main() {
    uint i = texelFetch(low, ivec2(gl_FragCoord.xy), 0).r;
    vec4 c = texelFetch(palette, ivec2(int(i), 0), 0);
    if (heat) o = c.a > 0.5 ? vec4(1.0, 0.38, 0.08, 1.0) : vec4(0.0, 0.0, 0.0, 1.0);
    else if (i == 0u) o = vec4(0.0, 0.0, 0.0, 1.0);
    else {
        float bright = max(0.0, dot(c.rgb, vec3(0.3, 0.59, 0.11)) - 0.35) * 1.6;
        o = vec4(c.rgb * bright / 2.0, 1.0);      // bloom at half strength, so 8 bits hold it
    }
}`;

const BOX = HEAD + `
uniform sampler2D src;
uniform ivec2 axis;
uniform int radius;
out vec4 o;
void main() {
    ivec2 size = textureSize(src, 0), p = ivec2(gl_FragCoord.xy);
    vec3 sum = vec3(0.0);
    for (int k = -14; k <= 14; k++)
        if (k >= -radius && k <= radius)
            sum += texelFetch(src, clamp(p + axis * k, ivec2(0), size - 1), 0).rgb;
    o = vec4(sum / float(2 * radius + 1), 1.0);
}`;

const COMPOSE = HEAD + `
const int W = ${W}, H = ${H}, WIDE = ${WIDE}, LW = ${LW}, LH = ${LH}, LAVA_TOP = ${LAVA_TOP};
uniform usampler2D high;
uniform sampler2D palette, glow, heat, backdrop;
uniform float outW, time, flicker, arena, bloomAmount, heatAmount, bevelAmount;
uniform bool useBackdrop, shimmer, crt;
out vec4 o;

uint indexAt(ivec2 p) {
    if (p.x < 0 || p.y < 0 || p.x >= W || p.y >= H) return 0u;
    return texelFetch(high, p, 0).r;
}
vec4 pal(uint i) { return texelFetch(palette, ivec2(int(i), 0), 0); }
vec3 cavern(ivec2 p) { return texelFetch(backdrop, p, 0).rgb / 6.0; }
float channel(float v) {
    if (v > 0.8) v = 0.8 + 0.2 * (1.0 - exp(-(v - 0.8) * 5.0));
    return clamp(v, 0.0, 1.0);
}
float scan(int y) { int r = y & 3; return r == 3 ? 0.62 : r == 0 ? 0.9 : 1.0; }

void main() {
    int X = int(gl_FragCoord.x), y = int(gl_FragCoord.y);
    int ox = (int(outW) - W) / 2, x = X - ox;
    vec3 rgb;
    if (x < 0 || x >= W) {
        // The 16:9 side panels: plain cavern, darker towards the screen edge.
        float s = x < 0 ? float(X) : outW - 1.0 - float(X);
        rgb = cavern(ivec2(X, y)) * (0.35 + 0.65 * (s / float(ox))) * arena;
        if (crt) rgb *= scan(y);
        o = vec4(channel(rgb.r), channel(rgb.g), channel(rgb.b), 1.0);
        return;
    }
    uint index = indexAt(ivec2(x, y));
    if (shimmer && y / 4 >= LAVA_TOP && pal(index).a > 0.5) {
        int shift = int(floor(sin(float(y) * 0.11 + time * 0.16) * 2.2 + 0.5));    // heat haze
        uint other = indexAt(ivec2(clamp(x + shift, 0, W - 1), y));
        if (pal(other).a > 0.5) index = other;
    }
    float v = float(y) / float(H);
    float lavaLight = pow(clamp((v - 0.55) / 0.45, 0.0, 1.0), 2.2) * flicker;
    if (index == 0u) {
        if (useBackdrop) {
            vec3 b = cavern(ivec2((WIDE - W) / 2 + x, y));
            rgb = vec3((b.r * (1.0 + lavaLight * 2.2) + lavaLight * 0.05) * arena,
                       (b.g * (1.0 + lavaLight * 0.9) + lavaLight * 0.012) * arena,
                       b.b * arena);
        } else rgb = vec3(0.0);
    } else {
        rgb = pal(index).rgb;
        if (bevelAmount > 0.0) {
            float light = 1.0;
            if (indexAt(ivec2(x - 2, y - 2)) == 0u) light += 0.28 * bevelAmount;         // lit from the top left
            else if (indexAt(ivec2(x + 2, y + 2)) == 0u) light -= 0.32 * bevelAmount;    // shadowed bottom right
            if (indexAt(ivec2(x + 1, y)) == 0u || indexAt(ivec2(x, y + 1)) == 0u) light -= 0.08 * bevelAmount;
            rgb *= light;
        }
    }
    vec2 lowUv = (vec2(x, y) + 0.5) / 4.0 / vec2(LW, LH);
    rgb += texture(glow, lowUv).rgb * 2.0 * bloomAmount
         + texture(heat, lowUv).rgb * heatAmount * flicker * (index == 0u ? 1.0 : 0.35);
    if (crt) {
        vec2 vxy = (vec2(x, y) - vec2(W, H) / 2.0) / (vec2(W, H) / 2.0);
        rgb *= scan(y) * (1.0 - 0.22 * dot(vxy, vxy));
    }
    o = vec4(channel(rgb.r), channel(rgb.g), channel(rgb.b), 1.0);
}`;

// Particles: one point each, its pixels weighted exp(-d²/size²) as the CPU's soft dots.
const DOT_VS = `#version 300 es
in vec2 pos;
in vec4 colour;   // r, g, b, brightness
in float size;
uniform vec2 target;
out vec4 vColour;
out float vSize, vSide;
void main() {
    vSide = 2.0 * ceil(size * 1.8) + 1.0;
    gl_PointSize = vSide;
    gl_Position = vec4(pos / target * 2.0 - 1.0, 0.0, 1.0);
    vColour = colour;
    vSize = size;
}`;

const DOT_FS = HEAD + `
in vec4 vColour;
in float vSize, vSide;
out vec4 o;
void main() {
    vec2 d = floor((gl_PointCoord - 0.5) * vSide + 0.5);
    float w = exp(-dot(d, d) / (vSize * vSize)) * vColour.a;
    if (w < 0.01) discard;
    o = vec4(vColour.rgb * w, 1.0);
}`;

// The finished picture onto the canvas, letterboxed and the right way up.
const PRESENT = HEAD + `
uniform sampler2D img;
uniform vec4 view;   // x, y, width, height on the canvas
uniform vec4 src;    // left, top, right, bottom in texture coordinates (top = row 0)
out vec4 o;
void main() {
    vec2 f = (gl_FragCoord.xy - view.xy) / view.zw;
    if (f.x < 0.0 || f.y < 0.0 || f.x > 1.0 || f.y > 1.0) { o = vec4(0.0, 0.0, 0.0, 1.0); return; }   // the bars
    o = texture(img, vec2(mix(src.x, src.z, f.x), mix(src.y, src.w, 1.0 - f.y)));
}`;

export class Renderer {
    constructor(canvas) {
        const gl = canvas.getContext('webgl2', { antialias: false, alpha: false, premultipliedAlpha: false });
        if (!gl) throw new Error('This browser has no WebGL2.');
        this.gl = gl;
        this.canvas = canvas;
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
        this.p = {
            scale2x: this.program(VS, SCALE2X), light: this.program(VS, LIGHT), box: this.program(VS, BOX),
            compose: this.program(VS, COMPOSE), dot: this.program(DOT_VS, DOT_FS), present: this.program(VS, PRESENT),
        };
        const U8 = [gl.R8UI, gl.RED_INTEGER, gl.UNSIGNED_BYTE], RGBA = [gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE];
        this.low = this.texture(LW, LH, U8, gl.NEAREST);
        this.palette = this.texture(16, 1, RGBA, gl.NEAREST);
        this.backdrop = this.texture(WIDE, H, RGBA, gl.NEAREST);
        this.classic = this.texture(304, 256, RGBA, gl.NEAREST);
        this.mid = this.target(LW * 2, LH * 2, U8, gl.NEAREST);
        this.high = this.target(W, H, U8, gl.NEAREST);
        this.glow = this.target(LW, LH, RGBA, gl.LINEAR);
        this.glow2 = this.target(LW, LH, RGBA, gl.LINEAR);
        this.heat = this.target(LW, LH, RGBA, gl.LINEAR);
        this.heat2 = this.target(LW, LH, RGBA, gl.LINEAR);
        this.narrow = this.target(W, H, RGBA, gl.LINEAR);
        this.wide = this.target(WIDE, H, RGBA, gl.LINEAR);
        this.dots = gl.createBuffer();
        this.vao = gl.createVertexArray();
    }

    program(vs, fs) {
        const gl = this.gl;
        const p = gl.createProgram();
        for (const [type, text] of [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]]) {
            const s = gl.createShader(type);
            gl.shaderSource(s, text);
            gl.compileShader(s);
            if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
            gl.attachShader(p, s);
        }
        gl.linkProgram(p);
        if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
        const u = {};
        const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
        for (let i = 0; i < n; i++) {
            const name = gl.getActiveUniform(p, i).name;
            u[name] = gl.getUniformLocation(p, name);
        }
        return { p, u };
    }

    texture(w, h, [internal, format, type], filter) {
        const gl = this.gl;
        const t = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, t);
        gl.texStorage2D(gl.TEXTURE_2D, 1, internal, w, h);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, filter);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, filter);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        return { t, w, h, format, type };
    }

    target(w, h, kind, filter) {
        const gl = this.gl;
        const tex = this.texture(w, h, kind, filter);
        tex.fb = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, tex.fb);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex.t, 0);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        return tex;
    }

    upload(tex, data) {
        const gl = this.gl;
        gl.bindTexture(gl.TEXTURE_2D, tex.t);
        gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, tex.w, tex.h, tex.format, tex.type, data);
    }

    setBackdrop(rgba) { this.upload(this.backdrop, rgba); }

    /** Draws a full-target pass of program <prog> into <dst>, with textures bound in order. */
    pass(prog, dst, textures, uniforms) {
        const gl = this.gl;
        gl.useProgram(prog.p);
        textures.forEach(([name, tex], unit) => {
            gl.activeTexture(gl.TEXTURE0 + unit);
            gl.bindTexture(gl.TEXTURE_2D, tex.t);
            gl.uniform1i(prog.u[name], unit);
        });
        uniforms?.(prog.u);
        gl.bindFramebuffer(gl.FRAMEBUFFER, dst.fb);
        gl.viewport(0, 0, dst.w, dst.h);
        gl.bindVertexArray(this.vao);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    /** The HD frame from the field's indices and the frame's state (WebGame.HdState). */
    hd(low, state, o) {
        const gl = this.gl;
        this.upload(this.low, low);
        const pal = new Uint8Array(64);
        for (let c = 0; c < 16; c++)
            for (let k = 0; k < 4; k++) pal[4 * c + k] = Math.round(Math.min(1, Math.max(0, state[3 + 4 * c + k])) * 255);
        this.upload(this.palette, pal);
        const [time, flicker, arena] = state;

        const light = (heatOn) => (u) => gl.uniform1i(u.heat, heatOn ? 1 : 0);
        const box = (x, y, r) => (u) => { gl.uniform2i(u.axis, x, y); gl.uniform1i(u.radius, r); };
        this.pass(this.p.light, this.glow, [['low', this.low], ['palette', this.palette]], light(false));
        for (let i = 0; i < 2; i++) {
            this.pass(this.p.box, this.glow2, [['src', this.glow]], box(1, 0, 3));
            this.pass(this.p.box, this.glow, [['src', this.glow2]], box(0, 1, 3));
        }
        this.pass(this.p.light, this.heat, [['low', this.low], ['palette', this.palette]], light(true));
        for (let i = 0; i < 3; i++) {
            this.pass(this.p.box, this.heat2, [['src', this.heat]], box(1, 0, 14));
            this.pass(this.p.box, this.heat, [['src', this.heat2]], box(0, 1, 14));
        }
        this.pass(this.p.scale2x, this.mid, [['src', this.low]]);
        this.pass(this.p.scale2x, this.high, [['src', this.mid]]);

        const out = o.widescreen ? this.wide : this.narrow;
        this.pass(this.p.compose, out,
            [['high', this.high], ['palette', this.palette], ['glow', this.glow], ['heat', this.heat], ['backdrop', this.backdrop]],
            (u) => {
                gl.uniform1f(u.outW, out.w);
                gl.uniform1f(u.time, time);
                gl.uniform1f(u.flicker, flicker);
                gl.uniform1f(u.arena, arena);
                gl.uniform1f(u.bloomAmount, 0.55);
                gl.uniform1f(u.heatAmount, 0.9);
                gl.uniform1f(u.bevelAmount, 1);
                gl.uniform1i(u.useBackdrop, 1);
                gl.uniform1i(u.shimmer, 1);
                gl.uniform1i(u.crt, o.crt ? 1 : 0);
            });
        this.particles(state, out);
        this.present(out, [0, 0, 1, 1], o.widescreen ? 16 / 9 : 4 / 3, gl.LINEAR);
    }

    particles(state, out) {
        const gl = this.gl;
        const n = state[67];
        if (!n) return;
        const ox = (out.w - W) / 2;
        const data = new Float32Array(n * 7);
        for (let i = 0, s = 68; i < n; i++, s += 7) {
            data.set([Math.trunc(state[s]) + ox + 0.5, Math.trunc(state[s + 1]) + 0.5,
                      state[s + 2], state[s + 3], state[s + 4], state[s + 6], state[s + 5]], i * 7);
        }
        const prog = this.p.dot;
        gl.useProgram(prog.p);
        gl.bindVertexArray(null);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.dots);
        gl.bufferData(gl.ARRAY_BUFFER, data, gl.STREAM_DRAW);
        const at = (name) => gl.getAttribLocation(prog.p, name);
        gl.enableVertexAttribArray(at('pos'));
        gl.vertexAttribPointer(at('pos'), 2, gl.FLOAT, false, 28, 0);
        gl.enableVertexAttribArray(at('colour'));
        gl.vertexAttribPointer(at('colour'), 4, gl.FLOAT, false, 28, 8);
        gl.enableVertexAttribArray(at('size'));
        gl.vertexAttribPointer(at('size'), 1, gl.FLOAT, false, 28, 24);
        gl.uniform2f(prog.u.target, out.w, out.h);
        gl.bindFramebuffer(gl.FRAMEBUFFER, out.fb);
        gl.viewport(0, 0, out.w, out.h);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.ONE, gl.ONE);                            // added, as the CPU's soft dots
        gl.drawArrays(gl.POINTS, 0, n);
        gl.disable(gl.BLEND);
        for (const name of ['pos', 'colour', 'size']) gl.disableVertexAttribArray(at(name));
    }

    /** The original picture: the 304x256 frame buffer as RGBA, its visible 292x240 shown. */
    classicFrame(rgba) {
        this.upload(this.classic, rgba);
        this.present(this.classic, [6 / 304, 7 / 256, 298 / 304, 247 / 256], 4 / 3, this.gl.NEAREST);
    }

    present(tex, src, aspect, filter) {
        const gl = this.gl, c = this.canvas;
        const dpr = window.devicePixelRatio || 1;
        const cw = Math.round(c.clientWidth * dpr), ch = Math.round(c.clientHeight * dpr);
        if (c.width !== cw || c.height !== ch) { c.width = cw; c.height = ch; }
        let w = cw, h = Math.round(cw / aspect);
        if (h > ch) { h = ch; w = Math.round(ch * aspect); }
        const x = Math.floor((cw - w) / 2), y = Math.floor((ch - h) / 2);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.viewport(0, 0, cw, ch);
        gl.clearColor(0, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.bindTexture(gl.TEXTURE_2D, tex.t);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, filter);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, filter);
        this.pass(this.p.present, { fb: null, w: cw, h: ch }, [['img', tex]], (u) => {
            gl.uniform4f(u.view, x, y, w, h);
            gl.uniform4f(u.src, ...src);
        });
    }
}
