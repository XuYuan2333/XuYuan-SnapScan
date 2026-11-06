/* XuYuan-SnapScan · 前端逻辑
   通道：state / config / recheck / pickwx / capture / clipcopy / win / hotkey / importmodel / hidetrans
   后端推送：capture-done / state-refresh
   识码 / 识字 / 翻译是三条独立入口，翻译只在 mode=mt 时叠译文 */

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
    if (!e.effective) pill.classList.add("is-none");
    else if (e.effective !== e.mode) pill.classList.add("is-system");

    $("engineName").textContent = e.name || "—";
    const badge = $("engineBadge");
    if (!e.effective) {
      badge.textContent = "不可用"; badge.classList.add("warn"); badge.style.display = "";
    } else if (e.effective !== e.mode) {
      badge.textContent = "已降级"; badge.classList.add("warn"); badge.style.display = "";
    } else {
      badge.textContent = "就绪"; badge.classList.remove("warn"); badge.style.display = "";
    }
    $("engineDesc").textContent = describeEngine(e);
    $("engineAccNote").textContent = e.name || "";

    const c = s.config || {};
    $("swFreehand").checked = !!c.freehand;
    $("swAutocopy").checked = !!c.autocopy;
    $("swRestoreWin").checked = !!c.restorewin;
    $("swRestoreQr").checked = !!c.restoreqr;
    if ($("swMtBar")) $("swMtBar").checked = c.mtbar !== false;
    if ($("selMtPerf")) $("selMtPerf").value = c.mtperf || "balanced";
    if ($("numMtIdle")) $("numMtIdle").value = (c.mtidle === undefined || c.mtidle === null) ? 30 : c.mtidle;
    if ($("rngMtAlpha")) {
      const a = (c.mtalpha === undefined || c.mtalpha === null) ? 100 : c.mtalpha;
      $("rngMtAlpha").value = a;
      const demo = $("maskDemo");
      if (demo) demo.style.background = "rgba(14,18,26," + (a / 100).toFixed(2) + ")";
      if ($("lblMtAlpha")) $("lblMtAlpha").textContent = a + "%";
    }
    if (c.dl && c.dl.running) { /* 下载功能已下线 */ }

    if (document.activeElement !== $("rgOpacity")) $("rgOpacity").value = c.opacity != null ? c.opacity : 30;
    if (document.activeElement !== $("rgFrost")) $("rgFrost").value = c.frost != null ? c.frost : 3;
    $("opacityVal").textContent = (c.opacity != null ? c.opacity : 30) + "%";
    $("frostVal").textContent = (c.frost != null ? c.frost : 3);
    applyGlass(c.opacity, c.frost);

    const hk = s.hotkeys || {};
    $("hkQr").textContent = hk.qr || "—";
    $("hkOcr").textContent = hk.ocr || "—";
    $("hkMt").textContent = hk.mt || "—";
    if (document.activeElement !== $("hkQrInput")) $("hkQrInput").value = hk.qr || "";
    if (document.activeElement !== $("hkOcrInput")) $("hkOcrInput").value = hk.ocr || "";
    if (document.activeElement !== $("hkMtInput")) $("hkMtInput").value = hk.mt || "";
    if (document.activeElement !== $("hkHideInput")) $("hkHideInput").value = hk.hide || "";

    buildEngineList(e, c.engine);
    applyMtState(s.mt);

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

  /* ---------------- 翻译 ---------------- */
  function activeModel(mt) {
    const models = (mt && mt.models) || [];
    const ready = models.filter((m) => m && m.ready);
    if (mt && mt.selected) {
      const hit = ready.filter((m) => m.name === mt.selected)[0];
      if (hit) return hit;
    }
    return ready[0] || null;
  }

  function applyMtState(mt) {
    mt = mt || {};
    const models = mt.models || [];
    const anyReady = !!mt.ready;
    const model = activeModel(mt);

    // 右上角第二颗 pill：只显示当前翻译模型名（没有就写「未装翻译模型」，不显示语言对）
    const pill = $("mtPill");
    pill.classList.toggle("is-none", !anyReady);
    $("mtPillText").textContent = anyReady
      ? (model ? model.name : "翻译模型")
      : "未装翻译模型";
    $("mtAccNote").textContent = anyReady ? (model ? model.name : "") : "未装模型";

    // 主界面翻译按钮：没模型就置灰
    const btn = $("btnMt");
    btn.classList.toggle("disabled", !anyReady);
    btn.disabled = !anyReady;
    btn.title = anyReady ? "截图后原地显示译文" : "先到设置里导入翻译模型";
    $("mtSub").textContent = anyReady ? "原地显示译文" : "未装翻译模型";

    // 快捷区：翻译模型行（只写模型名）
    $("mtName").textContent = anyReady
      ? (model ? model.name : "已装模型")
      : "未装翻译模型";
    $("mtBadge").textContent = anyReady ? "就绪" : "未装";
    $("mtBadge").className = "q-badge" + (anyReady ? " ok" : " off");

    const sel = $("selMtLang");
    if (mt.lang) sel.value = mt.lang;

    buildMtList(models, mt.selected);

    let note = anyReady
      ? (model ? ("当前使用：" + model.name + "（" + (model.pair || "多语种") + "，以英→中 / 日→中 / 俄→中 为主）") : "已装翻译模型")
      : "还没装翻译模型。点下面的「导入翻译模型包」选一个 zip，或把模型目录放进「MT Model」。";
    if (!anyReady && models.length) note = "有 " + models.length + " 个模型目录，但文件不全（缺 encoder / decoder / tokenizer.json）。";
    if (mt.root) note += "\n目录：" + mt.root;
    $("mtNote").textContent = note;
  }

  // 翻译模型列表：和识字引擎一样的竖排单选，点一下就切过去
  function buildMtList(models, selected) {
    const box = $("mtList");
    box.innerHTML = "";
    const list = models || [];
    if (!list.length) {
      const row = document.createElement("div");
      row.className = "engine-opt off";
      const tick = document.createElement("span"); tick.className = "tick";
      const name = document.createElement("span"); name.className = "e-name"; name.textContent = "未装翻译模型";
      const note = document.createElement("span"); note.className = "e-note"; note.textContent = "（先导入一个模型包）";
      row.appendChild(tick); row.appendChild(name); row.appendChild(note);
      box.appendChild(row);
      return;
    }

    const readyNames = list.filter((m) => m.ready).map((m) => m.name);
    let active = selected && readyNames.indexOf(selected) >= 0 ? selected : (readyNames[0] || null);
    if (!selected) active = readyNames[0] || null;

    list.forEach((m) => {
      if (!m) return;
      const row = document.createElement("div");
      row.className = "engine-opt" + (m.ready ? (" pick" + (m.name === active ? " on" : "")) : " off");

      const tick = document.createElement("span"); tick.className = "tick";
      const name = document.createElement("span"); name.className = "e-name";
      name.textContent = m.label || m.pair || m.name;
      const note = document.createElement("span"); note.className = "e-note";
      note.textContent = m.ready ? "（" + m.name + "）" : "（文件不全）";

      row.appendChild(tick); row.appendChild(name); row.appendChild(note);
      if (m.ready) {
        row.addEventListener("click", () => {
          if (m.name !== active) setConfig({ mtpair: m.name });
        });
      } else {
        row.title = "这个模型目录里缺 encoder / decoder / tokenizer.json，用不了";
      }
      box.appendChild(row);
    });
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
    if (mode === "mt") {
      const btn = $("btnMt");
      if (btn.disabled) { toast("翻译不可用：" + (btn.title || "先装一个翻译模型")); return; }
    }
    busy = true;
    $("status").textContent = mode === "qr" ? "正在识码…" : (mode === "mt" ? "正在识字 + 翻译…" : "正在识字…");
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
    const mode = r.mode || "ocr";
    if (r.ok && r.text) {
      box.textContent = r.text;
      const meta = [];
      if (r.engine) meta.push(r.engine);
      if (r.ms) meta.push(r.ms + " ms");
      meta.push((r.text.length) + " 字");
      if (r.mtLine) meta.push("译文 " + r.mtLine + " 行");
      $("resultMeta").textContent = meta.join(" · ");
      $("status").textContent = mode === "qr" ? "识码完成" : (r.mtLine ? "翻译完成" : "识字完成");
      if (r.copied) flashCopy("已复制到剪贴板");
      box.focus();
    } else {
      // 本次没识别到内容时，不要覆盖上一次的统计信息（旧结果还留在框里，容易看不懂）
      const kept = $("result").innerText.trim().length > 0;
      $("resultMeta").textContent = kept ? "本次未识别到文字 · 保留上次结果" : "未识别到内容";
      $("status").textContent = mode === "qr" ? "没有发现二维码" : "没有识别到文字";
      toast(mode === "qr" ? "没有发现二维码，换个区域试试" : "没有识别到文字，换个区域试试");
    }
    if (r.detail) {
      $("engineDesc").dataset.lastDetail = r.detail;
      $("engineDesc").title = r.detail;
    }
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
      if (S && S.hotkeys) input.value = S.hotkeys[which] || "";
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
    $("btnMt").addEventListener("click", () => capture("mt"));

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

    // 折叠分组：一次只开一组，省地方
    document.querySelectorAll(".acc-head").forEach((head) => {
      head.addEventListener("click", () => {
        const acc = head.parentElement;
        const willOpen = !acc.classList.contains("open");
        document.querySelectorAll(".acc").forEach((a) => a.classList.remove("open"));
        if (willOpen) acc.classList.add("open");
      });
    });

    $("swFreehand").addEventListener("change", (e) => setConfig({ freehand: e.target.checked }));
    $("swAutocopy").addEventListener("change", (e) => setConfig({ autocopy: e.target.checked }));
    $("swRestoreWin").addEventListener("change", (e) => setConfig({ restorewin: e.target.checked }));
    $("swRestoreQr").addEventListener("change", (e) => setConfig({ restoreqr: e.target.checked }));

    $("selMtLang").addEventListener("change", (e) => setConfig({ mtlang: e.target.value }));
    $("swMtBar").addEventListener("change", (e) => setConfig({ mtbar: e.target.checked }));
    $("selMtPerf").addEventListener("change", (e) => setConfig({ mtperf: e.target.value }));

    $("numMtIdle").addEventListener("change", (e) => {
      let v = parseInt(e.target.value, 10);
      if (isNaN(v) || v < 0) v = 0;
      if (v > 86400) v = 86400;
      e.target.value = v;
      setConfig({ mtidle: v });
    });

    const rngA = $("rngMtAlpha");
    if (rngA) {
      const paint = (p) => {
        const demo = $("maskDemo");
        if (demo) demo.style.background = "rgba(14,18,26," + (p / 100).toFixed(2) + ")";
        const lbl = $("lblMtAlpha");
        if (lbl) lbl.textContent = p + "%";
      };
      rngA.addEventListener("input", (e) => paint(parseInt(e.target.value, 10) || 0));
      rngA.addEventListener("change", (e) => {
        const v = Math.max(0, Math.min(100, parseInt(e.target.value, 10) || 0));
        paint(v);
        setConfig({ mtalpha: v });
      });
    }

    // 快捷区的「识字引擎」行：点一下直接跳到设置里的引擎分组
    $("quickEngine").addEventListener("click", () => {
      openDrawer(true);
      const acc = $("engineList").closest(".acc");
      if (acc) {
        document.querySelectorAll(".acc").forEach((a) => a.classList.remove("open"));
        acc.classList.add("open");
      }
    });

    $("quickMt").addEventListener("click", () => {
      openDrawer(true);
      const acc = $("mtList").closest(".acc");
      if (acc) {
        document.querySelectorAll(".acc").forEach((a) => a.classList.remove("open"));
        acc.classList.add("open");
      }
    });

    $("btnHideTrans").addEventListener("click", async () => {
      try { await api("hidetrans", {}); $("status").textContent = "译文已关闭"; }
      catch (e) { toast("关闭译文失败：" + e); }
    });

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

    // 导入模型包：OCR 模型包与翻译模型包走同一个通道，后端自己认
    const doImport = async (btn) => {
      if (!btn || btn.disabled) return;
      btn.disabled = true;
      $("status").textContent = "正在导入模型包…";
      try {
        const r = await api("importmodel", {});
        if (r && r.state) applyState(r.state);
        const msg = (r && r.msg) ? r.msg : "导入完成";
        toast(msg);
        $("status").textContent = msg;
      } catch (e) {
        toast("导入失败：" + e);
        $("status").textContent = "导入失败";
      } finally {
        btn.disabled = false;
      }
    };
    $("btnImportModel").addEventListener("click", () => doImport($("btnImportModel")));
    $("btnImportMt").addEventListener("click", () => doImport($("btnImportMt")));

    bindHotkeyRecorder($("hkQrInput"), "qr");
    bindHotkeyRecorder($("hkOcrInput"), "ocr");
    bindHotkeyRecorder($("hkMtInput"), "mt");
    bindHotkeyRecorder($("hkHideInput"), "hide");

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
      // 翻译没有独立开关：只要装了模型就是可用状态
      if (s && s.mt && s.mt.on === false) setConfig({ translate: true });
      const hk = (s && s.hotkeys) || {};
      $("status").textContent = "就绪 · " + (hk.qr || "Ctrl+Alt+D") + " 识码 / " + (hk.mt || "Ctrl+Alt+F") + " 翻译";
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
