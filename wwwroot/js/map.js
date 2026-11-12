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
  let map = null, layer = null, georaster = null, popup = null;
  let wardLayer = null, outlineLayer = null;
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
    const band = gr.values[0];
    const nd = gr.noDataValue;
    for (let y = 0; y < h; y++) {
      const row = band[y];
      for (let x = 0; x < w; x++) {
        const v = row[x];
        const c = (v === nd) ? null : colorRgb(v);
        const i = (y * w + x) * 4;
        if (!c) { img.data[i + 3] = 0; continue; }
        img.data[i] = c[0]; img.data[i + 1] = c[1]; img.data[i + 2] = c[2]; img.data[i + 3] = 255;
      }
    }
    ctx.putImageData(img, 0, 0);
    return cv;
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
    if (georaster && typeof geoblaze !== 'undefined') {
      const v = geoblaze.identify(georaster, [e.latlng.lng, e.latlng.lat]);
      html += `<br>Điểm vừa bấm: ${v && !isNoData(v[0]) ? v[0].toFixed(3) : 'không có dữ liệu'}`;
    }
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
      layer = null; georaster = null; wardLayer = null; outlineLayer = null;
      wardStats = new Map(); wardMode = true; rasterVisible = true; filter = null;

      map = L.map(el, { maxBounds: MAX_BOUNDS, minZoom: 9 }).fitBounds(HCM);
      L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
        { attribution: '© OpenStreetMap' }).addTo(map);

      popup = L.popup();
      map.on('click', e => {
        if (!georaster || typeof geoblaze === 'undefined') return;
        const v = geoblaze.identify(georaster, [e.latlng.lng, e.latlng.lat]);
        const val = v && !isNoData(v[0]) ? v[0].toFixed(3) : 'không có dữ liệu';
        popup.setLatLng(e.latlng).setContent(`TVDI: <b>${val}</b>`).openOn(map);
      });

      addLegend();
      loadBoundaries();
      setTimeout(() => map.invalidateSize(), 200);
    },

    async loadTif(url, opacity = 0.8) {
      if (!map) throw new Error('Bản đồ chưa khởi tạo.');
      if (typeof parseGeoraster === 'undefined') throw new Error('georaster chưa tải.');

      const res = await fetch(url, { credentials: 'same-origin' });
      const type = res.headers.get('content-type') || '';
      if (!res.ok || type.includes('text/html')) {
        const detail = type.includes('text/plain') ? (await res.text()).slice(0, 200) : '';
        throw new Error(`Không tải được ảnh (HTTP ${res.status}) từ ${url}. ${detail}`);
      }

      georaster = await parseGeoraster(await res.arrayBuffer());
      if (layer) { map.removeLayer(layer); layer = null; }

      if (georaster.projection === 4326) {
        const dataUrl = buildCanvas(georaster).toDataURL('image/png');
        const b = [[georaster.ymin, georaster.xmin], [georaster.ymax, georaster.xmax]];
        layer = L.imageOverlay(dataUrl, b, { opacity, interactive: false, className: 'tvdi-overlay' });
      } else {
        console.warn('[tvdiMap] projection =', georaster.projection, '→ dùng GeoRasterLayer');
        if (typeof GeoRasterLayer === 'undefined') throw new Error('georaster-layer-for-leaflet chưa tải.');
        layer = new GeoRasterLayer({
          georaster, opacity, resolution: 256, resampleMethod: 'nearest',
          pixelValuesToColorFn: vals => colorHex(vals[0])
        });
      }

      if (rasterVisible) layer.addTo(map);
      map.fitBounds(layer.getBounds());
    },

    async stats() {
      if (!georaster || typeof geoblaze === 'undefined') return null;
      const s = (await geoblaze.stats(georaster))[0];
      return { mean: s.mean, min: s.min, max: s.max };
    },

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