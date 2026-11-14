/* Setup wizard: case display, HWiNFO check, sensor mapping, hardware captions.
   Host messages: state (once), hwinfo (every 2 s), saveFailed. Without a WebView2 host the page
   runs in design mode with sample data. */
(() => {
	'use strict';

	const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
	const LAST_STEP = 6;
	const GAME_VIEW_STEP = 5;

	/* Widgets fed by HWiNFO. Suggestions: entries of the right unit whose sensor matches, label
	   patterns tried in order (English and French HWiNFO names). */
	const METRICS = [
		{ key: 'cpuTemp', name: 'Température CPU', unit: '°C', sensor: /cpu/i, labels: [/tctl|tdie/i, /package|boîtier/i, /cpu.*(temp|tempé)/i, /core.*max/i] },
		{ key: 'cpuPower', name: 'Consommation CPU', unit: 'W', sensor: /cpu/i, labels: [/total.*cpu|cpu.*total|totale du cpu/i, /package power/i, /power|consommation|puissance/i] },
		{ key: 'gpuTemp', name: 'Température GPU', unit: '°C', sensor: /gpu/i, strict: true, labels: [/^gpu temperature$|^température gpu$/i, /^(gpu )?temp/i, /temp/i] },
		{ key: 'gpuPower', name: 'Consommation GPU', unit: 'W', sensor: /gpu/i, strict: true, labels: [/^gpu power$|gpu consommation|consommation d'énergie gpu/i, /power|consommation|puissance/i] },
		{ key: 'gpuLoad', name: 'Charge GPU', unit: '%', sensor: /gpu/i, strict: true, labels: [/core load|charge du c/i, /gpu (load|utili)/i, /load|charge|utili/i] },
		{ key: 'gpuVram', name: 'Mémoire GPU', unit: '%', sensor: /gpu/i, strict: true, labels: [/memory usage|utilisation de la mémoire/i, /memory|mémoire/i] },
		// HWiNFO's own PresentMon sensor first (no extra tool), RTSS otherwise; only filled in games,
		// so not counted as missing
		{ key: 'gpuFps', name: 'FPS en jeu', unit: 'FPS', sensor: /./, labels: [/displayed.*avg|framerate \(displayed\)/i, /displayed/i, /presented/i, /^framerate$/i, /^(framerate|fps)/i, /framerate|fps|images/i], optional: true },
		// Game view extras: computed from the framerate when left out
		{ key: 'gpuFpsLow', name: 'FPS 1 % low', unit: 'FPS', sensor: /./, labels: [/displayed.*1\s?%|1\s?%.*displayed/i, /1\s?%\s*low/i, /1\s?%/i], optional: true },
		{ key: 'gpuFrameTime', name: 'Temps de frame', unit: 'ms', sensor: /presentmon|rtss/i, labels: [/frame ?time.*displayed.*avg/i, /frame ?time.*displayed/i, /frame ?time|frametime|temps (de |d')?(trame|image|frame)/i], optional: true }
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
		hardwareFilled: false,
		// Opened from the tray once set up: the pages are settings, saved without the summary step
		settingsMode: false
	};

	const keyOf = entry => entry.sensor + '|' + entry.label;

	function send(message) {
		if (host) {
			host.postMessage(message);
		}
	}

	// ---------- Navigation ----------

	function goTo(step) {
		const last = model.settingsMode ? LAST_STEP - 1 : LAST_STEP;
		model.step = Math.max(0, Math.min(last, step));
		pages.forEach((page, index) => page.classList.toggle('current', index === model.step));
		stepButtons.forEach((button, index) => {
			button.classList.toggle('current', index === model.step);
			button.classList.toggle('done', !model.settingsMode && index < model.step);
		});
		$('back').disabled = model.step === 0;
		$('next').textContent = model.settingsMode ? 'Enregistrer' : model.step === LAST_STEP ? 'Enregistrer et redémarrer' : 'Suivant';
		// The map is measured from its box, which has no size while its page is hidden
		if (model.step === 0 && model.state) {
			renderScreens();
		}
		if (model.step === LAST_STEP) {
			renderSummary();
		}
		// Game view step: the applications open now, to add one as a game
		if (model.step === GAME_VIEW_STEP) {
			send({ type: 'listProcesses' });
		}
	}

	window.addEventListener('resize', () => {
		if (model.step === 0 && model.state) {
			renderScreens();
		}
	});

	stepButtons.forEach((button, index) => button.addEventListener('click', () => goTo(index)));
	$('back').addEventListener('click', () => goTo(model.step - 1));
	$('next').addEventListener('click', () => (model.settingsMode || model.step === LAST_STEP ? save() : goTo(model.step + 1)));
	$('close').addEventListener('click', () => send({ type: 'close' }));

	function setMode(settings) {
		model.settingsMode = settings;
		document.body.classList.toggle('settings-mode', settings);
		$('close').hidden = !settings;
		const name = settings ? 'Paramètres' : 'Configuration';
		document.querySelector('.brand-sub').textContent = name;
		document.title = 'Arka Hyte · ' + name;
		goTo(model.step);
	}

	function showFootStatus(message, bad) {
		const status = $('footStatus');
		status.textContent = message;
		status.classList.toggle('bad', Boolean(bad));
	}
	$('skip').addEventListener('click', () => send({ type: 'skip' }));
	$('identify').addEventListener('click', () => send({ type: 'identify' }));
	$('startHwInfo').addEventListener('click', () => send({ type: 'startHwInfo' }));
	$('configureHwInfo').addEventListener('click', () => {
		$('configureHwInfo').disabled = true;
		showConfigureStatus('Configuration en cours : HWiNFO va se fermer puis se relancer…', '');
		send({ type: 'configureHwInfo' });
		if (!host) {
			setTimeout(() => onHwInfoConfigured({ result: { ok: true, added: ['CPU [#0]: AMD Ryzen 7 9800X3D | Average Effective Clock'], discovered: 412 } }), 1500);
		}
	});

	function showConfigureStatus(text, level) {
		const status = $('configureStatus');
		status.hidden = false;
		status.className = 'status ' + level;
		status.textContent = text;
	}

	const CONFIGURE_ERRORS = {
		declined: 'Autorisation refusée : HWiNFO n’a pas été modifié.',
		notInstalled: 'HWiNFO est introuvable sur ce PC.',
		gadgetOff: 'Gadget n’est pas activé dans HWiNFO (étape 2).',
		noReadings: 'HWiNFO n’a rien transmis à Gadget pendant la découverte : rien n’a été changé. Vérifiez qu’il démarre bien, Gadget activé (étape 2).',
		failed: 'La configuration a échoué : voir le journal (menu de l’icône › Ouvrir le dossier des journaux).'
	};

	function onHwInfoConfigured(message) {
		$('configureHwInfo').disabled = false;
		const result = message.result || { ok: false, error: message.error };
		if (!result.ok) {
			showConfigureStatus(CONFIGURE_ERRORS[result.error] || CONFIGURE_ERRORS.failed, 'bad');
			return;
		}
		const added = result.added || [];
		showConfigureStatus(added.length
			? added.length + ' valeur' + (added.length > 1 ? 's' : '') + ' ajoutée' + (added.length > 1 ? 's' : '') + ' à Gadget (' + result.discovered + ' disponibles dans HWiNFO).'
			: 'Rien à ajouter : les valeurs utiles étaient déjà transmises à Gadget.', '');
	}
	$('downloadHwInfo').addEventListener('click', () => send({ type: 'openHwInfoSite' }));
	$('useDetected').addEventListener('click', () => fillHardware(model.state.detected));

	// ---------- Weather ----------
	// The place picked here: { place, latitude, longitude }, null until one is chosen or loaded
	let weatherPlace = null;

	function searchPlaces() {
		const query = $('placeQuery').value.trim();
		if (query.length < 2) {
			return;
		}
		$('placeStatus').className = 'status';
		$('placeStatus').textContent = 'Recherche…';
		send({ type: 'searchPlace', query });
		if (!host) {
			onPlaces({ places: [
				{ name: 'Lyon', detail: 'Auvergne-Rhône-Alpes, France', latitude: 45.75, longitude: 4.85 },
				{ name: 'Lyons', detail: 'Colorado, États-Unis', latitude: 40.22, longitude: -105.27 }
			] });
		}
	}

	function onPlaces(message) {
		const list = $('places');
		list.textContent = '';
		const places = message.places || [];
		const status = $('placeStatus');
		if (message.failed) {
			status.className = 'status bad';
			status.textContent = 'Recherche impossible : connexion internet ?';
		} else if (!places.length) {
			status.className = 'status warn';
			status.textContent = 'Aucune ville trouvée.';
		} else {
			status.textContent = '';
		}
		for (const place of places) {
			const button = document.createElement('button');
			button.type = 'button';
			button.className = 'place';
			button.innerHTML = '<span></span><small></small>';
			button.firstChild.textContent = place.name;
			button.lastChild.textContent = place.detail;
			button.addEventListener('click', () => {
				weatherPlace = { place: place.name, latitude: place.latitude, longitude: place.longitude };
				$('weatherEnabled').checked = true;
				[...list.children].forEach((b) => b.classList.toggle('selected', b === button));
				renderPlaceStatus();
			});
			list.append(button);
		}
	}

	function renderPlaceStatus() {
		const status = $('placeStatus');
		if (weatherPlace) {
			status.className = 'status';
			status.textContent = 'Ville choisie : ' + weatherPlace.place + '.';
		}
	}

	$('placeSearch').addEventListener('click', searchPlaces);
	$('placeQuery').addEventListener('keydown', (event) => {
		if (event.key === 'Enter') {
			searchPlaces();
		}
	});

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

	const DRIVE_PREFIX = 'drive:';
	// HWiNFO names a disk's sensors after its model: "S.M.A.R.T.: Samsung SSD 990 PRO 2TB (S73W...)"
	const DRIVE_TEMP_LABELS = [/(drive temperature|température du disque)$/i, /temp/i];
	const squeeze = text => text.toLowerCase().replace(/[^a-z0-9]+/g, '');

	/**
	 * Widget rows, then one row per physical disk: its drives (C: and D: on one SSD) share a single
	 * temperature. Drives whose disk is unknown get a row of their own. Disk temperatures are optional.
	 */
	function sensorRows() {
		const disks = [];
		for (const drive of (model.state && model.state.drives) || []) {
			const same = Number.isInteger(drive.disk) && disks.find(d => d.number === drive.disk);
			if (same) {
				same.names.push(drive.name);
			}
			else {
				disks.push({ number: drive.disk, model: drive.model, names: [drive.name] });
			}
		}
		return METRICS.concat(disks.map(disk => ({
			key: Number.isInteger(disk.number) ? 'disk:' + disk.number : DRIVE_PREFIX + disk.names[0],
			name: 'Disque ' + disk.names.join(' · '),
			detail: disk.model || '',
			unit: '°C',
			disk,
			optional: true
		})));
	}

	/** Entry the current settings point to for a row; for a disk, the first of its drives that has one. */
	function configuredEntry(metric) {
		const { entries, matches } = model.hwinfo;
		const keys = metric.disk ? metric.disk.names.map(name => DRIVE_PREFIX + name) : [metric.key];
		for (const key of keys) {
			const entry = entries[matches[key]];
			if (entry) {
				return entry;
			}
		}
		return undefined;
	}

	const DISK_SENSOR = /s\.m\.a\.r\.t|drive|disk|disque|nvme|ssd/i;

	// HWiNFO ends a disk sensor name with the drive letters it holds: "... (S7YF...) [C:, D:]"
	function holdsDrive(sensor, driveName) {
		const letters = /\[([A-Z]:(?:,\s*[A-Z]:)*)\]\s*$/i.exec(sensor);
		return letters ? letters[1].toUpperCase().split(/,\s*/).includes(driveName.toUpperCase()) : false;
	}

	/** Entries that make sense for a row: same unit, and disk sensors only for a drive. */
	function fits(metric, entry) {
		return unitOf(entry) === metric.unit
			&& (!metric.disk || DISK_SENSOR.test(entry.sensor))
			// GPU values only come from a GPU sensor; a CPU temperature may come from the motherboard
			&& (!metric.strict || metric.sensor.test(entry.sensor));
	}

	function suggest(metric, entries) {
		if (metric.disk) {
			const disk = metric.disk.model ? squeeze(metric.disk.model) : '';
			const candidates = entries.filter(e => fits(metric, e)
				&& (metric.disk.names.some(name => holdsDrive(e.sensor, name)) || (disk && squeeze(e.sensor).includes(disk))));
			for (const pattern of DRIVE_TEMP_LABELS) {
				const found = candidates.find(e => pattern.test(e.label));
				if (found) {
					return found;
				}
			}
			return null;
		}
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
		for (const metric of sensorRows()) {
			const current = model.sensors[metric.key] || (model.sensors[metric.key] = { choice: '', touched: false, origin: 'missing' });
			if (current.touched) {
				continue;
			}
			const configured = configuredEntry(metric);
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
		const rows = sensorRows();
		const entryKeys = entries.map(keyOf).join('\n') + '\n' + rows.map(r => r.key).join('\n');

		// Rebuilding the lists would close an open dropdown: only when the entries themselves change
		if (entryKeys !== renderedEntryKeys) {
			renderedEntryKeys = entryKeys;
			body.textContent = '';
			rows.forEach((metric, index) => {
				if (metric.disk && (index === 0 || !rows[index - 1].disk)) {
					const heading = body.insertRow();
					heading.className = 'group';
					const cell = heading.insertCell();
					cell.colSpan = 3;
					cell.textContent = 'Températures des disques';
				}
				const row = body.insertRow();
				row.dataset.key = metric.key;
				const name = row.insertCell();
				name.textContent = metric.name;
				if (metric.detail) {
					const detail = document.createElement('span');
					detail.className = 'detail';
					detail.textContent = metric.detail;
					name.append(detail);
				}
				const tag = document.createElement('span');
				tag.className = 'tag';
				name.append(tag);
				const select = document.createElement('select');
				select.append(new Option('— Aucune —', ''));
				const current = model.sensors[metric.key] && model.sensors[metric.key].choice;
				const groups = new Map();
				for (const entry of entries) {
					if (!fits(metric, entry) && keyOf(entry) !== current) {
						continue;
					}
					if (!groups.has(entry.sensor)) {
						groups.set(entry.sensor, []);
					}
					groups.get(entry.sensor).push(entry);
				}
				if (groups.size === 0) {
					select.options[0].text = '— Aucune valeur en ' + metric.unit + ' —';
				}
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
			});
		}

		const byKey = new Map(entries.map(e => [keyOf(e), e]));
		for (const row of body.rows) {
			if (!row.dataset.key) {
				continue;
			}
			const sensor = model.sensors[row.dataset.key];
			const select = row.cells[1].firstChild;
			if (select.value !== sensor.choice) {
				select.value = sensor.choice;
			}
			const entry = byKey.get(sensor.choice);
			row.cells[2].textContent = entry ? entry.text : '--';
			const tag = row.cells[0].querySelector('.tag');
			const labels = { configured: 'configuré', suggested: 'suggéré', missing: 'non trouvé', chosen: 'choisi' };
			const optional = rows.find(m => m.key === row.dataset.key).optional;
			tag.textContent = sensor.choice ? labels[sensor.origin] : (optional ? 'facultatif' : 'non attribué');
			tag.className = 'tag ' + (sensor.choice ? sensor.origin : (optional ? '' : 'missing'));
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
			const required = METRICS.filter(m => !m.optional);
			const assigned = required.filter(m => model.sensors[m.key] && model.sensors[m.key].choice).length;
			const fps = model.sensors.gpuFps && model.sensors.gpuFps.choice;
			add(assigned === required.length ? 'ok' : 'warn', 'Capteurs : <b>' + assigned + ' / ' + required.length + '</b> attribués'
				+ (fps ? ', FPS en jeu compris' : ', sans FPS en jeu'));
			const disks = sensorRows().filter(r => r.disk);
			if (disks.length) {
				const withTemp = disks.filter(r => model.sensors[r.key] && model.sensors[r.key].choice).length;
				add('ok', 'Disques : <b>' + withTemp + ' / ' + disks.length + '</b> avec leur température');
			}
		}
		else {
			add('warn', 'Capteurs : réglage actuel conservé');
		}
		const hardware = readHardware();
		const delay = $('gameAutoDelay');
		add('ok', !$('gameAutoGame').checked
			? 'Vue jeu : <b>seulement au balayage</b>, jamais automatique'
			: $('gameAutoNormal').checked
				? 'Vue jeu : automatique, retour à la vue normale <b>' + escapeHtml(delay.options[delay.selectedIndex].text) + '</b> après le passage du jeu en arrière-plan'
				: 'Vue jeu : automatique, <b>gardée</b> quand le jeu passe en arrière-plan');
		const transition = $('gameTransition');
		add('ok', 'Changement de vue : <b>' + escapeHtml(transition.options[transition.selectedIndex].text.split(' :')[0]) + '</b>');
		add('ok', 'Jeux ajoutés : <b>' + (extraGames.length ? extraGames.map(g => escapeHtml(g.name || g.exe)).join(', ') : 'aucun') + '</b>');
		const weatherOn = $('weatherEnabled').checked;
		add(!weatherOn || weatherPlace ? 'ok' : 'warn', weatherOn
			? (weatherPlace ? 'Météo : <b>' + escapeHtml(weatherPlace.place) + '</b>' : 'Météo : <b>aucune ville choisie</b>, rien ne s\'affichera')
			: 'Météo : <b>non affichée</b>');
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
		message.weather = { enabled: $('weatherEnabled').checked };
		message.gameView = {
			autoGame: $('gameAutoGame').checked,
			autoNormal: $('gameAutoNormal').checked,
			delaySec: Number($('gameAutoDelay').value),
			transition: $('gameTransition').value,
			extraGames
		};
		if (weatherPlace) {
			Object.assign(message.weather, weatherPlace);
		}
		if (model.caseScreen) {
			message.display = { width: model.caseScreen.width, height: model.caseScreen.height };
		}
		// Without HWiNFO entries nothing can be chosen: the current mapping is kept as is
		const { entries, matches } = model.hwinfo;
		if (entries.length) {
			const byKey = new Map(entries.map(e => [keyOf(e), e]));
			message.sensors = {};
			message.drives = {};
			for (const metric of sensorRows()) {
				const choice = model.sensors[metric.key].choice;
				// Unchanged rows are left out so the existing selector (wildcards included) stays;
				// a disk is unchanged only when every one of its drives already points to the choice
				const keys = metric.disk ? metric.disk.names.map(name => DRIVE_PREFIX + name) : [metric.key];
				if (choice && keys.every(key => entries[matches[key]] && keyOf(entries[matches[key]]) === choice)) {
					continue;
				}
				// FPS only exist in games, disk entries may be unchecked: an empty optional row keeps its selector
				if (metric.optional && !choice && !model.sensors[metric.key].touched) {
					continue;
				}
				const entry = byKey.get(choice);
				// PresentMon's sensor carries the foreground process name: saved without it
				const selector = entry ? { sensor: entry.sensor.replace(/\[[^\]]+\.exe\]$/i, '[*]'), label: entry.label } : null;
				if (metric.disk) {
					metric.disk.names.forEach(name => {
						message.drives[name] = selector;
					});
				}
				else {
					message.sensors[metric.key] = selector;
				}
			}
		}
		$('saveError').hidden = true;
		$('next').disabled = true;
		if (model.settingsMode) {
			showFootStatus('Enregistrement…');
		}
		send(message);
		if (!host) {
			console.log('save', message);
			dispatch({ type: 'saved' });
		}
	}

	// ---------- Game view ----------

	// The delay set by hand in appsettings.json may be none of the listed ones: it is added to the list
	function showGameView(gameView) {
		const select = $('gameAutoDelay');
		const seconds = String(gameView.autoNormalDelaySec);
		if (![...select.options].some(option => option.value === seconds)) {
			const option = new Option(seconds + ' secondes', seconds);
			const after = [...select.options].find(o => Number(o.value) > Number(seconds));
			select.add(option, after || null);
		}
		select.value = seconds;
		$('gameAutoGame').checked = gameView.autoGame !== false;
		$('gameAutoNormal').checked = Boolean(gameView.autoNormal);
		$('gameTransition').value = gameView.transition || 'morph';
		extraGames = (gameView.extraGames || []).map(game => ({ exe: game.exe, name: game.name || '' }));
		renderExtraGames();
		updateAutoControls();
	}

	// Games added by hand ({ exe, name }) and the applications open now, to pick one from
	let extraGames = [];
	let openProcesses = [];
	const sameExe = (a, b) => a.toLowerCase() === b.toLowerCase();

	function renderExtraGames() {
		const list = $('extraGames');
		list.textContent = '';
		extraGames.forEach((game, index) => {
			const row = document.createElement('div');
			row.className = 'game-row';
			row.innerHTML = '<span class="game-name"></span><span class="game-exe"></span><button type="button" class="link">Retirer</button>';
			row.querySelector('.game-name').textContent = game.name || game.exe;
			row.querySelector('.game-exe').textContent = game.exe;
			row.querySelector('button').addEventListener('click', () => {
				extraGames.splice(index, 1);
				renderExtraGames();
			});
			list.append(row);
		});
		renderProcessPick();
	}

	function renderProcessPick() {
		const select = $('processPick');
		const choices = openProcesses.filter(p => !extraGames.some(g => sameExe(g.exe, p.exe)));
		select.textContent = '';
		select.add(new Option(choices.length ? 'Choisir une application ouverte…' : 'Aucune autre application ouverte', ''));
		choices.forEach(p => select.add(new Option(p.name + ' · ' + p.exe, p.exe)));
	}

	$('processAdd').addEventListener('click', () => {
		const picked = openProcesses.find(p => sameExe(p.exe, $('processPick').value));
		if (picked) {
			extraGames.push({ exe: picked.exe, name: picked.name });
			renderExtraGames();
		}
	});
	$('processRefresh').addEventListener('click', () => send({ type: 'listProcesses' }));

	// The automatic return only means something when the game view comes by itself
	function updateAutoControls() {
		const auto = $('gameAutoGame').checked;
		$('gameAutoNormal').disabled = !auto;
		$('gameAutoDelay').disabled = !auto || !$('gameAutoNormal').checked;
	}
	$('gameAutoGame').addEventListener('change', updateAutoControls);
	$('gameAutoNormal').addEventListener('change', updateAutoControls);

	// ---------- Host messages ----------

	function onState(state) {
		if (Boolean(state.completed) !== model.settingsMode) {
			setMode(Boolean(state.completed));
		}
		if (state.gameView) {
			showGameView(state.gameView);
		}
		const weather = state.weather;
		if (weather) {
			$('weatherEnabled').checked = Boolean(weather.enabled);
			if (weather.latitude != null && weather.longitude != null) {
				weatherPlace = { place: weather.place, latitude: weather.latitude, longitude: weather.longitude };
				$('placeQuery').value = weather.place || '';
				renderPlaceStatus();
			}
		}
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
			case 'places':
				onPlaces(message);
				break;
			case 'hwinfoConfigured':
				onHwInfoConfigured(message);
				break;
			case 'saveFailed':
				$('saveError').hidden = false;
				$('next').disabled = false;
				if (model.settingsMode) {
					showFootStatus('Enregistrement impossible : voir le journal', true);
				}
				break;
			case 'processes':
				openProcesses = message.processes || [];
				renderProcessPick();
				break;
			case 'saved':
				$('next').disabled = false;
				showFootStatus('Réglages enregistrés et appliqués');
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
			type: 'processes',
			processes: [
				{ exe: 'Discord.exe', name: 'Discord' },
				{ exe: 'Hades2.exe', name: 'Hades II' },
				{ exe: 'Spotify.exe', name: 'Spotify Premium' }
			]
		});
		dispatch({
			type: 'state',
			// "?mode=settings" previews the settings window instead of the first-run wizard
			completed: new URLSearchParams(location.search).get('mode') === 'settings',
			gameView: { autoGame: new URLSearchParams(location.search).get('autoGame') !== 'off', autoNormal: true, autoNormalDelaySec: 20, transition: 'morph', detectEngines: true, extraGames: [{ exe: 'TFTClient-Win64-Shipping.exe', name: 'TFT' }] },
			caseSize: { width: 682, height: 2560 },
			screens: [
				{ number: 1, name: 'DISPLAY1', x: -682, y: 1440, width: 682, height: 2560, primary: false },
				{ number: 2, name: 'DISPLAY2', x: 0, y: 0, width: 3440, height: 1440, primary: true }
			],
			hardware: { cpuMaker: 'AMD', cpuModel: 'Ryzen 7 9800X3D', gpuMaker: 'NVIDIA', gpuModel: 'GeForce RTX 4090', vramTotalGb: 24 },
			detected: { cpuMaker: 'AMD', cpuModel: 'Ryzen 7 9800X3D', gpuMaker: 'NVIDIA', gpuModel: 'GeForce RTX 4090', vramTotalGb: 24 },
			hwInfoPath: 'C:\\Program Files\\HWiNFO64\\HWiNFO64.exe',
			drives: [
				{ name: 'C:', label: 'Windows', disk: 0, model: 'Samsung SSD 9100 PRO 2TB' },
				{ name: 'D:', label: 'Données', disk: 0, model: 'Samsung SSD 9100 PRO 2TB' },
				{ name: 'E:', label: 'Jeux', disk: 1, model: 'Samsung SSD 990 PRO 4TB' }
			]
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
				{ sensor: sensorGpu, label: 'Utilisation de la mémoire GPU', text: '11.2 %' },
				{ sensor: 'PresentMon [dwm.exe]', label: "Fréquence d'images Presented (avg)", text: '38.0 FPS' },
				{ sensor: 'PresentMon [dwm.exe]', label: "Fréquence d'images Displayed (avg)", text: '38.0 FPS' },
				{ sensor: 'S.M.A.R.T.: Samsung SSD 9100 PRO 2TB (S7YFNJ0Y123456Z) [C:, D:]', label: 'Température du disque', text: '53 °C' },
				{ sensor: 'S.M.A.R.T.: Samsung SSD 9100 PRO 2TB (S7YFNJ0Y123456Z) [C:, D:]', label: 'Température du disque 2', text: '61 °C' },
				{ sensor: 'S.M.A.R.T.: Samsung SSD 990 PRO 4TB (S7DPNU0X123456Y) [E:]', label: 'Température du disque', text: '40 °C' }
			],
			matches: { cpuTemp: 0, cpuPower: 1, gpuTemp: -1, gpuPower: -1, gpuLoad: 4, gpuVram: 5 }
		});
	}
})();
