(() => {
  const view = document.body.dataset.view;
  const dashboard = document.getElementById('dashboard');
  const map = document.getElementById('map');
  const error = document.getElementById('connection-error');
  const mapCanvas = document.getElementById('station-map');
  document.getElementById('heading').textContent = view === 'map' ? 'TCM+ treatment centre' : 'TCM+ dashboard';
  dashboard.hidden = view !== 'dashboard';
  map.hidden = view !== 'map';

  const setText = (id, value) => { document.getElementById(id).textContent = value; };
  const escapeHtml = (value) => String(value).replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[char]));
  const elapsed = (addedAt, generatedAt) => {
    const minutes = Math.floor((new Date(generatedAt) - new Date(addedAt)) / 60000);
    if (minutes < 1) return 'now';
    if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'} ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`;
    const days = Math.floor(hours / 24); return `${days} day${days === 1 ? '' : 's'} ago`;
  };
  const drawTrend = (id, points, maxValue, color) => {
    const svg = document.getElementById(id); const width = 640, height = 320, pad = { left: 38, right: 12, top: 12, bottom: 34 };
    const graphWidth = width - pad.left - pad.right, graphHeight = height - pad.top - pad.bottom;
    const max = Math.max(1, maxValue || 0, ...points.map(point => point.value));
    const coords = points.map((point, index) => `${pad.left + (points.length === 1 ? graphWidth / 2 : index * graphWidth / (points.length - 1))},${pad.top + graphHeight - point.value / max * graphHeight}`);
    const labels = points.length ? [points[0], points[Math.floor((points.length - 1) / 2)], points[points.length - 1]].filter((point, index, list) => list.indexOf(point) === index) : [];
    svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
    svg.innerHTML = `<line class="chart-axis" x1="${pad.left}" y1="${pad.top + graphHeight}" x2="${width - pad.right}" y2="${pad.top + graphHeight}"/><line class="chart-axis" x1="${pad.left}" y1="${pad.top}" x2="${pad.left}" y2="${pad.top + graphHeight}"/><text class="chart-label" x="${pad.left - 8}" y="${pad.top + 4}" text-anchor="end">${max}</text><text class="chart-label" x="${pad.left - 8}" y="${pad.top + graphHeight}" text-anchor="end">0</text>${coords.length ? `<polyline class="chart-line" stroke="${color}" points="${coords.join(' ')}"/>` : ''}${labels.map(point => `<text class="chart-label" x="${pad.left + (points.length === 1 ? graphWidth / 2 : points.indexOf(point) * graphWidth / (points.length - 1))}" y="${height - 9}" text-anchor="middle">${escapeHtml(point.label)}</text>`).join('')}`;
  };
  const resizeMap = () => {
    if (!mapCanvas) return; const frame = mapCanvas.parentElement; const scale = frame.clientWidth / 1200; mapCanvas.style.transform = `scale(${scale})`; frame.style.height = `${720 * scale}px`;
  };
  const renderMap = data => {
    mapCanvas.style.backgroundSize = `${data.gridSizePixels}px ${data.gridSizePixels}px`;
    mapCanvas.innerHTML = data.stations.map(station => `<article class="station ${station.isOccupied ? 'occupied' : ''}" style="left:${station.gridX * data.gridSizePixels}px;top:${station.gridY * data.gridSizePixels}px;width:${station.gridWidth * data.gridSizePixels}px;height:${station.gridHeight * data.gridSizePixels}px"><div class="station-header">${escapeHtml(station.type)}</div><div class="station-body"><div class="station-name">${escapeHtml(station.name)}</div>${station.isOccupied ? `<div class="patient-counter">Patient ${station.patientNumber}</div><div class="station-status">Occupied</div><div class="arrival">${elapsed(station.addedAt, data.generatedAt)}</div>` : '<div class="station-status">Available</div>'}</div></article>`).join('');
    resizeMap();
  };
  const render = data => {
    setText('clock', new Date(data.generatedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }));
    if (view === 'dashboard') { const d = data.dashboard; setText('available', d.availableStations); setText('occupied', d.occupiedStations); setText('seen', d.patientsSeenThisShift); setText('average', d.averageDischargeText); drawTrend('occupancy-chart', d.occupancy, d.totalStations, '#b94b4b'); drawTrend('arrivals-chart', d.cumulativeArrivals, null, '#3b6064'); } else renderMap(data);
  };
  const refresh = async () => {
    try { const response = await fetch('/api/snapshot', { cache: 'no-store' }); if (response.status === 401) { location.assign(`/login?returnUrl=${location.pathname}`); return; } if (!response.ok) throw new Error(); render(await response.json()); error.hidden = true; }
    catch { error.hidden = false; }
  };
  window.addEventListener('resize', resizeMap); refresh(); window.setInterval(refresh, 1000);
})();
