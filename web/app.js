/* XuYuan-SnapScan · 前端逻辑
   通道：state / config / recheck / pickwx / capture / clipcopy / win / hotkey
   后端推送：capture-done / state-refresh */

(function () {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const api = (channel, payload) => {
    try { return jade.invoke(channel, payload || {}); }
    catch (e) { return Promise.reject(e); }
  };

  let S = null;            // 后端状态
  let busy = false;

  /* ---------------- 状态渲染 ---------------- */
  function applyGlass(opacity, frost) {
    const root = document.documentElement;
    const o = typeof opacity === "number" ? opacity : 30;
    const f = typeof frost === "number" ? frost : 3;
    // 0..100 全区间；30 这一档保持原来的观感（alpha≈.46），0 全透、100 不透明
    const alpha = o >= 30 ? 0.46 + ((o - 30) / 70) * 0.54 : 0.46 * (o / 30);
    root.style.setProperty("--glass-alpha", alpha.toFixed(3));
    root.style.setProperty("--bar-alpha", Math.min(1, alpha + 0.06).toFixed(3));
    root.style.setProperty("--glass-blur", (2 + f * 2) + "px");
  }

  function applyState(s) {
    if (!s || typeof s !== "object") return;
    S = s;

    const e = s.engine || {};
    const pill = $("enginePill");
    pill.classList.remove("is-system", "is-none");
    // 首页只显示当前真正在用的模型名称，补充说明统一放到设置里
    $("enginePillText").textContent = e.name || "—";
    // 只有真正命中的引擎才显示绿色“已就绪”，降级用橙色，无可用用红色
    if (!e.effective) pill.classList.add("is-none");
    else if (e.effective !== e.mode) pill.classList.add("is-system");

    $("engineName").textContent = e.name || "—";
    const badge = $("engineBadge");
    // 正常可用时不显示任何状态字样，只在真正有问题（降级 / 不可用）时提示
    if (!e.effective) {
      badge.textContent = "不可用";
      badge.classList.add("warn");
      badge.style.display = "";
    } else if (e.effective !== e.mode) {
      badge.textContent = "已降级";
      badge.classList.add("warn");
      badge.style.display = "";
    } else {
      badge.textContent = "";
      badge.classList.remove("warn");
      badge.style.display = "none";
    }
    $("engineDesc").textContent = describeEngine(e);

    const c = s.config || {};
    $("swFreehand").checked = !!c.freehand;
    $("swAutocopy").checked = !!c.autocopy;
    $("swRestoreWin").checked = !!c.restorewin;
    $("swRestoreQr").checked = !!c.restoreqr;

    if (document.activeElement !== $("rgOpacity")) $("rgOpacity").value = c.opacity != null ? c.opacity : 30;
    if (document.activeElement !== $("rgFrost")) $("rgFrost").value = c.frost != null ? c.frost : 3;
    $("opacityVal").textContent = (c.opacity != null ? c.opacity : 30) + "%";
    $("frostVal").textContent = (c.frost != null ? c.frost : 3);
    applyGlass(c.opacity, c.frost);

    const hk = s.hotkeys || {};
    $("hkQr").textContent = hk.qr || "—";
    $("hkOcr").textContent = hk.ocr || "—";
    if (document.activeElement !== $("hkQrInput")) $("hkQrInput").value = hk.qr || "";
    if (document.activeElement !== $("hkOcrInput")) $("hkOcrInput").value = hk.ocr || "";

    buildEngineList(e, c.engine);

    $("verText").textContent = s.version || "—";
    $("verText2").textContent = s.version || "—";
    const rt = (s.runtime && s.runtime.win11) ? "WebView2 · Windows 11" : "WebView2 · Windows 10";
    $("rtText").textContent = rt;
    $("rtText2").textContent = rt;
  }

  function describeEngine(e) {
    if (!e) return "—";
    if (e.effective === "wechat") {
      let t = e.wechatBundled
        ? "随包自带的微信 OCR 模型，不依赖本机装没装微信。"
        : "已检测到微信 " + (e.wechatVersion || "") + "，直接复用微信自带的 OCR 模型";
      if (!e.wechatBundled && e.wechatVersion === "") t = "已复用本机微信自带的 OCR 模型";
      if (e.verified) t += "（实测可用）";
      return t;
    }
    if (e.effective && e.effective.indexOf("pp") === 0) {
      return "本地 PP-OCR 模型，已从「OCR Model」目录加载，不需要额外装任何软件。";
    }
    if (e.wechatUsable) return "微信 OCR 可用，但没有被选中（可在设置里切换）";
    return "当前使用 Windows 系统自带 OCR。" + (e.problem ? " 原因：" + e.problem : "");
  }

  // 引擎选择：竖排选项（比下拉更好读，长说明也不会被截断）
  // 本机没找到模型的整行置灰、不响应点击，不会出现“选了却用不了”的情况
  function buildEngineList(e, current) {
    const box = $("engineList");
    box.innerHTML = "";

    const list = (e && e.list) ? e.list : [];
    const readyKeys = list.filter((it) => it && it.ready).map((it) => it.key);
    let active = (readyKeys.indexOf(current) >= 0) ? current : null;
    if (!active && e && readyKeys.indexOf(e.effective) >= 0) active = e.effective;
    if (!active && readyKeys.length) active = readyKeys[0];

    list.forEach((it) => {
      if (!it) return;
      const row = document.createElement("div");
      row.className = "engine-opt";
      if (it.ready) row.className += " pick" + (it.key === active ? " on" : "");
      else row.className += " off";

      const tick = document.createElement("span");
      tick.className = "tick";

      const name = document.createElement("span");
      name.className = "e-name";
      name.textContent = it.name || it.key;

      const note = document.createElement("span");
      note.className = "e-note";
      note.textContent = it.ready
        ? (it.note ? "（" + it.note + "）" : "")
        : "（未找到模型）";

      row.appendChild(tick);
      row.appendChild(name);
      row.appendChild(note);

      if (it.ready) {
        row.addEventListener("click", () => {
          if (it.key !== active) setConfig({ engine: it.key });
        });
      } else {
        row.title = "本机没有找到这一档模型，无法选择";
      }
      box.appendChild(row);
    });

    const missing = list.filter((it) => it && !it.ready).map((it) => it.name);
    let note = "只列出本机真正检测到的可用引擎；灰掉的是没找到模型的。";
    if (missing.length) note += " 缺少：" + missing.join(" / ") + "。";
    if (e && e.modelRoot) note += "\n模型目录：" + e.modelRoot;
    $("engineNote").textContent = note;
  }

  /* ---------------- 识别 ---------------- */
  async function capture(mode) {
    if (busy) return;
    busy = true;
    $("status").textContent = mode === "qr" ? "正在识码…" : "正在识字…";
    try {
      const r = await api("capture", { mode });
      if (r && r.cancelled) { $("status").textContent = "已取消"; return; }
      if (r) showResult(r);
    } catch (e) {
      $("status").textContent = "识别失败";
      toast("调用后端失败：" + e);
    } finally {
      busy = false;
      setTimeout(() => { if (!busy) $("status").textContent = "就绪"; }, 1200);
    }
  }

  function showResult(r) {
    const box = $("result");
    if (r.ok && r.text) {
      box.textContent = r.text;
      const meta = [];
      if (r.engine) meta.push(r.engine);
      if (r.ms) meta.push(r.ms + " ms");
      meta.push((r.text.length) + " 字");
      $("resultMeta").textContent = meta.join(" · ");
      $("status").textContent = r.mode === "qr" ? "识码完成" : "识字完成";
      if (r.copied) flashCopy("已复制到剪贴板");
      box.focus();
    } else {
      // 本次没识别到内容时，不要覆盖上一次的统计信息（旧结果还留在框里，容易看不懂）
      const kept = $("result").innerText.trim().length > 0;
      $("resultMeta").textContent = kept ? "本次未识别到文字 · 保留上次结果" : "未识别到内容";
      $("status").textContent = r.mode === "qr" ? "没有发现二维码" : "没有识别到文字";
      toast(r.mode === "qr" ? "没有发现二维码，换个区域试试" : "没有识别到文字，换个区域试试");
    }
    if (r.detail) $("engineDesc").dataset.lastDetail = r.detail;
  }

  function flashCopy(text) {
    const el = $("copyTip");
    el.textContent = text;
    el.classList.add("show");
    clearTimeout(flashCopy._t);
    flashCopy._t = setTimeout(() => el.classList.remove("show"), 1600);
  }

  function toast(msg) {
    const el = $("toast");
    el.textContent = msg;
    el.classList.add("show");
    clearTimeout(toast._t);
    toast._t = setTimeout(() => el.classList.remove("show"), 1800);
  }

  /* ---------------- 设置 ---------------- */
  function openDrawer(open) {
    $("drawer").classList.toggle("open", open);
    $("mask").classList.toggle("show", open);
  }

  async function setConfig(patch) {
    try {
      const s = await api("config", patch);
      applyState(s);
    } catch (e) { toast("保存失败：" + e); }
  }

  /* ---------------- 快捷键录制 ---------------- */
  function bindHotkeyRecorder(input, which) {
    let recording = false;
    input.addEventListener("focus", () => {
      recording = true;
      input.value = "按下组合键…";
    });
    input.addEventListener("blur", () => {
      recording = false;
      if (S && S.hotkeys) input.value = which === "qr" ? S.hotkeys.qr : S.hotkeys.ocr;
    });
    input.addEventListener("keydown", async (ev) => {
      if (!recording) return;
      ev.preventDefault();
      ev.stopPropagation();
      if (ev.key === "Escape") { input.blur(); return; }

      const mods = [];
      if (ev.ctrlKey) mods.push("Ctrl");
      if (ev.altKey) mods.push("Alt");
      if (ev.shiftKey) mods.push("Shift");
      if (ev.metaKey) mods.push("Win");
      const k = ev.key;
      if (["Control", "Alt", "Shift", "Meta"].includes(k)) {
        input.value = mods.join("+") + "+…";
        return;
      }
      if (mods.length === 0) { input.value = "需要至少一个修饰键"; return; }

      let key = k.length === 1 ? k.toUpperCase() : k;
      if (k === " ") key = "Space";
      if (/^F\d{1,2}$/.test(k)) key = k;
      const combo = mods.join("+") + "+" + key;
      input.value = combo;
      recording = false;
      input.blur();
      try {
        const s = await api("hotkey", { which, combo });
        applyState(s);
        toast("快捷键已更新：" + combo);
      } catch (e) { toast("设置失败：" + e); }
    });
  }

  /* ---------------- 初始化 ---------------- */
  async function boot() {
    $("btnQr").addEventListener("click", () => capture("qr"));
    $("btnOcr").addEventListener("click", () => capture("ocr"));

    $("btnCopy").addEventListener("click", async () => {
      const t = $("result").innerText;
      if (!t) { toast("没有可复制的内容"); return; }
      try { await api("clipcopy", { text: t }); flashCopy("已复制到剪贴板"); }
      catch (e) { toast("复制失败：" + e); }
    });

    $("btnClear").addEventListener("click", () => {
      $("result").textContent = "";
      $("resultMeta").textContent = "等待识别";
      $("result").focus();
    });

    $("btnSettings").addEventListener("click", () => openDrawer(true));
    $("btnCloseDrawer").addEventListener("click", () => openDrawer(false));
    $("mask").addEventListener("click", () => openDrawer(false));
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") openDrawer(false); });

    $("swFreehand").addEventListener("change", (e) => setConfig({ freehand: e.target.checked }));
    $("swAutocopy").addEventListener("change", (e) => setConfig({ autocopy: e.target.checked }));
    $("swRestoreWin").addEventListener("change", (e) => setConfig({ restorewin: e.target.checked }));
    $("swRestoreQr").addEventListener("change", (e) => setConfig({ restoreqr: e.target.checked }));

    // 透明度 / 毛玻璃：拖动时实时生效，松手后写配置
    const rgO = $("rgOpacity"), rgF = $("rgFrost");
    rgO.addEventListener("input", () => {
      $("opacityVal").textContent = rgO.value + "%";
      applyGlass(Number(rgO.value), Number(rgF.value));
    });
    rgF.addEventListener("input", () => {
      $("frostVal").textContent = rgF.value;
      applyGlass(Number(rgO.value), Number(rgF.value));
    });
    rgO.addEventListener("change", () => setConfig({ opacity: Number(rgO.value) }));
    rgF.addEventListener("change", () => setConfig({ frost: Number(rgF.value) }));

    const recheck = async () => {
      try {
        $("status").textContent = "正在检测引擎…";
        const s = await api("recheck", {});
        applyState(s);
        toast("引擎检测完成");
        $("status").textContent = "就绪";
      } catch (err) { toast("检测失败：" + err); }
    };
    $("btnRecheck").addEventListener("click", recheck);
    $("btnRecheck2").addEventListener("click", recheck);

    bindHotkeyRecorder($("hkQrInput"), "qr");
    bindHotkeyRecorder($("hkOcrInput"), "ocr");

    // 后端推送（热键触发识别 / 窗口重新显示）
    if (typeof jade !== "undefined" && typeof jade.on === "function") {
      try {
        jade.on("capture-done", (data) => {
          let r = data;
          if (typeof r === "string") { try { r = JSON.parse(r); } catch (e) { return; } }
          if (r) showResult(r);
        });
        jade.on("state-refresh", (data) => {
          let s = data;
          if (typeof s === "string") { try { s = JSON.parse(s); } catch (e) { return; } }
          applyState(s);
        });
      } catch (e) { /* 事件 API 不可用时静默降级 */ }
    }

    try {
      const s = await api("state", {});
      applyState(s);
      $("status").textContent = "就绪 · 按 Ctrl+Alt+D 识码 / Ctrl+Alt+X 识字";
    } catch (e) {
      $("status").textContent = "与后端通信失败";
      toast("初始化失败：" + e);
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
