(() => {
	'use strict';

	const W = 682;
	const H = 2560;
	const $ = (id) => document.getElementById(id);
	const frame = $('frame');

	// WebView2 host bridge; absent when the page is opened in a regular browser (design mode)
	const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;

	const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
	const motion = !reduceMotion;
	const FRAME_INTERVAL = 1000 / 24;

	const nf0Format = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 0 });
	const nf0 = (value) => nf0Format.format(value);
	const nf1 = new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
	const clamp = (v, min, max) => Math.min(max, Math.max(min, v));
	const isNum = (v) => typeof v === 'number' && Number.isFinite(v);

	// ---------- Fit the native frame to the window ----------
	function applyScale() {
		const scale = Math.min(window.innerWidth / W, window.innerHeight / H);
		frame.style.transform = `scale(${scale})`;
	}

	// ---------- Background image + hot spots (frame coordinates) ----------
	const BACKGROUND_SRC = 'assets/background.jpg';
	const LAVA_POOL = { x: 170, y: 1210 };
	const FISSURE = { x: 215, y: 1290 };
	const CRACKS = { x: 520, y: 1520 };
	const VOLCANO = { x: 645, y: 890 };

	function haze(ctx, x, y, r, color) {
		const g = ctx.createRadialGradient(x, y, 0, x, y, r);
		g.addColorStop(0, color);
		g.addColorStop(1, 'rgba(0,0,0,0)');
		ctx.fillStyle = g;
		ctx.fillRect(x - r, y - r, r * 2, r * 2);
	}

	function addGrain(ctx) {
		const tile = document.createElement('canvas');
		tile.width = 160;
		tile.height = 160;
		const tc = tile.getContext('2d');
		const img = tc.createImageData(160, 160);
		for (let i = 0; i < img.data.length; i += 4) {
			const v = Math.random() * 255;
			img.data[i] = v;
			img.data[i + 1] = v;
			img.data[i + 2] = v;
			img.data[i + 3] = Math.random() * 14;
		}
		tc.putImageData(img, 0, 0);
		ctx.fillStyle = ctx.createPattern(tile, 'repeat');
		ctx.fillRect(0, 0, W, H);
	}

	function drawBackground() {
		const ctx = $('bg').getContext('2d');
		ctx.fillStyle = '#0e0707';
		ctx.fillRect(0, 0, W, H);
		const photo = new Image();
		photo.onload = () => {
			ctx.drawImage(photo, 0, 0, W, H);
			// Darken the bright sky behind the clock so the digits keep their contrast
			const shade = ctx.createLinearGradient(0, 0, 0, 560);
			shade.addColorStop(0, 'rgba(0, 0, 0, 0.5)');
			shade.addColorStop(0.6, 'rgba(0, 0, 0, 0.18)');
			shade.addColorStop(1, 'rgba(0, 0, 0, 0)');
			ctx.fillStyle = shade;
			ctx.fillRect(0, 0, W, 560);
			addGrain(ctx);
		};
		photo.onerror = () => addGrain(ctx);
		photo.src = BACKGROUND_SRC;
	}

	// ---------- State fed by the host (or the design-mode simulator) ----------
	const RING = 72;
	const cpuHist = new Array(RING).fill(0);
	const shown = { cpu: 0 };
	let current = null;
	let lastTick = performance.now();

	const MB = 1024 * 1024;
	function formatRate(bytesPerSec) {
		if (!isNum(bytesPerSec)) {
			return '--';
		}
		return bytesPerSec < MB ? `${nf0(bytesPerSec / 1024)} Ko/s` : `${nf1.format(bytesPerSec / MB)} Mo/s`;
	}
	// Storage sizes in the largest fitting unit (binary, like the Windows Explorer), three
	// significant digits: 1,82 To / 47,3 Go / 512 Go / 850 Mo
	const SIZE_UNITS = ['Ko', 'Mo', 'Go', 'To', 'Po'];
	const sizeFormats = [0, 1, 2].map((digits) => new Intl.NumberFormat('fr-FR', { maximumFractionDigits: digits }));
	function formatSize(gb) {
		if (!isNum(gb) || gb <= 0) {
			return { value: '0', unit: 'Go' };
		}
		let value = gb * 1024 * 1024;
		let unit = 0;
		// Switch below 1024 (at what would round to 1000), so no size ever shows four digits
		while (value >= 999.5 && unit < SIZE_UNITS.length - 1) {
			value /= 1024;
			unit++;
		}
		const digits = value < 10 ? 2 : value < 100 ? 1 : 0;
		return { value: sizeFormats[digits].format(value), unit: SIZE_UNITS[unit] };
	}

	const text = (id, value) => {
		$(id).textContent = value;
	};
	const orDash = (value, format) => (isNum(value) ? format(value) : '--');

	// ---------- Static widget skeletons ----------
	const leds = $('leds');
	for (let i = 0; i < 32; i++) {
		const led = document.createElement('span');
		led.className = 'led';
		leds.appendChild(led);
	}
	const segs = $('segs');
	for (let i = 0; i < 20; i++) {
		const bar = document.createElement('span');
		bar.className = 'seg-bar';
		segs.appendChild(bar);
	}

	// Value + blade gauge, with warm / hot states from thresholds
	function setMetric(prefix, value, min, max, warm, hot) {
		const tile = $(`${prefix}Tile`);
		if (!isNum(value)) {
			text(prefix, '--');
			$(`${prefix}Fill`).style.width = '0%';
			tile.classList.remove('hot', 'warm');
			return;
		}
		text(prefix, nf0(value));
		$(`${prefix}Fill`).style.width = `${clamp(((value - min) / (max - min)) * 100, 2, 100)}%`;
		tile.classList.toggle('hot', value >= hot);
		tile.classList.toggle('warm', value >= warm && value < hot);
	}

	function renderCpu(cpu) {
		text('cpuLoad', orDash(cpu.load, Math.round));
		$('cpuLoad').parentElement.classList.toggle('wide', isNum(cpu.load) && Math.round(cpu.load) >= 100);
		setMetric('cpuTemp', cpu.temp, 30, 95, 75, 85);
		setMetric('cpuPow', cpu.power, 0, 160, 120, 150);
	}

	function renderGpu(gpu) {
		text('gpuLoad', orDash(gpu.load, Math.round));
		const litSegs = isNum(gpu.load) ? Math.round(gpu.load / 5) : 0;
		[...segs.children].forEach((bar, i) => {
			bar.classList.toggle('on', i < litSegs);
			bar.classList.toggle('peak', i === litSegs - 1);
		});
		const total = isNum(gpu.vramTotalGb) ? gpu.vramTotalGb : 24;
		text('vramTotal', nf0(total));
		if (isNum(gpu.vramPercent)) {
			text('vramUsed', nf1.format((gpu.vramPercent * total) / 100));
			$('vramFill').style.width = `${clamp(gpu.vramPercent, 1.5, 100)}%`;
		} else {
			text('vramUsed', '--');
			$('vramFill').style.width = '0%';
		}
		setMetric('gpuTemp', gpu.temp, 30, 90, 70, 80);
		setMetric('gpuPow', gpu.power, 0, 450, 350, 420);
		// RTSS reports 0 (or nothing) outside games: the readout only shows while one runs
		const playing = isNum(gpu.fps) && gpu.fps >= 1;
		if ($('gpuFps').hidden === playing) {
			$('gpuFps').hidden = !playing;
		}
		if (playing) {
			text('gpuFpsValue', nf0(gpu.fps));
		}
	}

	function renderVramTicks(totalGb) {
		const ticks = $('vramTicks').children;
		for (let i = 0; i < ticks.length; i++) {
			const value = nf0((totalGb * i) / (ticks.length - 1));
			ticks[i].textContent = i === ticks.length - 1 ? `${value} Go` : value;
		}
	}
	let vramTicksFor = 24;

	// One card per drive reported by the host, then one bracket per run of drives on the same physical
	// disk (its temperature); the DOM is only rebuilt when that layout changes
	let diskLayout = null;
	let diskSlots = [];
	let diskGroups = [];

	function ensureDiskLayout(disks) {
		const runs = [];
		disks.forEach((disk, i) => {
			const last = runs[runs.length - 1];
			if (last && isNum(disk.disk) && disks[last.start].disk === disk.disk) {
				last.count++;
			} else {
				runs.push({ start: i, count: 1 });
			}
		});
		const layout = disks.length + ':' + runs.map(r => r.count).join(',');
		if (layout !== diskLayout) {
			diskLayout = layout;
			const container = $('disks');
			diskSlots = disks.map(() => $('diskTemplate').content.firstElementChild.cloneNode(true));
			diskGroups = runs.map(run => {
				const group = $('diskGroupTemplate').content.firstElementChild.cloneNode(true);
				group.style.gridColumn = `${run.start + 1} / span ${run.count}`;
				return group;
			});
			container.replaceChildren(...diskSlots, ...diskGroups);
			container.style.setProperty('--disk-count', Math.max(disks.length, 1));
		}
		return runs;
	}

	function renderMemory(ram, disks) {
		if (isNum(ram.usedGb) && isNum(ram.totalGb)) {
			text('ramUsed', nf1.format(ram.usedGb));
			text('ramTotal', nf0(ram.totalGb));
			const lit = Math.round((ram.usedGb / ram.totalGb) * 32);
			[...leds.children].forEach((led, i) => led.classList.toggle('on', i < lit));
		}
		const runs = ensureDiskLayout(disks);
		const circumference = 2 * Math.PI * 47;
		diskSlots.forEach((slot, i) => {
			const disk = disks[i];
			slot.querySelector('.disk-letter').textContent = disk.name;
			slot.querySelector('.disk-label').textContent = disk.label || '';
			const free = formatSize(disk.totalGb - disk.usedGb);
			slot.querySelector('.free').textContent = free.value;
			slot.querySelector('.unit').textContent = `${free.unit} libres`;
			const total = formatSize(disk.totalGb);
			slot.querySelector('.disk-total').textContent = `sur ${total.value} ${total.unit}`;
			slot.querySelector('.disk-arc').setAttribute('stroke-dasharray', `${(circumference * disk.usedGb) / disk.totalGb} ${circumference}`);
		});
		runs.forEach((run, i) => {
			// Drives of one disk read the same sensor; the first one configured stands for the disk
			const temp = disks.slice(run.start, run.start + run.count).map(d => d.temp).find(isNum);
			const group = diskGroups[i];
			group.hidden = !isNum(temp);
			if (isNum(temp)) {
				group.querySelector('.disk-temp-value').textContent = `${nf0(temp)} °C`;
				group.classList.toggle('hot', temp >= 60);
			}
		});
	}

	function renderNetwork(net) {
		const wifi = net.wifi;
		if (wifi) {
			text('netChip', [wifi.generation, isNum(wifi.bandGhz) ? `${nf1.format(wifi.bandGhz).replace(',0', '')} GHz` : null].filter(Boolean).join(' · '));
			text('sigPct', orDash(wifi.signal, nf0));
			text('rssi', isNum(wifi.rssi) ? `${wifi.rssi} dBm` : ' ');
			text('ssid', wifi.ssid || '--');
			text('channel', orDash(wifi.channel, String));
			text('link', isNum(wifi.linkMbps) ? `${nf1.format(wifi.linkMbps)} Mbit/s` : '--');
			const arcs = isNum(wifi.signal) ? Math.ceil(wifi.signal / 25) : 0;
			for (let i = 1; i <= 4; i++) {
				$(`arc${i}`).classList.toggle('on', i <= arcs);
			}
		} else {
			text('netChip', net.adapter || 'Filaire');
			text('sigPct', '--');
			text('rssi', ' ');
			['ssid', 'channel'].forEach((id) => text(id, '--'));
			text('link', isNum(net.linkMbps) ? `${nf0(net.linkMbps)} Mbit/s` : '--');
			for (let i = 1; i <= 4; i++) {
				$(`arc${i}`).classList.remove('on');
			}
		}
		text('ping', isNum(net.pingMs) ? `${nf0(net.pingMs)} ms` : '--');
		text('rateDown', formatRate(net.downBytesPerSec));
		text('rateUp', formatRate(net.upBytesPerSec));

		// Connectivity alarm: the whole panel switches to an alert look, the chip says what and since when
		const status = net.status || 'online';
		$('holonet').classList.toggle('alert', status !== 'online');
		if (status !== 'online') {
			const what = status === 'offline' ? 'HORS LIGNE' : "PAS D'INTERNET";
			text('netChip', isNum(net.offlineSec) ? `⚠ ${what} · ${formatTime(net.offlineSec)}` : `⚠ ${what}`);
		}
	}

	// ---------- Speed test (runs in the host through the Speedtest by Ookla CLI) ----------
	// Shown inside the network panel, in place of its body, so the panel never changes size.
	const SPEEDTEST_PHASES = {
		install: 'Installation de Speedtest…',
		ping: 'Mesure du ping…',
		download: 'Réception…',
		upload: 'Envoi…',
		done: 'Test terminé',
		cancelled: 'Test annulé',
		error: 'Échec du test'
	};
	const SPEEDTEST_AUTO_CLOSE_MS = 30000;
	const SPEED_COLORS = { download: '255, 31, 51', upload: '255, 176, 32' };
	// Speedometer scales, graduations evenly spaced like Speedtest's dial; the wide one kicks in above 1 Gbit/s
	const GAUGE_SCALES = [
		[0, 5, 10, 50, 100, 250, 500, 750, 1000],
		[0, 10, 50, 100, 250, 500, 1000, 2500, 5000]
	];
	const GAUGE = { width: 236, height: 176, cx: 118, cy: 100, radius: 92, start: Math.PI * 5 / 6, sweep: Math.PI * 4 / 3 };
	// Needle speed limits, in dial sweeps per second: a steady climb, a quicker fall back to zero
	const NEEDLE_RISE = 0.55;
	const NEEDLE_FALL = 1.4;

	const speedTest = {
		running: false,
		open: false,
		closeTimer: 0,
		server: null,
		phase: 'ping',
		// Direction the dial currently shows: it lags the test phase while the needle falls back to zero
		dial: 'download',
		target: 0,
		fraction: 0,
		shownText: '',
		scale: GAUGE_SCALES[0]
	};
	const gaugeCtx = $('stGauge').getContext('2d');

	const formatMbps = (mbps) => (mbps >= 100 ? nf0(mbps) : nf1.format(mbps));

	// Phase title, followed by the Ookla server once it is known ("Réception… · Scaleway · Paris")
	function speedTestTitle(update) {
		if (update.server) {
			speedTest.server = update.server;
		}
		if (update.phase === 'error') {
			return update.reason === 'install' ? 'Échec du téléchargement de Speedtest' : 'Échec du test : connexion indisponible';
		}
		const title = SPEEDTEST_PHASES[update.phase] || 'Test de débit';
		return speedTest.server && !['install', 'cancelled'].includes(update.phase) ? `${title} · ${speedTest.server}` : title;
	}

	function setSpeedTestOpen(open) {
		clearTimeout(speedTest.closeTimer);
		speedTest.open = open;
		$('holonet').classList.toggle('testing', open);
		$('speedtest').hidden = !open;
		$('netTestBtn').hidden = open;
		$('stAction').hidden = !open;
	}

	// Colour and label of the dial follow the direction it shows, not the phase reported by the test
	function setDial(dial) {
		speedTest.dial = dial;
		$('speedtest').classList.toggle('upload', dial === 'upload');
		text('stDir', dial === 'upload' ? '↑ Envoi' : '↓ Réception');
	}

	function startSpeedTest() {
		if (speedTest.running) {
			return;
		}
		['stPing', 'stDown', 'stUp'].forEach((id) => text(id, '--'));
		speedTest.server = null;
		speedTest.fraction = 0;
		speedTest.target = 0;
		speedTest.scale = GAUGE_SCALES[0];
		renderSpeedTest({ phase: 'ping' });
		if (host) {
			host.postMessage({ type: 'speedtest', action: 'start' });
		} else {
			simulateSpeedTest();
		}
	}

	function renderSpeedTest(update) {
		setSpeedTestOpen(true);
		const running = ['install', 'ping', 'download', 'upload'].includes(update.phase);
		const finished = update.phase === 'done';
		const transferring = update.phase === 'download' || update.phase === 'upload';
		const enteringTransfer = transferring && speedTest.phase !== 'download' && speedTest.phase !== 'upload';
		speedTest.running = running;
		speedTest.phase = update.phase;
		text('stPhase', speedTestTitle(update));
		text('stAction', running ? 'Annuler' : 'Fermer');
		$('speedtest').classList.toggle('failed', update.phase === 'error');

		if (isNum(update.pingMs)) text('stPing', `${nf1.format(update.pingMs)} ms`);
		if (isNum(update.downMbps)) text('stDown', formatMbps(update.downMbps));
		if (isNum(update.upMbps)) text('stUp', formatMbps(update.upMbps));

		if (transferring) {
			const mbps = isNum(update.mbps) ? update.mbps : 0;
			if (mbps > 1000 && speedTest.scale !== GAUGE_SCALES[1]) {
				// Keep the needle where it is when the dial switches to the wide scale
				const value = gaugeValue(speedTest.fraction, speedTest.scale);
				speedTest.scale = GAUGE_SCALES[1];
				speedTest.fraction = gaugeFraction(value, speedTest.scale);
			}
			speedTest.target = mbps;
			if (enteringTransfer) {
				setDial(update.phase);
				speedTest.shownText = '';
				text('stUnit', 'Mbit/s');
			}
		} else {
			speedTest.target = 0;
			setDial('download');
			if (update.phase === 'ping' || update.phase === 'install') {
				text('stDir', update.phase === 'install' ? 'Installation' : 'Ping');
				text('stValue', isNum(update.pingMs) ? nf1.format(update.pingMs) : '--');
				text('stUnit', 'ms');
			} else if (!finished) {
				text('stDir', update.phase === 'error' ? 'Erreur' : 'Annulé');
				text('stValue', '--');
				text('stUnit', '');
			}
		}

		// End of test: the dial gives way to the result, download, upload and ping
		$('stLive').hidden = finished;
		$('stSummary').hidden = !finished;
		if (finished) {
			text('stSumDown', isNum(update.downMbps) ? formatMbps(update.downMbps) : '--');
			text('stSumUp', isNum(update.upMbps) ? formatMbps(update.upMbps) : '--');
			text('stSumPing', isNum(update.pingMs) ? nf1.format(update.pingMs) : '--');
			text('stServer', speedTest.server ? `Serveur ${speedTest.server}` : ' ');
		}

		if (!running) {
			speedTest.closeTimer = setTimeout(() => setSpeedTestOpen(false), SPEEDTEST_AUTO_CLOSE_MS);
		}
	}

	// Position on the dial (0..1) with the scale's graduations evenly spaced, linear in between
	function gaugeFraction(mbps, scale) {
		const steps = scale.length - 1;
		if (mbps <= 0) return 0;
		for (let i = 1; i < scale.length; i++) {
			if (mbps <= scale[i]) {
				return (i - 1 + (mbps - scale[i - 1]) / (scale[i] - scale[i - 1])) / steps;
			}
		}
		return 1;
	}

	// Inverse of gaugeFraction: the rate the needle points at, so the figure always matches the needle
	function gaugeValue(fraction, scale) {
		const position = clamp(fraction, 0, 1) * (scale.length - 1);
		const i = Math.min(Math.floor(position), scale.length - 2);
		return scale[i] + (scale[i + 1] - scale[i]) * (position - i);
	}

	// Called from the main loop while the view is open. The needle moves at a capped speed; when the
	// test switches to upload it first falls back to zero in the download colour, then climbs again.
	function drawSpeedGauge(ticks) {
		if (!speedTest.open || $('stLive').hidden) {
			return;
		}
		const seconds = ticks / 60;
		const transferring = speedTest.phase === 'download' || speedTest.phase === 'upload';
		const returning = transferring && speedTest.dial !== speedTest.phase;
		const goal = transferring && !returning ? gaugeFraction(speedTest.target, speedTest.scale) : 0;
		const delta = goal - speedTest.fraction;
		const eased = delta * Math.min(0.14 * ticks, 1);
		const limit = (delta > 0 ? NEEDLE_RISE : NEEDLE_FALL) * seconds;
		speedTest.fraction += clamp(eased, -limit, limit);
		if (returning && speedTest.fraction < 0.004) {
			speedTest.fraction = 0;
			setDial(speedTest.phase);
		}
		if (transferring) {
			const label = formatMbps(gaugeValue(speedTest.fraction, speedTest.scale));
			if (label !== speedTest.shownText) {
				speedTest.shownText = label;
				text('stValue', label);
			}
		}

		const ctx = gaugeCtx;
		const { width, height, cx, cy, radius, start, sweep } = GAUGE;
		const color = SPEED_COLORS[speedTest.dial];
		const fraction = speedTest.fraction;
		const angle = start + sweep * fraction;
		ctx.clearRect(0, 0, width, height);

		// Track
		ctx.lineCap = 'round';
		ctx.lineWidth = 12;
		ctx.strokeStyle = 'rgba(255, 255, 255, 0.06)';
		ctx.beginPath();
		ctx.arc(cx, cy, radius, start, start + sweep);
		ctx.stroke();

		// Graduations and labels
		ctx.font = "12px 'Share Tech Mono', monospace";
		ctx.textAlign = 'center';
		ctx.textBaseline = 'middle';
		speedTest.scale.forEach((value, i) => {
			const a = start + (sweep * i) / (speedTest.scale.length - 1);
			const lit = i / (speedTest.scale.length - 1) <= fraction + 0.001 && fraction > 0;
			ctx.strokeStyle = lit ? `rgba(${color}, 0.9)` : 'rgba(255, 255, 255, 0.25)';
			ctx.lineWidth = 2;
			ctx.beginPath();
			ctx.moveTo(cx + Math.cos(a) * (radius - 11), cy + Math.sin(a) * (radius - 11));
			ctx.lineTo(cx + Math.cos(a) * (radius - 18), cy + Math.sin(a) * (radius - 18));
			ctx.stroke();
			ctx.fillStyle = lit ? 'rgba(243, 233, 234, 0.95)' : 'rgba(143, 118, 121, 0.9)';
			ctx.fillText(value >= 1000 ? `${value / 1000}G` : String(value), cx + Math.cos(a) * (radius - 31), cy + Math.sin(a) * (radius - 31));
		});

		// Glowing blade arc up to the current rate
		if (fraction > 0.002) {
			ctx.lineWidth = 12;
			ctx.shadowColor = `rgb(${color})`;
			ctx.shadowBlur = 16;
			ctx.strokeStyle = `rgb(${color})`;
			ctx.beginPath();
			ctx.arc(cx, cy, radius, start, angle);
			ctx.stroke();
			ctx.shadowBlur = 4;
			ctx.lineWidth = 3;
			ctx.strokeStyle = '#fff4f4';
			ctx.beginPath();
			ctx.arc(cx, cy, radius, start, angle);
			ctx.stroke();
			ctx.shadowBlur = 0;
		}

		// Spark at the head of the arc
		if (fraction > 0.002) {
			const sx = cx + Math.cos(angle) * radius;
			const sy = cy + Math.sin(angle) * radius;
			const spark = ctx.createRadialGradient(sx, sy, 0, sx, sy, 11);
			spark.addColorStop(0, 'rgba(255, 246, 246, 0.95)');
			spark.addColorStop(0.35, `rgba(${color}, 0.8)`);
			spark.addColorStop(1, `rgba(${color}, 0)`);
			ctx.fillStyle = spark;
			ctx.beginPath();
			ctx.arc(sx, sy, 11, 0, Math.PI * 2);
			ctx.fill();
		}

		drawNeedle(ctx, cx, cy, angle, radius - 20, color);
	}

	// Tapered needle, dark at the base and white-hot at the tip, on a notched hub with a glowing ring
	function drawNeedle(ctx, x, y, angle, reach, color) {
		ctx.save();
		ctx.translate(x, y);
		ctx.rotate(angle);

		const body = ctx.createLinearGradient(0, 0, reach, 0);
		body.addColorStop(0, `rgba(${color}, 0.25)`);
		body.addColorStop(0.55, `rgba(${color}, 0.95)`);
		body.addColorStop(1, '#fff6f6');
		ctx.shadowColor = `rgb(${color})`;
		ctx.shadowBlur = 12;
		ctx.fillStyle = body;
		ctx.beginPath();
		ctx.moveTo(-10, 0);
		ctx.lineTo(0, -4.5);
		ctx.lineTo(reach, 0);
		ctx.lineTo(0, 4.5);
		ctx.closePath();
		ctx.fill();
		ctx.shadowBlur = 0;
		ctx.strokeStyle = 'rgba(255, 246, 246, 0.9)';
		ctx.lineWidth = 1.2;
		ctx.beginPath();
		ctx.moveTo(reach * 0.4, 0);
		ctx.lineTo(reach, 0);
		ctx.stroke();

		// Hub: notched dark disc, coloured ring, glowing core
		ctx.fillStyle = '#2a2324';
		for (let i = 0; i < 8; i++) {
			ctx.rotate(Math.PI / 4);
			ctx.fillRect(9, -1.6, 5, 3.2);
		}
		ctx.fillStyle = '#120d0e';
		ctx.beginPath();
		ctx.arc(0, 0, 11, 0, Math.PI * 2);
		ctx.fill();
		ctx.shadowColor = `rgb(${color})`;
		ctx.shadowBlur = 8;
		ctx.strokeStyle = `rgb(${color})`;
		ctx.lineWidth = 2;
		ctx.beginPath();
		ctx.arc(0, 0, 8, 0, Math.PI * 2);
		ctx.stroke();
		ctx.fillStyle = `rgb(${color})`;
		ctx.beginPath();
		ctx.arc(0, 0, 3, 0, Math.PI * 2);
		ctx.fill();
		ctx.restore();
	}

	// Design mode: plausible fake run to preview the view without the host
	function simulateSpeedTest() {
		const started = performance.now();
		const ping = 5 + Math.random() * 2;
		const server = 'Serveur de démo · Paris';
		const timer = setInterval(() => {
			const t = (performance.now() - started) / 1000;
			if (!speedTest.running && t > 0.5) {
				clearInterval(timer);
				return;
			}
			if (t < 2) {
				renderSpeedTest({ phase: 'ping', pingMs: ping, server });
			} else if (t < 10) {
				const ramp = Math.min(1, (t - 2) / 1.5);
				renderSpeedTest({ phase: 'download', mbps: ramp * (900 + Math.random() * 40), pingMs: ping, server });
			} else if (t < 18) {
				// Ookla's first upload samples already report a high rate: no ramp, like the real thing
				renderSpeedTest({ phase: 'upload', mbps: 910 + Math.random() * 40, pingMs: ping, downMbps: 924.7, server });
			} else {
				clearInterval(timer);
				renderSpeedTest({ phase: 'done', pingMs: ping, downMbps: 924.7, upMbps: 935.6, server });
			}
		}, 250);
	}

	$('netTestBtn').addEventListener('click', startSpeedTest);
	$('stAction').addEventListener('click', () => {
		if (!speedTest.running) {
			setSpeedTestOpen(false);
		} else if (host) {
			host.postMessage({ type: 'speedtest', action: 'cancel' });
		} else {
			speedTest.running = false;
			renderSpeedTest({ phase: 'cancelled' });
		}
	});

	// ---------- Dock (built from the host's "Shortcuts" setting) ----------
	const SVG_NS = 'http://www.w3.org/2000/svg';
	const BUILTIN_ICONS = {
		app: '<rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/>',
		gamepad: '<path d="M6 8h12a4 4 0 0 1 3.9 4.9l-1 4A2.6 2.6 0 0 1 16.4 18L14 15h-4l-2.4 3a2.6 2.6 0 0 1-4.5-1.1l-1-4A4 4 0 0 1 6 8z"/><path d="M7.5 11v3M6 12.5h3"/><circle cx="16" cy="11.5" r=".6"/><circle cx="17.5" cy="13.5" r=".6"/>',
		music: '<path d="M9 18V5l11-2v13"/><circle cx="6.5" cy="18" r="2.5"/><circle cx="17.5" cy="16" r="2.5"/>',
		chat: '<path d="M4 5h16v11H9l-5 4z"/><path d="M9 10.5h.01M15 10.5h.01"/>',
		phone: '<path d="M4.5 19.5 5.6 16A8.5 8.5 0 1 1 8.4 18.6z"/><path d="M9.2 8.6c.3-.6.8-.6 1.1 0l.6 1.3c.2.4.1.7-.2 1l-.5.5c.6 1.2 1.5 2.1 2.7 2.7l.5-.5c.3-.3.6-.4 1-.2l1.3.6c.6.3.6.8 0 1.1-.9.6-2 .8-3 .3a8 8 0 0 1-3.6-3.6c-.5-1-.3-2.1.1-3.2z"/>',
		pulse: '<path d="M3 12h4l2-6 4 12 2-6h6"/>',
		globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>',
		folder: '<path d="M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2.5h8.5A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z"/>',
		video: '<rect x="3" y="6" width="13" height="12" rx="2"/><path d="m16 10 5-3v10l-5-3z"/>',
		code: '<path d="m8 7-5 5 5 5M16 7l5 5-5 5M14 4l-4 16"/>',
		headset: '<path d="M4 14v-2a8 8 0 0 1 16 0v2"/><rect x="3" y="14" width="4" height="6" rx="1.5"/><rect x="17" y="14" width="4" height="6" rx="1.5"/><path d="M20 20a3 3 0 0 1-3 2h-3"/>',
		camera: '<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/>'
	};

	const LONG_PRESS_MS = 650;
	const PRESS_MOVE_TOLERANCE = 14;
	let dockShortcuts = [];

	// ---------- App logos turned into single-colour glyphs ----------
	// Colour logos clash with the line icons of the theme: each one is recoloured once, in the
	// colour of the built-in glyphs, keeping its inner details as cut-outs where it can.
	const monochromeCache = new Map();

	const smoothstep = (edge0, edge1, value) => {
		const t = clamp((value - edge0) / (edge1 - edge0), 0, 1);
		return t * t * (3 - 2 * t);
	};

	function monochromePixels(data, size, color) {
		const at = (x, y) => (y * size + x) * 4;
		const luma = (i) => 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
		const distance = (i, c) => Math.hypot(data[i] - c[0], data[i + 1] - c[1], data[i + 2] - c[2]);

		let left = size, top = size, right = -1, bottom = -1, opaque = 0;
		for (let y = 0; y < size; y++) {
			for (let x = 0; x < size; x++) {
				if (data[at(x, y) + 3] > 128) {
					opaque++;
					left = Math.min(left, x);
					right = Math.max(right, x);
					top = Math.min(top, y);
					bottom = Math.max(bottom, y);
				}
			}
		}
		if (opaque === 0) {
			return;
		}

		// A logo on a plate (square tile) fills its bounding box: the line just inside each side is opaque
		const inset = Math.max(2, Math.round((right - left) * 0.06));
		const ring = [];
		for (let x = left + inset; x <= right - inset; x++) {
			ring.push(at(x, top + inset), at(x, bottom - inset));
		}
		for (let y = top + inset; y <= bottom - inset; y++) {
			ring.push(at(left + inset, y), at(right - inset, y));
		}
		const ringOpaque = ring.filter((i) => data[i + 3] > 200);
		const plate = ringOpaque.length >= ring.length * 0.9;

		// Dominant colour (plate colour, or body colour of a cut-out logo): most frequent 4-bit bucket
		const buckets = new Map();
		const addBucket = (i) => {
			const key = ((data[i] >> 4) << 8) | ((data[i + 1] >> 4) << 4) | (data[i + 2] >> 4);
			const bucket = buckets.get(key) || { n: 0, r: 0, g: 0, b: 0 };
			bucket.n++;
			bucket.r += data[i];
			bucket.g += data[i + 1];
			bucket.b += data[i + 2];
			buckets.set(key, bucket);
		};
		if (plate) {
			ringOpaque.forEach(addBucket);
		} else {
			for (let i = 0; i < data.length; i += 4) {
				if (data[i + 3] > 128) {
					addBucket(i);
				}
			}
		}
		let best = null;
		for (const bucket of buckets.values()) {
			if (!best || bucket.n > best.n) {
				best = bucket;
			}
		}
		const dominant = [best.r / best.n, best.g / best.n, best.b / best.n];
		const dominantShare = best.n / (plate ? ringOpaque.length : opaque);

		// Multicolour logo: split its light and dark tones (Otsu threshold on luminance)
		let split = null;
		if (!plate && dominantShare < 0.35) {
			const histogram = new Array(256).fill(0);
			let sum = 0;
			for (let i = 0; i < data.length; i += 4) {
				if (data[i + 3] > 128) {
					const level = Math.round(luma(i));
					histogram[level]++;
					sum += level;
				}
			}
			const mean = sum / opaque;
			let variance = 0;
			for (let level = 0; level < 256; level++) {
				variance += histogram[level] * (level - mean) ** 2;
			}
			variance /= opaque;
			let below = 0, sumBelow = 0, bestBetween = -1, threshold = 128, darkCount = 0;
			for (let level = 0; level < 256; level++) {
				below += histogram[level];
				sumBelow += level * histogram[level];
				const above = opaque - below;
				if (below === 0 || above === 0) {
					continue;
				}
				const between = (below * above * (sumBelow / below - (sum - sumBelow) / above) ** 2) / (opaque * opaque);
				if (between > bestBetween) {
					bestBetween = between;
					threshold = level;
					darkCount = below;
				}
			}
			let nearThreshold = 0;
			for (let level = Math.max(0, threshold - 16); level <= Math.min(255, threshold + 16); level++) {
				nearThreshold += histogram[level];
			}
			const darkShare = darkCount / opaque;
			// Two clear tones with a real minority; a gradient piles up around the threshold instead
			if (bestBetween / variance > 0.6 && Math.min(darkShare, 1 - darkShare) > 0.06 && nearThreshold / opaque < 0.4) {
				split = { threshold, keepDark: darkShare >= 0.5 };
			}
		}

		for (let i = 0; i < data.length; i += 4) {
			let alpha = data[i + 3];
			if (plate) {
				// Plate dropped, what stands out from it kept
				alpha *= smoothstep(45, 120, distance(i, dominant));
			} else if (dominantShare >= 0.35) {
				// Body kept, details of another colour cut out
				alpha *= 1 - smoothstep(70, 150, distance(i, dominant));
			} else if (split) {
				const light = smoothstep(split.threshold - 12, split.threshold + 12, luma(i));
				alpha *= split.keepDark ? 1 - light : light;
			}
			data[i] = color[0];
			data[i + 1] = color[1];
			data[i + 2] = color[2];
			data[i + 3] = alpha;
		}
	}

	// Resolves to a PNG data URL of the logo in the given CSS colour ("rgb(r, g, b)")
	function monochromeIcon(source, cssColor) {
		const key = `${cssColor}|${source}`;
		if (!monochromeCache.has(key)) {
			monochromeCache.set(key, new Promise((resolve) => {
				const image = new Image();
				image.onload = () => {
					const size = Math.max(image.naturalWidth, image.naturalHeight);
					const canvas = document.createElement('canvas');
					canvas.width = size;
					canvas.height = size;
					const ctx = canvas.getContext('2d', { willReadFrequently: true });
					ctx.drawImage(image, (size - image.naturalWidth) / 2, (size - image.naturalHeight) / 2);
					const pixels = ctx.getImageData(0, 0, size, size);
					const color = (cssColor.match(/\d+/g) || [243, 233, 234]).slice(0, 3).map(Number);
					monochromePixels(pixels.data, size, color);
					ctx.putImageData(pixels, 0, 0);
					resolve(canvas.toDataURL('image/png'));
				};
				// Unreadable image: show it as it is
				image.onerror = () => resolve(source);
				image.src = source;
			}));
		}
		return monochromeCache.get(key);
	}

	// Fills a container with a built-in glyph or an app logo drawn in the glyphs' colour
	function renderIcon(container, icon) {
		const resolved = icon || { kind: 'builtin', value: 'app' };
		if (resolved.kind === 'image') {
			const img = document.createElement('img');
			img.alt = '';
			// Hidden until recoloured, so the colour logo never flashes
			img.style.visibility = 'hidden';
			container.replaceChildren(img);
			monochromeIcon(resolved.value, getComputedStyle(container).color).then((url) => {
				img.src = url;
				img.style.visibility = '';
			});
		} else {
			const svg = document.createElementNS(SVG_NS, 'svg');
			svg.setAttribute('viewBox', '0 0 24 24');
			// Own glyphs first (names saved by older versions), then the Lucide selection (js/icons.js)
			svg.innerHTML = BUILTIN_ICONS[resolved.value] || (window.LUCIDE_ICONS || {})[resolved.value] || BUILTIN_ICONS.app;
			container.replaceChildren(svg);
		}
	}

	// Short tap launches, holding the finger opens the shortcut editor
	function attachPress(button, index) {
		let timer = 0;
		let origin = null;
		let opened = false;
		const cancel = () => {
			clearTimeout(timer);
			timer = 0;
			button.classList.remove('pressing');
		};
		button.addEventListener('pointerdown', (event) => {
			opened = false;
			origin = { x: event.clientX, y: event.clientY };
			button.classList.add('pressing');
			timer = setTimeout(() => {
				cancel();
				opened = true;
				openEditor(index);
			}, LONG_PRESS_MS);
		});
		button.addEventListener('pointermove', (event) => {
			if (timer && origin && Math.hypot(event.clientX - origin.x, event.clientY - origin.y) > PRESS_MOVE_TOLERANCE) {
				cancel();
			}
		});
		['pointerup', 'pointerleave', 'pointercancel'].forEach((name) => button.addEventListener(name, cancel));
		button.addEventListener('click', () => {
			if (opened) {
				opened = false;
				return;
			}
			send({ type: 'launch', action: String(index) });
		});
	}

	function buildDock(shortcuts) {
		dockShortcuts = shortcuts;
		const dock = $('dock');
		dock.replaceChildren(...shortcuts.map((shortcut, index) => {
			const button = document.createElement('button');
			button.type = 'button';
			button.className = 'dock-item';
			button.setAttribute('aria-label', shortcut.label || `Raccourci ${index + 1}`);
			attachPress(button, index);

			const iconBox = document.createElement('span');
			iconBox.className = 'dock-icon';
			renderIcon(iconBox, shortcut.icon);

			const label = document.createElement('span');
			label.className = 'dock-label';
			label.textContent = shortcut.label || '';
			button.append(iconBox, label);
			return button;
		}));
	}

	// ---------- Shortcut editor ----------
	const APPS_PREFIX = 'shell:AppsFolder\\';
	const EDITOR_ICONS = ['app', 'phone', 'chat', 'music', 'gamepad', 'pulse', 'globe', 'folder', 'video', 'code', 'headset', 'camera'];
	const LETTERS = ['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'];
	let apps = null;
	const editor = { index: -1, appId: null, appName: null, appIcon: null, icon: 'app' };

	const letterOf = (name) => {
		const first = name.normalize('NFD').replace(/[̀-ͯ]/g, '').charAt(0).toUpperCase();
		return first >= 'A' && first <= 'Z' ? first : '#';
	};

	function openEditor(index) {
		const shortcut = dockShortcuts[index] || {};
		editor.index = index;
		editor.appId = shortcut.target && shortcut.target.startsWith(APPS_PREFIX) ? shortcut.target.slice(APPS_PREFIX.length) : null;
		editor.appName = editor.appId ? shortcut.label : null;
		editor.appIcon = shortcut.icon && shortcut.icon.kind === 'image' ? shortcut.icon.value : null;
		editor.icon = shortcut.icon && shortcut.icon.kind === 'builtin' ? shortcut.icon.value : 'app';
		text('editorTitle', `Raccourci ${index + 1}`);
		$('editor').hidden = false;
		if (apps) {
			renderAppList();
		} else {
			$('editorApps').innerHTML = '<div class="editor-status">Chargement des applications…</div>';
			$('editorLetters').replaceChildren();
			requestApps();
		}
		buildEditorIcons();
		renderEditorState();
		revealSelectedIcon();
	}

	function closeEditor() {
		$('editor').hidden = true;
	}

	function requestApps() {
		if (host) {
			host.postMessage({ type: 'listApps' });
		} else {
			// Design mode: a few sample entries
			onHostMessage({
				type: 'apps',
				apps: ['Discord', 'Microsoft Edge', 'Spotify', 'Steam', 'Visual Studio Code', 'WhatsApp'].map((name) => ({ id: name, name, icon: null }))
			});
		}
	}

	function renderAppList() {
		const list = $('editorApps');
		if (!apps || apps.length === 0) {
			list.innerHTML = '<div class="editor-status">Aucune application trouvée</div>';
			return;
		}
		const firstRowByLetter = {};
		list.replaceChildren(...apps.map((app) => {
			const row = document.createElement('button');
			row.type = 'button';
			row.className = 'editor-app';
			row.dataset.id = app.id;
			const letter = letterOf(app.name);
			if (!firstRowByLetter[letter]) {
				firstRowByLetter[letter] = row;
			}
			if (app.icon) {
				const img = document.createElement('img');
				img.src = app.icon;
				img.alt = '';
				img.loading = 'lazy';
				row.appendChild(img);
			} else {
				const placeholder = document.createElement('i');
				placeholder.className = 'placeholder';
				row.appendChild(placeholder);
			}
			const name = document.createElement('span');
			name.textContent = app.name;
			row.appendChild(name);
			row.addEventListener('click', () => {
				editor.appId = app.id;
				editor.appName = app.name;
				editor.appIcon = app.icon;
				renderEditorState();
			});
			return row;
		}));

		$('editorLetters').replaceChildren(...LETTERS.map((letter) => {
			const button = document.createElement('button');
			button.type = 'button';
			button.textContent = letter;
			const target = firstRowByLetter[letter];
			button.disabled = !target;
			if (target) {
				button.addEventListener('click', () => {
					list.scrollTop = target.offsetTop - list.offsetTop;
				});
			}
			return button;
		}));
		renderEditorState();
		const selected = list.querySelector('.editor-app.selected');
		if (selected) {
			list.scrollTop = selected.offsetTop - list.offsetTop - 120;
		}
	}

	function currentEditorIcon() {
		if (editor.icon === 'app') {
			return editor.appIcon ? { kind: 'image', value: editor.appIcon } : { kind: 'builtin', value: 'app' };
		}
		return { kind: 'builtin', value: editor.icon };
	}

	function renderEditorState() {
		text('editorAppName', editor.appName || 'Choisis une application');
		renderIcon($('editorPreviewIcon'), currentEditorIcon());
		$('editorSave').disabled = !editor.appId;
		document.querySelectorAll('.editor-app').forEach((row) => row.classList.toggle('selected', row.dataset.id === editor.appId));

		for (const tile of editorIconTiles.values()) {
			tile.classList.toggle('selected', editor.icon === tile.dataset.icon);
		}
		const appTile = editorIconTiles.get('app');
		if (appTile && appTile.dataset.source !== (editor.appIcon || '')) {
			appTile.dataset.source = editor.appIcon || '';
			renderIcon(appTile, editor.appIcon ? { kind: 'image', value: editor.appIcon } : { kind: 'builtin', value: 'app' });
		}
	}

	// Built once per opening (~160 tiles): rebuilding at each tap would also reset the scroll
	const editorIconTiles = new Map();
	function buildEditorIcons() {
		const sections = [{ label: 'Essentiels', icons: EDITOR_ICONS }, ...(window.LUCIDE_GROUPS || [])];
		editorIconTiles.clear();
		const children = [];
		for (const section of sections) {
			const heading = document.createElement('span');
			heading.className = 'editor-icons-group';
			heading.textContent = section.label;
			children.push(heading);
			for (const name of section.icons) {
				const tile = document.createElement('button');
				tile.type = 'button';
				tile.className = 'editor-icon';
				tile.dataset.icon = name;
				tile.setAttribute('aria-label', name === 'app' ? "Icône de l'application" : name);
				if (name !== 'app') {
					renderIcon(tile, { kind: 'builtin', value: name });
				}
				tile.addEventListener('click', () => {
					editor.icon = name;
					renderEditorState();
				});
				editorIconTiles.set(name, tile);
				children.push(tile);
			}
		}
		$('editorIcons').replaceChildren(...children);
	}

	// Brings the current choice into view, within the icon grid only
	function revealSelectedIcon() {
		const grid = $('editorIcons');
		const tile = editorIconTiles.get(editor.icon);
		grid.scrollTop = tile && editor.icon !== 'app' ? tile.offsetTop - 40 : 0;
	}

	function saveEditor() {
		if (!editor.appId) {
			return;
		}
		const label = editor.appName || 'Appli';
		if (host) {
			host.postMessage({ type: 'saveShortcut', index: editor.index, appId: editor.appId, label, icon: editor.icon });
		} else {
			dockShortcuts[editor.index] = { label, target: APPS_PREFIX + editor.appId, icon: currentEditorIcon() };
			buildDock(dockShortcuts);
			showToast(`${label} enregistré`);
		}
		closeEditor();
	}

	$('editorClose').addEventListener('click', closeEditor);
	$('editorCancel').addEventListener('click', closeEditor);
	$('editorSave').addEventListener('click', saveEditor);

	// ---------- Now playing ----------
	const SOURCE_ICONS = { music: '#i-note', video: '#i-play', live: '#i-live' };
	let coverKey = null;

	const formatTime = (seconds) => {
		const total = Math.max(0, Math.floor(seconds));
		const h = Math.floor(total / 3600);
		const m = Math.floor((total % 3600) / 60);
		const s = total % 60;
		return h ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
	};

	function drawArtPlaceholder(label) {
		const canvas = $('mediaArt');
		const ctx = canvas.getContext('2d');
		// Artwork is designed on a 132 px grid and scaled to the canvas
		const size = 132;
		ctx.setTransform(canvas.width / size, 0, 0, canvas.height / size, 0, 0);
		const g = ctx.createLinearGradient(0, 0, size, size);
		g.addColorStop(0, '#2a0508');
		g.addColorStop(1, '#070102');
		ctx.fillStyle = g;
		ctx.fillRect(0, 0, size, size);
		haze(ctx, 52, 46, 90, 'rgba(255, 31, 51, 0.45)');
		ctx.lineCap = 'round';
		for (const [x1, y1, x2, y2] of [[26, 110, 106, 22], [26, 22, 106, 110]]) {
			ctx.shadowColor = '#ff1f33';
			ctx.shadowBlur = 14;
			ctx.strokeStyle = '#ff1f33';
			ctx.lineWidth = 6;
			ctx.beginPath();
			ctx.moveTo(x1, y1);
			ctx.lineTo(x2, y2);
			ctx.stroke();
			ctx.shadowBlur = 0;
			ctx.strokeStyle = '#fff4f4';
			ctx.lineWidth = 2;
			ctx.stroke();
		}
		if (label) {
			ctx.fillStyle = 'rgba(243, 233, 234, 0.85)';
			ctx.font = "600 13px 'Rajdhani', sans-serif";
			ctx.textAlign = 'center';
			ctx.fillText(label.toUpperCase(), size / 2, size - 10);
		}
		ctx.setTransform(1, 0, 0, 1, 0, 0);
	}

	function renderMedia(media) {
		const box = $('media');
		box.classList.toggle('idle', !media);
		box.classList.toggle('playing', !!(media && media.playing));
		const chip = $('mediaSource');
		if (!media) {
			text('mediaTitle', 'Aucune lecture');
			text('mediaArtist', ' ');
			chip.querySelector('span').textContent = '--';
			$('mediaProgress').hidden = false;
			$('mediaLive').hidden = true;
			$('mediaFill').style.width = '0%';
			text('mediaPos', '0:00');
			text('mediaDur', '0:00');
			setPlayIcon(false);
			showCover(null, null);
			return;
		}
		chip.querySelector('span').textContent = media.source || '--';
		chip.querySelector('use').setAttribute('href', SOURCE_ICONS[media.live ? 'live' : media.kind] || SOURCE_ICONS.music);
		text('mediaTitle', media.title || 'Sans titre');
		text('mediaArtist', media.artist || ' ');
		$('mediaProgress').hidden = !!media.live;
		$('mediaLive').hidden = !media.live;
		if (!media.live && isNum(media.durationSec) && media.durationSec > 0) {
			text('mediaPos', formatTime(media.positionSec || 0));
			text('mediaDur', formatTime(media.durationSec));
			$('mediaFill').style.width = `${clamp(((media.positionSec || 0) / media.durationSec) * 100, 1, 100)}%`;
		}
		setPlayIcon(media.playing);
		if (media.artKey !== coverKey && !media.artKey) {
			showCover(null, media.source);
		}
	}

	function setPlayIcon(playing) {
		$('playBtn').setAttribute('aria-label', playing ? 'Pause' : 'Lecture');
		$('playIcon').innerHTML = playing ? '<path d="M7 5h4v14H7zM13 5h4v14h-4z"/>' : '<path d="M7 4v16l13-8z"/>';
	}

	function showCover(dataUrl, label) {
		const cover = $('mediaCover');
		if (dataUrl) {
			cover.src = dataUrl;
			cover.hidden = false;
		} else {
			cover.hidden = true;
			cover.removeAttribute('src');
			coverKey = null;
			drawArtPlaceholder(label);
		}
	}

	// ---------- Snapshot intake ----------
	function applySnapshot(s) {
		current = s;
		const cpu = s.cpu || {};
		const gpu = s.gpu || {};
		const net = s.network || {};

		cpuHist.push(isNum(cpu.load) ? cpu.load : 0);
		cpuHist.shift();
		lastTick = performance.now();

		renderCpu(cpu);
		renderGpu(gpu);
		if (isNum(gpu.vramTotalGb) && gpu.vramTotalGb !== vramTicksFor) {
			vramTicksFor = gpu.vramTotalGb;
			renderVramTicks(vramTicksFor);
		}
		renderMemory(s.ram || {}, s.disks || []);
		renderNetwork(net);
		renderMedia(s.media || null);
	}

	function applyConfig(config) {
		if (config.cpuMaker) text('cpuMaker', config.cpuMaker);
		if (config.cpuModel) text('cpuModel', config.cpuModel);
		if (config.gpuMaker) text('gpuMaker', config.gpuMaker);
		if (config.gpuModel) text('gpuModel', config.gpuModel);
		if (Array.isArray(config.shortcuts)) buildDock(config.shortcuts);
	}

	function onHostMessage(message) {
		if (!message || typeof message !== 'object') {
			return;
		}
		if (message.type === 'snapshot') {
			applySnapshot(message);
		} else if (message.type === 'cover') {
			coverKey = message.key;
			showCover(message.dataUrl, null);
		} else if (message.type === 'config') {
			applyConfig(message);
		} else if (message.type === 'toast') {
			showToast(message.text);
		} else if (message.type === 'update') {
			renderUpdate(message);
		} else if (message.type === 'speedtest') {
			renderSpeedTest(message);
		} else if (message.type === 'apps') {
			apps = Array.isArray(message.apps) ? message.apps : [];
			if (!$('editor').hidden) {
				renderAppList();
			}
		}
	}

	// ---------- Clock ----------
	let lastClock = '';
	function updateClock() {
		const date = new Date();
		const stamp = `${date.getHours()}:${date.getMinutes()}`;
		if (stamp === lastClock) {
			return;
		}
		lastClock = stamp;
		text('hh', String(date.getHours()).padStart(2, '0'));
		text('mm', String(date.getMinutes()).padStart(2, '0'));
		text('date', date.toLocaleDateString('fr-FR', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }));
	}

	// ---------- Ignition timing for canvas widgets ----------
	const igniteStart = performance.now();
	function ignition(now, delay, duration) {
		if (!motion) {
			return 1;
		}
		const t = clamp((now - igniteStart - delay) / duration, 0, 1);
		return 1 - Math.pow(1 - t, 3);
	}

	// ---------- Kyber ring: last 72 s of CPU load ----------
	const ringCtx = $('cpuRing').getContext('2d');
	const RING_SIZE = 360;
	const RING_CX = RING_SIZE / 2;
	const RING_CY = RING_SIZE / 2;
	const RING_R0 = 118;
	const RING_SPIKE_MAX = 44;
	const RING_LENS = 100;
	const RING_STEP = (Math.PI * 2) / RING;

	// The glowing spikes only change once per second: they live on their own canvas, redrawn at
	// each sample and turned in between by a CSS transform, which the compositor applies without
	// repainting. The lens and the load blade on top are redrawn only when the eased load moves.
	const spikeCanvas = $('cpuSpikes');
	const spikeCtx = spikeCanvas.getContext('2d');
	let spikeGrow = -1;
	let spikeColor = '';
	let spikeTick = -1;
	let spikeAngle = NaN;
	let ringKey = '';

	function ringColor() {
		const cpuTemp = current && current.cpu ? current.cpu.temp : null;
		return isNum(cpuTemp) && cpuTemp >= 85 ? '255, 122, 69' : '255, 31, 51';
	}

	function renderSpikes(color, grow) {
		const ctx = spikeCtx;
		ctx.clearRect(0, 0, RING_SIZE, RING_SIZE);
		ctx.lineCap = 'round';
		ctx.lineWidth = 2.6;
		ctx.shadowColor = `rgb(${color})`;
		ctx.shadowBlur = 7;
		const inner = RING_R0 + 8;
		for (let i = 0; i < RING; i++) {
			const value = cpuHist[RING - 1 - i];
			const angle = -Math.PI / 2 + i * RING_STEP;
			const length = (5 + (value / 100) * RING_SPIKE_MAX) * grow;
			const alpha = 1 - (i / RING) * 0.78;
			ctx.strokeStyle = `rgba(${color}, ${alpha})`;
			ctx.beginPath();
			ctx.moveTo(RING_CX + Math.cos(angle) * inner, RING_CY + Math.sin(angle) * inner);
			ctx.lineTo(RING_CX + Math.cos(angle) * (inner + length), RING_CY + Math.sin(angle) * (inner + length));
			ctx.stroke();
		}
	}

	function drawRing(now, progress) {
		const ctx = ringCtx;
		const cx = RING_CX;
		const cy = RING_CY;
		const r0 = RING_R0;
		const color = ringColor();
		const grow = ignition(now, 200, 900);

		if (lastTick !== spikeTick || grow !== spikeGrow || color !== spikeColor) {
			renderSpikes(color, grow);
			spikeTick = lastTick;
			spikeGrow = grow;
			spikeColor = color;
		}
		const angle = Math.round(progress * RING_STEP * 10000) / 10000;
		if (angle !== spikeAngle) {
			spikeAngle = angle;
			spikeCanvas.style.transform = `rotate(${angle}rad)`;
		}

		// Blade sweep quantised well below a pixel at the ring's radius
		const sweep = (shown.cpu / 100) * grow;
		const key = `${Math.round(sweep * 4000)}|${color}`;
		if (key === ringKey) {
			return;
		}
		ringKey = key;
		ctx.clearRect(0, 0, RING_SIZE, RING_SIZE);

		// Dark lens behind the load value
		const lens = ctx.createRadialGradient(cx, cy, 16, cx, cy, RING_LENS);
		lens.addColorStop(0, 'rgba(3, 1, 4, 0.92)');
		lens.addColorStop(0.8, 'rgba(8, 2, 4, 0.85)');
		lens.addColorStop(1, 'rgba(20, 4, 8, 0.6)');
		ctx.fillStyle = lens;
		ctx.beginPath();
		ctx.arc(cx, cy, RING_LENS, 0, Math.PI * 2);
		ctx.fill();

		ctx.strokeStyle = `rgba(${color}, 0.2)`;
		ctx.lineWidth = 1;
		ctx.beginPath();
		ctx.arc(cx, cy, r0 + 4, 0, Math.PI * 2);
		ctx.stroke();

		// Current load as a curved blade
		const start = -Math.PI / 2;
		const end = start + sweep * Math.PI * 2;
		const arcR = r0 - 12;
		ctx.shadowBlur = 0;
		ctx.strokeStyle = 'rgba(255, 255, 255, 0.06)';
		ctx.lineWidth = 8;
		ctx.beginPath();
		ctx.arc(cx, cy, arcR, 0, Math.PI * 2);
		ctx.stroke();
		ctx.shadowColor = `rgb(${color})`;
		ctx.shadowBlur = 26;
		ctx.strokeStyle = `rgb(${color})`;
		ctx.beginPath();
		ctx.arc(cx, cy, arcR, start, end);
		ctx.stroke();
		ctx.shadowBlur = 6;
		ctx.strokeStyle = '#fff4f4';
		ctx.lineWidth = 3;
		ctx.beginPath();
		ctx.arc(cx, cy, arcR, start, end);
		ctx.stroke();
		ctx.shadowBlur = 0;
	}

	// ---------- Embers from the lava + volcano eruptions ----------
	// Embers live on a half-resolution canvas covering only the lava band of the background
	const FX_TOP = 450;
	const FX_SCALE = 0.5;
	const fxCanvas = $('fx');
	const fxCtx = fxCanvas.getContext('2d');
	const embers = [];
	const MAX_DRIFTING_EMBERS = 40;
	const EMBER_SOURCES = [
		{ x: LAVA_POOL.x, y: LAVA_POOL.y, rx: 170, ry: 50, weight: 0.35 },
		{ x: FISSURE.x, y: FISSURE.y, rx: 25, ry: 10, weight: 0.2 },
		{ x: CRACKS.x, y: CRACKS.y, rx: 110, ry: 70, weight: 0.3 },
		{ x: VOLCANO.x, y: VOLCANO.y, rx: 8, ry: 4, weight: 0.15 }
	];

	function spawnEmber() {
		let pick = Math.random();
		let source = EMBER_SOURCES[EMBER_SOURCES.length - 1];
		for (const candidate of EMBER_SOURCES) {
			if (pick < candidate.weight) {
				source = candidate;
				break;
			}
			pick -= candidate.weight;
		}
		return {
			x: source.x + (Math.random() - 0.5) * 2 * source.rx,
			y: source.y + (Math.random() - 0.5) * 2 * source.ry,
			// Speeds are tuned per 60 fps tick; the loop scales them by elapsed time
			vy: -(0.25 + Math.random() * 0.85),
			phase: Math.random() * Math.PI * 2,
			life: 0,
			maxLife: 180 + Math.random() * 280,
			size: 0.7 + Math.random() * 1.7,
			green: 60 + Math.random() * 100
		};
	}

	// The volcano spits a fan of bright sparks every 12 to 25 s; they arc up and fall back
	let nextEruption = performance.now() + 6000;
	function erupt() {
		const count = 22 + Math.floor(Math.random() * 12);
		for (let i = 0; i < count; i++) {
			embers.push({
				x: VOLCANO.x + (Math.random() - 0.5) * 10,
				y: VOLCANO.y - 4,
				vx: (Math.random() - 0.6) * 1.6,
				vy: -(1.6 + Math.random() * 1.9),
				gravity: 0.028,
				phase: 0,
				life: 0,
				maxLife: 70 + Math.random() * 70,
				size: 1.2 + Math.random() * 1.6,
				green: 150 + Math.random() * 90,
				burst: true
			});
		}
	}

	function drawFx(now, ticks) {
		const ctx = fxCtx;
		ctx.clearRect(0, 0, fxCanvas.width, fxCanvas.height);
		if (!motion) {
			return;
		}

		if (now > nextEruption) {
			erupt();
			nextEruption = now + 12000 + Math.random() * 13000;
		}
		let drifting = 0;
		for (const e of embers) {
			if (!e.burst) {
				drifting++;
			}
		}
		for (; drifting < MAX_DRIFTING_EMBERS; drifting++) {
			embers.push(spawnEmber());
		}

		ctx.globalCompositeOperation = 'lighter';
		for (let i = embers.length - 1; i >= 0; i--) {
			const e = embers[i];
			e.life += ticks;
			if (e.burst) {
				e.vy += e.gravity * ticks;
				e.x += e.vx * ticks;
			} else {
				e.x += Math.sin(e.phase + e.life / 40) * 0.35 * ticks;
			}
			e.y += e.vy * ticks;
			const t = e.life / e.maxLife;
			if (t >= 1 || e.y < FX_TOP) {
				embers.splice(i, 1);
				continue;
			}
			const alpha = Math.sin(t * Math.PI) * 0.85;
			ctx.fillStyle = `rgba(255, ${e.green | 0}, 60, ${alpha})`;
			ctx.beginPath();
			ctx.arc(e.x * FX_SCALE, (e.y - FX_TOP) * FX_SCALE, Math.max(e.size * FX_SCALE, 0.6), 0, Math.PI * 2);
			ctx.fill();
		}
		ctx.globalCompositeOperation = 'source-over';
	}

	// ---------- Looping CSS effects, sampled here instead of CSS keyframes ----------
	// Set on the frame, an inherited custom property would restyle the whole page at every frame:
	// each looping value only goes to the elements whose CSS reads it
	const cssVars = new WeakMap();
	function setVar(elements, name, value) {
		const rounded = Math.round(value * 100) / 100;
		for (const element of elements) {
			let vars = cssVars.get(element);
			if (!vars) {
				vars = {};
				cssVars.set(element, vars);
			}
			if (vars[name] !== rounded) {
				vars[name] = rounded;
				element.style.setProperty(name, rounded);
			}
		}
	}
	const flickerTargets = [...document.querySelectorAll('.blade-h')];
	const breatheTargets = [...document.querySelectorAll('.time .colon')];
	// Live collections: hot metric tiles (glowing value) and the network panel in alert
	const hotTiles = frame.getElementsByClassName('hot');
	const alertPanels = frame.getElementsByClassName('alert');

	// Blade flicker: steady with three short dips per 2.7 s cycle
	function flickerAt(now) {
		const p = (now % 2700) / 2700;
		if (p >= 0.12 && p < 0.14) return 0.93;
		if (p >= 0.47 && p < 0.49) return 0.97;
		if (p >= 0.71 && p < 0.73) return 0.9;
		return 1;
	}
	const breatheAt = (now, period) => 0.35 + 0.55 * (0.5 - 0.5 * Math.cos((2 * Math.PI * now) / period));

	const eqBars = [...document.querySelectorAll('.eq i')];
	const eqPhases = [0, 0.33, 0.66, 0.17];
	const eqTransforms = eqBars.map(() => '');

	// Each lava spot mixes a long swell with a gentle ripple, periods offset so they never pulse in sync
	const lavaSpots = [...document.querySelectorAll('.lava-spot')].map((element, i) => ({
		element,
		slow: 10000 + i * 1700,
		fast: 3800 + i * 600,
		phase: i * 1.7,
		opacity: -1
	}));
	const smokeLayers = [...document.querySelectorAll('.smoke')].map((element) => ({
		element,
		period: Number(element.dataset.period) * 1000,
		amplitude: Number(element.dataset.amplitude),
		offset: -1
	}));

	function animateBackground(now) {
		for (const spot of lavaSpots) {
			const slow = 0.5 + 0.5 * Math.sin((2 * Math.PI * now) / spot.slow + spot.phase);
			const fast = 0.5 + 0.5 * Math.sin((2 * Math.PI * now) / spot.fast + spot.phase * 2);
			const opacity = Math.round((0.3 + 0.58 * slow + 0.12 * fast) * 50) / 50;
			if (opacity !== spot.opacity) {
				spot.opacity = opacity;
				spot.element.style.opacity = opacity;
			}
		}
		for (const smoke of smokeLayers) {
			const offset = Math.round(Math.sin((2 * Math.PI * now) / smoke.period) * smoke.amplitude);
			if (offset !== smoke.offset) {
				smoke.offset = offset;
				smoke.element.style.transform = `translateX(${offset}px)`;
			}
		}
	}

	// Wi-Fi: a light wave travels from the source dot outwards through the lit arcs every 4 s.
	// Lit arcs never fade below 70 %, so the number of bars never seems to change.
	const SIGNAL_PERIOD = 4000;
	const signalArcs = [1, 2, 3, 4].map((i) => ({ element: $(`arc${i}`), opacity: -1 }));
	const signalSource = document.querySelector('.signal .source');
	let signalScale = -1;

	function animateSignal(now) {
		const cycle = (now % SIGNAL_PERIOD) / SIGNAL_PERIOD;
		signalArcs.forEach((arc, i) => {
			// Wave front reaches arc i at 0.15 + i * 0.15 of the cycle, with a soft bell around it
			const distance = cycle - (0.15 + i * 0.15);
			const wave = Math.exp(-(distance * distance) / 0.006);
			const opacity = arc.element.classList.contains('on') ? Math.round((0.7 + 0.3 * wave) * 50) / 50 : 1;
			if (opacity !== arc.opacity) {
				arc.opacity = opacity;
				arc.element.style.opacity = opacity;
			}
		});
		// The source dot swells just before each wave leaves it
		const pulse = Math.exp(-((cycle - 0.06) * (cycle - 0.06)) / 0.004);
		const scale = Math.round((1 + 0.35 * pulse) * 50) / 50;
		if (scale !== signalScale) {
			signalScale = scale;
			signalSource.style.transform = `scale(${scale})`;
		}
	}

	function animateCss(now) {
		if (!motion) {
			return;
		}
		animateBackground(now);
		setVar(flickerTargets, '--flicker', flickerAt(now));
		setVar(breatheTargets, '--breathe-2', breatheAt(now, 4000));
		animateSignal(now);
		if (hotTiles.length > 0 || alertPanels.length > 0) {
			const alarm = 0.5 - 0.5 * Math.cos((2 * Math.PI * now) / 1200);
			setVar(hotTiles, '--alarm', alarm);
			setVar(alertPanels, '--alarm', alarm);
		}
		const playing = current && current.media && current.media.playing;
		eqBars.forEach((bar, i) => {
			const level = playing ? 0.2 + 0.8 * (0.5 - 0.5 * Math.cos(2 * Math.PI * (now / 1800 + eqPhases[i]))) : 0.3;
			const transform = `scaleY(${level.toFixed(2)})`;
			if (eqTransforms[i] !== transform) {
				eqTransforms[i] = transform;
				bar.style.transform = transform;
			}
		});
	}

	// ---------- Main loop, capped at 24 fps to stay light while gaming ----------
	// A timer instead of requestAnimationFrame: rAF would wake the page at every 73 Hz vsync just to skip it
	let lastFrame = performance.now();
	function loop() {
		const now = performance.now();
		setTimeout(loop, Math.max(0, FRAME_INTERVAL - (performance.now() - now)));
		const elapsed = now - lastFrame;
		lastFrame = now;
		const ticks = Math.min(elapsed / (1000 / 60), 6);
		const progress = motion ? clamp((now - lastTick) / 1000, 0, 1) : 0;
		const target = current && current.cpu && isNum(current.cpu.load) ? current.cpu.load : 0;
		shown.cpu += (target - shown.cpu) * Math.min(0.08 * ticks, 1);
		updateClock();
		animateCss(now);
		drawRing(now, progress);
		drawFx(now, ticks);
		drawSpeedGauge(ticks);
	}

	// ---------- Touch actions ----------
	function showToast(message) {
		const toast = $('toast');
		toast.textContent = message;
		toast.classList.add('show');
		clearTimeout(showToast.timer);
		showToast.timer = setTimeout(() => toast.classList.remove('show'), 1600);
	}

	// Update notice: { stage: available | downloading | failed | ..., version, progress }
	function renderUpdate(update) {
		const banner = $('updateBanner');
		const labels = {
			available: `Mise à jour ${update.version} · toucher pour installer`,
			downloading: `Téléchargement de la ${update.version} · ${update.progress || 0} %`,
			failed: `Échec de la ${update.version} · toucher pour réessayer`
		};
		const label = update.version ? labels[update.stage] : null;
		banner.hidden = !label;
		banner.disabled = update.stage === 'downloading';
		if (label) {
			text('updateText', label);
		}
	}
	$('updateBanner').addEventListener('click', () => {
		// The demo banner announces a made-up version: nothing to install
		if (simulating) {
			showToast('Mode démo : mise à jour simulée');
			return;
		}
		send({ type: 'update', action: 'install' });
	});

	function send(message) {
		if (host) {
			host.postMessage(message);
		} else {
			showToast(`Mode design : ${message.type} ${message.action}`);
		}
	}

	document.querySelectorAll('[data-action]').forEach((element) => {
		element.addEventListener('click', () => send({ type: 'launch', action: element.dataset.action }));
	});
	document.querySelectorAll('[data-media]').forEach((button) => {
		button.addEventListener('click', () => send({ type: 'media', action: button.dataset.media }));
	});
	// No context menu or text selection on a touch panel
	document.addEventListener('contextmenu', (event) => event.preventDefault());

	// ---------- Simulator: design mode (no host) or "?demo=" scenarios (idle, stress) ----------
	const SCENARIOS = {
		idle: { cpu: 14, cpuSpread: 9, gpu: 5, vram: 6, ram: 19.3, offline: false },
		// Everything maxed out and the connection lost, to preview every alarm state at once
		stress: { cpu: 100, cpuSpread: 0, gpu: 100, vram: 96, ram: 55, offline: true, fps: 144 }
	};

	// Set while simulated data replaces the host's: design mode or a demo scenario
	let simulating = false;

	function startSimulator(scenarioName) {
		simulating = true;
		const scenario = SCENARIOS[scenarioName] || SCENARIOS.idle;
		const sim = { cpu: scenario.cpu, gpu: scenario.gpu, cpuTemp: 30 + scenario.cpu * 0.6, gpuTemp: 31 + scenario.gpu * 0.52, vram: scenario.vram, ram: scenario.ram };
		let position = 72;
		let offlineSec = 74;
		const walk = (value, target, spread, min, max) => clamp(value + (target - value) * 0.25 + (Math.random() - 0.5) * spread, min, max);
		if (scenario.cpu > 50) {
			cpuHist.forEach((_, i) => {
				cpuHist[i] = clamp(scenario.cpu - 12 + Math.random() * 14, 0, 100);
			});
		}
		onHostMessage({
			type: 'config',
			shortcuts: [
				{ label: 'WhatsApp', icon: { kind: 'builtin', value: 'phone' } },
				{ label: 'Spotify', icon: { kind: 'builtin', value: 'music' } },
				{ label: 'Discord', icon: { kind: 'builtin', value: 'chat' } },
				{ label: 'Tâches', icon: { kind: 'builtin', value: 'pulse' } }
			]
		});
		// The update banner is part of the preview too
		renderUpdate({ stage: 'available', version: '2.0.0' });
		setInterval(() => {
			sim.cpu = scenario.cpu >= 100 ? 100 : walk(sim.cpu, scenario.cpu, scenario.cpuSpread, 1, 100);
			sim.gpu = scenario.gpu >= 100 ? 100 : walk(sim.gpu, scenario.gpu, 5, 0, 100);
			sim.cpuTemp += (30 + sim.cpu * 0.63 - sim.cpuTemp) * 0.15;
			sim.gpuTemp += (31 + sim.gpu * 0.52 - sim.gpuTemp) * 0.08;
			sim.vram = walk(sim.vram, scenario.vram, 1.5, 2, 98);
			sim.ram = walk(sim.ram, scenario.ram, 0.2, 8, 60);
			position = (position + 1) % 182;
			offlineSec += 1;
			const burst = Math.random() < 0.15;
			const down = scenario.offline ? Math.random() * 400 : (burst ? Math.random() * 22 : Math.random() * 0.7) * MB;
			onHostMessage({
				type: 'snapshot',
				cpu: { load: sim.cpu, temp: sim.cpuTemp, power: 30 + sim.cpu * 1.25 },
				gpu: { load: sim.gpu, temp: sim.gpuTemp, power: 25 + sim.gpu * 4.15, vramPercent: sim.vram, vramTotalGb: 24, fps: scenario.fps ? scenario.fps - Math.random() * 12 : null },
				ram: { usedGb: sim.ram, totalGb: 62 },
				disks: [
					{ name: 'C:', label: 'Windows', usedGb: 539.4, totalGb: 818.3, temp: 41, disk: 0 },
					{ name: 'D:', label: 'Données', usedGb: 454.7, totalGb: 1043.6, temp: 41, disk: 0 },
					{ name: 'E:', label: 'Jeux', usedGb: 1727.1, totalGb: 3726, temp: 36, disk: 1 }
				],
				network: {
					downBytesPerSec: down,
					upBytesPerSec: scenario.offline ? 0 : down * 0.06 + Math.random() * 0.15 * MB,
					pingMs: scenario.offline ? null : 9 + Math.random() * 5,
					wifi: { ssid: 'DarkWifi', signal: 100, rssi: -35, channel: 37, bandGhz: 6, standard: '802.11ax', generation: 'Wi-Fi 6E', security: 'wpa3-personal', linkMbps: 2401.9 },
					status: scenario.offline ? 'no-internet' : 'online',
					offlineSec: scenario.offline ? offlineSec : null
				},
				media: { source: 'Spotify', kind: 'music', title: 'The Imperial March', artist: 'John Williams · London Symphony Orchestra', positionSec: position, durationSec: 182, playing: true, live: false, artKey: null }
			});
		}, 1000);
	}

	// The GPU value box must fit "100%" so the segments never move, but no wider than that
	function fitGpuLoadBox() {
		const value = $('gpuLoad');
		const box = value.closest('.load-block');
		const previous = value.textContent;
		value.textContent = '100';
		const widest = value.parentElement.getBoundingClientRect().width / (frame.getBoundingClientRect().width / W);
		value.textContent = previous;
		box.style.width = `${Math.ceil(widest) + 2}px`;
	}

	// ---------- Boot ----------
	window.addEventListener('resize', applyScale);
	applyScale();
	document.fonts.ready.then(fitGpuLoadBox);
	drawBackground();
	drawArtPlaceholder(null);
	// "?demo=<scenario>" (ArkaHyte.exe --demo stress) replaces live sensor data with a scenario
	const demo = new URLSearchParams(location.search).get('demo');
	if (host) {
		host.addEventListener('message', (event) => {
			const message = event.data;
			if (demo && message && (message.type === 'snapshot' || message.type === 'cover' || message.type === 'update')) {
				return;
			}
			onHostMessage(message);
		});
		host.postMessage({ type: 'ready' });
	}
	if (!host || demo) {
		startSimulator(demo || 'idle');
	}
	// "?speedtest=preview|done" (ArkaHyte.exe --preview-speedtest [done]) shows the test view with sample values
	const speedTestPreview = new URLSearchParams(location.search).get('speedtest');
	if (speedTestPreview === 'preview') {
		renderSpeedTest({ phase: 'upload', mbps: 640, pingMs: 5.6, downMbps: 924.7, server: 'Scaleway · Paris' });
		setDial('upload');
		speedTest.fraction = gaugeFraction(640, speedTest.scale);
	} else if (speedTestPreview === 'done') {
		renderSpeedTest({ phase: 'done', pingMs: 5.97, downMbps: 924.7, upMbps: 935.6, server: 'Scaleway · Paris' });
	}
	// "?editor=<index>" (ArkaHyte.exe --open-editor N) opens a shortcut editor once the dock is known
	const editorParam = new URLSearchParams(location.search).get('editor');
	if (editorParam !== null) {
		setTimeout(() => openEditor(Number(editorParam) || 0), 1200);
	}
	loop();
})();
