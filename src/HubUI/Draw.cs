// ============================================================================
// Hub UI: sprite rendering. Layout is designed on a 512 px reference and
// scaled with k = min(width, height) / 512, so square and wide LCDs both work.
// ============================================================================
static readonly Color C_BG = new Color(4, 10, 18), C_PANEL = new Color(14, 30, 46), C_TRACK = new Color(26, 40, 56),
                      C_ACC = new Color(0, 170, 255), C_TXT = new Color(220, 235, 245), C_DIM = new Color(110, 130, 150),
                      C_SEL = new Color(0, 64, 104), C_GOOD = new Color(60, 220, 110), C_WARN = new Color(255, 190, 40),
                      C_BAD = new Color(255, 70, 60), C_DOCK = new Color(90, 140, 255), C_VIO = new Color(190, 120, 255),
                      C_ORANGE = new Color(255, 130, 40);

// Layout scale. Big LCDs: the 512 reference. Cockpit screens (cpt) are small, wide and 256 px,
// so their layout drops detail and is scaled to fill the reference size it really needs.
float Scale(float W, float H, float wref, float href)
{
    return cpt ? Math.Min(W / wref, H / href) * cockpitZoom : Math.Min(W, H) / 512f;
}

static void Box(ref MySpriteDrawFrame f, float x, float y, float w, float h, Color c)
{
    f.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(x + w / 2, y + h / 2), new Vector2(w, h), c));
}

static void Txt(ref MySpriteDrawFrame f, string s, float x, float y, float scale, Color c, TextAlignment al = TextAlignment.LEFT)
{
    var sp = MySprite.CreateText(s, "White", c, scale, al);
    sp.Position = new Vector2(x, y);
    f.Add(sp);
}

static void Bar(ref MySpriteDrawFrame f, float x, float y, float w, float h, int milli, Color c)
{
    Box(ref f, x, y, w, h, C_TRACK);
    if (milli > 0) Box(ref f, x, y, w * Math.Min(1000, milli) / 1000f, h, c);
}

static Color LevelColor(int milli) { return milli < 0 ? C_DIM : milli > 500 ? C_GOOD : milli > 250 ? C_WARN : C_BAD; }

static Color StateColor(Row r)
{
    if (r.LostS >= 0) return C_BAD;
    switch (r.State)
    {
        case Proto.S_ESCORT: case Proto.S_PATROL: return (r.Flags & Proto.F_ENGAGED) != 0 ? C_ORANGE : C_GOOD;
        case Proto.S_ENGAGED: return C_BAD;
        case Proto.S_HOLD: return C_WARN;
        case Proto.S_GOTO: return C_ACC;
        case Proto.S_APPROACH: case Proto.S_FINAL: case Proto.S_UNDOCKING: return C_VIO;
        case Proto.S_DOCKED: return C_DOCK;
        case Proto.S_LINKLOST: return C_ORANGE;
        case Proto.S_RECON: return C_ACC;
        case Proto.S_ATTACK: return C_BAD;
        case Proto.S_ERROR: return C_BAD;
        default: return C_DIM;
    }
}

static string OrderText(Row r)
{
    string o = r.Queued ? "DOCK (queue)" : Proto.OrderName(r.Dir) + (r.Acked ? "" : " ...");
    return (r.Managed ? "A " : "") + o;
}

static string StateText(Row r)
{
    if (r.LostS >= 0) return "LOST " + Util.Age(r.LostS);
    return Proto.StateName(r.State) + ((r.Flags & Proto.F_ENGAGED) != 0 && r.State != Proto.S_ENGAGED ? " +W" : "");
}

static Color AlertColor(int a) { return a == Proto.ALERT_RED ? C_BAD : a == Proto.ALERT_YELLOW ? C_WARN : C_GOOD; }

// Alert badge at the top-right of a screen
void AlertBadge(ref MySpriteDrawFrame f, float right, float top, float k)
{
    string t = Proto.AlertName(hAlert) + (hAlertForced ? "*" : "") + (hSafe ? " SAFE" : "");
    float w = (hSafe ? 120 : 76) * k, h = 24 * k;
    Box(ref f, right - w, top, w, h, AlertColor(hAlert));
    Txt(ref f, t, right - w / 2, top + 3 * k, 0.5f * k, C_BG, TextAlignment.CENTER);
}

void Prepare(IMyTextSurface s)
{
    if (s.ContentType != ContentType.SCRIPT) s.ContentType = ContentType.SCRIPT;
    if (s.Script != "") s.Script = "";
    if (s.ScriptBackgroundColor != C_BG) s.ScriptBackgroundColor = C_BG;
}

// Cockpit version: callsign / state / batt / hull, one action selector, last event.
void DrawFleetCompact(IMyTextSurface s)
{
    Vector2 sz = s.SurfaceSize, o = (s.TextureSize - sz) / 2f;
    float W = sz.X, H = sz.Y, k = Scale(W, H, 360, 230), x0 = o.X, y0 = o.Y;
    var f = s.DrawFrame();
    if (flip) f.Add(new MySprite());
    Box(ref f, x0, y0, W, H, C_BG);
    bool linked = Linked();
    Box(ref f, x0, y0, W, 40 * k, C_PANEL);
    Box(ref f, x0, y0 + 38 * k, W, 2 * k, C_ACC);
    Txt(ref f, linked ? "FLEET  " + hOnline + "/" + rows.Count : "FLEET: NO LINK", x0 + 8 * k, y0 + 6 * k, 0.75f * k, linked ? C_ACC : C_BAD);
    if (linked) AlertBadge(ref f, x0 + W - 6 * k, y0 + 8 * k, k);

    float[] cx = { x0 + 0.04f * W, x0 + 0.36f * W, x0 + 0.64f * W, x0 + 0.84f * W };
    float rowH = 26 * k, rowsTop = y0 + 46 * k, footer = 62 * k, ts = 0.6f * k;
    int maxRows = Math.Max(1, (int)((y0 + H - footer - rowsTop) / rowH));
    int total = rows.Count + 1, cur = Cursor();
    if (cur < scroll) scroll = cur;
    if (cur >= scroll + maxRows) scroll = cur - maxRows + 1;
    scroll = Math.Max(0, Math.Min(scroll, total - maxRows));
    for (int i = scroll; i < Math.Min(total, scroll + maxRows); i++)
    {
        float ry = rowsTop + (i - scroll) * rowH, ty = ry + 2 * k;
        if (i == cur) Box(ref f, x0 + 2 * k, ry, W - 4 * k, rowH - 2 * k, C_SEL);
        if (i == 0) { Txt(ref f, "ALL DRONES", cx[0], ty, ts, C_ACC); continue; }
        var r = rows[i - 1];
        Color sc = StateColor(r);
        Txt(ref f, r.Call, cx[0], ty, ts, C_TXT);
        Txt(ref f, StateText(r), cx[1], ty, ts, sc);
        Bar(ref f, cx[2], ry + 8 * k, 0.17f * W, 9 * k, r.Batt, LevelColor(r.Batt));
        Bar(ref f, cx[3], ry + 8 * k, 0.13f * W, 9 * k, r.Integ, LevelColor(r.Integ));
    }

    // one selector instead of two rows of buttons: < ACTION >  target
    float ay = y0 + H - footer + 4 * k;
    Box(ref f, x0 + 4 * k, ay, W - 8 * k, 30 * k, C_ACC);
    string tgt = action < TargetedActions ? "  " + (selCall.Length > 0 ? selCall : "ALL") : "";
    Txt(ref f, "<  " + Actions[action] + tgt + "  >", x0 + W / 2, ay + 3 * k, 0.65f * k, C_BG, TextAlignment.CENTER);
    if (evts.Count > 0) Txt(ref f, evts[evts.Count - 1], x0 + 8 * k, y0 + H - 24 * k, 0.5f * k, C_DIM);
    f.Dispose();
}

void DrawFleet(IMyTextSurface s)
{
    Prepare(s);
    if (cpt) { DrawFleetCompact(s); return; }
    Vector2 sz = s.SurfaceSize;
    Vector2 o = (s.TextureSize - sz) / 2f;
    float W = sz.X, H = sz.Y, k = Math.Min(W, H) / 512f;
    float x0 = o.X, y0 = o.Y;

    var f = s.DrawFrame();
    if (flip) f.Add(new MySprite());   // forces a resend so late-joining clients don't keep a stale cached screen
    Box(ref f, x0, y0, W, H, C_BG);

    // ---------------- header
    bool linked = Linked();
    Box(ref f, x0, y0, W, 64 * k, C_PANEL);
    Box(ref f, x0, y0 + 62 * k, W, 2 * k, C_ACC);
    Txt(ref f, "FLEET COMMAND", x0 + 10 * k, y0 + 6 * k, 0.8f * k, C_ACC);
    if (linked) AlertBadge(ref f, x0 + W - 8 * k, y0 + 8 * k, k);
    string info = linked
        ? Proto.ModeName(hMode) + (hCfgMode == Proto.MODE_AUTO ? " (auto)" : "") + "  " + hForm + " " + hSpacing + "m  DOCKS " + hFree + "/" + hDocks
          + "  DEF " + (hDefense ? "ON" : "OFF") + "  RTB " + (hAutoRtb ? "AUTO" : "MAN")
        : "NO LINK TO HUB CORE";
    Txt(ref f, info, x0 + 10 * k, y0 + 38 * k, 0.5f * k, linked ? C_TXT : C_BAD);

    if (linked && hJump != Proto.JUMP_NONE)
    {
        bool ready = hJump == Proto.JUMP_READY;
        int docked = 0; foreach (var r in rows) if (r.LostS < 0 && r.State == Proto.S_DOCKED) docked++;
        int live = 0; foreach (var r in rows) if (r.LostS < 0) live++;
        Box(ref f, x0 + W * 0.25f, y0 + 66 * k, W * 0.5f, 22 * k, ready ? C_GOOD : C_WARN);
        Txt(ref f, ready ? "JUMP READY" : "JUMP PREP  " + docked + "/" + live + " docked", x0 + W / 2, y0 + 68 * k, 0.5f * k, C_BG, TextAlignment.CENTER);
    }

    // ---------------- column headings
    float[] cx = { x0 + 0.04f * W, x0 + 0.30f * W, x0 + 0.47f * W, x0 + 0.63f * W, x0 + 0.80f * W, x0 + 0.97f * W };
    float colY = y0 + 72 * k, hs = 0.4f * k;
    Txt(ref f, "CALLSIGN", cx[0], colY, hs, C_DIM); Txt(ref f, "STATE", cx[1], colY, hs, C_DIM);
    Txt(ref f, "ORDER", cx[2], colY, hs, C_DIM); Txt(ref f, "BATT", cx[3], colY, hs, C_DIM);
    Txt(ref f, "HULL", cx[4], colY, hs, C_DIM); Txt(ref f, "DIST", cx[5], colY, hs, C_DIM, TextAlignment.RIGHT);

    // ---------------- rows
    float rowH = 28 * k, rowsTop = colY + 20 * k, footer = 208 * k;
    int maxRows = Math.Max(1, (int)((y0 + H - footer - rowsTop) / rowH));
    int total = rows.Count + 1, cur = Cursor();
    if (cur < scroll) scroll = cur;
    if (cur >= scroll + maxRows) scroll = cur - maxRows + 1;
    scroll = Math.Max(0, Math.Min(scroll, total - maxRows));
    float ts = 0.52f * k;
    for (int i = scroll; i < Math.Min(total, scroll + maxRows); i++)
    {
        float ry = rowsTop + (i - scroll) * rowH, ty = ry + 4 * k;
        if (i == cur) Box(ref f, x0 + 4 * k, ry, W - 8 * k, rowH - 2 * k, C_SEL);
        if (i == 0)
        {
            Txt(ref f, "ALL DRONES", cx[0], ty, ts, C_ACC);
            Txt(ref f, hOnline + " online / " + rows.Count + " known", cx[1], ty, ts, C_TXT);
            continue;
        }
        var r = rows[i - 1];
        Color sc = StateColor(r);
        Box(ref f, x0 + 8 * k, ry + 5 * k, 4 * k, rowH - 12 * k, sc);
        Txt(ref f, r.Call, cx[0], ty, ts, C_TXT);
        Txt(ref f, StateText(r), cx[1], ty, ts, sc);
        Txt(ref f, OrderText(r), cx[2], ty, ts, r.Unresp ? C_BAD : r.Acked ? C_DIM : C_WARN);
        Bar(ref f, cx[3], ry + 9 * k, 0.14f * W, 9 * k, r.Batt, LevelColor(r.Batt));
        Bar(ref f, cx[4], ry + 9 * k, 0.10f * W, 9 * k, r.Integ, LevelColor(r.Integ));
        Txt(ref f, Util.Dist(r.Dist), cx[5], ty, ts, C_TXT, TextAlignment.RIGHT);
    }
    if (scroll > 0) Txt(ref f, "more above", x0 + W - 10 * k, colY, hs, C_DIM, TextAlignment.RIGHT);
    if (scroll + maxRows < total) Txt(ref f, "more below", x0 + W - 10 * k, rowsTop + maxRows * rowH - 4 * k, hs, C_DIM, TextAlignment.RIGHT);

    // ---------------- detail panel
    float py = y0 + H - footer;
    Box(ref f, x0, py, W, 2 * k, C_PANEL);
    float ds = 0.46f * k;
    if (cur == 0)
    {
        int esc = 0, hold = 0, dock = 0, lost = 0, other = 0;
        foreach (var r in rows)
        {
            if (r.LostS >= 0) lost++;
            else if (r.State == Proto.S_ESCORT || r.State == Proto.S_PATROL || r.State == Proto.S_ENGAGED) esc++;
            else if (r.State == Proto.S_HOLD || r.State == Proto.S_GOTO) hold++;
            else if (r.State == Proto.S_DOCKED) dock++;
            else other++;
        }
        Txt(ref f, "OUT " + esc + "   HOLD/GOTO " + hold + "   DOCKED " + dock + "   OTHER " + other + "   LOST " + lost, x0 + 10 * k, py + 8 * k, ds, C_TXT);
        Txt(ref f, "Waypoint: " + (hWp ? "set" : "none") + "    Core " + Util.Num(hPerfUs / 1000.0) + " ms", x0 + 10 * k, py + 30 * k, ds, C_DIM);
    }
    else
    {
        var r = rows[cur - 1];
        Txt(ref f, r.Call + " [" + r.Role + "]   BATT " + Util.PctM(r.Batt) + "  AMMO " + Util.PctM(r.Ammo) + "  HULL " + Util.PctM(r.Integ) + "  H2 " + Util.PctM(r.H2),
            x0 + 10 * k, py + 8 * k, ds, C_TXT);
        string ackTxt = r.Unresp ? "NOT ACKNOWLEDGING" : r.Acked ? "order acknowledged" : "awaiting ack";
        string flags = ((r.Flags & Proto.F_WANT_RTB) != 0 ? "  LOW POWER" : "") + ((r.Flags & Proto.F_NO_THRUST) != 0 ? "  NO THRUST" : "");
        Txt(ref f, "Rel speed " + r.Speed + " m/s   " + ackTxt + flags, x0 + 10 * k, py + 30 * k, ds, r.Unresp ? C_BAD : C_DIM);
    }

    // ---------------- action bar (row 1: selected drone, row 2: fleet-wide)
    float ah = 30 * k;
    Txt(ref f, "TARGET: " + (selCall.Length > 0 ? selCall : "ALL") + "   (up/down select, left/right action, apply)", x0 + 10 * k, py + 54 * k, 0.4f * k, C_ACC);
    for (int i = 0; i < Actions.Length; i++)
    {
        bool row2 = i >= TargetedActions;
        int n = row2 ? Actions.Length - TargetedActions : TargetedActions, j = row2 ? i - TargetedActions : i;
        float aw = (W - 8 * k) / n, ax = x0 + 4 * k + j * aw, ay = y0 + H - (row2 ? 90 : 126) * k;
        bool on = i == action;
        Box(ref f, ax + 2 * k, ay, aw - 4 * k, ah, on ? C_ACC : (row2 ? C_TRACK : C_PANEL));
        Txt(ref f, Actions[i], ax + aw / 2, ay + 7 * k, 0.38f * k, on ? C_BG : C_TXT, TextAlignment.CENTER);
    }

    // ---------------- event log
    float ey = y0 + H - 52 * k;
    for (int i = 0; i < 2 && i < evts.Count; i++)
        Txt(ref f, evts[evts.Count - 1 - i], x0 + 10 * k, ey + i * 22 * k, 0.42f * k, i == 0 ? C_TXT : C_DIM);

    f.Dispose();
}

// ============================================================================
// Tactical map: top-down in the hub frame (hub forward = up on screen).
// Elevation is drawn as a stem from the ground point (pseudo-3D radar).
// ============================================================================
static double NiceRange(double v)
{
    double p = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, v))));
    foreach (var m in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }) if (m * p >= v) return m * p;
    return 10 * p;
}

void DrawMap(IMyTextSurface s)
{
    Prepare(s);
    Vector2 sz = s.SurfaceSize;
    Vector2 o = (s.TextureSize - sz) / 2f;
    float W = sz.X, H = sz.Y, k = Scale(W, H, 380, 260);
    float x0 = o.X, y0 = o.Y;
    bool linked = Linked();
    Color ac = linked ? AlertColor(hAlert) : C_DIM;

    var f = s.DrawFrame();
    if (flip) f.Add(new MySprite());
    Box(ref f, x0, y0, W, H, C_BG);
    float bt = 3 * k;   // alert border
    Box(ref f, x0, y0, W, bt, ac); Box(ref f, x0, y0 + H - bt, W, bt, ac);
    Box(ref f, x0, y0, bt, H, ac); Box(ref f, x0 + W - bt, y0, bt, H, ac);

    Txt(ref f, "TACTICAL  " + (linked ? Proto.ModeName(hMode) : "NO LINK"), x0 + 12 * k, y0 + 10 * k, 0.6f * k, C_ACC);
    if (linked) AlertBadge(ref f, x0 + W - 12 * k, y0 + 10 * k, k);

    float cx = x0 + W / 2, cy = y0 + H / 2 + 12 * k, R = Math.Min(W, H - 90 * k) / 2 - 16 * k;
    double maxD = 100;
    foreach (var r in rows) if (r.LostS < 0) maxD = Math.Max(maxD, Math.Sqrt((double)r.LX * r.LX + (double)r.LZ * r.LZ));
    double range = NiceRange(maxD * 1.15);
    float sc = (float)(R / range);

    // rings + cross
    f.Add(new MySprite(SpriteType.TEXTURE, "CircleHollow", new Vector2(cx, cy), new Vector2(2 * R, 2 * R), C_TRACK));
    f.Add(new MySprite(SpriteType.TEXTURE, "CircleHollow", new Vector2(cx, cy), new Vector2(R, R), C_TRACK));
    Box(ref f, cx - R, cy - 0.5f * k, 2 * R, 1 * k, C_TRACK);
    Box(ref f, cx - 0.5f * k, cy - R, 1 * k, 2 * R, C_TRACK);
    Txt(ref f, Util.Dist(range), cx + R * 0.71f, cy - R * 0.71f - 14 * k, 0.4f * k, C_DIM);
    Txt(ref f, Util.Dist(range / 2), cx + R * 0.355f, cy - R * 0.355f - 14 * k, 0.4f * k, C_DIM);

    // hub (triangle points to hub forward)
    f.Add(new MySprite(SpriteType.TEXTURE, "Triangle", new Vector2(cx, cy), new Vector2(16 * k, 20 * k), C_ACC));

    int engaged = 0, outN = 0, docked = 0, lost = 0;
    bool labels = rows.Count <= 8;
    foreach (var r in rows)
    {
        if (r.LostS >= 0) { lost++; continue; }
        if (r.State == Proto.S_DOCKED) { docked++; continue; }
        outN++;
        if ((r.Flags & Proto.F_ENGAGED) != 0) engaged++;
        float px = r.LX * sc, py = r.LZ * sc;
        float d = (float)Math.Sqrt(px * px + py * py);
        if (d > R) { px *= R / d; py *= R / d; }
        px += cx; py += cy;
        float elev = Math.Max(-R / 2, Math.Min(R / 2, r.LY * sc * 0.5f));
        Color c = StateColor(r);
        if (Math.Abs(elev) > 2 * k) Box(ref f, px - 1 * k, Math.Min(py, py - elev), 2 * k, Math.Abs(elev), C_DIM);
        f.Add(new MySprite(SpriteType.TEXTURE, "Circle", new Vector2(px, py), new Vector2(4 * k, 4 * k), C_DIM));
        Vector2 b = new Vector2(px, py - elev);
        f.Add(new MySprite(SpriteType.TEXTURE, "Circle", b, new Vector2(10 * k, 10 * k), c));
        if ((r.Flags & Proto.F_ENGAGED) != 0) f.Add(new MySprite(SpriteType.TEXTURE, "CircleHollow", b, new Vector2(18 * k, 18 * k), C_BAD));
        bool sel = r.Call == selCall;
        if (sel) f.Add(new MySprite(SpriteType.TEXTURE, "CircleHollow", b, new Vector2(24 * k, 24 * k), C_TXT));
        if (labels || sel) Txt(ref f, r.Call, b.X + 9 * k, b.Y - 8 * k, 0.38f * k, sel ? C_TXT : C_DIM);
    }

    Txt(ref f, "OUT " + outN + "   ENGAGED " + engaged + "   DOCKED " + docked + "   LOST " + lost + "   DEF " + (hDefense ? "ON" : "OFF"),
        x0 + 12 * k, y0 + H - 30 * k, 0.45f * k, C_TXT);
    f.Dispose();
}

// ============================================================================
// Defense menu: arrow-key control of the defense manager.
// up/down = setting, left/right = value, apply = send ("def up" etc. on the Hub UI PB).
// ============================================================================
void DrawDefense(IMyTextSurface s)
{
    Prepare(s);
    Vector2 sz = s.SurfaceSize, o = (s.TextureSize - sz) / 2f;
    float W = sz.X, H = sz.Y, k = Scale(W, H, 360, 296), x0 = o.X, y0 = o.Y;
    bool linked = Linked();
    var f = s.DrawFrame();
    if (flip) f.Add(new MySprite());
    Box(ref f, x0, y0, W, H, C_BG);
    Box(ref f, x0, y0, W, 40 * k, C_PANEL);
    Box(ref f, x0, y0 + 38 * k, W, 2 * k, C_ACC);
    Txt(ref f, linked ? "DEFENSE" : "DEFENSE: NO LINK", x0 + 8 * k, y0 + 6 * k, 0.75f * k, linked ? C_ACC : C_BAD);
    if (linked) AlertBadge(ref f, x0 + W - 6 * k, y0 + 8 * k, k);

    float rowH = 24 * k, y = y0 + 45 * k, ts = 0.56f * k;
    for (int i = 0; i < DefNames.Length; i++, y += rowH)
    {
        bool sel = i == defItem;
        if (sel) Box(ref f, x0 + 2 * k, y, W - 4 * k, rowH - 2 * k, C_SEL);
        bool action = DefOpts[i].Length == 1;
        Color nc = action ? (i == 7 ? C_BAD : C_WARN) : i <= 3 ? C_ORANGE : C_TXT;
        Txt(ref f, DefNames[i], x0 + 10 * k, y + 2 * k, ts, nc);
        int now = DefCurrent(i);
        string val;
        if (action) val = sel ? "[ APPLY ]" : "";
        else
        {
            int shown = sel ? defOpt : now;
            val = sel ? "<  " + DefOpts[i][shown] + "  >" : DefOpts[i][shown];
            if (i == 4 && shown == 0) val = sel ? "<  AUTO  >" : "AUTO (" + Proto.AlertName(hAlert) + ")";
            if (i == 0 && !sel) val = (hSad & 1) != 0 ? "SEARCHING" : "OFF";
            if (i == 2 && !sel) val = (hSad & 4) != 0 ? "ON RING" : (hSad & 2) != 0 ? "TARGET SET" : "NO TARGET";
        }
        Txt(ref f, val, x0 + W * 0.56f, y + 2 * k, ts, sel ? C_ACC : C_DIM, TextAlignment.CENTER);
        if (sel && !action && i != 2 && defOpt != now) Txt(ref f, "now " + DefOpts[i][now], x0 + W - 8 * k, y + 5 * k, 0.45f * k, C_DIM, TextAlignment.RIGHT);
    }

    int outN = 0, dock = 0;
    foreach (var r in rows) { if (r.LostS >= 0) continue; if (r.State == Proto.S_DOCKED) dock++; else outN++; }
    float fy = y0 + H - 26 * k;
    Box(ref f, x0, fy - 4 * k, W, 2 * k, C_PANEL);
    bool fresh = T - defMsgT < 4;
    Txt(ref f, fresh ? defMsg : hTgt.Length > 0 ? "TGT " + hTgt : "OUT " + outN + "   DOCKED " + dock + "   no target", x0 + 8 * k, fy, 0.48f * k, fresh ? C_GOOD : hTgt.Length > 0 ? C_ORANGE : C_DIM);
    f.Dispose();
}

// ============================================================================
// Hangar: docks, factory, repair/rearm/charging, jump interlock
// ============================================================================
void DrawHangar(IMyTextSurface s)
{
    Prepare(s);
    Vector2 sz = s.SurfaceSize;
    Vector2 o = (s.TextureSize - sz) / 2f;
    float W = sz.X, H = sz.Y, k = Scale(W, H, 380, 300);
    float x0 = o.X, y0 = o.Y;
    bool linked = Linked();

    var f = s.DrawFrame();
    if (flip) f.Add(new MySprite());
    Box(ref f, x0, y0, W, H, C_BG);
    Box(ref f, x0, y0, W, 44 * k, C_PANEL);
    Box(ref f, x0, y0 + 42 * k, W, 2 * k, C_ACC);
    Txt(ref f, "HANGAR", x0 + 10 * k, y0 + 7 * k, 0.8f * k, C_ACC);
    if (linked) AlertBadge(ref f, x0 + W - 8 * k, y0 + 10 * k, k);
    if (!linked) { Txt(ref f, "NO LINK TO HUB CORE", x0 + W / 2, y0 + H / 2, 0.7f * k, C_BAD, TextAlignment.CENTER); f.Dispose(); return; }

    float y = y0 + 54 * k, ts = 0.48f * k;

    // jump interlock
    if (hJump != Proto.JUMP_NONE)
    {
        bool ready = hJump == Proto.JUMP_READY;
        Box(ref f, x0 + 8 * k, y, W - 16 * k, 26 * k, ready ? C_GOOD : C_WARN);
        Txt(ref f, ready ? "JUMP READY: drives unlocked" : "JUMP PREP: drives locked until all drones dock", x0 + W / 2, y + 4 * k, 0.48f * k, C_BG, TextAlignment.CENTER);
        y += 34 * k;
    }

    // factory
    if (facOk || pStock > 0)
    {
        Txt(ref f, "FACTORY", x0 + 10 * k, y, 0.42f * k, C_DIM);
        Txt(ref f, "stock " + pLive + "/" + pStock + "   built " + facBuilt, x0 + W - 10 * k, y, 0.42f * k, C_DIM, TextAlignment.RIGHT);
        y += 20 * k;
        string fs = facOk ? Proto.FactoryName(facSt) : "NO FACTORY LINK";
        Color fc = !facOk || facSt == Proto.FAC_ERROR ? C_BAD : facSt == Proto.FAC_STALLED ? C_WARN : facSt == Proto.FAC_IDLE ? C_DIM : C_GOOD;
        Txt(ref f, fs, x0 + 10 * k, y, ts, fc);
        if (facProg >= 0) Bar(ref f, x0 + W * 0.42f, y + 6 * k, W * 0.5f, 10 * k, facProg, C_ACC);
        y += 24 * k;
        if (facOk && facMsg.Length > 0) { Txt(ref f, facMsg, x0 + 10 * k, y, 0.4f * k, C_DIM); y += 20 * k; }
        y += 6 * k;
    }

    // docks
    Txt(ref f, "DOCKS", x0 + 10 * k, y, 0.42f * k, C_DIM);
    Txt(ref f, "BATT", x0 + W * 0.62f, y, 0.42f * k, C_DIM);
    Txt(ref f, "HULL", x0 + W * 0.82f, y, 0.42f * k, C_DIM);
    y += 20 * k;
    float rowH = 26 * k, bottom = y0 + H - 70 * k;
    int shown = 0;
    foreach (var d in dockRows)
    {
        if (y + rowH > bottom) { Txt(ref f, "+" + (dockRows.Count - shown) + " more", x0 + 10 * k, y, 0.4f * k, C_DIM); break; }
        Color c = d.State == 2 ? C_DOCK : d.State == 1 ? C_WARN : C_DIM;
        Box(ref f, x0 + 8 * k, y + 4 * k, 4 * k, rowH - 10 * k, c);
        string label = cpt ? d.Name.Replace("Connector", "").Trim() : d.Name;   // "[Dock] L1" on a cockpit screen
        int lmax = cpt ? 12 : 22;
        if (label.Length > lmax) label = label.Substring(0, lmax);
        Txt(ref f, label, x0 + 18 * k, y + 2 * k, 0.44f * k, C_TXT);
        Txt(ref f, d.State == 2 ? (d.Call.Length > 0 ? d.Call : "occupied") : d.State == 1 ? d.Call + " inbound" : "free", x0 + W * 0.40f, y + 2 * k, 0.44f * k, c);
        if (d.State == 2 && d.Call.Length > 0)
        {
            Bar(ref f, x0 + W * 0.62f, y + 8 * k, W * 0.16f, 9 * k, d.Batt, LevelColor(d.Batt));
            Bar(ref f, x0 + W * 0.82f, y + 8 * k, W * 0.12f, 9 * k, d.Integ, LevelColor(d.Integ));
        }
        y += rowH; shown++;
    }
    if (dockRows.Count == 0) Txt(ref f, "no connectors tagged [Dock]", x0 + 18 * k, y, 0.44f * k, C_DIM);

    // logistics footer
    float fy = y0 + H - 62 * k;
    Box(ref f, x0, fy - 6 * k, W, 2 * k, C_PANEL);
    string rep = pRepairN > 0 ? (pRepair ? "REPAIR ON" : "repair idle") : "no [Repair]";
    string rea = pRearmN > 0 ? (pRearm ? "REARM ON" : "rearm idle") : "no [Rearm]";
    Txt(ref f, rep + "    " + rea, x0 + 10 * k, fy, ts, pRepair || pRearm ? C_GOOD : C_DIM);
    string chg = pCharging < 0 ? "charging: unrestricted" : "charging " + pCharging + (pMaxC > 0 ? "/" + pMaxC : "") + " drones";
    string res = pReserve >= 0 ? "   hub battery " + Util.PctM(pReserve) : "";
    Txt(ref f, chg + res, x0 + 10 * k, fy + 24 * k, ts, C_TXT);
    f.Dispose();
}
