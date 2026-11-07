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

  // 译文模式：效率走遮罩块，拟真走擦除+回填成品图；不透明度只对效率有意义
  function applyMtMode(mode) {
    const real = mode === "realistic";
    const pv = $("maskPreview");
    const row = $("rowMtAlpha");
    const n1 = $("noteMtAlpha");
    if (pv) pv.style.display = real ? "none" : "";
    if (row) row.style.display = real ? "none" : "";
    if (n1) n1.style.display = real ? "none" : "";
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
    if ($("swToast")) $("swToast").checked = c.toast !== false;
    if ($("swOcrFusion")) $("swOcrFusion").checked = !!c.ocrfusion;
    if ($("swMtBar")) $("swMtBar").checked = c.mtbar !== false;
    if ($("selMtPerf")) $("selMtPerf").value = c.mtperf || "balanced";
    if ($("selMtGpu")) $("selMtGpu").value = c.mtgpu || "auto";
    if ($("shotCacheInfo")) $("shotCacheInfo").textContent = c.cacheinfo || "—";
    if ($("numMtIdle")) $("numMtIdle").value = (c.mtidle === undefined || c.mtidle === null) ? 30 : c.mtidle;
    if ($("selMtMode")) $("selMtMode").value = c.mtmode === "realistic" ? "realistic" : "efficiency";
    applyMtMode(c.mtmode === "realistic" ? "realistic" : "efficiency");
    applyMtProfile(c.mtprofile || "standard");

    // AI 大模型：只回填参数，不把 key 回显到输入框（只显示末 4 位）
    const ai = c.ai || {};
    if ($("selAiProvider")) $("selAiProvider").value = ai.provider === "custom" ? "custom" : "deepseek";
    if ($("txtAiModel") && document.activeElement !== $("txtAiModel")) $("txtAiModel").value = ai.model || "deepseek-flash";
    if ($("txtAiBase") && document.activeElement !== $("txtAiBase")) $("txtAiBase").value = ai.base || "https://api.deepseek.com";
    if ($("swAiOcr")) $("swAiOcr").checked = !!ai.ocr;
    if ($("swAiCheck")) $("swAiCheck").checked = !!ai.check;
    if ($("swAiMerge")) $("swAiMerge").checked = !!ai.merge;
    if ($("swAiMt")) $("swAiMt").checked = !!ai.mt;
    if ($("txtAiKey")) $("txtAiKey").placeholder = ai.hasKey ? ((ai.keyTail || "Key") + " 已保存，留空则不改") : "粘贴你自己的 Key，留空则不改";
    applyAi(ai);
    if ($("rngMtAlpha")) {
      const a = (c.mtalpha === undefined || c.mtalpha === null) ? 100 : c.mtalpha;
      $("rngMtAlpha").value = a;
      const demo = $("maskDemo");
      if (demo) demo.style.background = "rgba(14,18,26," + (a / 100).toFixed(2) + ")";
      if ($("lblMtAlpha")) $("lblMtAlpha").textContent = a + "%";
    }

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
    applyGameState(s.game);

    $("verText2").textContent = s.version || "—";
    const rt = (s.runtime && s.runtime.win11) ? "WebView2 · Windows 11" : "WebView2 · Windows 10";
    $("rtText2").textContent = rt;
  }

  function applyMtProfile(v) {
    const p = v === "efficiency" ? "efficiency" : "standard";
    const name = { standard: "标准", efficiency: "效率" }[p];
    const hint = {
      standard: "HyMT1.5全语种模型",
      efficiency: "0.5B 效率模型",
    }[p];
    if ($("mtProfileHint")) $("mtProfileHint").textContent = hint;
    if ($("mtProfileBadge")) {
      $("mtProfileBadge").textContent = name;
      $("mtProfileBadge").className = "q-badge" + (p === "standard" ? "" : " ok");
    }
    buildMtProfList(p);
  }

  function applyGameState(g) {
    if (!g) return;
    if ($("gameOn")) $("gameOn").checked = !!g.on;
    if ($("gameTrigger")) $("gameTrigger").value = String(g.trigger || 0);
    if ($("gameInterval")) $("gameInterval").value = String(g.interval || 500);
    if ($("gameModel")) $("gameModel").value = g.profile === "standard" ? "standard" : "efficiency";
    if ($("gameFilter")) $("gameFilter").checked = g.filter !== false;
    if ($("gameMerge")) $("gameMerge").checked = g.merge !== false;

    const badge = $("gameBadge");
    if (badge) {
      badge.textContent = g.on ? "运行中" : (g.ready ? "未启用" : "未就绪");
      badge.className = "q-badge" + (g.on ? " ok" : (g.ready ? "" : " warn"));
    }
    if ($("gameMsg")) $("gameMsg").textContent = g.msg || "";

    const box = $("gameRegions");
    if (!box) return;
    box.innerHTML = "";
    const list = g.regions || [];
    if (!list.length) {
      const empty = document.createElement("div");
      empty.className = "game-region";
      empty.textContent = "还没有区域；点「添加区域」在屏幕上框选字幕位置。";
      box.appendChild(empty);
      return;
    }
    list.forEach((r, i) => {
      const row = document.createElement("div");
      row.className = "game-region";
      const label = document.createElement("span");
      label.textContent = "区域 " + (i + 1) + " · " + r.x + "," + r.y + " · " + r.w + "×" + r.h;
      const del = document.createElement("button");
      del.type = "button";
      del.textContent = "删除";
      del.dataset.index = String(i);
      row.appendChild(label);
      row.appendChild(del);
      box.appendChild(row);
    });
  }

  // 设置抽屉里的两档模式：标准 / 效率
  function buildMtProfList(cur) {
    const box = $("mtProfList");
    if (!box) return;
    const rows = [
      { key: "standard", title: "标准", note: "HyMT1.5全语种模型，所有语种，按需加载" },
      { key: "efficiency", title: "效率", note: "0.5B 短句/游戏字幕，常驻" },
    ];
    box.innerHTML = "";
    rows.forEach((r) => {
      const row = document.createElement("div");
      row.className = "engine-opt pick" + (r.key === cur ? " on" : "");
      const tick = document.createElement("span"); tick.className = "tick";
      const name = document.createElement("span"); name.className = "e-name"; name.textContent = r.title;
      const note = document.createElement("span"); note.className = "e-note"; note.textContent = "（" + r.note + "）";
      row.appendChild(tick); row.appendChild(name); row.appendChild(note);
      row.addEventListener("click", () => {
        if (r.key === cur) return;
        if (S && S.config) S.config.mtprofile = r.key;
        applyMtProfile(r.key);
        setConfig({ mtprofile: r.key });
      });
      box.appendChild(row);
    });
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
  function applyMtState(mt) {
    mt = mt || {};
    const ready = !!mt.ready;                        // 三件套齐全才为 true
    const missing = mt.missing || [];
    const active = mt.active || "";
    const prof = (S && S.config && S.config.mtprofile) || "standard";

    // 右上角 pill：只显示当前模式实际吃什么模型（用短文案，避免被 pill 宽度截断）
    const pill = $("mtPill");
    pill.classList.toggle("is-none", !ready);
    $("mtPillText").textContent = ready
      ? (mt.short || active || "翻译就绪")
      : (missing.length ? "缺 " + missing.length + " 个模型" : "未装翻译模型");
    $("mtAccNote").textContent = ready ? (active || "") : "模型不全";

    // 主界面翻译按钮：三件套缺一就整体置灰
    const btn = $("btnMt");
    btn.classList.toggle("disabled", !ready);
    btn.disabled = !ready;
    btn.title = ready ? "截图后原地显示译文" : ("缺 " + (missing.length ? missing.join("、") : "翻译模型") + "，翻译已停用");
    $("mtSub").textContent = ready ? "原地显示译文" : "模型不全，翻译已停用";

    // 快捷区：翻译模型行只做状态展示，不提供切换
    $("mtName").textContent = ready
      ? (active || "三件套已就绪")
      : ("缺 " + (missing.length ? missing.join("、") : "翻译模型"));
    if ($("mtBadge")) {
      $("mtBadge").textContent = ready ? "就绪" : "不全";
      $("mtBadge").className = "q-badge" + (ready ? " ok" : " off");
    }

    buildMtList(mt);

    const profText = prof === "efficiency" ? "效率（0.5B 短句模型）" : "标准（HyMT1.5全语种模型）";
    let note = ready
      ? ("当前档位：" + profText + "。"
        + (prof === "efficiency"
          ? "0.5B 模型常驻，约 0.4–0.6 GB 显存/内存；切档或退出软件时释放。"
          : "按需加载，空闲 60 秒释放。"))
      : ("当前档位缺模型：" + (missing.length ? missing.join("、") : "翻译模型") + "。可以切换档位，或导入对应的模型包。");
    if (mt.root) note += "\n目录：" + mt.root;
    $("mtNote").textContent = note;
  }

  // 三件套状态：只展示，不提供点选（三个模型都是必需品）
  function buildMtList(mt) {
    const box = $("mtList");
    if (!box) return;
    box.innerHTML = "";
    const kit = (mt && mt.kit) || [];
    if (!kit.length) {
      const row = document.createElement("div");
      row.className = "engine-opt off";
      const tick = document.createElement("span"); tick.className = "tick";
      const name = document.createElement("span"); name.className = "e-name"; name.textContent = "未装翻译模型";
      const note = document.createElement("span"); note.className = "e-note"; note.textContent = "（先导入模型包）";
      row.appendChild(tick); row.appendChild(name); row.appendChild(note);
      box.appendChild(row);
      return;
    }
    kit.forEach((k) => {
      const row = document.createElement("div");
      row.className = "engine-opt" + (k.ready ? " ok" : " off");
      const tick = document.createElement("span"); tick.className = "tick";
      tick.textContent = k.ready ? "✓" : "×";
      const name = document.createElement("span"); name.className = "e-name";
      name.textContent = k.title + (k.ready ? "" : "（缺）");
      const note = document.createElement("span"); note.className = "e-note";
      note.textContent = "（" + k.sub + "）";
      row.appendChild(tick); row.appendChild(name); row.appendChild(note);
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
    const fuse = !!(S && S.config && S.config.ocrfusion);
    const fuseReady = readyKeys.indexOf("wechat") >= 0 && readyKeys.indexOf("pptiny") >= 0;
    if ($("swOcrFusion")) {
      $("swOcrFusion").disabled = !fuseReady;
      $("swOcrFusion").title = fuseReady ? "融合：PP 出框 + 微信出字" : "需要同时装好微信 OCR 和 PP-OCR 极速";
    }
    let active = (readyKeys.indexOf(current) >= 0) ? current : null;
    if (!active && e && readyKeys.indexOf(e.effective) >= 0) active = e.effective;
    if (!active && readyKeys.length) active = readyKeys[0];
    const activeKeys = fuse && fuseReady ? ["wechat", "pptiny"] : [active];

    list.forEach((it) => {
      if (!it) return;
      const row = document.createElement("div");
      row.className = "engine-opt";
      if (it.ready) row.className += " pick" + (activeKeys.indexOf(it.key) >= 0 ? " on" : "");
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
          if (fuse) { setConfig({ ocrfusion: false, engine: it.key }); return; }
          if (it.key !== active) setConfig({ engine: it.key });
        });
      } else {
        row.title = "本机没有找到这一档模型，无法选择";
      }
      box.appendChild(row);
    });

    const missing = list.filter((it) => it && !it.ready).map((it) => it.name);
    let note = fuse
      ? "融合模式已开启：微信 OCR + PP-OCR 极速同时勾选；点任一引擎会退出融合并切回单引擎。"
      : "只列出本机真正检测到的可用引擎；灰掉的是没找到模型的。";
    if (!fuse && missing.length) note += " 缺少：" + missing.join(" / ") + "。";
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
    if (open) {
      const box = $("mtProfList");
      const acc = box ? box.closest(".acc") : null;
      if (acc) {
        document.querySelectorAll("#drawer .acc").forEach((a) => { if (a !== acc) a.classList.remove("open"); });
        acc.classList.add("open");
      }
    }
  }

  /* AI 大模型：状态只有「就绪」才让用，其余一律置灰 */
  const AI_LABEL = {
    unset: { t: "未配置", hint: "填个 Key 就能用", cls: "" },
    checking: { t: "校验中", hint: "正在连一次接口…", cls: "is-wait" },
    unknown: { t: "待校验", hint: "点保存或重新校验", cls: "is-wait" },
    ok: { t: "就绪", hint: "Key 有效，模型名对得上", cls: "is-ok" },
    badkey: { t: "Key 无效", hint: "Key 没复制全或已过期", cls: "is-bad" },
    badmodel: { t: "模型名有误", hint: "按提示换个模型名", cls: "is-bad" },
    offline: { t: "未连接", hint: "网络或接口没通，点右上重新校验", cls: "is-bad" }
  };

  function applyAi(ai) {
    ai = ai || {};
    const st = ai.status || (ai.hasKey ? "unknown" : "unset");
    const info = AI_LABEL[st] || AI_LABEL.unknown;
    const msg = ai.statusMsg || info.hint;

    const badge = $("aiBadge");
    if (badge) {
      badge.textContent = info.t;
      badge.classList.remove("ok", "bad", "wait", "plain");
      if (info.cls === "is-ok") badge.classList.add("ok");
      else if (info.cls === "is-bad") badge.classList.add("bad");
      else if (info.cls === "is-wait") badge.classList.add("wait");
      else badge.classList.add("plain");
      badge.style.display = "";
    }
    if ($("aiHint")) $("aiHint").textContent = msg;
    if ($("aiAccNote")) $("aiAccNote").textContent = info.t;
    if ($("aiStatusText")) $("aiStatusText").textContent = info.t;
    if ($("aiStatusMsg")) $("aiStatusMsg").textContent = msg + (ai.checkedAt ? "（" + ai.checkedAt + " 校验）" : "");
    const box = $("aiStatusBox");
    if (box) {
      box.classList.remove("is-ok", "is-bad", "is-wait");
      if (info.cls) box.classList.add(info.cls);
    }

    // 三个 AI 增强开关只在校验通过时可用，不通过就是打不开
    const ready = st === "ok";
    ["swAiOcr", "swAiCheck", "swAiMerge", "swAiMt"].forEach((id) => {
      const el = $(id);
      if (!el) return;
      el.disabled = !ready;
      const row = el.closest(".switch-row");
      if (row) row.classList.toggle("locked", !ready);
    });
  }

  // 校验在后台线程跑，前端小步轮询拿结果
  async function pollAi(maxMs) {
    const t0 = Date.now();
    while (Date.now() - t0 < maxMs) {
      await new Promise((r) => setTimeout(r, 600));
      try {
        const s = await api("state", {});
        applyState(s);
        const a = (s && s.config && s.config.ai) || {};
        if (a.status && a.status !== "checking" && a.status !== "unknown") return a;
      } catch (e) { /* 下一次再试 */ }
    }
    return null;
  }

  /* AI 抽屉：和设置抽屉互斥，同一时刻只开一个 */
  function openAI(open) {
    const d = $("aiDrawer");
    if (!d) return;
    if (open) {
      const acc = d.querySelector(".acc");
      if (acc) acc.classList.add("open");
      openDrawer(false);
    }
    d.classList.toggle("open", open);
    if ($("aiMask")) $("aiMask").classList.toggle("show", open);
  }

  // 保存 = 写配置 + 立刻校验一次；校验不通过就一直显示不可用
  async function saveAI() {
    const patch = {
      aiprovider: $("selAiProvider") ? $("selAiProvider").value : "deepseek",
      aimodel: $("txtAiModel").value.trim(),
      aibase: $("txtAiBase").value.trim(),
    };
    const k = $("txtAiKey").value.trim();
    if (k) patch.aikey = k;

    const btn = $("btnSaveAI");
    if (btn) { btn.disabled = true; btn.textContent = "校验中…"; }
    try {
      const s = await api("config", patch);
      applyState(s);
      if ($("txtAiKey")) $("txtAiKey").value = "";
      const a = await pollAi(20000);
      const st = a ? a.status : ((s && s.config && s.config.ai && s.config.ai.status) || "unknown");
      const info = AI_LABEL[st] || AI_LABEL.unknown;
      const detail = a && a.statusMsg ? a.statusMsg : info.hint;
      toast(st === "ok" ? "AI 已就绪" : "AI 不可用 · " + info.t + "：" + detail);
    } catch (e) {
      toast("保存失败：" + e);
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = "保存"; }
    }
  }

  async function setConfig(patch) {
    try {
      const s = await api("config", patch);
      applyState(s);
      return s;
    } catch (e) { toast("保存失败：" + e); return null; }
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
    if ($("btnAI")) $("btnAI").addEventListener("click", () => openAI(true));
    if ($("btnCloseAI")) $("btnCloseAI").addEventListener("click", () => openAI(false));
    if ($("aiMask")) $("aiMask").addEventListener("click", () => openAI(false));
    if ($("btnSaveAI")) $("btnSaveAI").addEventListener("click", saveAI);
    if ($("txtAiKey")) $("txtAiKey").addEventListener("keydown", (e) => { if (e.key === "Enter") saveAI(); });
    if ($("btnClearKey")) $("btnClearKey").addEventListener("click", async () => {
      await setConfig({ aikey: "" });
      if ($("txtAiKey")) $("txtAiKey").value = "";
      toast("已清除 Key，AI 功能不可用");
    });

    // 快捷区「AI 大模型」行：点一下直接开 AI 面板
    if ($("quickAi")) $("quickAi").addEventListener("click", () => openAI(true));

    // 手动重新校验（换网络、换 key 之后想立刻确认）
    if ($("btnAiRetry")) $("btnAiRetry").addEventListener("click", async () => {
      const btn = $("btnAiRetry");
      if (btn) btn.textContent = "校验中…";
      try {
        const s = await api("aiverify", {});
        applyState(s);
        const a = await pollAi(20000);
        const st = a ? a.status : ((s && s.config && s.config.ai && s.config.ai.status) || "unknown");
        const info = AI_LABEL[st] || AI_LABEL.unknown;
        toast(st === "ok" ? "AI 已就绪" : "AI 不可用 · " + info.t + "：" + (a && a.statusMsg ? a.statusMsg : info.hint));
      } catch (e) {
        toast("校验失败：" + e);
      } finally {
        if (btn) btn.textContent = "重新校验";
      }
    });
    if ($("selAiProvider")) $("selAiProvider").addEventListener("change", (e) => {
      // 切到 DeepSeek 官方就顺手把地址和默认模型名填回去，小白不用记
      if (e.target.value === "deepseek") setConfig({ aiprovider: "deepseek", aibase: "https://api.deepseek.com", aimodel: "deepseek-flash" });
      else setConfig({ aiprovider: "custom" });
    });
    if ($("swAiOcr")) $("swAiOcr").addEventListener("change", (e) => setConfig({ aiocr: e.target.checked }));
    if ($("swAiCheck")) $("swAiCheck").addEventListener("change", (e) => setConfig({ aicheck: e.target.checked }));
    if ($("swAiMerge")) $("swAiMerge").addEventListener("change", (e) => setConfig({ aimerge: e.target.checked }));
    if ($("swAiMt")) $("swAiMt").addEventListener("change", (e) => setConfig({ aimt: e.target.checked }));
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") { openDrawer(false); openAI(false); } });

    // 折叠分组：同一个抽屉里一次只开一组，省地方
    document.querySelectorAll(".acc-head").forEach((head) => {
      head.addEventListener("click", () => {
        const acc = head.parentElement;
        const scope = acc.closest(".drawer") || document;
        const willOpen = !acc.classList.contains("open");
        scope.querySelectorAll(".acc").forEach((a) => a.classList.remove("open"));
        if (willOpen) acc.classList.add("open");
      });
    });

    $("swFreehand").addEventListener("change", (e) => setConfig({ freehand: e.target.checked }));
    $("swAutocopy").addEventListener("change", (e) => setConfig({ autocopy: e.target.checked }));
    $("swRestoreWin").addEventListener("change", (e) => setConfig({ restorewin: e.target.checked }));
    $("swRestoreQr").addEventListener("change", (e) => setConfig({ restoreqr: e.target.checked }));
    if ($("swToast") && !$("swToast").dataset.bound) {
      $("swToast").dataset.bound = "1";
      $("swToast").addEventListener("change", (e) => setConfig({ toast: e.target.checked }));
    }
    if ($("swOcrFusion") && !$("swOcrFusion").dataset.bound) {
      $("swOcrFusion").dataset.bound = "1";
      $("swOcrFusion").addEventListener("change", (e) => setConfig({ ocrfusion: e.target.checked }));
    }

    $("swMtBar").addEventListener("change", (e) => setConfig({ mtbar: e.target.checked }));
    $("selMtPerf").addEventListener("change", (e) => setConfig({ mtperf: e.target.value }));
    if ($("selMtGpu")) $("selMtGpu").addEventListener("change", (e) => setConfig({ mtgpu: e.target.value }));
    if ($("btnClearShotCache")) $("btnClearShotCache").addEventListener("click", async () => {
      const btn = $("btnClearShotCache");
      const old = btn.textContent;
      btn.disabled = true;
      btn.textContent = "清理中…";
      try {
        const r = await api("cacheclear", {});
        let info = r && r.info;
        if (!info && typeof r === "string") { try { info = JSON.parse(r).info; } catch (e) { } }
        if ($("shotCacheInfo")) $("shotCacheInfo").textContent = info || "0 张";
        toast("缓存已清理");
      } catch (e) {
        toast("清理失败：" + e);
      } finally {
        btn.disabled = false;
        btn.textContent = old;
      }
    });

    if ($("selMtMode")) $("selMtMode").addEventListener("change", (e) => {
      applyMtMode(e.target.value);
      setConfig({ mtmode: e.target.value });
    });

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

    // 快捷区的「游戏专属配置」：就地展开，不另开抽屉
    if ($("quickGame")) $("quickGame").addEventListener("click", () => {
      const d = $("gameDetail");
      if (d) d.hidden = !d.hidden;
    });

    if ($("gameOn")) $("gameOn").addEventListener("change", async (e) => {
      const want = e.target.checked;
      const g = (S && S.game) || {};
      if (want && !(g.regions || []).length) {
        toast("先点「添加区域」，在屏幕上框出字幕位置");
        e.target.checked = false;
        return;
      }
      const s = await setConfig({ gameon: want });
      if (want && s && s.game && !s.game.on) toast(s.game.msg || "游戏模式启动失败");
    });

    if ($("gameTrigger")) $("gameTrigger").addEventListener("change", (e) => {
      setConfig({ gametrigger: Number(e.target.value) });
    });
    if ($("gameInterval")) $("gameInterval").addEventListener("change", (e) => {
      setConfig({ gameinterval: Number(e.target.value) });
    });
    if ($("gameFilter")) $("gameFilter").addEventListener("change", (e) => {
      setConfig({ gamefilter: e.target.checked });
    });
    if ($("gameMerge")) $("gameMerge").addEventListener("change", (e) => {
      setConfig({ gamemerge: e.target.checked });
    });
    if ($("gameModel")) $("gameModel").addEventListener("change", async (e) => {
      const v = e.target.value;
      if (v === "standard") {
        const ok = confirm("标准模型 HyMT1.5 更慢、更占显存，但质量更高。\n游戏模式仍会常驻，不会自动释放。确定切到标准？");
        if (!ok) { applyGameState(S.game); return; }
      }
      const s = await setConfig({ gamemodel: v });
      if (s && s.game && s.game.on && s.game.msg) toast(s.game.msg);
    });

    if ($("btnGameAdd")) $("btnGameAdd").addEventListener("click", async () => {
      const r = await api("gamepick", {});
      if (r && r.state) applyState(r.state);
      if (r && r.ok) toast(r.added ? "已添加字幕区域" : "区域重复或太小");
      else if (r && !r.cancelled) toast("添加区域失败：" + (r.msg || ""));
    });
    if ($("btnGameClear")) $("btnGameClear").addEventListener("click", async () => {
      if (!confirm("清空所有游戏字幕区域？")) return;
      const r = await api("gameclear", {});
      if (r && r.state) applyState(r.state);
      toast("已清空游戏区域");
    });
    if ($("gameRegions")) $("gameRegions").addEventListener("click", async (e) => {
      const btn = e.target.closest("button[data-index]");
      if (!btn) return;
      const r = await api("gamedel", { index: Number(btn.dataset.index) });
      if (r && r.state) applyState(r.state);
    });

    // 快捷区的「翻译模式」行：切换入口在设置里，点这一行直接跳到对应分组并展开
    $("quickMtProfile").addEventListener("click", () => {
      openDrawer(true);
      const acc = $("mtProfList").closest(".acc");
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
      $("status").textContent = "就绪";
    } catch (e) {
      $("status").textContent = "与后端通信失败";
      toast("初始化失败：" + e);
    }

    // 每次启动自动校验一次 AI 大模型：后端已经在启动时开跑，这里只负责把结果带回来
    try {
      const vs = await api("aiverify", {});
      applyState(vs);
      const a = (vs && vs.config && vs.config.ai) || {};
      if (a.status === "checking" || a.status === "unknown") pollAi(20000);
    } catch (e) { /* 校验通道不可用就保持原状态 */ }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
