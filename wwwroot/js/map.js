window.tvdiMap = (() => {
  // ================== Cấu hình ==================
  const HCM = [[10.35, 106.30], [11.25, 107.10]];
  const MAX_BOUNDS = [[10.20, 106.20], [11.70, 107.70]];

  const VMIN = 0, VMAX = 1;
  const PALETTE_HEX = ['#066b00', '#0bc400', '#0ce100', '#00ff00',
                       '#ffff00', '#ffcc00', '#ff6600', '#ff0000'];

  const WARDS_URL = 'data/tphcm-wards.json';
  const OUTLINE_URL = 'data/tphcm-outline.json';
  const WARD_MIN_ZOOM = 10;        // khi tắt chế độ giá trị: chỉ hiện viền từ zoom này
  const LABEL_MIN_ZOOM = 12;       // chỉ hiện nhãn số từ zoom này
  const EQ_TOLERANCE = 0.005;      // "=" nghĩa là chênh lệch dưới 0.005 (làm tròn 2 số lẻ)
  const WARD_STYLE = { color: '#333333', weight: 0.8, opacity: 0.75 };
  const OUTLINE_STYLE = { color: '#000000', weight: 2.5, opacity: 0.9, fill: false };

  // ================== Trạng thái ==================
  let map = null, layer = null, rasterPreview = null, popup = null;
  let wardLayer = null, outlineLayer = null;
  let wardFeatures = [];
  let wardStats = new Map();      // code → {mean, min, max, validPixels, totalPixels, estimated, ...}
  let wardMode = true;            // tô màu theo phường
  let rasterVisible = true;
  let filter = null;              // {op, a, b} hoặc null

  // ================== Màu sắc ==================
  const isNoData = v => v == null || isNaN(v) || v < -1000;
  const hexToRgb = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
  const PALETTE = PALETTE_HEX.map(hexToRgb);
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const fmt = v => (v == null ? '–' : Number(v).toFixed(3));

  const colorRgb = v => {
    if (isNoData(v)) return null;
    let t = (v - VMIN) / (VMAX - VMIN);
    t = Math.min(1, Math.max(0, t));
    const p = t * (PALETTE.length - 1);
    const i = Math.min(Math.floor(p), PALETTE.length - 2);
    const f = p - i;
    const a = PALETTE[i], b = PALETTE[i + 1];
    return [0, 1, 2].map(k => Math.round(a[k] + (b[k] - a[k]) * f));
  };
  const colorHex = v => {
    const c = colorRgb(v);
    return c ? '#' + c.map(x => x.toString(16).padStart(2, '0')).join('') : null;
  };

  // ================== Vẽ raster ==================
  function buildCanvas(gr) {
    const w = gr.width, h = gr.height;
    const cv = document.createElement('canvas');
    cv.width = w; cv.height = h;
    const ctx = cv.getContext('2d');
    const img = ctx.createImageData(w, h);
    const mercator = lat => Math.log(Math.tan(Math.PI / 4 + lat * Math.PI / 360));
    const north = mercator(gr.north), south = mercator(gr.south);
    for (let y = 0; y < h; y++) {
      const mercatorY = north - (y + 0.5) * (north - south) / h;
      const latitude = (2 * Math.atan(Math.exp(mercatorY)) - Math.PI / 2) * 180 / Math.PI;
      const sourceY = Math.min(h - 1, Math.max(0, Math.floor((gr.north - latitude) / (gr.north - gr.south) * h)));
      for (let x = 0; x < w; x++) {
        const c = colorRgb(gr.values[sourceY * w + x]);
        const i = (y * w + x) * 4;
        if (!c) { img.data[i + 3] = 0; continue; }
        img.data[i] = c[0]; img.data[i + 1] = c[1]; img.data[i + 2] = c[2]; img.data[i + 3] = 255;
      }
    }
    ctx.putImageData(img, 0, 0);
    return cv;
  }

  function samplePoint(point) {
    const g = rasterPreview;
    if (!g) return 'không có dữ liệu';
    const x = Math.floor((point.lng - g.west) / (g.east - g.west) * g.width);
    const y = Math.floor((g.north - point.lat) / (g.north - g.south) * g.height);
    const value = x >= 0 && x < g.width && y >= 0 && y < g.height ? g.values[y * g.width + x] : NaN;
    return isNoData(value) ? 'không có dữ liệu' : value.toFixed(3) + (g.reduced ? ' (xấp xỉ)' : '');
  }

  // ================== Chú giải ==================
  function addLegend() {
    const legend = L.control({ position: 'bottomright' });
    legend.onAdd = () => {
      const d = L.DomUtil.create('div', 'tvdi-legend');
      d.innerHTML = `
        <b>TVDI</b>
        <div class="bar" style="background:linear-gradient(to right, ${PALETTE_HEX.join(',')})"></div>
        <div class="lbl"><span>${VMIN}</span><span>${(VMIN + VMAX) / 2}</span><span>${VMAX}</span></div>
        <div class="lbl note"><span>Ẩm</span><span>Khô</span></div>`;
      return d;
    };
    legend.addTo(map);
  }

  // ================== Bộ lọc ==================
  function matches(v) {
    if (!filter) return true;
    if (v == null) return false;
    switch (filter.op) {
      case 'gte': return v >= filter.a;
      case 'gt':  return v > filter.a;
      case 'eq':  return Math.abs(v - filter.a) < EQ_TOLERANCE;
      case 'lte': return v <= filter.a;
      case 'lt':  return v < filter.a;
      case 'between': return v >= Math.min(filter.a, filter.b) && v <= Math.max(filter.a, filter.b);
      default: return true;
    }
  }

  // ================== Phường/xã: kiểu vẽ, nhãn, popup ==================
  function wardStyle(code) {
    if (!wardMode) return { ...WARD_STYLE, fill: true, fillOpacity: 0 };   // chỉ viền (vẫn "fill" để bắt sự kiện)
    const v = wardStats.get(code)?.mean;
    if (v == null)                                                            // không có dữ liệu
      return { color: '#555555', weight: 0.8, opacity: 0.6, fill: true, fillColor: '#999999', fillOpacity: filter ? 0 : 0.25 };
    if (!matches(v))                                                          // không thỏa bộ lọc
      return { ...WARD_STYLE, fill: true, fillOpacity: 0 };
    return {                                                                  // có dữ liệu (và thỏa bộ lọc)
      color: filter ? '#000000' : '#333333', weight: filter ? 1.8 : 0.8, opacity: 0.9,
      fill: true, fillColor: colorHex(v), fillOpacity: 0.85
    };
  }

  function updateWardVisibility() {
    if (!map || !wardLayer) return;
    const show = wardMode || map.getZoom() >= WARD_MIN_ZOOM;
    if (show && !map.hasLayer(wardLayer)) wardLayer.addTo(map);
    if (!show && map.hasLayer(wardLayer)) map.removeLayer(wardLayer);
    if (outlineLayer && map.hasLayer(outlineLayer)) outlineLayer.bringToFront();
    map.getContainer().classList.toggle('hide-ward-labels', map.getZoom() < LABEL_MIN_ZOOM);
  }

  function restyleWards() {
    if (!wardLayer) return;
    wardLayer.eachLayer(l => {
      const code = l.feature.properties.code;
      l.setStyle(wardStyle(code));
      l.options.bubblingMouseEvents = !wardMode;       // chế độ phường: click dừng ở phường

      const v = wardStats.get(code)?.mean;
      const showLabel = wardMode && v != null && matches(v);
      if (showLabel) {
        if (l.getTooltip()) l.setTooltipContent(fmt(v));
        else l.bindTooltip(fmt(v), { permanent: true, direction: 'center', className: 'ward-label', interactive: false });
      } else if (l.getTooltip()) {
        l.unbindTooltip();
      }
    });
    updateWardVisibility();
  }

  function onWardClick(e) {
    if (!wardMode) return;
    const p = e.layer.feature.properties;
    const s = wardStats.get(p.code);
    let html = `<b>${esc(p.name)}</b><br>`;
    if (!s || s.mean == null) {
      html += 'TVDI: không có dữ liệu';
    } else {
      html += `TVDI trung bình: <b>${fmt(s.mean)}</b><br>` +
              `Thấp nhất – cao nhất: ${fmt(s.min)} – ${fmt(s.max)}<br>` +
              `Pixel hợp lệ: ${s.validPixels}/${s.totalPixels}`;
      if (s.estimated) html += '<br><i>(phường nhỏ hơn 1 pixel: lấy giá trị pixel chứa tâm phường)</i>';
    }
    if (rasterPreview) html += "<br>Điểm vừa bấm: " + samplePoint(e.latlng);
    popup.setLatLng(e.latlng).setContent(html).openOn(map);
  }

  // ================== Tải ranh giới ==================
  async function fetchJson(url) {
    const res = await fetch(url, { credentials: 'same-origin' });
    if (!res.ok) throw new Error(`HTTP ${res.status} khi tải ${url}`);
    return res.json();
  }

  async function loadBoundaries() {
    const thisMap = map;
    try {
      const [wards, outline] = await Promise.all([fetchJson(WARDS_URL), fetchJson(OUTLINE_URL)]);
      if (map !== thisMap) return;
      wardFeatures = wards.features || [];

      if (!map.getPane('boundaries')) map.createPane('boundaries').style.zIndex = 450;
      const renderer = L.canvas({ pane: 'boundaries' });

      wardLayer = L.geoJSON(wards, {
        pane: 'boundaries', renderer, interactive: true,
        style: f => wardStyle(f.properties.code)
      });
      wardLayer.on('click', onWardClick);

      outlineLayer = L.geoJSON(outline, {
        pane: 'boundaries', renderer, interactive: false, style: () => OUTLINE_STYLE
      }).addTo(map);

      map.attributionControl.addAttribution('Ranh giới: tinhthanhpho.com (CC BY 4.0)');
      map.on('zoomend', updateWardVisibility);
      restyleWards();
    } catch (err) {
      console.warn('[tvdiMap] Không tải được ranh giới:', err);
    }
  }

  // ================== API gọi từ Blazor ==================
  return {
    init(elOrId) {
      console.log('[tvdiMap] init được gọi', elOrId);
      if (typeof L === 'undefined') throw new Error('Leaflet chưa tải. Kiểm tra thẻ <script> trong App.razor.');
      const el = typeof elOrId === 'string' ? document.getElementById(elOrId) : elOrId;
      if (!el) throw new Error('Không tìm thấy phần tử bản đồ.');

      if (map) { map.remove(); map = null; }
      layer = null; rasterPreview = null; wardLayer = null; outlineLayer = null; wardFeatures = [];
      wardStats = new Map(); wardMode = true; rasterVisible = true; filter = null;

      map = L.map(el, { maxBounds: MAX_BOUNDS, minZoom: 9 }).fitBounds(HCM);
      L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
        { attribution: '© OpenStreetMap' }).addTo(map);

      popup = L.popup();
      map.on('click', e => {
        if (!rasterPreview) return;
        const val = samplePoint(e.latlng);
        popup.setLatLng(e.latlng).setContent(`TVDI: <b>${val}</b>`).openOn(map);
      });

      addLegend();
      loadBoundaries();
      setTimeout(() => map.invalidateSize(), 200);
    },

    async loadTif(url, opacity = 0.8) {
      if (!map) throw new Error('Bản đồ chưa khởi tạo.');
      const targetMap = map;
      if (layer) { map.removeLayer(layer); layer = null; }
      rasterPreview = null;
      const previewUrl = new URL(url, window.location.origin);
      previewUrl.searchParams.set('preview', 'true');
      const res = await fetch(previewUrl, { credentials: 'same-origin' });
      const type = res.headers.get('content-type') || '';
      if (!res.ok || !type.includes('application/json')) {
        const detail = type.includes('text/plain') ? (await res.text()).slice(0, 200) : '';
        throw new Error('Không tải được ảnh TVDI (HTTP ' + res.status + '). ' + detail);
      }

      const preview = await res.json();
      if (map !== targetMap) return;
      const bytes = Uint8Array.from(atob(preview.samples), value => value.charCodeAt(0));
      const view = new DataView(bytes.buffer);
      preview.values = Float32Array.from({ length: preview.width * preview.height }, (_, index) => view.getFloat32(index * 4, true));
      rasterPreview = preview;
      const bounds = [[preview.south, preview.west], [preview.north, preview.east]];
      layer = L.imageOverlay(buildCanvas(preview).toDataURL('image/png'), bounds,
        { opacity, interactive: false, className: 'tvdi-overlay' });
      if (rasterVisible) layer.addTo(map);
      map.fitBounds(bounds);
    },

    stats() {
      if (!rasterPreview) return null;
      const { mean, min, max } = rasterPreview;
      return { mean, min, max };
    },

    getWards() { return wardFeatures.map(f => ({ code: f.properties.code, name: f.properties.name })); },

    findWard(query) {
      if (!map || !wardFeatures.length) throw new Error('Ranh giới phường/xã chưa tải xong.');
      const q = String(query || '').trim().toLocaleLowerCase('vi');
      const feature = wardFeatures.find(f => String(f.properties.name).toLocaleLowerCase('vi') === q)
        || wardFeatures.find(f => String(f.properties.name).toLocaleLowerCase('vi').includes(q));
      if (!feature) { alert('Không tìm thấy phường/xã phù hợp.'); return; }
      const bounds = L.geoJSON(feature).getBounds();
      map.fitBounds(bounds, { maxZoom: 13, padding: [24, 24] });
      const target = wardLayer?.getLayers().find(l => l.feature.properties.code === feature.properties.code);
      if (target) target.fire('click', { latlng: bounds.getCenter() });
    },

    resetView() { if (map) map.fitBounds(HCM); },

    setOpacity(o) { if (layer) layer.setOpacity(o); },

    setRasterVisible(flag) {
      rasterVisible = !!flag;
      if (!map || !layer) return;
      if (rasterVisible) layer.addTo(map); else map.removeLayer(layer);
    },

    // ---- Giá trị TVDI theo phường/xã ----
    async loadWardStats(url) {
      const arr = await fetchJson(url);
      wardStats = new Map(arr.map(s => [s.code, {
        ...s, mean: s.mean == null ? null : Math.round(s.mean * 1000) / 1000   // làm tròn 3 số lẻ: thấy gì lọc nấy
      }]));
      restyleWards();
      return { total: arr.length, withData: arr.filter(s => s.mean != null).length };
    },

    setWardMode(flag) { wardMode = !!flag; restyleWards(); },

    // op: gte | gt | eq | lte | lt | between
    applyFilter(op, a, b) {
      filter = { op, a: Number(a), b: Number(b) };
      restyleWards();
      return [...wardStats.entries()]
        .filter(([, s]) => s.mean != null && matches(s.mean))
        .map(([code, s]) => ({ code, name: s.name, type: s.type, mean: s.mean }))
        .sort((x, y) => y.mean - x.mean);
    },

    clearFilter() { filter = null; restyleWards(); }
  };
})();
