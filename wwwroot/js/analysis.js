window.tvdiAnalysis = (() => {
    const maps = new WeakMap();

    async function drawChange(element, rows, isError) {
        const response = await fetch('data/tphcm-wards.json');
        if (!response.ok) throw new Error('Không tải được ranh giới phường/xã.');
        const geo = await response.json();
        if (!element.isConnected) return;

        maps.get(element)?.remove();
        const map = L.map(element);
        maps.set(element, map);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '&copy; OpenStreetMap contributors',
            maxZoom: 19
        }).addTo(map);

        const byCode = new Map(rows.map(row => [String(row.code), row]));
        const color = value => {
            if (value == null) return '#a7aeb8';
            if (Math.abs(value) < 0.005) return '#eee9dd';
            const strength = Math.min(Math.abs(value) / 0.2, 1);
            return value < 0 ? 'hsl(215 70% ' + (86 - 46 * strength) + '%)'
                : 'hsl(17 80% ' + (86 - 45 * strength) + '%)';
        };
        const layer = L.geoJSON(geo, {
            style: feature => ({
                color: '#344252', weight: 0.65, fillOpacity: 0.8,
                fillColor: color(byCode.get(String(feature.properties.code))?.delta)
            }),
            onEachFeature(feature, shape) {
                const row = byCode.get(String(feature.properties.code));
                const popup = document.createElement('div');
                const title = document.createElement('strong');
                title.textContent = row?.name ?? feature.properties.name;
                const details = document.createElement('div');
                details.textContent = row?.delta == null ? 'Không có pixel chung hợp lệ'
                    : (isError ? 'Quan sát ' + row.before.toFixed(3) + ' · Dự báo ' + row.after.toFixed(3) + ' · Sai số ' : 'Chênh lệch ') + row.delta.toFixed(3) + ' · ' + row.pixels + ' pixel chung' + (row.estimated ? ' · ước tính' : '');
                popup.append(title, details);
                shape.bindPopup(popup);
            }
        }).addTo(map);
        if (layer.getBounds().isValid()) map.fitBounds(layer.getBounds(), { padding: [12, 12] });
        else map.setView([10.78, 106.69], 10);
        map.invalidateSize();
    }

    function saveCsv(records, filename) {
        const cell = value => {
            let text = String(value ?? '');
            if (typeof value === 'string' && /^[\s]*[=+@-]/.test(text)) text = "'" + text;
            return '"' + text.replaceAll('"', '""') + '"';
        };
        const blob = new Blob(['\ufeff', records.map(record => record.map(cell).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    function downloadCsv(rows, period) {
        saveCsv([['Kỳ so sánh', 'Mã', 'Phường/xã', 'TVDI trước', 'TVDI sau', 'Chênh lệch', 'Pixel chung', 'Ước tính'],
            ...rows.map(row => [period, row.code, row.name, row.before, row.after, row.delta, row.pixels, row.estimated ? 'Có' : 'Không'])], 'bien-dong-tvdi.csv');
    }

    function downloadValidation(validation) {
        saveCsv([['Kỳ kiểm định', 'Kỳ gốc', 'Mô hình', 'Pixel', 'MAE', 'RMSE', 'Mã phường', 'Phường/xã', 'Quan sát', 'Dự báo', 'Sai số', 'Pixel phường', 'Ước tính'],
            ...validation.metrics.flatMap(metric => metric.wardErrors.length
                ? metric.wardErrors.map(row => [validation.target, validation.origin, metric.model, metric.pixels, metric.mae, metric.rmse, row.code, row.name, row.before, row.after, row.delta, row.pixels, row.estimated ? 'Có' : 'Không'])
                : [[validation.target, validation.origin, metric.model, metric.pixels, metric.mae, metric.rmse, '', '', '', '', '', '', '']])], 'kiem-dinh-du-bao-tvdi.csv');
    }

    function downloadQuality(rows) {
        saveCsv([['Kỳ', 'Tệp', 'Tỷ lệ pixel hợp lệ', 'Kết quả'], ...rows.map(row => [row.period, row.fileName, row.coverage, row.issue ?? 'Hợp lệ'])], 'chat-luong-anh-tvdi.csv');
    }

    function downloadTrend(rows, name) {
        saveCsv([['Phường/xã', 'Kỳ', 'TVDI trung bình', 'Pixel hợp lệ', 'Ước tính'], ...rows.map(row => [name, row.period, row.mean, row.pixels, row.estimated ? 'Có' : 'Không'])], 'xu-huong-tvdi.csv');
    }

    function drawTrend(canvas, points, name) {
        const ctx = canvas.getContext('2d');
        const dpr = window.devicePixelRatio || 1;
        const width = Math.max(canvas.clientWidth, 320), height = 280;
        canvas.width = width * dpr; canvas.height = height * dpr;
        ctx.scale(dpr, dpr); ctx.clearRect(0, 0, width, height);
        const pad = {l: 48, r: 16, t: 24, b: 42}, plotW = width-pad.l-pad.r, plotH = height-pad.t-pad.b;
        ctx.font = '12px sans-serif'; ctx.fillStyle = '#334155'; ctx.strokeStyle = '#d8e0e8';
        for (let i=0;i<=4;i++) { const y=pad.t+plotH*i/4, v=1-i/4; ctx.beginPath();ctx.moveTo(pad.l,y);ctx.lineTo(width-pad.r,y);ctx.stroke();ctx.fillText(v.toFixed(2),8,y+4); }
        const valid=points.map((p,i)=>p.mean==null?null:{x:pad.l+(points.length<2?.5:i/(points.length-1))*plotW,y:pad.t+(1-p.mean)*plotH,p,i}).filter(Boolean);
        ctx.strokeStyle='#1667a8';ctx.lineWidth=2.5;ctx.beginPath();valid.forEach((v,i)=>i?ctx.lineTo(v.x,v.y):ctx.moveTo(v.x,v.y));ctx.stroke();
        valid.forEach(v=>{ctx.beginPath();ctx.fillStyle='#1667a8';ctx.arc(v.x,v.y,4,0,Math.PI*2);ctx.fill();});
        if(points.length){ctx.fillStyle='#334155';ctx.textAlign='left';ctx.fillText(points[0].period,pad.l,height-12);ctx.textAlign='right';ctx.fillText(points.at(-1).period,width-pad.r,height-12);}
        ctx.textAlign='left';ctx.fillText('TVDI theo tháng · '+name,pad.l,pad.t-8);
        canvas.onmousemove=e=>{const rect=canvas.getBoundingClientRect(),x=e.clientX-rect.left;const v=valid.reduce((a,b)=>Math.abs(a.x-x)<Math.abs(b.x-x)?a:b,null);if(v)canvas.title=`${v.p.period}: ${v.p.mean.toFixed(3)} · ${v.p.pixels} pixel${v.p.estimated?' · ước tính':''}`;};
    }

    return { drawChange, downloadCsv, downloadValidation, downloadQuality, downloadTrend, drawTrend };
})();
