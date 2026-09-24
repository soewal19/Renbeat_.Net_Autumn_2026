(() => {
  const $ = (selector) => document.querySelector(selector);
  const state = { user: null, resources: [], selected: null, slots: [], skills: [], hub: null, authMode: 'login', toastTimer: null };
  const authView = $('#auth-view');
  const appView = $('#app-view');
  const authForm = $('#auth-form');
  const authMessage = $('#auth-message');
  const dialog = $('#resource-dialog');

  async function api(url, options = {}) {
    const response = await fetch(url, {
      credentials: 'same-origin',
      ...options,
      headers: { ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...options.headers }
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
    $('#user-label').textContent = state.user.displayName || state.user.email;
    $('#logout-button').classList.remove('hidden');
    const isAdmin = state.user.roles?.includes('Admin');
    $('#new-resource-button').classList.toggle('hidden', !isAdmin);
    $('#admin-bookings-panel').classList.toggle('hidden', !isAdmin);
    $('#skill-admin-actions').classList.toggle('hidden', !isAdmin);
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
    for (const id of ['booking-view', 'my-bookings-view', 'ai-view', 'skills-view', 'help-view']) $(`#${id}`).classList.toggle('hidden', id !== viewId);
    document.querySelectorAll('[data-view]').forEach(tab => tab.classList.toggle('active', tab.dataset.view === viewId));
    if (viewId === 'skills-view') loadSkills();
    if (viewId === 'my-bookings-view') loadMyBookings();
  }

  async function loadMyBookings() {
    try {
      const bookings = await api('/api/bookings/me');
      const list = $('#my-bookings-list'); list.replaceChildren();
      $('#my-bookings-empty').classList.toggle('hidden', bookings.length > 0);
      for (const booking of bookings) {
        const row = document.createElement('div'); row.className = 'booking-row';
        row.innerHTML = `<span>${escapeHtml(booking.resourceName)}</span><span>${new Date(booking.slotStartUtc).toLocaleString()} – ${new Date(booking.slotEndUtc).toLocaleTimeString()}</span><span>Booked ${new Date(booking.createdAtUtc).toLocaleDateString()}</span>`;
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
      const result = await api('/api/ai/chat', { method: 'POST', body: JSON.stringify({ message }) });
      $('#ai-answer').textContent = result.answer;
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
      card.innerHTML = `<span class="room-icon">⌂</span><span class="room-text"><span class="room-name">${escapeHtml(resource.name)}${resource.isActive ? '' : ' · Inactive'}</span><span class="room-description">${escapeHtml(resource.description || 'Meeting room')}</span></span>`;
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
        row.innerHTML = `<span>${escapeHtml(booking.resourceName)}</span><span>${escapeHtml(booking.userEmail)}</span><span>${new Date(booking.slotStartUtc).toLocaleString()}</span><span>${new Date(booking.createdAtUtc).toLocaleString()}</span>`;
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
    $('#resource-dialog-title').textContent = resource ? 'Edit room' : 'Add a room';
    $('#resource-active-field').classList.toggle('hidden', !resource);
    form.elements.name.value = resource?.name || '';
    form.elements.description.value = resource?.description || '';
    form.elements.isActive.checked = resource?.isActive ?? true;
    form.dataset.resourceId = resource?.id || '';
    dialog.showModal();
  }

  function formatTime(date) { return new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' }).format(date); }
  function escapeHtml(value) { return String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]); }

  document.querySelectorAll('[data-auth-mode]').forEach(tab => tab.addEventListener('click', () => setAuthMode(tab.dataset.authMode)));
  document.querySelectorAll('[data-view]').forEach(tab => tab.addEventListener('click', () => showView(tab.dataset.view)));
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
  authForm.addEventListener('submit', submitAuth);
  $('#logout-button').addEventListener('click', async () => {
    try { await api('/api/auth/logout', { method: 'POST' }); } finally { window.location.reload(); }
  });
  $('#schedule-date').value = new Date().toISOString().slice(0, 10);
  $('#schedule-date').addEventListener('change', loadSchedule);
  $('#new-resource-button').addEventListener('click', () => openResourceDialog());
  $('#resource-form').addEventListener('submit', async event => {
    if (event.submitter?.value === 'cancel') return;
    event.preventDefault();
    const form = event.currentTarget;
    const resourceId = form.dataset.resourceId;
    const payload = { name: form.elements.name.value, description: form.elements.description.value };
    try {
      if (resourceId) await api(`/api/resources/${resourceId}`, { method: 'PUT', body: JSON.stringify({ ...payload, isActive: form.elements.isActive.checked }) });
      else await api('/api/resources', { method: 'POST', body: JSON.stringify(payload) });
      dialog.close(); toast(resourceId ? 'Room updated.' : 'Room added.'); await loadResources();
    } catch (error) { toast(error.message, true); }
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
