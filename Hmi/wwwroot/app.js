"use strict";

// ---------------------------------------------------------------- helpers
const $ = (id) => document.getElementById(id);

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await response.json().catch(() => null);
  if (!response.ok) {
    const detail = data && (data.detail || data.title);
    throw new Error(detail || `${response.status} ${response.statusText}`);
  }
  return data;
}

let toastTimer;
function toast(message, isError = false) {
  const el = $("toast");
  el.textContent = message;
  el.classList.toggle("error", isError);
  el.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove("show"), 2600);
}

function time(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  return d.toLocaleTimeString(undefined, { hour12: false }) +
    "." + String(d.getMilliseconds()).padStart(3, "0");
}

function dateTime(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  const today = new Date().toDateString() === d.toDateString();
  return today ? time(iso) : d.toLocaleDateString() + " " + time(iso);
}

// "WaitForConfirmation" -> "Wait for confirmation"
function words(name) {
  const s = String(name || "").replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function sevChip(severity) {
  const span = document.createElement("span");
  span.className = "sev sev-" + String(severity).toLowerCase();
  span.textContent = severity;
  return span;
}

function cell(row, content, className) {
  const td = row.insertCell();
  if (className) td.className = className;
  if (content instanceof Node) td.appendChild(content);
  else td.textContent = content ?? "";
  return td;
}

function setLink(id, ok, detail) {
  const link = $(id);
  link.classList.toggle("ok", ok);
  link.classList.toggle("bad", !ok);
  link.querySelector(".detail").textContent = detail;
}

// ---------------------------------------------------------------- PLC status
let lastHeartbeat = null;
let sliderBusy = false;

function renderStatus(s) {
  if (!s.connected) {
    setLink("plcLink", false, "not reachable");
    lastHeartbeat = null;
    return;
  }
  const alive = lastHeartbeat === null || s.heartbeat !== lastHeartbeat;
  lastHeartbeat = s.heartbeat;
  if (!alive) setLink("plcLink", false, "stopped");
  else if (!s.isInitialised) setLink("plcLink", false, "events not created");
  else setLink("plcLink", true, "running");

  const limit = s.ovenTemperatureLimit || 80;
  $("tempLimit").textContent = limit.toFixed(0);
  const slider = $("tempSlider");
  $("sliderLimit").style.left = `calc(${(limit / slider.max) * 100}% - 1px)`;
  if (!sliderBusy) {
    slider.value = Math.round(s.ovenTemperature);
    showTemperature(s.ovenTemperature, limit);
  }

  stateLine("ovenState", s.overTemperatureRaised, "OverTemperature raised");
  stateLine("airState", s.lowAirPressureRaised, "LowAirPressure raised");
  stateLine("driveState", s.driveFaultRaised, "DriveFault raised");
}

function stateLine(id, on, text) {
  const el = $(id);
  el.classList.toggle("on", on);
  el.textContent = on ? "● " + text : "Not raised";
}

function showTemperature(value, limit) {
  $("tempValue").textContent = Math.round(value);
  $("ovenCard").querySelector(".temp").classList.toggle("hot", value > limit);
}

// ---------------------------------------------------------------- alarms
let alarmSignature = "";

function renderAlarms(alarms) {
  const signature = JSON.stringify(alarms);
  if (signature === alarmSignature) return; // keeps hover and focus stable
  alarmSignature = signature;

  const body = $("alarmTable").tBodies[0];
  body.replaceChildren();
  for (const a of alarms) {
    const row = body.insertRow();
    row.classList.add("sev-row-" + String(a.severity).toLowerCase());
    if (!a.isRaised) row.classList.add("cleared");

    cell(row, sevChip(a.severity));
    cell(row, dateTime(a.raisedUtc), "time");
    cell(row, a.source, "source");
    cell(row, a.text, "text");

    const state = document.createElement("div");
    state.className = "state";
    const raised = document.createElement("div");
    raised.className = a.isRaised ? "raised" : "";
    raised.textContent = a.isRaised ? "Raised" : "Cleared " + time(a.clearedUtc);
    const confirm = document.createElement("div");
    const waiting = /wait/i.test(a.confirmationState);
    confirm.className = waiting ? "wait" : "";
    confirm.textContent = words(a.confirmationState);
    state.append(raised, confirm);
    cell(row, state);

    const action = row.insertCell();
    if (waiting) {
      const btn = document.createElement("button");
      btn.className = "btn btn-small";
      btn.textContent = "Confirm";
      btn.addEventListener("click", () => confirmAlarm(a.key, btn));
      action.appendChild(btn);
    }
  }

  const count = $("alarmCount");
  count.textContent = alarms.length;
  count.classList.toggle("has", alarms.some((a) => a.isRaised));
  $("alarmEmpty").hidden = alarms.length > 0;
}

async function confirmAlarm(key, btn) {
  btn.disabled = true;
  try {
    const r = await api("POST", "/api/alarms/confirm", key);
    toast(r.message, !r.accepted);
  } catch (e) {
    toast(e.message, true);
  }
  refresh();
}

// ---------------------------------------------------------------- history
let eventSignature = "";

function renderEvents(events) {
  const signature = JSON.stringify(events);
  if (signature === eventSignature) return;
  eventSignature = signature;

  const body = $("eventTable").tBodies[0];
  body.replaceChildren();
  for (const e of events) {
    const row = body.insertRow();
    cell(row, dateTime(e.raisedUtc), "time");
    cell(row, words(e.type));
    cell(row, sevChip(e.severity));
    cell(row, e.source, "source");
    cell(row, e.text, "text");
    cell(row, time(e.clearedUtc), "time");
    cell(row, time(e.confirmedUtc), "time");
  }
  $("eventEmpty").hidden = events.length > 0;
}

// ---------------------------------------------------------------- polling
let refreshing = false;

async function refresh() {
  if (refreshing) return;
  refreshing = true;
  try {
    const [status, alarms, events] = await Promise.allSettled([
      api("GET", "/api/status"),
      api("GET", "/api/alarms"),
      api("GET", "/api/events"),
    ]);

    if (status.status === "fulfilled") renderStatus(status.value);
    else setLink("plcLink", false, "HMI server down");

    if (alarms.status === "fulfilled" && events.status === "fulfilled") {
      setLink("loggerLink", true, "connected");
      renderAlarms(alarms.value);
      renderEvents(events.value);
    } else {
      const reason = (alarms.reason || events.reason || {}).message || "not reachable";
      setLink("loggerLink", false, reason.length > 40 ? "not reachable" : reason);
    }
  } finally {
    refreshing = false;
  }
}

// ---------------------------------------------------------------- controls
function intValue(id) {
  const n = parseInt($(id).value, 10);
  return Number.isFinite(n) ? n : 0;
}

document.querySelectorAll("button[data-request]").forEach((btn) => {
  btn.addEventListener("click", async () => {
    const body = { request: btn.dataset.request };
    if (btn.dataset.int) body.integerArgument = intValue(btn.dataset.int);
    if (btn.dataset.int2) body.secondIntegerArgument = intValue(btn.dataset.int2);
    if (btn.dataset.text) body.textArgument = $(btn.dataset.text).value;

    btn.disabled = true;
    try {
      const r = await api("POST", "/api/requests", body);
      toast(r.message, !r.accepted);
    } catch (e) {
      toast(e.message, true);
    } finally {
      btn.disabled = false;
    }
    refresh();
  });
});

$("confirmAll").addEventListener("click", async () => {
  try {
    const r = await api("POST", "/api/alarms/confirm-all");
    toast(r.message, !r.accepted);
  } catch (e) {
    toast(e.message, true);
  }
  refresh();
});

// The slider writes the simulated temperature; the PLC decides about the alarm.
let sliderTimer;
const slider = $("tempSlider");
slider.addEventListener("input", () => {
  sliderBusy = true;
  showTemperature(Number(slider.value), Number($("tempLimit").textContent));
  clearTimeout(sliderTimer);
  sliderTimer = setTimeout(async () => {
    try {
      const r = await api("PUT", "/api/oven-temperature", { value: Number(slider.value) });
      if (!r.accepted) toast(r.message, true);
    } catch (e) {
      toast(e.message, true);
    } finally {
      sliderBusy = false;
    }
  }, 120);
});

refresh();
setInterval(refresh, 1000);
