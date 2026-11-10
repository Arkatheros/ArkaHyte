/* Setup wizard: case display, HWiNFO check, sensor mapping, hardware captions.
   Host messages: state (once), hwinfo (every 2 s), saveFailed. Without a WebView2 host the page
   runs in design mode with sample data. */
(() => {
	'use strict';

	const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
	const LAST_STEP = 4;

	/* Widgets fed by HWiNFO. Suggestions: entries of the right unit whose sensor matches, label
	   patterns tried in order (English and French HWiNFO names). */
	const METRICS = [
		{ key: 'cpuTemp', name: 'Température CPU', unit: '°C', sensor: /cpu/i, labels: [/tctl|tdie/i, /package|boîtier/i, /cpu.*(temp|tempé)/i, /core.*max/i] },
		{ key: 'cpuPower', name: 'Consommation CPU', unit: 'W', sensor: /cpu/i, labels: [/total.*cpu|cpu.*total|totale du cpu/i, /package power/i, /power|consommation|puissance/i] },
		{ key: 'gpuTemp', name: 'Température GPU', unit: '°C', sensor: /gpu/i, labels: [/^gpu temperature$|^température gpu$/i, /^(gpu )?temp/i, /temp/i] },
		{ key: 'gpuPower', name: 'Consommation GPU', unit: 'W', sensor: /gpu/i, labels: [/^gpu power$|gpu consommation|consommation d'énergie gpu/i, /power|consommation|puissance/i] },
		{ key: 'gpuLoad', name: 'Charge GPU', unit: '%', sensor: /gpu/i, labels: [/core load|charge du c/i, /gpu (load|utili)/i, /load|charge|utili/i] },
		{ key: 'gpuVram', name: 'Mémoire GPU', unit: '%', sensor: /gpu/i, labels: [/memory usage|utilisation de la mémoire/i, /memory|mémoire/i] }
	];

	const $ = id => document.getElementById(id);
	const pages = [...document.querySelectorAll('.page')];
	const stepButtons = [...document.querySelectorAll('#steps button')];

	const model = {
		step: 0,
		state: null,
		hwinfo: { running: false, entries: [], matches: {} },
		caseScreen: null,
		// Per widget: chosen entry key ('sensor|label' or '') and whether the user picked it
		sensors: {},
		hardwareFilled: false
	};

	const keyOf = entry => entry.sensor + '|' + entry.label;

	function send(message) {
		if (host) {
			host.postMessage(message);
		}
	}

	// ---------- Navigation ----------

	function goTo(step) {
		model.step = Math.max(0, Math.min(LAST_STEP, step));
		pages.forEach((page, index) => page.classList.toggle('current', index === model.step));
		stepButtons.forEach((button, index) => {
			button.classList.toggle('current', index === model.step);
			button.classList.toggle('done', index < model.step);
		});
		$('back').disabled = model.step === 0;
		$('next').textContent = model.step === LAST_STEP ? 'Enregistrer et redémarrer' : 'Suivant';
		// The map is measured from its box, which has no size while its page is hidden
		if (model.step === 0 && model.state) {
			renderScreens();
		}
		if (model.step === LAST_STEP) {
			renderSummary();
		}
	}

	window.addEventListener('resize', () => {
		if (model.step === 0 && model.state) {
			renderScreens();
		}
	});

	stepButtons.forEach((button, index) => button.addEventListener('click', () => goTo(index)));
	$('back').addEventListener('click', () => goTo(model.step - 1));
	$('next').addEventListener('click', () => (model.step === LAST_STEP ? save() : goTo(model.step + 1)));
	$('skip').addEventListener('click', () => send({ type: 'skip' }));
	$('identify').addEventListener('click', () => send({ type: 'identify' }));
	$('startHwInfo').addEventListener('click', () => send({ type: 'startHwInfo' }));
	$('downloadHwInfo').addEventListener('click', () => send({ type: 'openHwInfoSite' }));
	$('useDetected').addEventListener('click', () => fillHardware(model.state.detected));

	// ---------- Case display ----------

	function sameSize(screen, size) {
		return (screen.width === size.width && screen.height === size.height)
			|| (screen.width === size.height && screen.height === size.width);
	}

	// Configured size first, then a very elongated screen (the Y70 panel is 3.75:1)
	function guessCaseScreen(screens, caseSize) {
		return screens.find(s => sameSize(s, caseSize))
			|| screens.find(s => Math.max(s.width, s.height) / Math.min(s.width, s.height) >= 3)
			|| null;
	}

	function renderScreens() {
		const screens = model.state.screens;
		const map = $('screenMap');
		map.textContent = '';
		const left = Math.min(...screens.map(s => s.x));
		const top = Math.min(...screens.map(s => s.y));
		const right = Math.max(...screens.map(s => s.x + s.width));
		const bottom = Math.max(...screens.map(s => s.y + s.height));
		const pad = 16;
		const scale = Math.min((map.clientWidth - 2 * pad) / (right - left), (map.clientHeight - 2 * pad) / (bottom - top));
		const offsetX = (map.clientWidth - (right - left) * scale) / 2;
		const offsetY = (map.clientHeight - (bottom - top) * scale) / 2;

		for (const screen of screens) {
			const tile = document.createElement('button');
			tile.type = 'button';
			tile.className = 'screen' + (screen === model.caseScreen ? ' selected' : '');
			tile.style.left = offsetX + (screen.x - left) * scale + 1 + 'px';
			tile.style.top = offsetY + (screen.y - top) * scale + 1 + 'px';
			tile.style.width = screen.width * scale - 2 + 'px';
			tile.style.height = screen.height * scale - 2 + 'px';
			const number = document.createElement('span');
			number.className = 'screen-number';
			number.textContent = screen.number;
			const size = document.createElement('span');
			size.textContent = screen.width + ' × ' + screen.height;
			tile.append(number, size);
			const tag = screen === model.caseScreen ? 'Boîtier' : (screen.primary ? 'Principal' : '');
			if (tag) {
				const label = document.createElement('span');
				label.className = 'screen-tag';
				label.textContent = tag;
				tile.append(label);
			}
			tile.title = screen.name;
			tile.addEventListener('click', () => {
				model.caseScreen = screen;
				renderScreens();
			});
			map.append(tile);
		}

		const status = $('screenStatus');
		const selected = model.caseScreen;
		if (!selected) {
			status.className = 'status warn';
			status.textContent = 'Aucun écran choisi : l\'affichage attendra un écran de '
				+ model.state.caseSize.width + ' × ' + model.state.caseSize.height + '.';
			return;
		}
		const twins = screens.filter(s => s !== selected && sameSize(s, selected));
		if (selected.primary) {
			status.className = 'status bad';
			status.textContent = 'C\'est l\'écran principal : l\'affichage le couvrirait entièrement.';
		}
		else if (twins.length) {
			status.className = 'status warn';
			status.textContent = 'L\'écran ' + twins.map(s => s.number).join(', ')
				+ ' a la même taille : Arka Hyte pourrait s\'afficher sur l\'un ou l\'autre.';
		}
		else {
			status.className = 'status';
			status.textContent = 'Écran ' + selected.number + ' (' + selected.name + ', ' + selected.width + ' × '
				+ selected.height + ') : Arka Hyte s\'y affichera.';
		}
	}

	// ---------- HWiNFO ----------

	function setCheck(element, level, html) {
		element.className = 'check ' + level;
		element.querySelector('.check-text').innerHTML = html;
	}

	function renderHwInfo() {
		const { running, entries } = model.hwinfo;
		setCheck($('checkRunning'), running ? 'ok' : 'bad',
			running ? '<b>HWiNFO</b> est lancé.' : '<b>HWiNFO</b> n\'est pas lancé.');
		setCheck($('checkGadget'), entries.length ? 'ok' : (running ? 'warn' : 'bad'),
			entries.length
				? '<b>' + entries.length + ' valeur' + (entries.length > 1 ? 's' : '') + '</b> transmise' + (entries.length > 1 ? 's' : '') + ' à Gadget.'
				: 'Aucune valeur transmise à Gadget.');
		$('startHwInfo').hidden = running || !(model.state && model.state.hwInfoPath);
	}

	// ---------- Sensors ----------

	function unitOf(entry) {
		const match = /[\d.,-]+\s*(.*)$/.exec(entry.text);
		return match ? match[1].trim() : '';
	}

	function suggest(metric, entries) {
		const candidates = entries.filter(e => unitOf(e) === metric.unit && metric.sensor.test(e.sensor));
		for (const pattern of metric.labels) {
			const found = candidates.find(e => pattern.test(e.label));
			if (found) {
				return found;
			}
		}
		return null;
	}

	// Rows the user has not touched follow the configuration, or a suggestion when it matches nothing
	function updateSensorChoices() {
		const { entries, matches } = model.hwinfo;
		for (const metric of METRICS) {
			const current = model.sensors[metric.key] || (model.sensors[metric.key] = { choice: '', touched: false, origin: 'missing' });
			if (current.touched) {
				continue;
			}
			const configured = entries[matches[metric.key]];
			const suggestion = configured ? null : suggest(metric, entries);
			current.choice = configured ? keyOf(configured) : (suggestion ? keyOf(suggestion) : '');
			current.origin = configured ? 'configured' : (suggestion ? 'suggested' : 'missing');
		}
	}

	let renderedEntryKeys = null;

	function renderSensors() {
		const entries = model.hwinfo.entries;
		$('sensorsEmpty').hidden = entries.length > 0;
		const body = $('sensors').tBodies[0];
		const entryKeys = entries.map(keyOf).join('\n');

		// Rebuilding the lists would close an open dropdown: only when the entries themselves change
		if (entryKeys !== renderedEntryKeys) {
			renderedEntryKeys = entryKeys;
			body.textContent = '';
			const groups = new Map();
			for (const entry of entries) {
				if (!groups.has(entry.sensor)) {
					groups.set(entry.sensor, []);
				}
				groups.get(entry.sensor).push(entry);
			}
			for (const metric of METRICS) {
				const row = body.insertRow();
				row.dataset.key = metric.key;
				const name = row.insertCell();
				name.textContent = metric.name;
				const tag = document.createElement('span');
				tag.className = 'tag';
				name.append(tag);
				const select = document.createElement('select');
				select.append(new Option('— Aucune —', ''));
				for (const [sensor, items] of groups) {
					const group = document.createElement('optgroup');
					group.label = sensor;
					items.forEach(e => group.append(new Option(e.label, keyOf(e))));
					select.append(group);
				}
				select.addEventListener('change', () => {
					model.sensors[metric.key] = { choice: select.value, touched: true, origin: 'chosen' };
					renderSensors();
				});
				row.insertCell().append(select);
				row.insertCell().className = 'num';
			}
		}

		const byKey = new Map(entries.map(e => [keyOf(e), e]));
		for (const row of body.rows) {
			const sensor = model.sensors[row.dataset.key];
			const select = row.cells[1].firstChild;
			if (select.value !== sensor.choice) {
				select.value = sensor.choice;
			}
			const entry = byKey.get(sensor.choice);
			row.cells[2].textContent = entry ? entry.text : '--';
			const tag = row.cells[0].querySelector('.tag');
			const labels = { configured: 'configuré', suggested: 'suggéré', missing: 'non trouvé', chosen: 'choisi' };
			tag.textContent = sensor.choice ? labels[sensor.origin] : 'non attribué';
			tag.className = 'tag ' + (sensor.choice ? sensor.origin : 'missing');
		}
	}

	// ---------- Hardware ----------

	const HARDWARE_FIELDS = ['cpuMaker', 'cpuModel', 'gpuMaker', 'gpuModel', 'vramTotalGb'];

	function fillHardware(hardware) {
		HARDWARE_FIELDS.forEach(field => {
			$(field).value = hardware[field] ?? '';
		});
	}

	function readHardware() {
		const hardware = {};
		HARDWARE_FIELDS.forEach(field => {
			hardware[field] = field === 'vramTotalGb' ? (Number($(field).value) || 0) : $(field).value.trim();
		});
		return hardware;
	}

	// ---------- Summary and save ----------

	function renderSummary() {
		const summary = $('summary');
		summary.textContent = '';
		const add = (level, html) => {
			const row = document.createElement('div');
			row.className = 'check ' + level;
			row.innerHTML = '<span class="mark"></span><span class="check-text">' + html + '</span>';
			summary.append(row);
		};
		const screen = model.caseScreen;
		add(screen && !screen.primary ? 'ok' : 'warn', screen
			? 'Écran du boîtier : <b>écran ' + screen.number + '</b> (' + screen.width + ' × ' + screen.height + ')'
			: 'Écran du boîtier : <b>non choisi</b>, réglage actuel conservé');
		const { running, entries } = model.hwinfo;
		add(running && entries.length ? 'ok' : 'warn', running
			? 'HWiNFO : <b>' + entries.length + '</b> valeur' + (entries.length > 1 ? 's' : '') + ' transmise' + (entries.length > 1 ? 's' : '')
			: 'HWiNFO : <b>non lancé</b>, les widgets de capteurs afficheront « -- »');
		if (entries.length) {
			const assigned = METRICS.filter(m => model.sensors[m.key] && model.sensors[m.key].choice).length;
			add(assigned === METRICS.length ? 'ok' : 'warn', 'Capteurs : <b>' + assigned + ' / ' + METRICS.length + '</b> attribués');
		}
		else {
			add('warn', 'Capteurs : réglage actuel conservé');
		}
		const hardware = readHardware();
		add('ok', 'Matériel : <b>' + escapeHtml([hardware.cpuMaker, hardware.cpuModel].join(' ').trim() || '--')
			+ '</b> · <b>' + escapeHtml([hardware.gpuMaker, hardware.gpuModel].join(' ').trim() || '--') + '</b>');
	}

	function escapeHtml(text) {
		const span = document.createElement('span');
		span.textContent = text;
		return span.innerHTML;
	}

	function save() {
		const message = { type: 'save', hardware: readHardware() };
		if (model.caseScreen) {
			message.display = { width: model.caseScreen.width, height: model.caseScreen.height };
		}
		// Without HWiNFO entries nothing can be chosen: the current mapping is kept as is
		const { entries, matches } = model.hwinfo;
		if (entries.length) {
			const byKey = new Map(entries.map(e => [keyOf(e), e]));
			message.sensors = {};
			for (const metric of METRICS) {
				const choice = model.sensors[metric.key].choice;
				const configured = entries[matches[metric.key]];
				// Unchanged rows are left out so the existing selector (wildcards included) stays
				if (configured && keyOf(configured) === choice) {
					continue;
				}
				const entry = byKey.get(choice);
				message.sensors[metric.key] = entry ? { sensor: entry.sensor, label: entry.label } : null;
			}
		}
		$('saveError').hidden = true;
		$('next').disabled = true;
		send(message);
		if (!host) {
			console.log('save', message);
			$('next').disabled = false;
		}
	}

	// ---------- Host messages ----------

	function onState(state) {
		model.state = state;
		model.caseScreen = guessCaseScreen(state.screens, state.caseSize);
		if (model.step === 0) {
			renderScreens();
		}
		if (!model.hardwareFilled) {
			model.hardwareFilled = true;
			// First run: the shipped captions describe the author's machine, the detection is closer
			fillHardware(state.completed ? state.hardware : state.detected);
		}
		const detected = state.detected;
		$('detectedText').textContent = 'Détecté : ' + [detected.cpuMaker, detected.cpuModel].join(' ').trim()
			+ ' · ' + [detected.gpuMaker, detected.gpuModel].join(' ').trim() + ' · ' + detected.vramTotalGb + ' Go';
		renderHwInfo();
	}

	function onHwInfo(hwinfo) {
		model.hwinfo = hwinfo;
		updateSensorChoices();
		renderHwInfo();
		renderSensors();
		if (model.step === LAST_STEP) {
			renderSummary();
		}
	}

	function dispatch(message) {
		switch (message.type) {
			case 'state':
				onState(message);
				break;
			case 'hwinfo':
				onHwInfo(message);
				break;
			case 'saveFailed':
				$('saveError').hidden = false;
				$('next').disabled = false;
				break;
		}
	}

	const startStep = Number(new URLSearchParams(location.search).get('step')) || 0;
	goTo(startStep);

	if (host) {
		host.addEventListener('message', event => dispatch(event.data));
		send({ type: 'ready' });
	}
	else {
		// Design mode: the author's setup, HWiNFO in French
		const sensorCpu = 'CPU [#0]: AMD Ryzen 7 9800X3D: Enhanced';
		const sensorGpu = 'GPU [#0]: NVIDIA GeForce RTX 4090: ';
		dispatch({
			type: 'state',
			completed: false,
			caseSize: { width: 682, height: 2560 },
			screens: [
				{ number: 1, name: 'DISPLAY1', x: -682, y: 1440, width: 682, height: 2560, primary: false },
				{ number: 2, name: 'DISPLAY2', x: 0, y: 0, width: 3440, height: 1440, primary: true }
			],
			hardware: { cpuMaker: 'AMD', cpuModel: 'Ryzen 7 9800X3D', gpuMaker: 'NVIDIA', gpuModel: 'GeForce RTX 4090', vramTotalGb: 24 },
			detected: { cpuMaker: 'AMD', cpuModel: 'Ryzen 7 9800X3D', gpuMaker: 'NVIDIA', gpuModel: 'GeForce RTX 4090', vramTotalGb: 24 },
			hwInfoPath: 'C:\\Program Files\\HWiNFO64\\HWiNFO64.exe'
		});
		dispatch({
			type: 'hwinfo',
			running: true,
			entries: [
				{ sensor: sensorCpu, label: 'CPU (Tctl/Tdie)', text: '52.2 °C' },
				{ sensor: sensorCpu, label: 'Consommation d\'énergie totale du CPU', text: '33.013 W' },
				{ sensor: sensorGpu, label: 'Température GPU', text: '41.0 °C' },
				{ sensor: sensorGpu, label: 'GPU Consommation d\'énergie', text: '28.410 W' },
				{ sensor: sensorGpu, label: 'Charge du cœur du GPU', text: '3.0 %' },
				{ sensor: sensorGpu, label: 'Utilisation de la mémoire GPU', text: '11.2 %' }
			],
			matches: { cpuTemp: 0, cpuPower: 1, gpuTemp: -1, gpuPower: -1, gpuLoad: 4, gpuVram: 5 }
		});
	}
})();
