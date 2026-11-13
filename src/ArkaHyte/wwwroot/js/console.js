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

	// ---------- Themes ----------
	// Colours come from the active theme's CSS variables (console.css); the background picture and
	// the particle effect belong to the theme as well
	// skyShade: darkening of the sky behind the clock (the Jedi theme keeps its sunlit sky and makes its
	// text readable with a halo instead, see console.css)
	const THEMES = {
		sith: { background: 'assets/background.jpg', particles: 'embers', skyShade: 0.5 },
		jedi: { background: 'assets/background-jedi.jpg', particles: 'sand', skyShade: 0 }
	};
	// "r, g, b" triplets for the canvas drawings, read from the CSS when the theme changes
	const palette = {};
	let themeName = null;
	let theme = THEMES.sith;

	function readPalette() {
		const style = getComputedStyle(document.documentElement);
		const read = (name) => style.getPropertyValue(name).trim();
		palette.blade = read('--blade-rgb');
		palette.core = read('--core-rgb');
		palette.bone = read('--bone-rgb');
		palette.ash = read('--ash-rgb');
		palette.void = read('--void-rgb');
		palette.panel = read('--panel-rgb');
		palette.panelHi = read('--panel-hi-rgb');
		palette.artTop = read('--art-top-rgb');
		palette.artBottom = read('--art-bottom-rgb');
		palette.ember = read('--ember-rgb');
	}

	function applyTheme(name) {
		const next = THEMES[name] ? name : 'sith';
		if (next === themeName) {
			return;
		}
		themeName = next;
		theme = THEMES[next];
		// Set once per change, on the root: every themed colour follows from these variables
		document.documentElement.dataset.theme = next;
		readPalette();
		drawBackground();
		// Embers belong to the volcano, sand to the desert: the next frames respawn the right ones
		embers.length = 0;
		if ($('mediaCover').hidden) {
			drawArtPlaceholder(placeholderLabel);
		}
	}

	// ---------- Background image + hot spots (frame coordinates) ----------
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

	// Desert world drawn in code: the Jedi theme's backdrop until assets/background-jedi.jpg exists
	function drawDesert(ctx) {
		const horizon = 1000;
		const sky = ctx.createLinearGradient(0, 0, 0, horizon);
		sky.addColorStop(0, '#040d22');
		sky.addColorStop(0.42, '#16325a');
		sky.addColorStop(0.72, '#5d5a6e');
		sky.addColorStop(0.9, '#c9814a');
		sky.addColorStop(1, '#f0b36e');
		ctx.fillStyle = sky;
		ctx.fillRect(0, 0, W, horizon);
		// Twin suns low over the horizon
		haze(ctx, 420, 925, 260, 'rgba(255, 200, 130, 0.35)');
		for (const [x, y, r, color] of [[420, 925, 40, '255, 244, 214'], [505, 958, 26, '255, 206, 150']]) {
			haze(ctx, x, y, r * 3, `rgba(${color}, 0.45)`);
			ctx.fillStyle = `rgb(${color})`;
			ctx.beginPath();
			ctx.arc(x, y, r, 0, Math.PI * 2);
			ctx.fill();
		}
		// Distant mesas
		ctx.fillStyle = 'rgba(122, 74, 46, 0.85)';
		ctx.beginPath();
		ctx.moveTo(0, horizon);
		for (const [x, y] of [[0, 968], [60, 968], [78, 948], [170, 946], [186, 972], [300, 975], [330, 990], [560, 988], [585, 955], [640, 952], [655, 970], [682, 972], [682, horizon]]) {
			ctx.lineTo(x, y);
		}
		ctx.fill();
		// Dune ridges, lit from the suns, darker towards the bottom
		const dunes = [
			{ base: 1010, amp: 26, k: 0.011, phase: 0.6, top: '#e3a868', bottom: '#a8683b' },
			{ base: 1120, amp: 40, k: 0.008, phase: 2.1, top: '#d18d52', bottom: '#7c4727' },
			{ base: 1300, amp: 60, k: 0.006, phase: 4.0, top: '#b06f3d', bottom: '#4e2a17' },
			{ base: 1560, amp: 80, k: 0.005, phase: 1.3, top: '#7e4c2a', bottom: '#2a160c' }
		];
		for (const dune of dunes) {
			const fill = ctx.createLinearGradient(0, dune.base - dune.amp, 0, H);
			fill.addColorStop(0, dune.top);
			fill.addColorStop(0.25, dune.bottom);
			fill.addColorStop(1, '#0b0603');
			ctx.fillStyle = fill;
			ctx.beginPath();
			ctx.moveTo(0, H);
			for (let x = 0; x <= W; x += 6) {
				const y = dune.base + Math.sin(x * dune.k + dune.phase) * dune.amp + Math.sin(x * dune.k * 2.7 + dune.phase * 1.9) * dune.amp * 0.35;
				ctx.lineTo(x, y);
			}
			ctx.lineTo(W, H);
			ctx.fill();
		}
	}

	// Each change of theme bumps the token: a picture still loading for the previous theme is dropped
	let backgroundToken = 0;

	function drawBackground() {
		const token = ++backgroundToken;
		const ctx = $('bg').getContext('2d');
		ctx.fillStyle = `rgb(${palette.void})`;
		ctx.fillRect(0, 0, W, H);
		const finish = () => {
			// Darken the bright sky behind the clock so the digits keep their contrast
			if (theme.skyShade > 0) {
				const shade = ctx.createLinearGradient(0, 0, 0, 560);
				shade.addColorStop(0, `rgba(0, 0, 0, ${theme.skyShade})`);
				shade.addColorStop(0.6, `rgba(0, 0, 0, ${theme.skyShade * 0.36})`);
				shade.addColorStop(1, 'rgba(0, 0, 0, 0)');
				ctx.fillStyle = shade;
				ctx.fillRect(0, 0, W, 560);
			}
			addGrain(ctx);
		};
		const photo = new Image();
		photo.onload = () => {
			if (token !== backgroundToken) {
				return;
			}
			// Any picture fills the frame: scaled to cover it, centred, the overflow cropped
			const scale = Math.max(W / photo.naturalWidth, H / photo.naturalHeight);
			const width = photo.naturalWidth * scale;
			const height = photo.naturalHeight * scale;
			ctx.drawImage(photo, (W - width) / 2, (H - height) / 2, width, height);
			finish();
		};
		photo.onerror = () => {
			if (token !== backgroundToken) {
				return;
			}
			if (theme.particles === 'sand') {
				drawDesert(ctx);
			}
			finish();
		};
		photo.src = theme.background;
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

	// Text and inline styles are only written when they change: the glides below redraw at every frame
	function setText(element, value) {
		const content = String(value);
		if (element.textContent !== content) {
			element.textContent = content;
		}
	}
	const text = (id, value) => setText($(id), value);
	const orDash = (value, format) => (isNum(value) ? format(value) : '--');
	const styleCache = new WeakMap();
	function setStyle(element, property, value) {
		let styles = styleCache.get(element);
		if (!styles) {
			styles = {};
			styleCache.set(element, styles);
		}
		if (styles[property] !== value) {
			styles[property] = value;
			element.style[property] = value;
		}
	}

	// ---------- Glides: readouts move smoothly from one sample to the next ----------
	// Samples arrive once per second, HWiNFO values at its own pace: a shown value travels from where
	// it stands to the new sample at a steady speed, over the time the previous change took, so it
	// never jumps and keeps moving until the next one. Stepped by the 24 fps loop (stepGlides); idle
	// glides cost nothing.
	// Gauges glide on the raw samples; numbers read calmer: their own glide follows the mean of the
	// last samples (NUMBER.average) and only rewrites the text a few times per second (NUMBER.every).
	const GLIDE_MIN_MS = 400;
	const GLIDE_MAX_MS = 2500;
	const NUMBER = { average: 3, every: 250 };
	const glides = new Map();
	const activeGlides = new Set();

	// Moves the value named key towards target, drawing each step with draw(value). Options: average
	// (glide towards the mean of that many samples), every (ms between two draws), jump (a wider gap
	// is drawn at once: a seek, another track). No data (null) is drawn at once.
	function glide(key, target, draw, options = {}) {
		const now = performance.now();
		let entry = glides.get(key);
		if (!entry) {
			entry = { from: null, to: undefined, value: null, start: now, duration: 0, changed: now, draw, samples: [], every: 0, drawn: 0 };
			glides.set(key, entry);
		}
		entry.draw = draw;
		entry.every = options.every || 0;
		let next = isNum(target) ? target : null;
		if (options.average > 1) {
			if (next === null) {
				entry.samples.length = 0;
			} else {
				entry.samples.push(next);
				if (entry.samples.length > options.average) {
					entry.samples.shift();
				}
				next = entry.samples.reduce((sum, value) => sum + value, 0) / entry.samples.length;
			}
		}
		if (next === entry.to) {
			return;
		}
		const previous = entry.changed;
		entry.changed = now;
		entry.to = next;
		const jump = isNum(options.jump) ? options.jump : Infinity;
		if (next === null || entry.value === null || !motion || Math.abs(next - entry.value) > jump) {
			activeGlides.delete(entry);
			entry.value = next;
			entry.drawn = now;
			draw(next);
			return;
		}
		entry.from = entry.value;
		entry.start = now;
		entry.duration = clamp(now - previous, GLIDE_MIN_MS, GLIDE_MAX_MS);
		activeGlides.add(entry);
	}

	// Draws a glide again where it stands (its widget was rebuilt or rescaled)
	function redrawGlide(key) {
		const entry = glides.get(key);
		if (entry) {
			entry.draw(entry.value);
		}
	}

	// The next value starts from scratch (drawn at once): its widget is gone
	function forgetGlide(key) {
		const entry = glides.get(key);
		if (entry) {
			activeGlides.delete(entry);
			glides.delete(key);
		}
	}

	function stepGlides(now) {
		for (const entry of activeGlides) {
			const t = clamp((now - entry.start) / entry.duration, 0, 1);
			entry.value = entry.from + (entry.to - entry.from) * t;
			// The end of a glide is always drawn, so the last value shown is the exact one
			if (t >= 1 || now - entry.drawn >= entry.every) {
				entry.drawn = now;
				entry.draw(entry.value);
			}
			if (t >= 1) {
				activeGlides.delete(entry);
			}
		}
	}

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
	// Gauge range and alert levels per metric: these defaults until the host sends the settings
	// (appsettings.json "Thresholds", edited from the on-screen settings)
	const limits = {
		cpuTemp: { min: 30, max: 95, warm: 75, hot: 85 },
		cpuPower: { min: 0, max: 160, warm: 120, hot: 150 },
		gpuTemp: { min: 30, max: 90, warm: 70, hot: 80 },
		gpuPower: { min: 0, max: 450, warm: 350, hot: 420 },
		diskTemp: { min: 0, max: 80, warm: 50, hot: 60 }
	};
	// Gauge tiles: element prefix, threshold key, unit shown at the end of the scale
	const METRIC_TILES = [
		{ prefix: 'cpuTemp', key: 'cpuTemp', unit: '°C' },
		{ prefix: 'cpuPow', key: 'cpuPower', unit: 'W' },
		{ prefix: 'gpuTemp', key: 'gpuTemp', unit: '°C' },
		{ prefix: 'gpuPow', key: 'gpuPower', unit: 'W' }
	];

	function applyLimits(thresholds) {
		for (const key of Object.keys(limits)) {
			const value = thresholds && thresholds[key];
			if (value && isNum(value.min) && isNum(value.max) && isNum(value.warm) && isNum(value.hot)) {
				limits[key] = { min: value.min, max: value.max, warm: value.warm, hot: value.hot };
			}
		}
		for (const tile of METRIC_TILES) {
			const scale = $(`${tile.prefix}Tile`).querySelectorAll('.gauge-scale span');
			scale[0].textContent = nf0(limits[tile.key].min);
			scale[scale.length - 1].textContent = `${nf0(limits[tile.key].max)} ${tile.unit}`;
			redrawGlide(tile.prefix);
			redrawGlide(`${tile.prefix}Gauge`);
		}
	}

	function setMetric(prefix, value, key) {
		glide(`${prefix}Gauge`, value, (shownValue) => drawMetricGauge(prefix, shownValue, key));
		glide(prefix, value, (shownValue) => drawMetricValue(prefix, shownValue, key), NUMBER);
	}

	function drawMetricGauge(prefix, value, key) {
		const { min, max } = limits[key];
		setStyle($(`${prefix}Fill`), 'width', isNum(value) ? `${clamp(((value - min) / (max - min)) * 100, 2, 100).toFixed(2)}%` : '0%');
	}

	// Alert colours follow the number they colour
	function drawMetricValue(prefix, value, key) {
		const { warm, hot } = limits[key];
		const tile = $(`${prefix}Tile`);
		text(prefix, orDash(value, nf0));
		tile.classList.toggle('hot', isNum(value) && value >= hot);
		tile.classList.toggle('warm', isNum(value) && value >= warm && value < hot);
	}

	function renderCpu(cpu) {
		// Speedometer in the ring (drawn by the loop)
		glide('cpuRing', cpu.load, (load) => {
			shown.cpu = isNum(load) ? load : 0;
		});
		glide('cpuLoad', cpu.load, (load) => {
			text('cpuLoad', orDash(load, Math.round));
			$('cpuLoad').parentElement.classList.toggle('wide', isNum(load) && Math.round(load) >= 100);
		}, NUMBER);
		setMetric('cpuTemp', cpu.temp, 'cpuTemp');
		setMetric('cpuPow', cpu.power, 'cpuPower');
	}

	let litSegs = -1;
	function renderGpu(gpu) {
		glide('gpuLoad', gpu.load, (load) => text('gpuLoad', orDash(load, Math.round)), NUMBER);
		glide('gpuSegs', gpu.load, (load) => {
			// 5 % per segment, but any load shown above 0 % lights at least the first one
			const lit = isNum(load) && Math.round(load) > 0 ? Math.max(1, Math.round(load / 5)) : 0;
			if (lit !== litSegs) {
				litSegs = lit;
				[...segs.children].forEach((bar, i) => {
					bar.classList.toggle('on', i < lit);
					bar.classList.toggle('peak', i === lit - 1);
				});
			}
		});
		const total = isNum(gpu.vramTotalGb) ? gpu.vramTotalGb : 24;
		text('vramTotal', nf0(total));
		glide('vramUsed', gpu.vramPercent, (percent) => text('vramUsed', orDash(percent, (value) => nf1.format((value * total) / 100))), NUMBER);
		glide('vramFill', gpu.vramPercent, (percent) => {
			setStyle($('vramFill'), 'width', isNum(percent) ? `${clamp(percent, 1.5, 100).toFixed(2)}%` : '0%');
		});
		setMetric('gpuTemp', gpu.temp, 'gpuTemp');
		setMetric('gpuPow', gpu.power, 'gpuPower');
		// RTSS reports 0 (or nothing) outside games: the readout only shows while one runs
		const playing = isNum(gpu.fps) && gpu.fps >= 1;
		if ($('gpuFps').hidden === playing) {
			$('gpuFps').hidden = !playing;
		}
		glide('gpuFps', playing ? gpu.fps : null, (fps) => text('gpuFpsValue', orDash(fps, nf0)), NUMBER);
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
			diskGroups.forEach((_, i) => forgetGlide(`diskTemp${i}`));
			const container = $('disks');
			diskSlots = disks.map((_, i) => {
				const slot = $('diskTemplate').content.firstElementChild.cloneNode(true);
				attachLongPress(slot, () => openDiskSheet(i));
				return slot;
			});
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

	let litLeds = -1;
	function renderMemory(ram, disks) {
		if (isNum(ram.usedGb) && isNum(ram.totalGb)) {
			const totalGb = ram.totalGb;
			text('ramTotal', nf0(totalGb));
			glide('ramUsed', ram.usedGb, (used) => text('ramUsed', orDash(used, (value) => nf1.format(value))), NUMBER);
			glide('ramLeds', ram.usedGb, (used) => {
				if (!isNum(used)) {
					return;
				}
				const lit = Math.round((used / totalGb) * 32);
				if (lit !== litLeds) {
					litLeds = lit;
					[...leds.children].forEach((led, i) => led.classList.toggle('on', i < lit));
				}
			});
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
			glide(`diskTemp${i}`, temp, (value) => {
				if (isNum(value)) {
					setText(group.querySelector('.disk-temp-value'), `${nf0(value)} °C`);
					group.classList.toggle('hot', value >= limits.diskTemp.hot);
				}
			}, NUMBER);
		});
	}

	function renderNetwork(net) {
		const wifi = net.wifi;
		glide('netSignal', wifi ? wifi.signal : null, (signal) => text('sigPct', orDash(signal, nf0)), NUMBER);
		glide('netArcs', wifi ? wifi.signal : null, (signal) => {
			const arcs = isNum(signal) ? Math.ceil(signal / 25) : 0;
			for (let i = 1; i <= 4; i++) {
				$(`arc${i}`).classList.toggle('on', i <= arcs);
			}
		});
		glide('netRssi', wifi ? wifi.rssi : null, (rssi) => text('rssi', isNum(rssi) ? `${Math.round(rssi)} dBm` : ' '), NUMBER);
		if (wifi) {
			text('netChip', [wifi.generation, isNum(wifi.bandGhz) ? `${nf1.format(wifi.bandGhz).replace(',0', '')} GHz` : null].filter(Boolean).join(' · '));
			text('ssid', wifi.ssid || '--');
			text('channel', orDash(wifi.channel, String));
			text('link', isNum(wifi.linkMbps) ? `${nf1.format(wifi.linkMbps)} Mbit/s` : '--');
		} else {
			text('netChip', net.adapter || 'Filaire');
			['ssid', 'channel'].forEach((id) => text(id, '--'));
			text('link', isNum(net.linkMbps) ? `${nf0(net.linkMbps)} Mbit/s` : '--');
		}
		glide('ping', net.pingMs, (ping) => text('ping', isNum(ping) ? `${nf0(ping)} ms` : '--'), NUMBER);
		glide('rateDown', net.downBytesPerSec, (rate) => text('rateDown', formatRate(rate)), NUMBER);
		glide('rateUp', net.upBytesPerSec, (rate) => text('rateUp', formatRate(rate)), NUMBER);

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
	// Download in the blade colour, upload in amber
	const speedColor = (dial) => (dial === 'upload' ? '255, 176, 32' : palette.blade);
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
		const color = speedColor(speedTest.dial);
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
			ctx.fillStyle = lit ? `rgba(${palette.bone}, 0.95)` : `rgba(${palette.ash}, 0.9)`;
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
			ctx.strokeStyle = `rgb(${palette.core})`;
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
			spark.addColorStop(0, `rgba(${palette.core}, 0.95)`);
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
		body.addColorStop(1, `rgb(${palette.core})`);
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
		ctx.strokeStyle = `rgba(${palette.core}, 0.9)`;
		ctx.lineWidth = 1.2;
		ctx.beginPath();
		ctx.moveTo(reach * 0.4, 0);
		ctx.lineTo(reach, 0);
		ctx.stroke();

		// Hub: notched dark disc, coloured ring, glowing core
		ctx.fillStyle = `rgb(${palette.panelHi})`;
		for (let i = 0; i < 8; i++) {
			ctx.rotate(Math.PI / 4);
			ctx.fillRect(9, -1.6, 5, 3.2);
		}
		ctx.fillStyle = `rgb(${palette.panel})`;
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

	// Redrawn with the same caption when the theme changes
	let placeholderLabel = null;

	function drawArtPlaceholder(label) {
		placeholderLabel = label;
		const canvas = $('mediaArt');
		const ctx = canvas.getContext('2d');
		// Artwork is designed on a 132 px grid and scaled to the canvas
		const size = 132;
		ctx.setTransform(canvas.width / size, 0, 0, canvas.height / size, 0, 0);
		const g = ctx.createLinearGradient(0, 0, size, size);
		g.addColorStop(0, `rgb(${palette.artTop})`);
		g.addColorStop(1, `rgb(${palette.artBottom})`);
		ctx.fillStyle = g;
		ctx.fillRect(0, 0, size, size);
		haze(ctx, 52, 46, 90, `rgba(${palette.blade}, 0.45)`);
		ctx.lineCap = 'round';
		for (const [x1, y1, x2, y2] of [[26, 110, 106, 22], [26, 22, 106, 110]]) {
			ctx.shadowColor = `rgb(${palette.blade})`;
			ctx.shadowBlur = 14;
			ctx.strokeStyle = `rgb(${palette.blade})`;
			ctx.lineWidth = 6;
			ctx.beginPath();
			ctx.moveTo(x1, y1);
			ctx.lineTo(x2, y2);
			ctx.stroke();
			ctx.shadowBlur = 0;
			ctx.strokeStyle = `rgb(${palette.core})`;
			ctx.lineWidth = 2;
			ctx.stroke();
		}
		if (label) {
			ctx.fillStyle = `rgba(${palette.bone}, 0.85)`;
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
			glide('mediaProgress', null, drawMediaProgress);
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
			// A seek or another track (more than 5 % away) is drawn at once
			glide('mediaProgress', (media.positionSec || 0) / media.durationSec, drawMediaProgress, { jump: 0.05 });
		}
		setPlayIcon(media.playing);
		if (media.artKey !== coverKey && !media.artKey) {
			showCover(null, media.source);
		}
	}

	function drawMediaProgress(fraction) {
		setStyle($('mediaFill'), 'width', isNum(fraction) ? `${clamp(fraction * 100, 1, 100).toFixed(2)}%` : '0%');
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

	// ---------- Game view: while a recognised game runs (snapshot.game) ----------
	// The view stays 10 s after the game is gone (its process exited); a view picked with the switch
	// holds until then. Values glide between samples and the graph scrolls in the 24 fps loop.
	const GAME_GRACE_MS = 10000;
	const FPS_HISTORY = 60;
	const DIAL = { cx: 300, cy: 300, radius: 250, start: 135, sweep: 270 };
	const GRAPH = { width: 586, height: 140 };
	// One second of the curve
	const GRAPH_STEP = GRAPH.width / (FPS_HISTORY - 1);
	const GAME_GLIDES = ['gameFps', 'gameDial', 'gameLow', 'gameLowMark', 'frameTime', 'fpsAvg'];
	const DIAL_STEPS = [10, 20, 25, 30, 40, 50, 60, 75, 100, 120, 150, 200, 250, 300];
	let gameSession = null;
	let gameView = false;
	// Settings (setup wizard): back to the normal view while the game stays in the background
	let gameViewConfig = { autoNormal: true, autoNormalDelaySec: 20 };
	const dial = { max: 0, ticks: [], blade: null, core: null, low: null };
	const graph = { area: null, line: null, visible: false, frozen: false, bottom: 0, top: 0, shift: -1 };
	const switchButtons = [...document.querySelectorAll('#viewSwitch button')];

	function startGameSession() {
		// manual: the view was picked with the switch; backgroundSince: when the game lost the focus (0: it has it)
		return { start: Date.now(), lastSeen: 0, view: 'game', manual: false, backgroundSince: 0, name: '', refreshHz: null, fps: [], fpsSum: 0, fpsCount: 0 };
	}

	function svgElement(parent, name, attributes) {
		const element = document.createElementNS(SVG_NS, name);
		for (const [key, value] of Object.entries(attributes)) {
			element.setAttribute(key, value);
		}
		parent.appendChild(element);
		return element;
	}

	function dialPoint(degrees, radius) {
		const angle = (degrees * Math.PI) / 180;
		return [DIAL.cx + radius * Math.cos(angle), DIAL.cy + radius * Math.sin(angle)];
	}

	function dialArc(radius) {
		const [x1, y1] = dialPoint(DIAL.start, radius);
		const [x2, y2] = dialPoint(DIAL.start + DIAL.sweep, radius);
		return `M${x1} ${y1} A${radius} ${radius} 0 1 1 ${x2} ${y2}`;
	}

	// Scale of the dial: up to the refresh rate of the game's monitor, numbered at round steps
	function buildDial(max) {
		const svg = $('fpsDial');
		svg.textContent = '';
		dial.max = max;
		dial.ticks = [];
		svgElement(svg, 'circle', { class: 'dial-lens', cx: DIAL.cx, cy: DIAL.cy, r: 222 });
		svgElement(svg, 'path', { class: 'dial-track', d: dialArc(DIAL.radius) });
		// pathLength 1000: the lit part is a dash of the whole arc
		dial.blade = svgElement(svg, 'path', { class: 'dial-blade', d: dialArc(DIAL.radius), pathLength: 1000, 'stroke-dasharray': '0 1000' });
		dial.core = svgElement(svg, 'path', { class: 'dial-core', d: dialArc(DIAL.radius), pathLength: 1000, 'stroke-dasharray': '0 1000' });
		const major = DIAL_STEPS.find((step) => step >= (max / 4) * 0.9) || DIAL_STEPS[DIAL_STEPS.length - 1];
		const minor = major / (major % 6 === 0 ? 6 : 5);
		for (let value = 0; value <= max + 0.01; value += minor) {
			const degrees = DIAL.start + (DIAL.sweep * value) / max;
			const isMajor = Math.abs(value / major - Math.round(value / major)) < 0.001;
			const [x1, y1] = dialPoint(degrees, 274);
			const [x2, y2] = dialPoint(degrees, isMajor ? 292 : 284);
			const tick = svgElement(svg, 'line', { class: isMajor ? 'dial-tick major' : 'dial-tick', x1, y1, x2, y2 });
			dial.ticks.push({ element: tick, value, on: false });
			if (isMajor) {
				const [tx, ty] = dialPoint(degrees, 200);
				svgElement(svg, 'text', { class: 'dial-num', x: tx, y: ty + 6 }).textContent = Math.round(value);
			}
		}
		const [lx1, ly1] = dialPoint(DIAL.start, 236);
		const [lx2, ly2] = dialPoint(DIAL.start, 266);
		dial.low = svgElement(svg, 'line', { class: 'dial-low', x1: lx1, y1: ly1, x2: lx2, y2: ly2 });
	}

	function renderDial(fps) {
		if (!dial.blade) {
			return;
		}
		const lit = isNum(fps) ? clamp(fps / dial.max, 0, 1) : 0;
		const dash = `${Math.round(lit * 1000)} 1000`;
		dial.blade.setAttribute('stroke-dasharray', dash);
		dial.core.setAttribute('stroke-dasharray', dash);
		// A round cap would still draw a dot for an empty arc
		dial.blade.style.visibility = lit > 0 ? '' : 'hidden';
		dial.core.style.visibility = lit > 0 ? '' : 'hidden';
		for (const tick of dial.ticks) {
			const on = isNum(fps) && tick.value <= fps;
			if (on !== tick.on) {
				tick.on = on;
				tick.element.classList.toggle('on', on);
			}
		}
	}

	// 1 % low: a mark on the dial and a dashed line across the graph
	function renderLow(low) {
		if (dial.low) {
			dial.low.style.visibility = isNum(low) ? '' : 'hidden';
			if (isNum(low)) {
				dial.low.setAttribute('transform', `rotate(${(DIAL.sweep * clamp(low / dial.max, 0, 1)).toFixed(2)} ${DIAL.cx} ${DIAL.cy})`);
			}
		}
		const line = $('graphLow');
		const visible = graph.visible && isNum(low);
		setStyle(line, 'visibility', visible ? 'visible' : 'hidden');
		if (visible) {
			setStyle(line, 'transform', `translateY(${clamp(graphY(low), 0, GRAPH.height).toFixed(1)}px)`);
		}
	}

	const graphY = (value) => GRAPH.height - ((value - graph.bottom) / (graph.top - graph.bottom)) * GRAPH.height;

	// The curve is one step wider than its window, the newest sample just past the right edge, and
	// slides left by one step until the next sample (scrollGraph, a compositor transform): it scrolls
	// steadily instead of jumping every second. Its scale only changes when the curve leaves it or
	// would fit in a much tighter one.
	function renderGraph(history, low) {
		const svg = $('fpsGraph');
		const width = GRAPH.width + GRAPH_STEP;
		if (!graph.line) {
			svg.setAttribute('width', width.toFixed(2));
			svg.setAttribute('viewBox', `0 0 ${width.toFixed(2)} ${GRAPH.height}`);
			for (const y of [0, GRAPH.height / 2, GRAPH.height]) {
				svgElement(svg, 'line', { class: 'graph-grid', x1: 0, y1: y, x2: width, y2: y });
			}
			graph.area = svgElement(svg, 'path', { class: 'graph-area' });
			graph.line = svgElement(svg, 'path', { class: 'graph-line' });
		}
		graph.visible = history.length >= 2;
		if (!graph.visible) {
			graph.area.removeAttribute('d');
			graph.line.removeAttribute('d');
			redrawGlide('gameLowMark');
			return;
		}
		const values = isNum(low) ? [...history, low] : history;
		const min = Math.min(...values);
		const max = Math.max(...values);
		const bottom = Math.max(0, Math.floor((min * 0.85) / 10) * 10);
		const top = Math.max(bottom + 20, Math.ceil((max * 1.05) / 10) * 10);
		if (graph.top <= graph.bottom || min < graph.bottom || max > graph.top || top - bottom < (graph.top - graph.bottom) * 0.6) {
			graph.bottom = bottom;
			graph.top = top;
			redrawGlide('gameLowMark');
		}
		const x = (i) => (width - (history.length - 1 - i) * GRAPH_STEP).toFixed(1);
		const line = history.map((value, i) => `${x(i)} ${graphY(value).toFixed(1)}`).join(' L');
		graph.line.setAttribute('d', `M${line}`);
		graph.area.setAttribute('d', `M${line} L${width.toFixed(1)} ${GRAPH.height} L${x(0)} ${GRAPH.height} Z`);
		// Back to the start of the step together with the new curve, not one frame later
		graph.shift = 0;
		svg.style.transform = 'translateX(0px)';
	}

	// Pause notice centred between the 1 % low line and the bottom of the graph, or between the top and
	// the line when there is no room left below it
	const PAUSE_HEIGHT = 20;
	function placePause() {
		const entry = glides.get('gameLowMark');
		const low = entry && graph.visible && isNum(entry.value) ? clamp(graphY(entry.value), 0, GRAPH.height) : 0;
		const top = GRAPH.height - low >= PAUSE_HEIGHT + 8 ? (low + GRAPH.height - PAUSE_HEIGHT) / 2 : (low - PAUSE_HEIGHT) / 2;
		setStyle($('graphPause'), 'transform', `translateY(${Math.round(top)}px)`);
	}

	function scrollGraph(progress) {
		const shift = Math.round(progress * GRAPH_STEP * 10) / 10;
		if (shift !== graph.shift) {
			graph.shift = shift;
			$('fpsGraph').style.transform = `translateX(${-shift}px)`;
		}
	}

	// Lowest 1 % of the last minute, when HWiNFO does not report it: with one sample per second it is
	// the minimum, labelled as such
	function computedLow(history) {
		if (history.length < 5) {
			return null;
		}
		const sorted = [...history].sort((a, b) => a - b);
		return sorted[Math.floor(sorted.length * 0.01)];
	}

	function applyView() {
		const inGame = gameSession !== null;
		const showGame = inGame && gameSession.view === 'game';
		frame.classList.toggle('has-game', inGame);
		$('viewSwitch').hidden = !inGame;
		switchButtons.forEach((button) => {
			button.setAttribute('aria-checked', String(inGame && button.dataset.view === (showGame ? 'game' : 'normal')));
		});
		if (showGame !== gameView) {
			gameView = showGame;
			frame.classList.toggle('game', showGame);
		}
	}

	function renderGame(game, gpu) {
		const now = Date.now();
		if (game) {
			if (!gameSession) {
				gameSession = startGameSession();
			}
			gameSession.lastSeen = now;
			gameSession.name = game.name || '';
			gameSession.refreshHz = isNum(game.refreshHz) ? game.refreshHz : null;
			if (game.foreground !== false) {
				gameSession.backgroundSince = 0;
			} else if (!gameSession.backgroundSince) {
				gameSession.backgroundSince = now;
			}
		} else if (gameSession && now - gameSession.lastSeen > GAME_GRACE_MS) {
			gameSession = null;
			GAME_GLIDES.forEach(forgetGlide);
		}
		// Unless the switch was used: the normal view once the game has stayed long enough in the
		// background, the game view as soon as it is back in front
		if (gameSession && !gameSession.manual) {
			const away = gameViewConfig.autoNormal && gameSession.backgroundSince > 0
				&& now - gameSession.backgroundSince >= gameViewConfig.autoNormalDelaySec * 1000;
			gameSession.view = away ? 'normal' : 'game';
		}
		applyView();
		if (!gameSession) {
			return;
		}
		const session = gameSession;
		const fps = isNum(gpu.fps) && gpu.fps >= 1 ? gpu.fps : null;
		// No frames (game in the background, loading): the last values and curve stay, greyed and still
		const stale = fps === null;
		$('gameFps').classList.toggle('stale', stale);
		document.querySelector('.game-trio').classList.toggle('stale', stale);
		document.querySelector('.graph-window').classList.toggle('stale', stale);
		graph.frozen = stale;
		const pause = $('graphPause');
		pause.hidden = !stale;
		if (stale) {
			text('graphPause', session.backgroundSince ? 'Jeu en arrière-plan' : 'En attente des images');
			placePause();
		}
		if (fps !== null) {
			session.fps.push(fps);
			if (session.fps.length > FPS_HISTORY) {
				session.fps.shift();
			}
			session.fpsSum += fps;
			session.fpsCount++;
		}
		// Top of the dial: the refresh rate, or a round value above what the game reached
		const max = session.refreshHz || Math.max(144, Math.ceil(Math.max(0, ...session.fps) / 60) * 60);
		if (max !== dial.max) {
			buildDial(max);
			redrawGlide('gameDial');
			redrawGlide('gameLowMark');
		}
		const reported = isNum(gpu.fpsLow) && gpu.fpsLow >= 1;
		const low = reported ? gpu.fpsLow : computedLow(session.fps);
		const frameTime = isNum(gpu.frameTimeMs) && gpu.frameTimeMs > 0 ? gpu.frameTimeMs : fps !== null ? 1000 / fps : null;

		text('gameTitle', session.name || 'Jeu');
		text('gameSince', `en jeu depuis ${formatTime((now - session.start) / 1000)}`);
		text('fpsScale', session.refreshHz ? `écran ${session.refreshHz} Hz` : ' ');
		if (stale) {
			// Values, labels and curve stay as they were last drawn
			return;
		}
		text('fpsLowLabel', reported ? '1 % low' : 'Min 60 s');
		text('graphLowLabel', reported ? '1 % low' : 'min');
		renderGraph(session.fps, low);
		glide('gameDial', fps, renderDial);
		glide('gameFps', fps, (value) => text('fpsValue', orDash(value, nf0)), NUMBER);
		glide('gameLowMark', low, renderLow);
		glide('gameLow', low, (value) => text('fpsLow', orDash(value, nf0)), NUMBER);
		glide('frameTime', frameTime, (value) => text('frameTime', orDash(value, (ms) => nf1.format(ms))), NUMBER);
		// Already an average: only its redraws are spaced out
		glide('fpsAvg', session.fpsCount > 0 ? session.fpsSum / session.fpsCount : null, (value) => text('fpsAvg', orDash(value, nf0)), { every: NUMBER.every });
	}

	switchButtons.forEach((button) => {
		button.addEventListener('click', () => {
			if (gameSession) {
				gameSession.view = button.dataset.view;
				gameSession.manual = true;
				applyView();
			}
		});
	});

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
		renderGame(s.game || null, gpu);
	}

	function applyConfig(config) {
		if (config.cpuMaker) text('cpuMaker', config.cpuMaker);
		if (config.cpuModel) text('cpuModel', config.cpuModel);
		if (config.gpuMaker) text('gpuMaker', config.gpuMaker);
		if (config.gpuModel) text('gpuModel', config.gpuModel);
		if (Array.isArray(config.shortcuts)) buildDock(config.shortcuts);
		if (config.theme) applyTheme(config.theme);
		if (config.thresholds) applyLimits(config.thresholds);
		if (config.weather) weatherConfig = config.weather;
		if (config.gameView) gameViewConfig = config.gameView;
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
		} else if (message.type === 'hwinfo') {
			onHwInfoList(message);
		} else if (message.type === 'weather') {
			renderWeather(message.weather);
		} else if (message.type === 'speedtest') {
			renderSpeedTest(message);
		} else if (message.type === 'apps') {
			apps = Array.isArray(message.apps) ? message.apps : [];
			if (!$('editor').hidden) {
				renderAppList();
			}
		}
	}

	// ---------- Weather (option, Open-Meteo) ----------
	// WMO weather codes grouped into a few skies: icon (24 px stroke glyph) and French caption
	const WEATHER_ICONS = {
		sun: '<circle cx="12" cy="12" r="4.5"/><path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4"/>',
		moon: '<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/>',
		partly: '<path d="M8 4.5v1.5M3.5 9H5M4.8 5.8l1 1M11.3 5.8l-1 1"/><path d="M5.6 11.6A3.5 3.5 0 0 1 11 7.6"/><path d="M8 19h9.5a3.5 3.5 0 0 0 .3-7A5 5 0 0 0 8.2 12.4 3.3 3.3 0 0 0 8 19z"/>',
		cloud: '<path d="M7 18.5h10.5a4 4 0 0 0 .4-8A5.5 5.5 0 0 0 7.3 11 3.8 3.8 0 0 0 7 18.5z"/>',
		fog: '<path d="M4 9h16M3 13h18M5 17h14"/>',
		drizzle: '<path d="M7 14h10.5a3.5 3.5 0 0 0 .3-7A5 5 0 0 0 7.2 7.5 3.3 3.3 0 0 0 7 14z"/><path d="M9 17v1M13 17v1M11 20v1M15 20v1"/>',
		rain: '<path d="M7 13.5h10.5a3.5 3.5 0 0 0 .3-7A5 5 0 0 0 7.2 7 3.3 3.3 0 0 0 7 13.5z"/><path d="m9 16-1 3M13 16l-1 3M17 16l-1 3"/>',
		snow: '<path d="M7 13.5h10.5a3.5 3.5 0 0 0 .3-7A5 5 0 0 0 7.2 7 3.3 3.3 0 0 0 7 13.5z"/><path d="M9 17.5h.01M13 17.5h.01M11 20.5h.01M15 20.5h.01"/>',
		storm: '<path d="M7 13.5h10.5a3.5 3.5 0 0 0 .3-7A5 5 0 0 0 7.2 7 3.3 3.3 0 0 0 7 13.5z"/><path d="m12.5 15-2 3.5h3l-2 3.5"/>'
	};
	function weatherSky(code, isDay) {
		if (code === 0) return isDay ? ['sun', 'Ensoleillé'] : ['moon', 'Ciel dégagé'];
		if (code <= 2) return [isDay ? 'partly' : 'moon', 'Éclaircies'];
		if (code === 3) return ['cloud', 'Nuageux'];
		if (code === 45 || code === 48) return ['fog', 'Brouillard'];
		if (code >= 51 && code <= 57) return ['drizzle', 'Bruine'];
		if ((code >= 61 && code <= 67) || (code >= 80 && code <= 82)) return ['rain', code >= 80 ? 'Averses' : 'Pluie'];
		if ((code >= 71 && code <= 77) || code === 85 || code === 86) return ['snow', 'Neige'];
		if (code >= 95) return ['storm', 'Orage'];
		return ['cloud', ''];
	}

	// Weather settings as the host reports them (the place is chosen in the setup wizard)
	let weatherConfig = { enabled: false, place: '', located: false };

	function renderWeather(weather) {
		const row = $('weather');
		row.hidden = !weather;
		if (!weather) {
			return;
		}
		const [icon, caption] = weatherSky(weather.code, weather.isDay);
		$('wxIcon').innerHTML = WEATHER_ICONS[icon];
		text('wxTemp', `${nf0(weather.temperature)}°`);
		text('wxText', caption);
		text('wxRange', isNum(weather.min) && isNum(weather.max) ? `${nf0(weather.min)}° / ${nf0(weather.max)}°` : '');
		row.title = weather.place || '';
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
	// Speedometer inside the lens: same 240° dial as the speed test, opening at the bottom
	const LENS_DIAL = { start: Math.PI * 5 / 6, sweep: Math.PI * 4 / 3 };
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
		return isNum(cpuTemp) && cpuTemp >= limits.cpuTemp.hot ? palette.ember : palette.blade;
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

	// Speedometer in the lens, like the speed test dial: a graduated arc lit up to the load and a
	// needle; the value sits in the opening at the bottom (DOM, .cpu-core)
	function drawLensDial(ctx, cx, cy, sweep, color) {
		const dialR = RING_LENS * 0.74;
		const dialEnd = LENS_DIAL.start + LENS_DIAL.sweep * sweep;
		ctx.lineCap = 'round';
		ctx.lineWidth = 7;
		ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
		ctx.beginPath();
		ctx.arc(cx, cy, dialR, LENS_DIAL.start, LENS_DIAL.start + LENS_DIAL.sweep);
		ctx.stroke();
		ctx.font = "11px 'Share Tech Mono', monospace";
		ctx.textAlign = 'center';
		ctx.textBaseline = 'middle';
		for (let i = 0; i <= 10; i++) {
			const a = LENS_DIAL.start + (LENS_DIAL.sweep * i) / 10;
			const lit = i / 10 <= sweep + 0.001 && sweep > 0;
			const major = i % 5 === 0;
			ctx.strokeStyle = lit ? `rgba(${color}, 0.9)` : 'rgba(255, 255, 255, 0.25)';
			ctx.lineWidth = major ? 2 : 1.2;
			ctx.beginPath();
			ctx.moveTo(cx + Math.cos(a) * (dialR - 7), cy + Math.sin(a) * (dialR - 7));
			ctx.lineTo(cx + Math.cos(a) * (dialR - (major ? 14 : 11)), cy + Math.sin(a) * (dialR - (major ? 14 : 11)));
			ctx.stroke();
			if (major) {
				ctx.fillStyle = lit ? `rgba(${palette.bone}, 0.95)` : `rgba(${palette.ash}, 0.9)`;
				ctx.fillText(String(i * 10), cx + Math.cos(a) * (dialR - 25), cy + Math.sin(a) * (dialR - 25));
			}
		}
		if (sweep > 0.002) {
			ctx.lineWidth = 7;
			ctx.shadowColor = `rgb(${color})`;
			ctx.shadowBlur = 12;
			ctx.strokeStyle = `rgb(${color})`;
			ctx.beginPath();
			ctx.arc(cx, cy, dialR, LENS_DIAL.start, dialEnd);
			ctx.stroke();
			ctx.shadowBlur = 3;
			ctx.lineWidth = 2;
			ctx.strokeStyle = `rgb(${palette.core})`;
			ctx.beginPath();
			ctx.arc(cx, cy, dialR, LENS_DIAL.start, dialEnd);
			ctx.stroke();
			ctx.shadowBlur = 0;
		}
		ctx.lineCap = 'butt';
		drawNeedle(ctx, cx, cy, dialEnd, dialR - 16, color);
	}

	// Lit rim around the lens: the blade colour all round, a frame rather than a load indicator
	// (the gauge inside the lens shows the load)
	function drawRim(ctx, cx, cy, r, color) {
		ctx.save();
		ctx.lineWidth = 3;
		ctx.shadowColor = `rgb(${color})`;
		ctx.shadowBlur = 16;
		ctx.strokeStyle = `rgba(${color}, 0.85)`;
		ctx.beginPath();
		ctx.arc(cx, cy, r, 0, Math.PI * 2);
		ctx.stroke();
		ctx.shadowBlur = 0;
		ctx.lineWidth = 1;
		ctx.strokeStyle = `rgba(${palette.core}, 0.6)`;
		ctx.stroke();
		ctx.restore();
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
		// A new sample shifts the history bars in the lens: the tick is part of the key
		const key = `${Math.round(sweep * 4000)}|${color}`;
		if (key === ringKey) {
			return;
		}
		ringKey = key;
		ctx.clearRect(0, 0, RING_SIZE, RING_SIZE);

		// Dark lens behind the load value
		const lens = ctx.createRadialGradient(cx, cy, 16, cx, cy, RING_LENS);
		lens.addColorStop(0, `rgba(${palette.void}, 0.92)`);
		lens.addColorStop(0.8, `rgba(${palette.panel}, 0.85)`);
		lens.addColorStop(1, `rgba(${palette.panelHi}, 0.6)`);
		ctx.fillStyle = lens;
		ctx.beginPath();
		ctx.arc(cx, cy, RING_LENS, 0, Math.PI * 2);
		ctx.fill();

		drawLensDial(ctx, cx, cy, sweep, color);


		ctx.strokeStyle = `rgba(${color}, 0.2)`;
		ctx.lineWidth = 1;
		ctx.beginPath();
		ctx.arc(cx, cy, r0 + 4, 0, Math.PI * 2);
		ctx.stroke();
		drawRim(ctx, cx, cy, r0 - 12, color);

		ctx.shadowBlur = 0;
	}

	// ---------- Particles: embers from the lava + volcano eruptions (Sith), blowing sand (Jedi) ----------
	// They live on a half-resolution canvas covering only the middle band of the background
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

	// Sand grains carried by the wind across the dunes, left to right
	function spawnSand() {
		return {
			x: Math.random() * W,
			y: FX_TOP + 450 + Math.random() * 800,
			vx: 0.35 + Math.random() * 0.9,
			vy: 0,
			phase: Math.random() * Math.PI * 2,
			life: 0,
			maxLife: 220 + Math.random() * 260,
			size: 0.5 + Math.random() * 1.1,
			rgb: '236, 206, 160',
			sand: true
		};
	}

	function spawnEmber() {
		if (theme.particles === 'sand') {
			return spawnSand();
		}
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

		if (now > nextEruption && theme.particles === 'embers') {
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
			} else if (e.sand) {
				e.x += e.vx * ticks;
				e.y += Math.sin(e.phase + e.life / 30) * 0.25 * ticks;
			} else {
				e.x += Math.sin(e.phase + e.life / 40) * 0.35 * ticks;
			}
			e.y += e.vy * ticks;
			const t = e.life / e.maxLife;
			if (t >= 1 || e.y < FX_TOP || e.x > W) {
				embers.splice(i, 1);
				continue;
			}
			const alpha = Math.sin(t * Math.PI) * (e.sand ? 0.5 : 0.85);
			ctx.fillStyle = e.rgb ? `rgba(${e.rgb}, ${alpha})` : `rgba(255, ${e.green | 0}, 60, ${alpha})`;
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
		stepGlides(now);
		updateClock();
		animateCss(now);
		drawRing(now, progress);
		// The FPS graph only shows in the game view
		if (gameView) {
			// Frozen at the end of its step: the newest point on the right edge
			scrollGraph(motion && !graph.frozen ? progress : 1);
		}
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

	// ---------- On-screen settings: long press on a metric ----------
	// Sheets: which HWiNFO values can feed the widget (pickers) and, for gauges, its thresholds.
	// Picker filters mirror the setup wizard: same unit, GPU values from a GPU sensor, disks from a disk sensor.
	const DISK_SENSOR = /s\.m\.a\.r\.t|drive|disk|disque|nvme|ssd/i;
	const DRIVE_PREFIX = 'drive:';
	const SHEETS = {
		cpuTemp: { title: 'Température CPU', pickers: [{ key: 'cpuTemp', title: 'Valeur HWiNFO', unit: '°C' }], threshold: { key: 'cpuTemp', unit: '°C', step: 1, maxStep: 5 } },
		cpuPower: { title: 'Consommation CPU', pickers: [{ key: 'cpuPower', title: 'Valeur HWiNFO', unit: 'W' }], threshold: { key: 'cpuPower', unit: 'W', step: 5, maxStep: 10 } },
		gpuTemp: { title: 'Température GPU', pickers: [{ key: 'gpuTemp', title: 'Valeur HWiNFO', unit: '°C', gpu: true }], threshold: { key: 'gpuTemp', unit: '°C', step: 1, maxStep: 5 } },
		gpuPower: { title: 'Consommation GPU', pickers: [{ key: 'gpuPower', title: 'Valeur HWiNFO', unit: 'W', gpu: true }], threshold: { key: 'gpuPower', unit: 'W', step: 10, maxStep: 25 } },
		gpuLoad: { title: 'Charge GPU', pickers: [{ key: 'gpuLoad', title: 'Charge (HWiNFO)', unit: '%', gpu: true }, { key: 'gpuFps', title: 'FPS en jeu (HWiNFO)', unit: 'FPS' }] },
		gpuVram: { title: 'Mémoire vidéo', pickers: [{ key: 'gpuVram', title: 'Utilisation (HWiNFO, %)', unit: '%', gpu: true }] },
		fps: {
			title: 'Images par seconde',
			pickers: [
				{ key: 'gpuFps', title: 'FPS en jeu (HWiNFO)', unit: 'FPS' },
				{ key: 'gpuFpsLow', title: '1 % low (HWiNFO, sinon minimum calculé)', unit: 'FPS' },
				{ key: 'gpuFrameTime', title: 'Temps de frame (HWiNFO, sinon déduit des FPS)', unit: 'ms' }
			]
		},
		clock: { title: 'Météo', pickers: [], weather: true }
	};
	// Threshold rows; the disk row has no gauge, only its alert level
	const THRESHOLD_ROWS = [
		{ field: 'warm', label: 'Chaud à partir de', gauge: true },
		{ field: 'hot', label: 'Alerte à partir de' },
		{ field: 'max', label: 'Fin de l’échelle', gauge: true }
	];
	const SHEET_REFRESH_MS = 2000;
	const sheet = { def: null, choices: {}, original: {}, limits: null, weather: null, open: null, timer: 0 };
	let hwinfoList = { entries: [], matches: {} };

	const entryKey = (entry) => `${entry.sensor}|${entry.label}`;
	const unitOf = (entry) => {
		const match = /[\d.,-]+\s*(.*)$/.exec(entry.text || '');
		return match ? match[1].trim() : '';
	};

	// Long press opens a sheet; a short tap keeps whatever the element already does
	function attachLongPress(element, open) {
		let timer = 0;
		let origin = null;
		let opened = false;
		const cancel = () => {
			clearTimeout(timer);
			timer = 0;
		};
		element.addEventListener('pointerdown', (event) => {
			origin = { x: event.clientX, y: event.clientY };
			opened = false;
			timer = setTimeout(() => {
				timer = 0;
				opened = true;
				open();
			}, LONG_PRESS_MS);
		});
		// Capture phase: runs before the element's own click handler, which a long press must not trigger
		element.addEventListener('click', (event) => {
			if (opened) {
				opened = false;
				event.stopImmediatePropagation();
			}
		}, true);
		element.addEventListener('pointermove', (event) => {
			if (timer && origin && Math.hypot(event.clientX - origin.x, event.clientY - origin.y) > PRESS_MOVE_TOLERANCE) {
				cancel();
			}
		});
		['pointerup', 'pointerleave', 'pointercancel'].forEach((name) => element.addEventListener(name, cancel));
	}

	function pickerFits(picker, entry) {
		return unitOf(entry) === picker.unit
			&& (!picker.gpu || /gpu/i.test(entry.sensor))
			&& (!picker.drives || DISK_SENSOR.test(entry.sensor));
	}

	// Entry the settings read now for a picker; a disk: the first of its drives that has one
	function configuredKey(picker) {
		const keys = picker.drives ? picker.drives.map((name) => DRIVE_PREFIX + name) : [picker.key];
		for (const key of keys) {
			const entry = hwinfoList.entries[hwinfoList.matches[key]];
			if (entry) {
				return entryKey(entry);
			}
		}
		return '';
	}

	function openSheet(def) {
		sheet.def = def;
		sheet.open = null;
		sheet.choices = {};
		sheet.original = {};
		sheet.limits = def.threshold ? { ...limits[def.threshold.key] } : null;
		sheet.weather = def.weather ? weatherConfig.enabled : null;
		text('sheetTitle', def.title);
		$('sheet').hidden = false;
		requestHwInfoList(true);
		clearInterval(sheet.timer);
		// Live values while the sheet is open; HWiNFO itself refreshes every 2 s by default
		sheet.timer = setInterval(() => requestHwInfoList(false), SHEET_REFRESH_MS);
		renderSheet();
	}

	function openDiskSheet(index) {
		const disks = (current && current.disks) || [];
		const disk = disks[index];
		if (!disk) {
			return;
		}
		// Every drive on the same physical disk shares the setting
		const drives = disks.filter((d) => isNum(disk.disk) ? d.disk === disk.disk : d.name === disk.name).map((d) => d.name);
		openSheet({
			title: `Disque ${drives.join(' · ')}`,
			pickers: [{ key: 'disk', title: 'Température (HWiNFO)', unit: '°C', drives }],
			threshold: { key: 'diskTemp', unit: '°C', step: 1, maxStep: 5, alertOnly: true }
		});
	}

	function closeSheet() {
		clearInterval(sheet.timer);
		sheet.timer = 0;
		sheet.def = null;
		$('sheet').hidden = true;
	}

	function requestHwInfoList(first) {
		if (host) {
			host.postMessage({ type: 'hwinfoList' });
		} else if (first) {
			onHwInfoList(SAMPLE_HWINFO);
		}
	}

	function onHwInfoList(list) {
		const firstList = hwinfoList.entries.length === 0;
		hwinfoList = { entries: list.entries || [], matches: list.matches || {} };
		if (!sheet.def) {
			return;
		}
		for (const picker of sheet.def.pickers) {
			if (!(picker.key in sheet.original) || firstList) {
				sheet.original[picker.key] = configuredKey(picker);
				if (!(picker.key in sheet.choices)) {
					sheet.choices[picker.key] = sheet.original[picker.key];
				}
			}
		}
		renderSheet();
	}

	function sheetChanged() {
		if (!sheet.def) {
			return false;
		}
		const pickerChanged = sheet.def.pickers.some((p) => sheet.choices[p.key] !== sheet.original[p.key]);
		const original = sheet.def.threshold ? limits[sheet.def.threshold.key] : null;
		const limitsChanged = original && ['min', 'max', 'warm', 'hot'].some((f) => sheet.limits[f] !== original[f]);
		const weatherChanged = sheet.def.weather && sheet.weather !== weatherConfig.enabled;
		return pickerChanged || Boolean(limitsChanged) || Boolean(weatherChanged);
	}

	function sheetRow(className, title, detail, value, selected) {
		const row = document.createElement('button');
		row.type = 'button';
		row.className = className + (selected ? ' selected' : '');
		const texts = document.createElement('span');
		texts.className = 'sheet-texts';
		const name = document.createElement('span');
		name.className = 'sheet-name';
		name.textContent = title;
		texts.append(name);
		if (detail) {
			const small = document.createElement('span');
			small.className = 'sheet-detail';
			small.textContent = detail;
			texts.append(small);
		}
		const number = document.createElement('span');
		number.className = 'sheet-value data';
		number.textContent = value || '';
		row.append(texts, number);
		return row;
	}

	function renderSheet() {
		const body = $('sheetBody');
		const panel = body.parentElement;
		const scroll = body.scrollTop;
		// The open list floats over the sheet like a dropdown: rebuilt with its scroll position kept
		const previous = panel.querySelector('.sheet-dropdown');
		const listScroll = previous ? previous.scrollTop : 0;
		previous?.remove();
		let dropdown = null;
		let anchor = null;
		body.replaceChildren();
		const byKey = new Map(hwinfoList.entries.map((e) => [entryKey(e), e]));
		for (const picker of sheet.def.pickers) {
			const section = document.createElement('span');
			section.className = 'label editor-section';
			section.textContent = picker.title;
			const choice = byKey.get(sheet.choices[picker.key]);
			const head = sheetRow('sheet-current', choice ? choice.label : 'Aucune valeur', choice ? choice.sensor : 'Touche pour choisir', choice ? choice.text : '--', false);
			head.addEventListener('click', () => {
				sheet.open = sheet.open === picker.key ? null : picker.key;
				renderSheet();
			});
			body.append(section, head);
			if (sheet.open === picker.key) {
				head.classList.add('open');
				const list = document.createElement('div');
				list.className = 'sheet-dropdown';
				const fitting = hwinfoList.entries.filter((e) => pickerFits(picker, e) || entryKey(e) === sheet.choices[picker.key]);
				const none = sheetRow('sheet-option', 'Aucune', fitting.length ? null : `Aucune valeur en ${picker.unit} dans HWiNFO Gadget`, '', sheet.choices[picker.key] === '');
				none.addEventListener('click', () => pick(picker.key, ''));
				list.append(none);
				for (const entry of fitting) {
					const row = sheetRow('sheet-option', entry.label, entry.sensor, entry.text, entryKey(entry) === sheet.choices[picker.key]);
					row.addEventListener('click', () => pick(picker.key, entryKey(entry)));
					list.append(row);
				}
				dropdown = list;
				anchor = head;
			}
		}
		if (sheet.def.weather) {
			// No keyboard on the case screen: the place is picked in the setup wizard on the main screen
			const located = weatherConfig.located;
			const detail = located ? `${weatherConfig.place} · lieu modifiable dans les paramètres`
				: 'Choisir d\u2019abord une ville : menu de l\u2019icône › Paramètres';
			const row = sheetRow('sheet-option sheet-toggle', 'Afficher la météo', detail, sheet.weather ? 'Oui' : 'Non', sheet.weather);
			row.setAttribute('role', 'switch');
			row.setAttribute('aria-checked', String(sheet.weather));
			row.disabled = !located;
			row.addEventListener('click', () => {
				sheet.weather = !sheet.weather;
				renderSheet();
			});
			body.append(row);
		}
		if (sheet.def.threshold) {
			const t = sheet.def.threshold;
			const section = document.createElement('span');
			section.className = 'label editor-section';
			section.textContent = 'Seuils';
			body.append(section);
			for (const row of THRESHOLD_ROWS) {
				if (t.alertOnly && row.gauge) {
					continue;
				}
				const line = document.createElement('div');
				line.className = 'sheet-stepper';
				const name = document.createElement('span');
				name.className = 'sheet-name';
				name.textContent = row.label;
				const minus = document.createElement('button');
				minus.type = 'button';
				minus.textContent = '−';
				const value = document.createElement('span');
				value.className = 'sheet-step-value data';
				value.textContent = `${nf0(sheet.limits[row.field])} ${t.unit}`;
				const plus = document.createElement('button');
				plus.type = 'button';
				plus.textContent = '+';
				const step = row.field === 'max' ? t.maxStep : t.step;
				minus.addEventListener('click', () => adjust(row.field, -step));
				plus.addEventListener('click', () => adjust(row.field, step));
				line.append(name, minus, value, plus);
				body.append(line);
			}
		}
		$('sheetSave').disabled = !sheetChanged();
		body.scrollTop = scroll;
		if (dropdown) {
			// Right under the row that opened it, in the panel's frame (offsets ignore the page scale)
			dropdown.style.top = `${body.offsetTop + anchor.offsetTop - body.scrollTop + anchor.offsetHeight}px`;
			panel.append(dropdown);
			dropdown.scrollTop = listScroll;
		}
	}

	function pick(key, choice) {
		sheet.choices[key] = choice;
		sheet.open = null;
		renderSheet();
	}

	// Levels stay ordered: min < warm <= hot <= max
	function adjust(field, delta) {
		const l = sheet.limits;
		l[field] += delta;
		if (field === 'max') {
			l.max = Math.max(l.max, l.hot, l.min + delta);
		} else if (field === 'hot') {
			l.hot = clamp(l.hot, l.warm, l.max);
		} else if (field === 'warm') {
			l.warm = clamp(l.warm, l.min, l.hot);
		}
		// Alert-only rows (disks) move warm with hot so the pair stays ordered
		if (sheet.def.threshold.alertOnly) {
			l.warm = Math.min(l.warm, l.hot);
		}
		renderSheet();
	}

	function saveSheet() {
		const byKey = new Map(hwinfoList.entries.map((e) => [entryKey(e), e]));
		const message = { type: 'saveMetric', sensors: {}, drives: {}, thresholds: {} };
		for (const picker of sheet.def.pickers) {
			if (sheet.choices[picker.key] === sheet.original[picker.key]) {
				continue;
			}
			const entry = byKey.get(sheet.choices[picker.key]);
			// PresentMon's sensor carries the foreground process name: saved without it
			const selector = entry ? { sensor: entry.sensor.replace(/\[[^\]]+\.exe\]$/i, '[*]'), label: entry.label } : null;
			if (picker.drives) {
				picker.drives.forEach((name) => {
					message.drives[name] = selector;
				});
			} else {
				message.sensors[picker.key] = selector;
			}
		}
		if (sheet.def.threshold) {
			message.thresholds[sheet.def.threshold.key] = sheet.limits;
		}
		if (sheet.def.weather) {
			message.weather = { enabled: sheet.weather };
		}
		send(message);
		if (!host && sheet.def.threshold) {
			// Design mode: apply locally so the result can be seen
			applyLimits({ [sheet.def.threshold.key]: sheet.limits });
		}
		closeSheet();
	}

	// A touch anywhere else in the sheet folds the open list, like a dropdown
	$('sheet').addEventListener('click', (event) => {
		if (sheet.open && !event.target.closest('.sheet-dropdown, .sheet-current')) {
			sheet.open = null;
			renderSheet();
		}
	});
	$('sheetClose').addEventListener('click', closeSheet);
	$('sheetCancel').addEventListener('click', closeSheet);
	$('sheetSave').addEventListener('click', saveSheet);
	attachLongPress($('cpuTempTile'), () => openSheet(SHEETS.cpuTemp));
	attachLongPress($('cpuPowTile'), () => openSheet(SHEETS.cpuPower));
	attachLongPress($('gpuTempTile'), () => openSheet(SHEETS.gpuTemp));
	attachLongPress($('gpuPowTile'), () => openSheet(SHEETS.gpuPower));
	attachLongPress($('gpu'), () => openSheet(SHEETS.gpuLoad));
	attachLongPress(document.querySelector('.vram'), () => openSheet(SHEETS.gpuVram));
	attachLongPress(document.querySelector('.clock'), () => openSheet(SHEETS.clock));
	attachLongPress($('gameFps'), () => openSheet(SHEETS.fps));

	// Design mode: Gadget entries like the author's machine
	const SAMPLE_HWINFO = {
		entries: [
			{ sensor: 'CPU [#0]: AMD Ryzen 7 9800X3D: Enhanced', label: 'CPU (Tctl/Tdie)', text: '52.2 °C' },
			{ sensor: 'CPU [#0]: AMD Ryzen 7 9800X3D: Enhanced', label: 'CPU Die (average)', text: '49.8 °C' },
			{ sensor: 'CPU [#0]: AMD Ryzen 7 9800X3D: Enhanced', label: 'Consommation d\'énergie totale du CPU', text: '33.0 W' },
			{ sensor: 'dGPU [#0]: NVIDIA GeForce RTX 4090: Zotac RTX 4090', label: 'Température GPU', text: '41.0 °C' },
			{ sensor: 'dGPU [#0]: NVIDIA GeForce RTX 4090: Zotac RTX 4090', label: 'GPU Hot Spot Temperature', text: '50.3 °C' },
			{ sensor: 'dGPU [#0]: NVIDIA GeForce RTX 4090: Zotac RTX 4090', label: 'GPU Consommation d\'énergie', text: '28.4 W' },
			{ sensor: 'dGPU [#0]: NVIDIA GeForce RTX 4090: Zotac RTX 4090', label: 'Charge du cœur du GPU', text: '3.0 %' },
			{ sensor: 'dGPU [#0]: NVIDIA GeForce RTX 4090: Zotac RTX 4090', label: 'Utilisation de la mémoire GPU', text: '11.2 %' },
			{ sensor: 'S.M.A.R.T.: Samsung SSD 9100 PRO 2TB (S7YF) [C:, D:]', label: 'Température du disque', text: '53 °C' },
			{ sensor: 'S.M.A.R.T.: Samsung SSD 990 PRO 4TB (S7DP) [E:]', label: 'Température du disque', text: '40 °C' }
		],
		matches: { cpuTemp: 0, cpuPower: 2, gpuTemp: 3, gpuPower: 5, gpuLoad: 6, gpuVram: 7, gpuFps: -1, gpuFpsLow: -1, gpuFrameTime: -1, 'drive:C:': 8, 'drive:D:': 8, 'drive:E:': 9 }
	};

	// ---------- Simulator: design mode (no host) or "?demo=" scenarios (idle, stress) ----------
	const SCENARIOS = {
		idle: { cpu: 14, cpuSpread: 9, gpu: 5, vram: 6, ram: 19.3, offline: false },
		// Everything maxed out and the connection lost, to preview every alarm state at once
		stress: { cpu: 100, cpuSpread: 0, gpu: 100, vram: 96, ram: 55, offline: true, fps: 144 },
		// A windowed game on a 240 Hz monitor, 42 min in: previews the game view
		game: { cpu: 21, cpuSpread: 8, gpu: 46, vram: 13, ram: 24, offline: false, fps: 212, fpsLow: 164, game: { name: 'League of Legends', refreshHz: 240 } },
		// The same game sent to the background (desktop) after 3 s: frozen game view, then the normal view
		desktop: { cpu: 8, cpuSpread: 5, gpu: 6, vram: 13, ram: 24, offline: false, fps: 212, fpsLow: 164, backgroundAfter: 3, game: { name: 'League of Legends', refreshHz: 240 } }
	};

	// Set while simulated data replaces the host's: design mode or a demo scenario
	let simulating = false;

	function startSimulator(scenarioName) {
		simulating = true;
		const scenario = SCENARIOS[scenarioName] || SCENARIOS.idle;
		const sim = { cpu: scenario.cpu, gpu: scenario.gpu, cpuTemp: 30 + scenario.cpu * 0.6, gpuTemp: 31 + scenario.gpu * 0.52, vram: scenario.vram, ram: scenario.ram };
		let position = 72;
		let offlineSec = 74;
		let seconds = 0;
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
		// Weather line with a sample reading (design mode / demo)
		weatherConfig = { enabled: true, place: 'Lyon', located: true };
		renderWeather({ place: 'Lyon', temperature: 14.6, code: 2, isDay: true, min: 8, max: 16 });
		// The update banner is part of the preview too
		renderUpdate({ stage: 'available', version: '2.0.0' });
		if (scenario.game) {
			// A session already under way: elapsed time and a full minute of history
			gameSession = startGameSession();
			gameSession.start -= (42 * 60 + 18) * 1000;
			for (let i = 0; i < FPS_HISTORY - 1; i++) {
				const fps = i === 38 ? 150 : scenario.fps - 6 + Math.sin(i * 1.7) * 9 + Math.cos(i * 0.53) * 7;
				gameSession.fps.push(fps);
				gameSession.fpsSum += fps;
				gameSession.fpsCount++;
			}
		}
		setInterval(() => {
			sim.cpu = scenario.cpu >= 100 ? 100 : walk(sim.cpu, scenario.cpu, scenario.cpuSpread, 1, 100);
			sim.gpu = scenario.gpu >= 100 ? 100 : walk(sim.gpu, scenario.gpu, 5, 0, 100);
			sim.cpuTemp += (30 + sim.cpu * 0.63 - sim.cpuTemp) * 0.15;
			sim.gpuTemp += (31 + sim.gpu * 0.52 - sim.gpuTemp) * 0.08;
			sim.vram = walk(sim.vram, scenario.vram, 1.5, 2, 98);
			sim.ram = walk(sim.ram, scenario.ram, 0.2, 8, 60);
			position = (position + 1) % 182;
			offlineSec += 1;
			seconds += 1;
			const background = isNum(scenario.backgroundAfter) && seconds > scenario.backgroundAfter;
			const burst = Math.random() < 0.15;
			const down = scenario.offline ? Math.random() * 400 : (burst ? Math.random() * 22 : Math.random() * 0.7) * MB;
			onHostMessage({
				type: 'snapshot',
				cpu: { load: sim.cpu, temp: sim.cpuTemp, power: 30 + sim.cpu * 1.25 },
				gpu: { load: sim.gpu, temp: sim.gpuTemp, power: 25 + sim.gpu * 4.15, vramPercent: sim.vram, vramTotalGb: 24, fps: scenario.fps && !background ? scenario.fps - Math.random() * 12 : null, fpsLow: scenario.fpsLow && !background ? scenario.fpsLow + Math.random() * 4 : null, frameTimeMs: null },
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
				media: { source: 'Spotify', kind: 'music', title: 'The Imperial March', artist: 'John Williams · London Symphony Orchestra', positionSec: position, durationSec: 182, playing: true, live: false, artKey: null },
				game: scenario.game ? { ...scenario.game, foreground: !background } : null
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
	// "?theme=jedi" (set by the host from the settings, or by hand in design mode); config messages switch it live
	applyTheme(new URLSearchParams(location.search).get('theme') || 'sith');
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
	// "?sheet=cpuTemp" (or disk0, disk1...) opens an on-screen settings sheet at load, for captures
	const sheetPreview = new URLSearchParams(location.search).get('sheet');
	if (sheetPreview) {
		setTimeout(() => {
			if (sheetPreview.startsWith('disk')) {
				openDiskSheet(Number(sheetPreview.slice(4)) || 0);
			} else if (SHEETS[sheetPreview]) {
				openSheet(SHEETS[sheetPreview]);
			}
			// "&list" also unfolds the first value list
			if (sheet.def && new URLSearchParams(location.search).has('list')) {
				sheet.open = sheet.def.pickers[0].key;
				renderSheet();
			}
		}, 1500);
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
