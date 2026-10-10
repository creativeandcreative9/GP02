// frames.json を読み込み、2つの方式を左右に並べて1コマずつPNGに描く。
// 使い方: node render.js <frames.json> <出力フォルダ>
const fs = require('fs');
const path = require('path');

function loadPlaywright() {
  try {
    return require('playwright');
  } catch {
    // クラウド環境ではグローバルに入っている
    return require('/opt/node-tools/node_modules/playwright');
  }
}

const [input = 'out/frames.json', outDir = 'out/frames'] = process.argv.slice(2);
const data = JSON.parse(fs.readFileSync(input, 'utf8'));
const WIDTH = 1280;
const HEIGHT = 640;

// ブラウザ内で実行する描画処理
function draw({ data, index, width, height }) {
  const canvas = document.getElementById('c');
  const ctx = canvas.getContext('2d');
  const frame = data.frames[index];
  const panelWidth = width / 2;

  ctx.fillStyle = '#1d2027';
  ctx.fillRect(0, 0, width, height);

  // 少し右斜め上から見る
  const yaw = -0.6, pitch = 0.25, distance = 1.45, focal = 520;
  const center = [0.3, 0.3, 0.08];
  const light = normalize([0.4, 0.6, 0.7]);

  function normalize(v) {
    const l = Math.hypot(v[0], v[1], v[2]) || 1;
    return [v[0] / l, v[1] / l, v[2] / l];
  }

  function toCamera(p) {
    const x = p[0] - center[0], y = p[1] - center[1], z = p[2] - center[2];
    const x1 = Math.cos(yaw) * x - Math.sin(yaw) * z;
    const z1 = Math.sin(yaw) * x + Math.cos(yaw) * z;
    const y2 = Math.cos(pitch) * y - Math.sin(pitch) * z1;
    const z2 = Math.sin(pitch) * y + Math.cos(pitch) * z1;
    return [x1, y2, z2 + distance];
  }

  function panel(state, offsetX, title) {
    const pos = state.positions;
    const tris = data.triangles;
    const cam = [];
    for (let i = 0; i < pos.length; i += 3) cam.push(toCamera([pos[i], pos[i + 1], pos[i + 2]]));

    const order = [];
    for (let t = 0; t < tris.length; t += 3) {
      const depth = cam[tris[t]][2] + cam[tris[t + 1]][2] + cam[tris[t + 2]][2];
      order.push([depth, t]);
    }
    order.sort((a, b) => b[0] - a[0]);

    const project = (c) => [offsetX + panelWidth / 2 + (c[0] * focal) / c[2], height * 0.55 - (c[1] * focal) / c[2]];

    for (const [, t] of order) {
      const a = tris[t], b = tris[t + 1], c = tris[t + 2];
      const pa = [pos[3 * a], pos[3 * a + 1], pos[3 * a + 2]];
      const pb = [pos[3 * b], pos[3 * b + 1], pos[3 * b + 2]];
      const pc = [pos[3 * c], pos[3 * c + 1], pos[3 * c + 2]];
      const u = [pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2]];
      const v = [pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2]];
      const n = normalize([u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]]);
      const shade = 0.35 + 0.65 * Math.abs(n[0] * light[0] + n[1] * light[1] + n[2] * light[2]);

      // 伸びが15%で真っ赤になる
      const s = Math.min(1, Math.max(0, state.strain[t / 3] / 0.15));
      const base = [178, 196, 222], red = [235, 64, 64];
      const color = base.map((v0, k) => Math.round((v0 + (red[k] - v0) * s) * shade));

      const qa = project(cam[a]), qb = project(cam[b]), qc = project(cam[c]);
      ctx.beginPath();
      ctx.moveTo(qa[0], qa[1]);
      ctx.lineTo(qb[0], qb[1]);
      ctx.lineTo(qc[0], qc[1]);
      ctx.closePath();
      ctx.fillStyle = `rgb(${color[0]},${color[1]},${color[2]})`;
      ctx.strokeStyle = ctx.fillStyle;
      ctx.lineWidth = 0.6;
      ctx.fill();
      ctx.stroke();
    }

    // つまんだ点
    const g = project(cam[data.grabbed]);
    ctx.beginPath();
    ctx.arc(g[0], g[1], 7, 0, Math.PI * 2);
    ctx.fillStyle = '#ffd34d';
    ctx.fill();

    let maxStrain = 0;
    for (const v0 of state.strain) maxStrain = Math.max(maxStrain, v0);
    ctx.fillStyle = '#ffffff';
    ctx.font = '28px "WenQuanYi Zen Hei", sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText(title, offsetX + panelWidth / 2, 48);
    ctx.font = '22px "WenQuanYi Zen Hei", sans-serif';
    ctx.fillStyle = maxStrain > 0.05 ? '#ff8a8a' : '#9be39b';
    ctx.fillText(`最大の伸び ${(maxStrain * 100).toFixed(1)}%`, offsetX + panelWidth / 2, height - 28);
  }

  panel(frame.falloff, 0, '範囲指定で動かす');
  panel(frame.particle, panelWidth, '粒子で動かす');

  ctx.strokeStyle = '#3a3f4a';
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.moveTo(panelWidth, 70);
  ctx.lineTo(panelWidth, height - 70);
  ctx.stroke();
}

(async () => {
  fs.mkdirSync(outDir, { recursive: true });
  const { chromium } = loadPlaywright();
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: WIDTH, height: HEIGHT } });
  await page.setContent(`<html><body style="margin:0"><canvas id="c" width="${WIDTH}" height="${HEIGHT}"></canvas></body></html>`);
  await page.evaluate((d) => { window.__data = d; }, data);

  for (let i = 0; i < data.frames.length; i++) {
    await page.evaluate(
      ({ fn, index, width, height }) => new Function('return ' + fn)()({ data: window.__data, index, width, height }),
      { fn: draw.toString(), index: i, width: WIDTH, height: HEIGHT },
    );
    const file = path.join(outDir, `${String(i).padStart(4, '0')}.png`);
    await page.locator('#c').screenshot({ path: file });
  }

  await browser.close();
  console.log(`描画: ${data.frames.length} コマ → ${outDir}`);
})();
