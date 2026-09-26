(() => {
  const $ = (selector) => document.querySelector(selector);
  const state = { user: null, resources: [], selected: null, slots: [], skills: [], hub: null, authMode: 'login', toastTimer: null };
  const directoryState = { loaded: false, kind: 'users', page: 1, pageSize: 8, search: '', localUsers: [], localRooms: [], users: [], rooms: [], searchSequence: 0 };
  const authView = $('#auth-view');
  const appView = $('#app-view');
  const authForm = $('#auth-form');
  const authMessage = $('#auth-message');
  const dialog = $('#resource-dialog');
  let directorySearchTimer = null;
  let quickSearchTimer = null;
  let quickSearchSequence = 0;
  let directoryLoadPromise = null;
  let googleChartsPromise = null;
  let bookingActivityChart = null;
  let popularRoomsChart = null;
  let lastAdminAnalytics = null;
  let chartResizeTimer = null;
  let roomImagePreviewUrl = null;

  async function api(url, options = {}) {
    const response = await fetch(url, {
      credentials: 'same-origin',
      ...options,
      headers: { ...(options.body && !(options.body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}), ...options.headers }
    });
    if (response.status === 204) return null;
    const contentType = response.headers.get('content-type') || '';
    const body = contentType.includes('json') ? await response.json() : await response.text();
    if (!response.ok) {
      const error = new Error(body?.detail || body?.title || body?.message || `Request failed (${response.status})`);
      error.status = response.status;
      error.body = body;
      throw error;
    }
    return body;
  }

  function toast(message, isError = false) {
    const element = $('#app-message');
    element.textContent = message;
    element.classList.toggle('error', isError);
    element.classList.add('show');
    clearTimeout(state.toastTimer);
    state.toastTimer = setTimeout(() => element.classList.remove('show'), 3500);
  }

  function setAuthMode(mode) {
    state.authMode = mode;
    document.querySelectorAll('.auth-tab').forEach(tab => tab.classList.toggle('active', tab.dataset.authMode === mode));
    const isRegister = mode === 'register';
    $('#display-name-field').classList.toggle('hidden', !isRegister);
    $('#auth-title').textContent = isRegister ? 'Create your account' : 'Welcome back';
    $('#auth-subtitle').textContent = isRegister ? 'Get started with your meeting room workspace.' : 'Sign in to book a meeting room.';
    $('#auth-submit').innerHTML = isRegister ? 'Create account <span>→</span>' : 'Sign in <span>→</span>';
    authForm.elements.password.autocomplete = isRegister ? 'new-password' : 'current-password';
    authMessage.textContent = '';
  }

  async function submitAuth(event) {
    event.preventDefault();
    const values = Object.fromEntries(new FormData(authForm));
    const isRegister = state.authMode === 'register';
    const button = $('#auth-submit');
    button.disabled = true;
    authMessage.textContent = '';
    try {
      if (isRegister) await api('/api/auth/register', { method: 'POST', body: JSON.stringify({ email: values.email, password: values.password, displayName: values.displayName }) });
      await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ email: values.email, password: values.password }) });
      await loadWorkspace();
    } catch (error) {
      authMessage.textContent = error.body?.errors ? Object.values(error.body.errors).flat().join(' ') : error.message;
    } finally {
      button.disabled = false;
    }
  }

  async function loadWorkspace() {
    state.user = await api('/api/auth/me');
    authView.classList.add('hidden');
    appView.classList.remove('hidden');
    renderUserSummary();
    $('#logout-button').classList.remove('hidden');
    $('#quick-search-shell').classList.remove('hidden');
    const isAdmin = state.user.roles?.includes('Admin');
    $('#new-resource-button').classList.toggle('hidden', !isAdmin);
    $('#admin-bookings-panel').classList.toggle('hidden', !isAdmin);
    $('#skill-admin-actions').classList.toggle('hidden', !isAdmin);
    $('#admin-tab').classList.toggle('hidden', !isAdmin);
    await loadResources();
    if (isAdmin) await loadAllBookings();
    await Promise.all([loadSkills(), checkAiStatus()]);
    await connectHub();
  }

  async function checkAiStatus() {
    try { $('#ai-unavailable').classList.toggle('hidden', (await api('/api/ai/status')).available); }
    catch { $('#ai-unavailable').classList.remove('hidden'); }
  }

  function showView(viewId) {
    for (const id of ['booking-view', 'my-bookings-view', 'directory-view', 'profile-view', 'ai-view', 'skills-view', 'admin-view', 'help-view']) $(`#${id}`).classList.toggle('hidden', id !== viewId);
    document.querySelectorAll('[data-view]').forEach(tab => tab.classList.toggle('active', tab.dataset.view === viewId));
    if (viewId === 'skills-view') loadSkills();
    if (viewId === 'my-bookings-view') loadMyBookings();
    if (viewId === 'directory-view') loadDirectory();
    if (viewId === 'profile-view') renderProfile();
    if (viewId === 'admin-view') loadAdminOverview();
  }

  async function loadDirectory() {
    if (directoryState.loading) return;
    try {
      await ensureDirectoryData();
      await refreshDirectoryResults(directoryState.search);
    } catch (error) {
      const message = $('#directory-error'); message.textContent = error.message; message.classList.remove('hidden');
    }
  }

  async function ensureDirectoryData() {
    if (directoryState.loaded) return;
    if (!directoryLoadPromise) directoryLoadPromise = (async () => {
      const response = await fetch('/data/directory.json', { cache: 'no-cache' });
      if (!response.ok) throw new Error(`Could not load directory data (${response.status}).`);
      const data = await response.json();
      if (!Array.isArray(data.users) || !Array.isArray(data.rooms)) throw new Error('Directory data has an invalid format.');
      directoryState.localUsers = data.users; directoryState.localRooms = data.rooms; directoryState.loaded = true;
      $('#people-count').textContent = data.users.length;
      $('#rooms-count').textContent = data.rooms.length;
    })();
    try { await directoryLoadPromise; }
    finally { if (!directoryState.loaded) directoryLoadPromise = null; }
  }

  function mergeDirectoryItems(kind, databaseItems, localItems) {
    const keyOf = item => String(kind === 'users' ? item.email : item.name || item.Name || '').trim().toLocaleLowerCase();
    const merged = new Map();
    for (const item of localItems) merged.set(keyOf(item), { ...item, source: 'file' });
    for (const item of databaseItems) {
      const key = keyOf(item);
      const local = merged.get(key) || {};
      merged.set(key, kind === 'users'
        ? { ...local, id: item.id, name: item.displayName, email: item.email, role: local.role || 'Workspace member', team: local.team || 'Workspace', location: local.location || '', avatar: local.avatar || null, source: 'database' }
        : { ...local, id: item.id, name: item.name, description: local.description || item.description, image: local.image || null, isActive: item.isActive, source: 'database' });
    }
    return [...merged.values()];
  }

  async function searchDirectorySources(query) {
    await ensureDirectoryData();
    const database = await api('/api/directory/search', { method: 'POST', body: JSON.stringify({ query }) });
    const users = mergeDirectoryItems('users', database.users, directoryState.localUsers);
    const rooms = mergeDirectoryItems('rooms', database.rooms, directoryState.localRooms);
    const term = query.trim().toLocaleLowerCase();
    const matches = items => items.filter(item => !term || Object.values(item).flat().join(' ').toLocaleLowerCase().includes(term));
    return { users: matches(users), rooms: matches(rooms) };
  }

  async function refreshDirectoryResults(query) {
    const sequence = ++directoryState.searchSequence;
    const result = await searchDirectorySources(query);
    if (sequence !== directoryState.searchSequence) return;
    directoryState.users = result.users; directoryState.rooms = result.rooms;
    $('#people-count').textContent = result.users.length;
    $('#rooms-count').textContent = result.rooms.length;
    renderDirectory();
  }

  function renderDirectory() {
    const filtered = directoryState.kind === 'users' ? directoryState.users : directoryState.rooms;
    const pageCount = Math.max(1, Math.ceil(filtered.length / directoryState.pageSize));
    directoryState.page = Math.min(directoryState.page, pageCount);
    const start = (directoryState.page - 1) * directoryState.pageSize;
    const grid = $('#directory-grid'); grid.replaceChildren();
    for (const item of filtered.slice(start, start + directoryState.pageSize)) {
      const card = document.createElement('article'); card.className = directoryState.kind === 'users' ? 'directory-card person-card' : 'directory-card room-directory-card';
      const image = document.createElement('img'); image.className = directoryState.kind === 'users' ? 'directory-avatar' : 'directory-room-image';
      image.loading = 'lazy'; image.decoding = 'async'; image.alt = directoryState.kind === 'users' ? `Profile photo of ${item.name}` : item.name;
      const placeholder = directoryState.kind === 'users' ? '/images/placeholders/avatar.svg' : '/images/placeholders/room.svg';
      image.src = item.avatar || item.image || placeholder;
      image.addEventListener('error', () => { if (image.src !== new URL(placeholder, location.href).href) image.src = placeholder; }, { once: true });
      card.append(image);
      const body = document.createElement('div'); body.className = 'directory-card-body';
      const name = document.createElement('h2'); name.textContent = item.name; body.append(name);
      if (directoryState.kind === 'users') {
        const role = document.createElement('p'); role.className = 'directory-subtitle'; role.textContent = item.role; body.append(role);
        const meta = document.createElement('p'); meta.className = 'directory-meta'; meta.textContent = `${item.team} · ${item.location}`; body.append(meta);
        const email = document.createElement('a'); email.className = 'directory-email'; email.href = `mailto:${item.email}`; email.textContent = item.email; body.append(email);
      } else {
        const description = document.createElement('p'); description.className = 'directory-description'; description.textContent = item.description; body.append(description);
        const details = document.createElement('p'); details.className = 'directory-meta'; details.textContent = `${item.capacity} people · ${item.location}`; body.append(details);
        const features = document.createElement('div'); features.className = 'directory-features';
        for (const feature of item.features || []) { const tag = document.createElement('span'); tag.textContent = feature; features.append(tag); }
        body.append(features);
      }
      card.append(body); grid.append(card);
    }
    $('#directory-empty').classList.toggle('hidden', filtered.length > 0);
    renderDirectoryPagination(pageCount, filtered.length);
  }

  function renderDirectoryPagination(pageCount, total) {
    const nav = $('#directory-pagination'); nav.replaceChildren();
    if (!total) return;
    const addButton = (label, page, disabled = false, current = false) => {
      const button = document.createElement('button'); button.type = 'button'; button.className = 'page-button'; button.textContent = label;
      button.disabled = disabled; if (current) button.setAttribute('aria-current', 'page');
      button.addEventListener('click', () => { directoryState.page = page; renderDirectory(); }); nav.append(button);
    };
    addButton('Previous', directoryState.page - 1, directoryState.page === 1);
    for (let page = 1; page <= pageCount; page++) addButton(String(page), page, false, page === directoryState.page);
    addButton('Next', directoryState.page + 1, directoryState.page === pageCount);
    const summary = document.createElement('span'); summary.className = 'pagination-summary';
    const first = (directoryState.page - 1) * directoryState.pageSize + 1;
    summary.textContent = `${first}–${Math.min(first + directoryState.pageSize - 1, total)} of ${total}`; nav.append(summary);
  }

  async function runQuickSearch(query) {
    const sequence = ++quickSearchSequence;
    const menu = $('#quick-search-results');
    if (query.trim().length < 2) { menu.classList.add('hidden'); menu.replaceChildren(); return; }
    menu.classList.remove('hidden'); menu.textContent = 'Searching…';
    try {
      const results = await searchDirectorySources(query);
      if (sequence !== quickSearchSequence) return;
      menu.replaceChildren();
      for (const [kind, label, items] of [['users', 'People', results.users], ['rooms', 'Rooms', results.rooms]]) {
        for (const item of items.slice(0, 4)) {
          const button = document.createElement('button'); button.type = 'button'; button.className = 'quick-search-result'; button.setAttribute('role', 'option');
          const title = document.createElement('strong'); title.textContent = item.name;
          const detail = document.createElement('span'); detail.textContent = kind === 'users' ? `${item.role || item.team} · ${item.email}` : `${item.location ? `${item.location} · ` : ''}${item.description}`;
          const badge = document.createElement('small'); badge.textContent = label;
          button.append(title, detail, badge);
          button.addEventListener('click', () => {
            directoryState.kind = kind; directoryState.page = 1; directoryState.search = item.name;
            $('#directory-search').value = item.name;
            document.querySelectorAll('[data-directory-kind]').forEach(tab => {
              const active = tab.dataset.directoryKind === kind;
              tab.classList.toggle('active', active); tab.setAttribute('aria-selected', String(active));
            });
            menu.classList.add('hidden'); $('#quick-search').value = ''; showView('directory-view');
          });
          menu.append(button);
        }
      }
      if (!menu.childElementCount) { menu.textContent = 'No people or rooms found.'; return; }
    } catch (error) { if (sequence === quickSearchSequence) menu.textContent = error.message; }
  }

  function renderProfile() {
    if (!state.user) return;
    const form = $('#profile-form');
    form.elements.displayName.value = state.user.displayName || '';
    form.elements.email.value = state.user.email || '';
    form.elements.phoneNumber.value = state.user.phoneNumber || '';
    $('#profile-display-name').textContent = state.user.displayName || state.user.email || 'User';
    const image = $('#profile-avatar');
    const fallback = $('#profile-avatar-fallback');
    image.classList.toggle('hidden', !state.user.avatarUrl);
    fallback.classList.toggle('hidden', Boolean(state.user.avatarUrl));
    $('#avatar-remove').classList.toggle('hidden', !state.user.avatarUrl);
    if (state.user.avatarUrl) image.src = `${state.user.avatarUrl}?v=${Date.now()}`;
    fallback.textContent = (state.user.displayName || state.user.email || 'R').trim().charAt(0).toUpperCase();
    renderUserSummary();
  }

  function renderUserSummary() {
    if (!state.user) return;
    const name = state.user.displayName || state.user.email || 'User';
    $('#user-summary').classList.remove('hidden');
    $('#user-label').textContent = name;
    const image = $('#header-avatar');
    const fallback = $('#header-avatar-fallback');
    fallback.textContent = name.trim().charAt(0).toUpperCase();
    image.onerror = () => { image.classList.add('hidden'); fallback.classList.remove('hidden'); };
    if (state.user.avatarUrl) {
      image.src = `${state.user.avatarUrl}?v=${Date.now()}`;
      image.classList.remove('hidden');
      fallback.classList.add('hidden');
    } else {
      image.classList.add('hidden');
      fallback.classList.remove('hidden');
    }
  }

  async function loadAdminOverview() {
    try {
      const info = await api('/api/admin/overview');
      const metrics = [['Rooms', info.resources], ['Time slots', info.timeSlots], ['Bookings', info.bookings], ['Users', info.users], ['AI skills', info.aiSkills]];
      const container = $('#admin-metrics'); container.replaceChildren();
      for (const [label, value] of metrics) {
        const card = document.createElement('article'); card.className = 'panel admin-metric';
        const title = document.createElement('span'); title.textContent = label;
        const count = document.createElement('strong'); count.textContent = value;
        card.append(title, count); container.append(card);
      }
      try {
        lastAdminAnalytics = await api('/api/admin/analytics');
        await renderAdminAnalytics(lastAdminAnalytics);
      } catch (error) {
        $('#booking-activity-chart').textContent = 'Analytics are temporarily unavailable.';
        $('#popular-rooms-chart').textContent = error.message || 'Analytics are temporarily unavailable.';
      }
      const integrations = $('#admin-integration-list'); integrations.replaceChildren();
      for (const [name, status] of [['Database', info.database], ['AI provider', info.aiConfigured ? 'Groq configured' : 'Not configured'], ['Realtime', info.signalR]]) {
        const row = document.createElement('div'); row.className = 'integration-row';
        const title = document.createElement('strong'); title.textContent = name;
        const value = document.createElement('span'); value.textContent = status;
        row.append(title, value); integrations.append(row);
      }
    } catch (error) { toast(error.message, true); }
  }

  async function renderAdminAnalytics(analytics) {
    await ensureGoogleCharts();
    const chartDuration = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 850;
    $('#analytics-bookings-total').textContent = analytics.bookingsLast14Days;
    $('#upcoming-utilization').textContent = `${analytics.upcomingUtilizationPercent}%`;
    $('#upcoming-utilization-detail').textContent = analytics.upcomingSlots
      ? `${analytics.bookedUpcomingSlots} of ${analytics.upcomingSlots} bookable slots reserved`
      : 'No bookable slots are scheduled for the next 30 days.';

    const activityData = new google.visualization.DataTable();
    activityData.addColumn('string', 'Date');
    activityData.addColumn('number', 'Bookings');
    activityData.addRows(analytics.dailyBookings.map(day => [day.date.slice(5), day.count]));
    const activityElement = $('#booking-activity-chart');
    const firstActivityDraw = bookingActivityChart === null;
    bookingActivityChart ||= new google.visualization.ColumnChart(activityElement);
    bookingActivityChart.draw(activityData, {
      width: Math.max(280, activityElement.clientWidth), height: 270,
      backgroundColor: 'transparent', colors: ['#5276e8'],
      legend: { position: 'none' },
      chartArea: { left: 42, top: 18, width: '88%', height: '72%' },
      hAxis: { textStyle: { color: '#77849a', fontSize: 10 }, slantedText: false },
      vAxis: { minValue: 0, format: '0', gridlines: { color: '#edf0f5' }, textStyle: { color: '#77849a', fontSize: 10 } },
      tooltip: { isHtml: false },
      animation: { startup: firstActivityDraw, duration: chartDuration, easing: 'out' }
    });

    const popularElement = $('#popular-rooms-chart');
    if (!analytics.mostBookedRooms.length) {
      popularElement.textContent = 'No bookings in the last 14 days.';
      popularRoomsChart = null;
      return;
    }
    popularElement.textContent = '';
    const roomData = new google.visualization.DataTable();
    roomData.addColumn('string', 'Room');
    roomData.addColumn('number', 'Bookings');
    roomData.addRows(analytics.mostBookedRooms.map(room => [room.room, room.count]));
    const firstRoomDraw = popularRoomsChart === null;
    popularRoomsChart ||= new google.visualization.BarChart(popularElement);
    popularRoomsChart.draw(roomData, {
      width: Math.max(280, popularElement.clientWidth), height: Math.max(190, analytics.mostBookedRooms.length * 44),
      backgroundColor: 'transparent', colors: ['#44a987'],
      legend: { position: 'none' },
      chartArea: { left: 105, top: 10, width: '76%', height: '82%' },
      hAxis: { minValue: 0, format: '0', gridlines: { color: '#edf0f5' }, textStyle: { color: '#77849a', fontSize: 10 } },
      vAxis: { textStyle: { color: '#596780', fontSize: 11 } },
      animation: { startup: firstRoomDraw, duration: chartDuration, easing: 'out' }
    });
  }

  function ensureGoogleCharts() {
    if (window.google?.visualization?.ColumnChart) return Promise.resolve();
    if (googleChartsPromise) return googleChartsPromise;
    googleChartsPromise = new Promise((resolve, reject) => {
      if (!window.google?.charts) { reject(new Error('Google Charts could not be loaded.')); return; }
      google.charts.load('current', { packages: ['corechart'] });
      google.charts.setOnLoadCallback(resolve);
    });
    return googleChartsPromise;
  }

  async function loadMyBookings() {
    try {
      const bookings = await api('/api/bookings/me');
      const list = $('#my-bookings-list'); list.replaceChildren();
      $('#my-bookings-empty').classList.toggle('hidden', bookings.length > 0);
      for (const booking of bookings) {
        const row = document.createElement('div'); row.className = 'booking-row';
        row.innerHTML = `<span>${escapeHtml(booking.resourceName)}${booking.isAiGenerated ? ' <span class="ai-booking-badge">Booked by AI</span>' : ''}</span><span>${new Date(booking.slotStartUtc).toLocaleString()} – ${new Date(booking.slotEndUtc).toLocaleTimeString()}</span><span>Booked ${new Date(booking.createdAtUtc).toLocaleDateString()}</span><button type="button" class="button button-quiet cancel-booking" ${new Date(booking.slotStartUtc) <= new Date() ? 'disabled title="Past bookings cannot be cancelled"' : ''}>Cancel booking</button>`;
        const cancel = row.querySelector('.cancel-booking');
        cancel.addEventListener('click', async () => {
          if (!confirm(`Cancel your booking for ${booking.resourceName}?`)) return;
          cancel.disabled = true;
          try { await api(`/api/bookings/${booking.id}`, { method: 'DELETE' }); toast('Booking cancelled.'); await Promise.all([loadMyBookings(), loadSchedule()]); }
          catch (error) { cancel.disabled = false; toast(error.message, true); }
        });
        list.append(row);
      }
    } catch (error) { toast(error.message, true); }
  }

  async function loadSkills() {
    try {
      state.skills = await api('/api/ai/skills');
      $('#skill-empty').classList.toggle('hidden', state.skills.length > 0);
      const list = $('#skill-list'); list.replaceChildren();
      for (const skill of state.skills) {
        const row = document.createElement('article'); row.className = 'skill-row';
        const instructions = skill.instructions.map(item => `<li>${escapeHtml(item)}</li>`).join('');
        row.innerHTML = `<div class="skill-info"><div class="skill-title"><strong>${escapeHtml(skill.name)}</strong><span class="skill-status ${skill.isActive ? 'active' : 'draft'}">${skill.isActive ? 'Active' : 'Draft'}</span></div><p>${escapeHtml(skill.description)}</p><small>Version ${skill.version} · ${skill.instructions.length} instruction(s)</small><details><summary>View instructions</summary><ul>${instructions}</ul></details></div>`;
        if (state.user?.roles?.includes('Admin')) {
          const actions = document.createElement('div'); actions.className = 'skill-actions';
          actions.innerHTML = `<button type="button" class="button button-quiet" data-action="edit">Edit</button><button type="button" class="button button-quiet" data-action="toggle">${skill.isActive ? 'Deactivate' : 'Activate'}</button><button type="button" class="button button-quiet danger-text" data-action="delete">Delete</button>`;
          actions.querySelector('[data-action="edit"]').addEventListener('click', () => editSkill(skill));
          actions.querySelector('[data-action="toggle"]').addEventListener('click', async () => {
            try { await api(`/api/ai/skills/${skill.id}/${skill.isActive ? 'deactivate' : 'activate'}`, { method: 'POST' }); await loadSkills(); }
            catch (error) { toast(error.message, true); }
          });
          actions.querySelector('[data-action="delete"]').addEventListener('click', async () => {
            if (!confirm(`Delete skill “${skill.name}”?`)) return;
            try { await api(`/api/ai/skills/${skill.id}`, { method: 'DELETE' }); await loadSkills(); }
            catch (error) { toast(error.message, true); }
          });
          row.append(actions);
        }
        list.append(row);
      }
    } catch (error) { toast(error.message, true); }
  }

  function editSkill(skill = null) {
    const form = $('#skill-form'); form.reset(); form.classList.remove('hidden');
    form.elements.id.value = skill?.id || '';
    form.elements.name.value = skill?.name || '';
    form.elements.description.value = skill?.description || '';
    form.elements.instructions.value = skill?.instructions.join('\n') || '';
    form.elements.examples.value = skill?.examples.join('\n') || '';
    $('#skill-form-title').textContent = skill ? 'Edit skill' : 'Review skill draft';
    form.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  async function askAi(message) {
    $('#ai-message').value = message;
    $('#ai-answer').classList.remove('hidden'); $('#ai-answer').textContent = 'Thinking…';
    try {
      const result = await api('/api/ai/chat', { method: 'POST', body: JSON.stringify({ message, timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC' }) });
      const answer = $('#ai-answer');
      answer.replaceChildren(document.createTextNode(result.answer));
      if (result.bookingProposal) {
        const proposal = result.bookingProposal;
        const card = document.createElement('div'); card.className = 'ai-booking-proposal';
        const details = document.createElement('p'); details.textContent = `${proposal.resourceName} · ${new Date(proposal.startUtc).toLocaleString()} – ${new Date(proposal.endUtc).toLocaleTimeString()}`;
        const confirm = document.createElement('button'); confirm.type = 'button'; confirm.className = 'button button-primary'; confirm.textContent = 'Confirm booking';
        confirm.addEventListener('click', async () => {
          confirm.disabled = true;
          try {
            await api('/api/bookings', { method: 'POST', body: JSON.stringify({ timeSlotId: proposal.timeSlotId }) });
            confirm.textContent = 'Booking confirmed';
            toast('Room booked successfully.');
            await Promise.all([loadSchedule(), loadMyBookings(), state.user?.roles?.includes('Admin') ? loadAllBookings() : Promise.resolve()]);
          } catch (error) {
            confirm.disabled = false;
            toast(error.status === 409 ? 'That slot was just booked by someone else. Choose another slot.' : error.message, true);
            if (error.status === 409) await loadSchedule();
          }
        });
        card.append(details, confirm); answer.append(document.createElement('br'), card);
      }
      if (result.bookingReceipt) {
        const receipt = result.bookingReceipt;
        toast(`Booked by AI: ${receipt.resourceName}, ${new Date(receipt.startUtc).toLocaleString()}.`);
        await Promise.all([loadSchedule(), loadMyBookings(), state.user?.roles?.includes('Admin') ? loadAllBookings() : Promise.resolve()]);
      }
      $('#ai-unavailable').classList.add('hidden');
    } catch (error) {
      $('#ai-answer').textContent = error.message;
      if (error.status === 503) $('#ai-unavailable').classList.remove('hidden');
    }
  }

  async function loadResources() {
    state.resources = await api('/api/resources');
    $('#resource-count').textContent = state.resources.length;
    $('#resource-empty').classList.toggle('hidden', state.resources.length > 0);
    renderResources();
    if (state.selected && state.resources.some(item => item.id === state.selected.id)) {
      state.selected = state.resources.find(item => item.id === state.selected.id);
      await selectResource(state.selected, false);
    } else if (state.resources.length) {
      await selectResource(state.resources[0]);
    } else {
      state.selected = null;
      $('#schedule-content').classList.add('hidden');
      $('#schedule-placeholder').classList.remove('hidden');
    }
  }

  function renderResources() {
    const list = $('#resource-list');
    list.replaceChildren();
    const isAdmin = state.user?.roles?.includes('Admin');
    for (const resource of state.resources) {
      const card = document.createElement('div');
      card.className = `resource-card${state.selected?.id === resource.id ? ' selected' : ''}`;
      card.tabIndex = 0;
      card.setAttribute('role', 'button');
      card.innerHTML = `<img class="resource-thumbnail" alt="" loading="lazy" decoding="async"><span class="room-text"><span class="room-name">${escapeHtml(resource.name)}${resource.isActive ? '' : ' · Inactive'}</span><span class="room-description">${escapeHtml(resource.description || 'Meeting room')}</span></span>`;
      setImageWithFallback(card.querySelector('.resource-thumbnail'), resource.imageUrl);
      const pick = () => selectResource(resource);
      card.addEventListener('click', pick);
      card.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); pick(); } });
      if (isAdmin) {
        const actions = document.createElement('span');
        actions.className = 'resource-actions';
        actions.innerHTML = '<button type="button" title="Edit room" aria-label="Edit room">Edit</button><button type="button" title="Add availability" aria-label="Add availability">＋</button><button type="button" title="Delete room" aria-label="Delete room">×</button>';
        const [editButton, slotsButton, deleteButton] = actions.querySelectorAll('button');
        editButton.addEventListener('click', event => { event.stopPropagation(); openResourceDialog(resource); });
        slotsButton.addEventListener('click', async event => { event.stopPropagation(); await selectResource(resource); $('#slots-dialog').showModal(); });
        deleteButton.addEventListener('click', async event => {
          event.stopPropagation();
          if (!confirm(`Delete “${resource.name}”?`)) return;
          try { await api(`/api/resources/${resource.id}`, { method: 'DELETE' }); toast('Room deleted.'); await loadResources(); }
          catch (error) { toast(error.message, true); }
        });
        card.append(actions);
      }
      list.append(card);
    }
  }

  async function selectResource(resource, updateGroup = true) {
    if (!resource) return;
    if (updateGroup && state.hub?.state === signalR.HubConnectionState.Connected && state.selected && state.selected.id !== resource.id) {
      try { await state.hub.invoke('LeaveResource', state.selected.id); } catch { /* reconnect will rejoin the active room */ }
    }
    state.selected = resource;
    renderResources();
    $('#schedule-placeholder').classList.add('hidden');
    $('#schedule-content').classList.remove('hidden');
    $('#selected-room-name').textContent = resource.name;
    $('#selected-room-description').textContent = resource.description || 'Meeting room';
    setImageWithFallback($('#selected-room-image'), resource.imageUrl);
    if (updateGroup && state.hub?.state === signalR.HubConnectionState.Connected) {
      try { await state.hub.invoke('JoinResource', resource.id); } catch { setConnection(false); }
    }
    await loadSchedule();
  }

  async function loadSchedule() {
    if (!state.selected) return;
    const date = $('#schedule-date').value;
    try {
      state.slots = await api(`/api/resources/${state.selected.id}/schedule?date=${encodeURIComponent(date)}`);
      renderSlots();
    } catch (error) { toast(error.message, true); }
  }

  function renderSlots() {
    const list = $('#slot-list');
    list.replaceChildren();
    $('#slot-empty').classList.toggle('hidden', state.slots.length > 0);
    const isAdmin = state.user?.roles?.includes('Admin');
    for (const slot of state.slots) {
      const row = document.createElement('div');
      row.className = 'slot-row';
      const start = new Date(slot.startUtc);
      const end = new Date(slot.endUtc);
      const dateText = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric' }).format(start);
      const timeText = `${formatTime(start)} – ${formatTime(end)}`;
      row.innerHTML = `<div class="slot-time">${timeText}<span class="slot-date">${dateText}</span></div><div class="slot-state${slot.isBooked ? ' booked' : ''}"><i></i>${slot.isBooked ? 'Booked' : 'Available'}</div>${slot.isBooked ? `<span class="slot-booking">${escapeHtml(slot.booking?.userEmail || 'Reserved')}</span>` : `<button class="button button-primary button-book" ${!state.selected.isActive ? 'disabled title="This room is inactive"' : ''}>Book slot</button>`}`;
      const book = row.querySelector('.button-book');
      if (book) book.addEventListener('click', () => bookSlot(slot));
      if (isAdmin) {
        const label = row.querySelector('.slot-state');
        label.title = 'Manage slots through the room controls';
      }
      list.append(row);
    }
  }

  async function bookSlot(slot) {
    try {
      await api('/api/bookings', { method: 'POST', body: JSON.stringify({ timeSlotId: slot.id }) });
      toast('Room booked. Everyone viewing this schedule has been updated.');
      await Promise.all([loadSchedule(), state.user?.roles?.includes('Admin') ? loadAllBookings() : Promise.resolve()]);
    } catch (error) {
      if (error.status === 409) {
        toast('That slot was just booked by someone else. The schedule has been refreshed.', true);
        await loadSchedule();
      } else toast(error.message, true);
    }
  }

  async function connectHub() {
    if (!window.signalR) {
      setConnection(false);
      toast('Live updates library could not be loaded. Refresh to try again.', true);
      return;
    }
    if (state.hub) return;
    state.hub = new signalR.HubConnectionBuilder().withUrl('/hubs/schedule').withAutomaticReconnect().build();
    state.hub.on('SlotBooked', event => {
      if (state.selected?.id === event.resourceId) {
        const slot = state.slots.find(item => item.id === event.slotId);
        if (slot) { slot.isBooked = true; slot.booking = { bookingId: event.bookingId, bookedAtUtc: event.bookedAtUtc }; renderSlots(); }
        else loadSchedule();
        toast(`A time slot was booked in ${state.selected.name}.`);
      }
      if (state.user?.roles?.includes('Admin')) loadAllBookings();
    });
    state.hub.on('SlotCancelled', event => {
      if (state.selected?.id === event.resourceId) loadSchedule();
      if (state.user?.roles?.includes('Admin')) loadAllBookings();
    });
    state.hub.onreconnecting(() => setConnection(false));
    state.hub.onreconnected(async () => {
      setConnection(true);
      if (state.selected) await state.hub.invoke('JoinResource', state.selected.id);
      await loadSchedule();
    });
    state.hub.onclose(() => setConnection(false));
    try {
      await state.hub.start();
      setConnection(true);
      if (state.selected) await state.hub.invoke('JoinResource', state.selected.id);
    } catch { setConnection(false); }
  }

  function setConnection(connected) {
    const element = $('#connection-state');
    element.classList.toggle('offline', !connected);
    element.lastChild.textContent = connected ? ' Live updates' : ' Reconnecting';
  }

  async function loadAllBookings() {
    try {
      const bookings = await api('/api/admin/bookings');
      $('#booking-count').textContent = bookings.length;
      const list = $('#admin-bookings-list');
      list.replaceChildren();
      const header = document.createElement('div');
      header.className = 'booking-row header';
      header.innerHTML = '<span>Room</span><span>Booked by</span><span>Time slot</span><span>Booked at</span>';
      list.append(header);
      for (const booking of bookings) {
        const row = document.createElement('div');
        row.className = 'booking-row';
        row.innerHTML = `<span>${escapeHtml(booking.resourceName)}${booking.isAiGenerated ? ' <span class="ai-booking-badge">Booked by AI</span>' : ''}</span><span>${escapeHtml(booking.userEmail)}</span><span>${new Date(booking.slotStartUtc).toLocaleString()}</span><span>${new Date(booking.createdAtUtc).toLocaleString()}</span>`;
        list.append(row);
      }
      if (!bookings.length) {
        const empty = document.createElement('p'); empty.className = 'muted'; empty.textContent = 'No bookings have been made yet.'; list.append(empty);
      }
    } catch (error) { toast(error.message, true); }
  }

  function openResourceDialog(resource = null) {
    const form = $('#resource-form');
    form.reset();
    if (roomImagePreviewUrl) URL.revokeObjectURL(roomImagePreviewUrl);
    roomImagePreviewUrl = null;
    $('#resource-dialog-title').textContent = resource ? 'Edit room' : 'Add a room';
    $('#resource-active-field').classList.toggle('hidden', !resource);
    form.elements.name.value = resource?.name || '';
    form.elements.description.value = resource?.description || '';
    form.elements.isActive.checked = resource?.isActive ?? true;
    form.dataset.resourceId = resource?.id || '';
    setImageWithFallback($('#resource-image-preview'), resource?.imageUrl);
    dialog.showModal();
  }

  function setImageWithFallback(image, imageUrl) {
    image.onerror = () => {
      image.onerror = null;
      image.src = '/images/rooms/no_image_rooms.png';
    };
    image.src = imageUrl || '/images/rooms/no_image_rooms.png';
  }

  function formatTime(date) { return new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' }).format(date); }
  function escapeHtml(value) { return String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]); }

  document.querySelectorAll('[data-auth-mode]').forEach(tab => tab.addEventListener('click', () => setAuthMode(tab.dataset.authMode)));
  document.querySelectorAll('[data-view]').forEach(tab => tab.addEventListener('click', () => showView(tab.dataset.view)));
  document.querySelectorAll('[data-directory-kind]').forEach(tab => tab.addEventListener('click', () => {
    directoryState.kind = tab.dataset.directoryKind; directoryState.page = 1;
    document.querySelectorAll('[data-directory-kind]').forEach(item => {
      const active = item.dataset.directoryKind === directoryState.kind;
      item.classList.toggle('active', active); item.setAttribute('aria-selected', String(active));
    });
    renderDirectory();
  }));
  $('#directory-search').addEventListener('input', event => {
    directoryState.search = event.target.value; directoryState.page = 1;
    const term = directoryState.search.trim().toLocaleLowerCase();
    directoryState.users = mergeDirectoryItems('users', [], directoryState.localUsers).filter(item => !term || Object.values(item).flat().join(' ').toLocaleLowerCase().includes(term));
    directoryState.rooms = mergeDirectoryItems('rooms', [], directoryState.localRooms).filter(item => !term || Object.values(item).flat().join(' ').toLocaleLowerCase().includes(term));
    renderDirectory(); clearTimeout(directorySearchTimer);
    directorySearchTimer = setTimeout(() => refreshDirectoryResults(directoryState.search).catch(error => { const message = $('#directory-error'); message.textContent = error.message; message.classList.remove('hidden'); }), 220);
  });
  $('#quick-search').addEventListener('input', event => {
    const value = event.target.value; clearTimeout(quickSearchTimer);
    if (value.trim().length < 2) { ++quickSearchSequence; $('#quick-search-results').classList.add('hidden'); return; }
    quickSearchTimer = setTimeout(() => runQuickSearch(value), 220);
  });
  $('#quick-search').addEventListener('keydown', event => { if (event.key === 'Escape') $('#quick-search-results').classList.add('hidden'); });
  document.addEventListener('click', event => { if (!event.target.closest('#quick-search-shell')) $('#quick-search-results').classList.add('hidden'); });
  document.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLocaleLowerCase() === 'k' && !$('#quick-search-shell').classList.contains('hidden')) {
      event.preventDefault(); $('#quick-search').focus(); $('#quick-search').select();
    }
  });
  document.querySelectorAll('.suggestion').forEach(button => button.addEventListener('click', () => askAi(button.textContent)));
  $('#ai-chat-form').addEventListener('submit', async event => { event.preventDefault(); await askAi($('#ai-message').value.trim()); });
  $('#skill-new').addEventListener('click', () => editSkill());
  $('#skill-cancel').addEventListener('click', () => $('#skill-form').classList.add('hidden'));
  $('#skill-generate').addEventListener('click', async () => {
    const description = window.prompt('Describe what this reusable assistant skill should help with:');
    if (!description?.trim()) return;
    try {
      const draft = await api('/api/ai/skills/generate', { method: 'POST', body: JSON.stringify({ description }) });
      editSkill(draft);
      $('#skill-form').classList.remove('hidden');
      toast('AI draft generated. Review it and save it before activation.');
    } catch (error) { toast(error.message, true); }
  });
  $('#skill-upload').addEventListener('change', async event => {
    const file = event.target.files[0]; if (!file) return;
    const formData = new FormData(); formData.append('file', file);
    try {
      const response = await fetch('/api/ai/skills/upload', { method: 'POST', credentials: 'same-origin', body: formData });
      const result = await response.json();
      if (!response.ok) throw new Error(result.detail || result.title || 'Upload failed');
      editSkill(result); toast('Upload validated as an inactive draft. Review before saving.');
    } catch (error) { toast(error.message, true); }
    finally { event.target.value = ''; }
  });
  $('#skill-form').addEventListener('submit', async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const payload = { name: form.elements.name.value, description: form.elements.description.value, instructions: form.elements.instructions.value.split('\n').map(x => x.trim()).filter(Boolean), examples: form.elements.examples.value.split('\n').map(x => x.trim()).filter(Boolean) };
    try {
      const id = form.elements.id.value;
      await api(id ? `/api/ai/skills/${id}` : '/api/ai/skills', { method: id ? 'PUT' : 'POST', body: JSON.stringify(payload) });
      form.classList.add('hidden'); toast(id ? 'Skill updated.' : 'Skill saved as an inactive draft.'); await loadSkills();
    } catch (error) { toast(error.message, true); }
  });
  $('#profile-form').addEventListener('submit', async event => {
    event.preventDefault(); const form = event.currentTarget; const message = $('#profile-message'); message.textContent = '';
    try {
      state.user = await api('/api/auth/profile', { method: 'PUT', body: JSON.stringify({ displayName: form.elements.displayName.value, phoneNumber: form.elements.phoneNumber.value }) });
      $('#user-label').textContent = state.user.displayName || state.user.email; renderProfile(); toast('Profile saved.');
    } catch (error) { message.textContent = error.body?.errors ? Object.values(error.body.errors).flat().join(' ') : error.message; }
  });
  $('#avatar-file').addEventListener('change', async event => {
    const file = event.target.files[0]; if (!file) return;
    if (file.size <= 0 || file.size > 10 * 1024 * 1024) { toast('Avatar must be no larger than 10 MB.', true); event.target.value = ''; return; }
    const data = new FormData(); data.append('file', file);
    try {
      const response = await fetch('/api/auth/me/avatar', { method: 'POST', credentials: 'same-origin', body: data });
      const result = await response.json(); if (!response.ok) throw new Error(result.detail || Object.values(result.errors || {}).flat().join(' ') || 'Avatar upload failed');
      state.user = result; renderProfile(); toast('Avatar updated.');
    } catch (error) { toast(error.message, true); }
    finally { event.target.value = ''; }
  });
  $('#avatar-remove').addEventListener('click', async () => {
    try { await api('/api/auth/me/avatar', { method: 'DELETE' }); state.user.avatarUrl = null; renderProfile(); toast('Avatar removed.'); }
    catch (error) { toast(error.message, true); }
  });
  $('#password-form').addEventListener('submit', async event => {
    event.preventDefault(); const form = event.currentTarget; const message = $('#password-message'); message.textContent = '';
    try { await api('/api/auth/password', { method: 'POST', body: JSON.stringify({ currentPassword: form.elements.currentPassword.value, newPassword: form.elements.newPassword.value }) }); form.reset(); toast('Password updated.'); }
    catch (error) { message.textContent = error.body?.errors ? Object.values(error.body.errors).flat().join(' ') : error.message; }
  });
  $('#admin-refresh').addEventListener('click', loadAdminOverview);
  window.addEventListener('resize', () => {
    if (!lastAdminAnalytics || $('#admin-view').classList.contains('hidden')) return;
    clearTimeout(chartResizeTimer);
    chartResizeTimer = setTimeout(() => renderAdminAnalytics(lastAdminAnalytics).catch(() => {}), 180);
  });
  document.querySelectorAll('[data-admin-view]').forEach(button => button.addEventListener('click', () => showView(button.dataset.adminView)));
  authForm.addEventListener('submit', submitAuth);
  $('#logout-button').addEventListener('click', async () => {
    try { await api('/api/auth/logout', { method: 'POST' }); } finally { window.location.reload(); }
  });
  $('#schedule-date').value = new Date().toISOString().slice(0, 10);
  $('#schedule-date').addEventListener('change', loadSchedule);
  $('#new-resource-button').addEventListener('click', () => openResourceDialog());
  $('#resource-image-file').addEventListener('change', event => {
    const file = event.target.files?.[0];
    if (!file) return;
    if (file.size <= 0 || file.size > 10 * 1024 * 1024) {
      event.target.value = '';
      toast('Choose an image up to 10 MB.', true);
      return;
    }
    if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type)) {
      event.target.value = '';
      toast('Choose a JPEG, PNG, or WebP image.', true);
      return;
    }
    if (roomImagePreviewUrl) URL.revokeObjectURL(roomImagePreviewUrl);
    roomImagePreviewUrl = URL.createObjectURL(file);
    $('#resource-image-preview').src = roomImagePreviewUrl;
  });
  $('#resource-form').addEventListener('submit', async event => {
    if (event.submitter?.value === 'cancel') return;
    event.preventDefault();
    const form = event.currentTarget;
    const resourceId = form.dataset.resourceId;
    const imageFile = form.elements.image.files?.[0];
    const saveButton = $('#resource-save');
    const payload = { name: form.elements.name.value, description: form.elements.description.value };
    saveButton.disabled = true;
    try {
      let savedResourceId = resourceId;
      if (savedResourceId) await api(`/api/resources/${savedResourceId}`, { method: 'PUT', body: JSON.stringify({ ...payload, isActive: form.elements.isActive.checked }) });
      else {
        const created = await api('/api/resources', { method: 'POST', body: JSON.stringify(payload) });
        savedResourceId = created.id;
        form.dataset.resourceId = savedResourceId;
        $('#resource-dialog-title').textContent = 'Edit room';
        $('#resource-active-field').classList.remove('hidden');
      }
      if (imageFile) {
        const upload = new FormData();
        upload.append('file', imageFile, imageFile.name);
        await api(`/api/resources/${savedResourceId}/image`, { method: 'POST', body: upload });
      }
      dialog.close(); toast(resourceId ? 'Room updated.' : 'Room added.'); await loadResources();
      form.reset();
      if (roomImagePreviewUrl) URL.revokeObjectURL(roomImagePreviewUrl);
      roomImagePreviewUrl = null;
      setImageWithFallback($('#resource-image-preview'), null);
    } catch (error) { toast(error.message, true); }
    finally { saveButton.disabled = false; }
  });
  $('#slots-form').addEventListener('submit', async event => {
    if (event.submitter?.value === 'cancel') return;
    event.preventDefault();
    const form = event.currentTarget;
    const startUtc = new Date(form.elements.start.value).toISOString();
    const endUtc = new Date(form.elements.end.value).toISOString();
    try {
      await api(`/api/resources/${state.selected.id}/slots`, { method: 'POST', body: JSON.stringify([{ startUtc, endUtc }]) });
      $('#slots-dialog').close(); form.reset(); toast('Availability added.'); await loadSchedule();
    } catch (error) { toast(error.message, true); }
  });

  api('/api/auth/me').then(loadWorkspace).catch(error => {
    if (error.status !== 401) authMessage.textContent = error.message;
  });
})();
