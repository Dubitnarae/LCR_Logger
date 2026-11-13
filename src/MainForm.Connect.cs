using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Media;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Charting = System.Windows.Forms.DataVisualization.Charting;

namespace LcrLogger
{
    partial class MainForm
    {
        // ---------------- 연결 ----------------

        // 연결 방식에 맞는 칸만 보이게 하고, 방식마다 따로 기억한 주소를 넣음
        void OnIfChanged()
        {
            if (ifShown == 1) lanAddr = txtAddr.Text.Trim();
            else if (ifShown == 2) gpibAddr = txtAddr.Text.Trim();
            ifShown = cbIf.SelectedIndex;
            bool com = ifShown == 0;
            foreach (Control c in new Control[] { lblPort, cbPort, btnRefresh, lblBaud, cbBaud }) ShowField(c, com);
            ShowField(lblAddr, !com);
            ShowField(txtAddr, !com);
            lblAddr.Text = ifShown == 2 ? "GPIB 주소(0~30)" : "IP:포트";
            if (!com) txtAddr.Text = ifShown == 1 ? lanAddr : gpibAddr;
            chkReconnect.Text = com ? "USB 자동 재연결" : "자동 재연결";
        }

        static void ShowField(Control c, bool visible)
        {
            (c.Parent is FieldBox ? c.Parent : c).Visible = visible;
        }

        // LAN "IP[:포트]"(포트 생략 시 3500), GPIB "주소" → 연결을 여는 함수. 입력이 틀리면 바로 알림
        internal static Func<IMeter> NetOpener(int kind, string text)
        {
            text = (text ?? "").Trim();
            if (kind == 2)
            {
                int addr;
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out addr) || addr < 0 || addr > 30)
                    throw new InvalidOperationException("GPIB 주소 '" + text + "'가 올바르지 않습니다. 계측기에 설정된 0~30 사이 번호를 넣으세요.");
                return () => new GpibMeter(addr);
            }
            string host = text;
            int port = 3500;
            int colon = text.LastIndexOf(':');
            if (colon >= 0)
            {
                host = text.Substring(0, colon).Trim();
                if (!int.TryParse(text.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                    throw new InvalidOperationException("포트 번호 '" + text.Substring(colon + 1) + "'가 올바르지 않습니다 (예: 192.168.0.1:3500).");
            }
            if (host.Length == 0) throw new InvalidOperationException("계측기의 IP 주소를 넣으세요 (예: 192.168.0.1:3500).");
            return () => new LanMeter(host, port);
        }

        void RefreshPorts()
        {
            cbPort.Items.Clear();
            foreach (var p in PortItem.Enumerate()) cbPort.Items.Add(p);
            if (cbPort.Items.Count == 0) return;
            cbPort.SelectedIndex = 0;
            // HIOKI 장비가 안 보일 때만 지난번에 쓴 포트를 다시 선택
            string last = settings.Get("Port", "");
            if (!((PortItem)cbPort.Items[0]).Hioki)
            {
                for (int i = 0; i < cbPort.Items.Count; i++)
                    if (((PortItem)cbPort.Items[i]).Port == last) cbPort.SelectedIndex = i;
            }
        }

        async void OnConnect(object sender, EventArgs e)
        {
            if (waitingReconnect)
            {
                CancelReconnect();
                Log("자동 재연결을 취소했습니다.");
                UpdateUi();
                return;
            }
            if (meter != null)
            {
                autoRunning = false;
                CloseMeter(true);
                Log("연결 해제.");
                UpdateUi();
                return;
            }

            IMeter m = null;
            busy = true;
            UpdateUi();
            try
            {
                netOpener = null;
                if (chkDemo.Checked)
                {
                    m = new DemoMeter();
                }
                else if (cbIf.SelectedIndex != 0)
                {
                    var open = NetOpener(cbIf.SelectedIndex, txtAddr.Text);
                    m = await Task.Run(open);
                    netOpener = open;
                }
                else
                {
                    var port = cbPort.SelectedItem as PortItem;
                    if (port == null) throw new InvalidOperationException("COM 포트를 선택하세요. 계측기가 보이지 않으면 USB 드라이버를 확인하세요.");
                    int baud = int.Parse(cbBaud.Text, CultureInfo.InvariantCulture);
                    m = await Task.Run(() => (IMeter)new SerialMeter(port.Port, baud));
                }

                var newFreqs = new List<double>();
                if (chkMulti.Checked)
                {
                    foreach (TextBox t in txtFreq)
                    {
                        if (t.Text.Trim().Length == 0) continue;
                        double hz;
                        if (!Fmt.TryParseFrequency(t.Text, out hz))
                            throw new InvalidOperationException("주파수 '" + t.Text + "'를 읽을 수 없습니다. 4 Hz ~ 8 MHz 범위로 입력하세요 (예: 1k, 100k, 1M).");
                        newFreqs.Add(hz);
                    }
                    if (newFreqs.Count == 0) throw new InvalidOperationException("다중 주파수 측정에 사용할 주파수를 하나 이상 입력하세요.");
                }

                MeasCond cond = ReadCond();
                savedCond = null;
                var hs = await Handshake(m, cond, true, false);
                string idn = hs.Key;
                List<string> names = hs.Value;

                meter = m;
                connectCond = cond;
                pars = names.Where(p => p.Length > 0 && p != "OFF").ToList();
                freqs = newFreqs;
                freqLabels = freqs.Select(Fmt.FrequencyLabel).ToList();
                lblModel.Text = idn;
                lblModel.ForeColor = Theme.Green;
                Log("연결됨: " + idn);
                Log("측정 파라미터: " + string.Join(", ", names));
                if (freqs.Count > 0)
                    Log("다중 주파수 측정: " + string.Join(", ", freqLabels));
                if (!pars.Any(Fmt.IsInductance))
                    Log("※ 인덕턴스(Ls/Lp)가 파라미터에 없습니다. 계측기의 PAR 설정을 확인하세요.");
                StartNewTable(null);
            }
            catch (Exception ex)
            {
                if (m != null) m.Dispose();
                Log("연결 실패: " + ex.Message);
                MessageBox.Show(this, ex.Message, "연결 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                busy = false;
                UpdateUi();
            }
        }

        // 화면의 측정 조건을 읽음. 잘못된 입력이면 연결 전에 알려 줌
        // 그 밖의 항목 칸의 값 (빈칸·변경 안 함이면 "")
        string GetExtra(string key)
        {
            var cb = condExtra[key] as ComboBox;
            return cb != null ? (cb.SelectedIndex <= 0 ? "" : Fmt.ParToken((string)cb.SelectedItem)) : condExtra[key].Text.Trim();
        }

        void SetExtra(string key, string value)
        {
            var cb = condExtra[key] as ComboBox;
            if (cb == null) { condExtra[key].Text = value; return; }
            int index = 0;
            for (int i = 1; i < cb.Items.Count; i++)
                if (Fmt.ParToken((string)cb.Items[i]) == value) index = i;
            cb.SelectedIndex = index;
        }

        MeasCond ReadCond()
        {
            var c = new MeasCond();
            foreach (string key in CondExtraKeys)
            {
                string v = GetExtra(key);
                if (v.Length == 0) continue;
                string name = MeterSettings.Items.First(i => i.Cmd == key).Name;
                double x;
                if (key == ":FREQ")
                {
                    if (!Fmt.TryParseFrequency(v, out x)) throw new InvalidOperationException("주파수 '" + v + "'를 읽을 수 없습니다. 4 Hz ~ 8 MHz 범위로 입력하세요 (예: 1k, 100k, 1M).");
                    v = x.ToString("F2", CultureInfo.InvariantCulture);
                }
                else if (key == ":AVER")
                {
                    int n;
                    if (!int.TryParse(v, out n) || n < 1 || n > 256) throw new InvalidOperationException("평균 횟수 '" + v + "'를 읽을 수 없습니다. 1 ~ 256으로 입력하세요 (1 = OFF).");
                    c.Average = n;
                    continue;
                }
                else if (!(condExtra[key] is ComboBox))
                {
                    if (!Fmt.TryParse(v, out x) || x < 0) throw new InvalidOperationException(name + " 값 '" + v + "'를 읽을 수 없습니다. 0 이상의 숫자로 입력하세요.");
                    v = x.ToString("G6", CultureInfo.InvariantCulture);
                }
                c.Extra.Add(new KeyValuePair<string, string>(key, v));
            }
            if (cbLevel.SelectedIndex > 0)
            {
                string[] modes = { null, "V", "CV", "CC" };
                string mode = modes[cbLevel.SelectedIndex];
                double v;
                bool cc = mode == "CC";
                if (!Fmt.TryParse(txtCv.Text.Trim(), out v) || (cc ? (v <= 0 || v > 100) : (v < 0.005 || v > 5)))
                    throw new InvalidOperationException((string)cbLevel.SelectedItem + " 값 '" + txtCv.Text + "'를 읽을 수 없습니다. "
                        + (cc ? "0 초과 100 mA 이하로 입력하세요 (예: 10)." : "0.005 ~ 5 V 범위로 입력하세요 (예: 1)."));
                c.Level = mode;
                c.LevelValue = cc ? v / 1000 : v;   // CC는 화면에서 mA, 계측기에는 A로 보냄
            }
            c.Pars[0] = "LS";   // 스프레드시트 입력 등이 PAR1의 Ls를 기준으로 하므로 고정
            for (int i = 0; i < cbPar.Length; i++)
                c.Pars[i + 1] = cbPar[i].SelectedIndex == 0 ? null : (string)cbPar[i].SelectedItem;
            var chosen = c.Pars.Where(p => p != null && p != "OFF").ToList();
            if (chosen.Distinct().Count() != chosen.Count)
                throw new InvalidOperationException("PAR1~PAR4에 같은 파라미터가 두 번 들어갔습니다. 서로 다르게 고르세요 (PAR1은 Ls 고정).");
            string[] speeds = { null, "FAST", "MEDium", "SLOW", "SLOW2" };
            c.Speed = speeds[cbSpeed.SelectedIndex];
            c.Bias = cbBias.SelectedIndex;
            if (c.Bias == 2)
            {
                double v;
                if (!Fmt.TryParse(txtBias.Text.Trim(), out v) || v < 0 || v > 2.5)
                    throw new InvalidOperationException("DC 바이어스 값 '" + txtBias.Text + "'를 읽을 수 없습니다. 0 ~ 2.5 V 범위로 입력하세요 (예: 1.5).");
                c.BiasVolts = v;
            }
            return c;
        }

        // 측정 조건을 계측기에 보냄. keepOriginal=true(자동 재연결)면 처음 읽어 둔 원래 값을 유지
        async Task ApplyConditions(IMeter m, MeasCond cond, bool keepOriginal)
        {
            if (cond == null || !cond.Any) return;
            if (!keepOriginal)
            {
                savedCond = await Io(() => cond.Read(m));
                PersistPending();   // 적용 도중 프로그램이 죽어도 다음 연결 때 되돌릴 수 있도록 먼저 파일에 남김
            }
            List<string> lines = await Io(() => cond.Apply(m));
            foreach (string line in lines) Log(line);
        }

        static List<string> ReadParNames(IMeter m)
        {
            var list = new List<string>();
            for (int i = 1; i <= 4; i++)
                list.Add(m.Query(":PAR" + i + "?").ToUpperInvariant());
            return list;
        }

        // CSV 측정조건 열에 남길 값을 계측기에서 읽음
        async Task UpdateCondText(IMeter m)
        {
            try { condText = await Io(() => MeasCond.Summary(m)); }
            catch (TimeoutException) { condText = "확인 불가"; }
            Log("측정 조건(계측기): " + condText);
        }

        // ---------------- 복원 못 한 측정 조건 (비정상 종료 대비) ----------------

        // 바꾸기 전 원래 값을 설정 파일에 저장. 정상적으로 복원하면 지움
        void PersistPending()
        {
            // 복소 투자율 측정 중이면 그 직전 값도 함께. 같은 항목은 연결 전 값(더 오래된 원래 값)이 우선
            Dictionary<string, string> s = savedCond;
            if (muSaved != null)
            {
                s = new Dictionary<string, string>(muSaved);
                if (savedCond != null) foreach (var kv in savedCond) s[kv.Key] = kv.Value;
            }
            settings.Set("PendingRestore", s == null ? "" : MeasCond.Serialize(s));
            settings.Set("PendingIdn", s == null ? "" : currentIdn ?? "");
            settings.Save();
        }

        // 지난번에 되돌리지 못한 값이 있으면 같은 계측기일 때만 되돌릴지 물음
        async Task OfferPendingRestore(IMeter local, string idn)
        {
            string pending = settings.Get("PendingRestore", "");
            if (pending.Length == 0) return;
            string pendingIdn = settings.Get("PendingIdn", "");
            settings.Set("PendingRestore", "");
            settings.Set("PendingIdn", "");
            settings.Save();
            if (pendingIdn != idn)
            {
                Log("이전에 복원하지 못한 측정 조건이 있지만 다른 계측기라 되돌리지 않습니다.");
                return;
            }
            Dictionary<string, string> s = MeasCond.Deserialize(pending);
            if (s.Count == 0) return;
            string list = string.Join("\n", s.Select(kv => "  " + kv.Key + " " + kv.Value));
            if (MessageBox.Show(this,
                    "지난번 연결을 정상적으로 끝내지 못해(프로그램 비정상 종료, USB 끊김 등) 계측기의 측정 조건을 원래대로 되돌리지 못했습니다.\n\n"
                    + "아래 원래 값으로 되돌린 뒤 연결할까요?\n\n" + list,
                    "측정 조건 복원", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Log("이전 측정 조건 복원을 건너뛰었습니다.");
                return;
            }
            await Io(() => { MeasCond.Restore(local, s); return true; });
            Log("이전에 복원하지 못한 측정 조건을 원래대로 되돌렸습니다.");
        }

        // ---------------- 연결 중 측정 조건 적용 ----------------

        // 처음 연결 전 값으로 한 번 되돌린 뒤 새 조건을 적용 (복원 기준은 계속 처음 연결 전 값)
        async void OnApplyCond(object sender, EventArgs e)
        {
            if (meter == null || busy || autoRunning) return;
            MeasCond cond;
            try { cond = ReadCond(); }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "측정 조건", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            IMeter m = meter;
            busy = true;
            UpdateUi();
            try
            {
                if (savedCond != null)
                {
                    var original = savedCond;
                    await Io(() => { MeasCond.Restore(m, original); return true; });
                }
                await ApplyConditions(m, cond, false);
                connectCond = cond;
                List<string> names = await Io(() => ReadParNames(m));
                await UpdateCondText(m);
                var newPars = names.Where(p => p.Length > 0 && p != "OFF").ToList();
                if (!newPars.SequenceEqual(pars))
                {
                    pars = newPars;
                    StartNewTable("측정 파라미터가 바뀌어 새 파일로 시작합니다: " + string.Join(", ", names));
                }
                Log("측정 조건을 적용했습니다.");
            }
            catch (Exception ex)
            {
                if (IsDisconnect(ex, m)) OnLost("USB 연결이 끊어졌습니다: " + ex.Message);
                else Log("측정 조건 적용 실패: " + ex.Message);
            }
            finally
            {
                busy = false;
                UpdateUi();
            }
        }

        // ---------------- 측정 조건 프리셋 ----------------

        static readonly string[] LevelModes = { "-", "V", "CV", "CC" };

        static string ItemToken(ComboBox cb)
        {
            return cb.SelectedIndex <= 0 ? "-" : (string)cb.SelectedItem;
        }

        static void SelectToken(ComboBox cb, Dictionary<string, string> d, string key)
        {
            string v;
            if (!d.TryGetValue(key, out v)) return;
            int index = v == "-" ? 0 : cb.Items.IndexOf(v);
            if (index >= 0) cb.SelectedIndex = index;
        }

        // 형식: "L=CV;LV=1.000;S=FAST;B=ON;BV=1.50;P2=Z;P3=-;P4=-" (콤보 번호 대신 값으로 저장해 목록이 바뀌어도 유지)
        string PresetText()
        {
            var parts = new List<string>
            {
                "L=" + LevelModes[Math.Max(0, cbLevel.SelectedIndex)],
                "LV=" + txtCv.Text.Trim().Replace(";", ""),
                "S=" + ItemToken(cbSpeed),
                "B=" + ItemToken(cbBias),
                "BV=" + txtBias.Text.Trim().Replace(";", "")
            };
            for (int i = 0; i < cbPar.Length; i++)
                parts.Add("P" + (i + 2) + "=" + ItemToken(cbPar[i]));
            foreach (string key in CondExtraKeys)
                parts.Add("X" + key + "=" + GetExtra(key).Replace(";", ""));
            return string.Join(";", parts);
        }

        void LoadPreset(string name)
        {
            if (name == null) return;
            string text = settings.Get(PresetPrefix + name, null);
            if (text == null) return;
            var d = new Dictionary<string, string>();
            foreach (string part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) d[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            string v;
            if (d.TryGetValue("L", out v) && Array.IndexOf(LevelModes, v) >= 0) cbLevel.SelectedIndex = Array.IndexOf(LevelModes, v);
            if (d.TryGetValue("LV", out v) && v.Length > 0) txtCv.Text = v;   // 종류를 바꾼 뒤에 넣어야 기본값으로 덮이지 않음
            SelectToken(cbSpeed, d, "S");
            SelectToken(cbBias, d, "B");
            if (d.TryGetValue("BV", out v) && v.Length > 0) txtBias.Text = v;
            for (int i = 0; i < cbPar.Length; i++)
                SelectToken(cbPar[i], d, "P" + (i + 2));
            foreach (string key in CondExtraKeys)
                SetExtra(key, d.TryGetValue("X" + key, out v) ? v : "");   // 1.22 이전 프리셋에는 없음 → 변경 안 함
            Log("프리셋 불러옴: " + name + (meter != null ? " - [연결 중 적용]을 눌러야 계측기에 반영됩니다." : ""));
        }

        void RefreshPresets(string select)
        {
            cbPreset.Items.Clear();
            foreach (string key in settings.KeysWithPrefix(PresetPrefix).OrderBy(k => k, StringComparer.CurrentCulture))
                cbPreset.Items.Add(key.Substring(PresetPrefix.Length));
            cbPreset.Text = select ?? "";
        }

        void OnPresetSave(object sender, EventArgs e)
        {
            string name = cbPreset.Text.Trim();
            if (name.Length == 0 || name.IndexOfAny(new[] { '=', '\r', '\n' }) >= 0)
            {
                MessageBox.Show(this, "프리셋 칸에 이름을 입력한 뒤 [프리셋 저장]을 누르세요.\n(이름에 '='는 쓸 수 없습니다)", "프리셋", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { ReadCond(); }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "프리셋", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (settings.Get(PresetPrefix + name, null) != null
                && MessageBox.Show(this, "프리셋 '" + name + "'이 이미 있습니다. 지금 설정으로 덮어쓸까요?", "프리셋", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            settings.Set(PresetPrefix + name, PresetText());
            settings.Save();
            RefreshPresets(name);
            Log("프리셋 저장: " + name);
        }

        void OnPresetDelete(object sender, EventArgs e)
        {
            string name = cbPreset.Text.Trim();
            if (name.Length == 0 || settings.Get(PresetPrefix + name, null) == null)
            {
                MessageBox.Show(this, "삭제할 프리셋을 목록에서 고르세요.", "프리셋", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "프리셋 '" + name + "'을 삭제할까요?", "프리셋", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            settings.Remove(PresetPrefix + name);
            settings.Save();
            RefreshPresets(null);
            Log("프리셋 삭제: " + name);
        }

        // 연결 확인 절차: *IDN?, LCR 모드 확인, 측정 파라미터 읽기. 결과는 (IDN, PAR1~4)
        // interactive=false(자동 재연결)이면 묻지 않고 LCR 모드로 바꿈 (끊기기 전에 LCR 모드로 쓰던 계측기)
        async Task<KeyValuePair<string, List<string>>> Handshake(IMeter local, MeasCond cond, bool interactive, bool keepOriginal)
        {
            string idn = await Io(() =>
            {
                string id = local.Query("*IDN?");
                local.Send(":HEAD OFF");
                return id;
            });
            Log("응답: " + idn);
            currentIdn = idn;

            // :PAR1? 등은 LCR 모드 전용이라 연속 측정(CONT) 모드에서는 응답하지 않음
            string mode = null;
            try
            {
                mode = await Io(() => local.Query(":MODE?").ToUpperInvariant());
            }
            catch (TimeoutException)
            {
                Log("측정 모드 확인(:MODE?) 응답 없음 - 계속 진행합니다.");
            }
            if (mode != null && mode != "LCR")
            {
                if (interactive)
                {
                    var answer = MessageBox.Show(this,
                        "계측기가 '" + mode + "' 모드입니다. 이 프로그램은 LCR 모드에서 측정합니다.\n\nLCR 모드로 바꿀까요?\n\n"
                        + "연속 측정 모드에 설정한 여러 주파수는 '다중 주파수 측정'에 입력하면 같은 방식으로 측정됩니다.",
                        "측정 모드", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes)
                        throw new InvalidOperationException("계측기가 LCR 모드가 아니어서 연결을 취소했습니다.");
                }
                await Io(() => { local.Send(":MODE LCR"); Thread.Sleep(500); return true; });
                Log("계측기를 LCR 모드로 바꿨습니다.");
            }

            try
            {
                if (interactive) await OfferPendingRestore(local, idn);
                // 측정 조건(PAR 포함)을 먼저 보낸 뒤 PAR를 읽어야 표 열이 설정과 맞음
                await ApplyConditions(local, cond, keepOriginal);
                var names = await Io(() => ReadParNames(local));
                await UpdateCondText(local);
                return new KeyValuePair<string, List<string>>(idn, names);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(ex.Message + "\n계측기 화면이 LCR 모드(일반 측정 화면)인지 확인하세요.");
            }
        }

        // restore=true(사용자가 해제/종료)면 바꿨던 측정 조건을 원래대로 되돌린 뒤 닫음. 통신이 끊긴 경우는 false
        void CloseMeter(bool restore = false)
        {
            if (meter == null) return;
            lock (ioLock)
            {
                if (restore && muSaved != null)
                {
                    // 스윕(복소 투자율·스윕 측정) 직전 값으로 먼저 되돌림 (실패하면 파일에 남은 값으로 다음 연결 때 묻습니다)
                    try { MeasCond.Restore(meter, muSaved); muSaved = null; PersistPending(); Log("스윕 측정 전 계측기 설정으로 되돌렸습니다."); }
                    catch (Exception ex) { Log("스윕 측정 전 설정 복원 실패: " + ex.Message); }
                }
                if (restore && savedCond != null && connectCond != null && !chkRestoreCond.Checked)
                {
                    savedCond = null;
                    PersistPending();
                    Log("측정 조건을 계측기에 그대로 둡니다 (연결 해제 때 되돌리기 꺼짐).");
                }
                if (restore && savedCond != null && connectCond != null)
                {
                    try
                    {
                        MeasCond.Restore(meter, savedCond);
                        savedCond = null;
                        PersistPending();   // 복원했으니 파일에 남긴 원래 값도 지움
                        Log("측정 조건을 원래대로 복원했습니다.");
                    }
                    catch (Exception ex)
                    {
                        // 파일에 남은 원래 값으로 다음 연결 때 되돌릴지 물음
                        Log("측정 조건 복원 실패 (다음 연결 때 다시 묻습니다): " + ex.Message);
                    }
                    savedCond = null;
                }
                try { meter.Dispose(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                meter = null;
            }
            lblModel.Text = "연결 안 됨";
            lblModel.ForeColor = Theme.Secondary;
        }

        // ---------------- USB 자동 재연결 ----------------

        // 통신 오류가 케이블 분리 때문인지 판단 (응답 없음은 포트가 사라졌을 때만 분리로 봄)
        internal static bool IsDisconnect(Exception ex, IMeter m)
        {
            if (ex is IOException || ex is UnauthorizedAccessException) return true;
            var serial = m as SerialMeter;
            if (serial == null) return false;
            if (ex is InvalidOperationException && !serial.IsOpen) return true;
            return !PortExists(serial.PortName);
        }

        static bool PortExists(string port)
        {
            return SerialPort.GetPortNames().Any(p => string.Equals(p, port, StringComparison.OrdinalIgnoreCase));
        }

        void OnLost(string reason)
        {
            if (meter == null) return;
            var serial = meter as SerialMeter;
            bool demo = meter is DemoMeter;
            autoRunning = false;
            CloseMeter();
            SystemSounds.Hand.Play();
            if (!chkReconnect.Checked)
            {
                Log(reason + " 케이블을 확인하고 다시 [연결]하세요.");
                UpdateUi();
                return;
            }
            waitingReconnect = true;
            lostDemo = demo;
            lostOpener = demo ? null : netOpener;
            lostPort = serial != null ? serial.PortName : null;
            int baud;
            lostBaud = int.TryParse(cbBaud.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out baud) ? baud : 9600;
            reconnectTries = 0;
            reconnectGen++;
            portSeenAt = DateTime.MinValue;
            lastPortList = null;
            lblModel.Text = (lostOpener != null ? "" : "USB ") + "연결 끊김 - 다시 연결되기를 기다리는 중...";
            lblModel.ForeColor = Theme.Red;
            Log(reason + " 케이블이 다시 연결되면 자동으로 연결합니다.");
            UpdateUi();
        }

        void CancelReconnect()
        {
            waitingReconnect = false;
            reconnectGen++;
            savedCond = null;   // 통신이 끊긴 상태라 지금은 복원할 수 없음 (파일에 남은 값으로 다음 연결 때 물음)
            lblModel.Text = "연결 안 됨";
            lblModel.ForeColor = Theme.Secondary;
        }

        void OnWatchTick(object sender, EventArgs e)
        {
            if (busy || reconnecting) return;
            if (chkAutoUpdate.Checked && !autoRunning && !updating && updateAttemptDay != Today()) { var _ = CheckUpdateDaily(); }   // 켜 둔 채 날짜가 바뀌면 확인
            var serial = meter as SerialMeter;
            if (serial != null)
            {
                if (!PortExists(serial.PortName)) OnLost("USB 연결이 끊어졌습니다 (" + serial.PortName + ").");
                return;
            }
            if (!waitingReconnect) return;

            string candidate = null;
            if (lostDemo) candidate = "DEMO";
            else if (lostOpener != null) candidate = cbIf.Text;
            else
            {
                string[] names = SerialPort.GetPortNames();
                if (lostPort != null && names.Any(p => string.Equals(p, lostPort, StringComparison.OrdinalIgnoreCase)))
                    candidate = lostPort;
                else
                {
                    // 다른 USB 단자에 꽂으면 COM 번호가 바뀔 수 있으므로 HIOKI 장비를 찾음 (포트 목록이 바뀔 때만 조회)
                    string list = string.Join(",", names.OrderBy(p => p));
                    if (list != lastPortList)
                    {
                        lastPortList = list;
                        var hioki = PortItem.Enumerate().FirstOrDefault(p => p.Hioki);
                        if (hioki != null) lostPort = candidate = hioki.Port;
                    }
                }
            }
            if (candidate == null) { portSeenAt = DateTime.MinValue; return; }
            // 장치가 다시 나타난 뒤 준비될 시간을 잠깐 줌
            if (portSeenAt == DateTime.MinValue) { portSeenAt = DateTime.Now; return; }
            if ((DateTime.Now - portSeenAt).TotalMilliseconds < 1500) return;
            TryReconnect(candidate);
        }

        async void TryReconnect(string port)
        {
            reconnecting = true;
            int gen = reconnectGen;
            IMeter m = null;
            try
            {
                int baud = lostBaud;
                var open = lostOpener;
                m = lostDemo ? new DemoMeter() : open != null ? await Task.Run(open) : await Task.Run(() => (IMeter)new SerialMeter(port, baud));
                var hs = await Handshake(m, connectCond, false, savedCond != null);
                if (gen != reconnectGen || meter != null)
                {
                    m.Dispose();   // 기다리는 동안 사용자가 취소함
                    return;
                }
                var newPars = hs.Value.Where(p => p.Length > 0 && p != "OFF").ToList();
                meter = m;
                m = null;
                waitingReconnect = false;
                lblModel.Text = hs.Key;
                lblModel.ForeColor = Theme.Green;
                Log("자동 재연결됨: " + hs.Key + (lostDemo ? "" : " (" + port + ")"));
                if (!newPars.SequenceEqual(pars))
                {
                    pars = newPars;
                    StartNewTable("계측기의 측정 파라미터가 바뀌어 새 파일로 시작합니다.");
                }
                SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                if (m != null) { try { m.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                reconnectTries++;
                portSeenAt = DateTime.Now;   // 다음 시도까지 잠깐 쉼
                if (reconnectTries == 1 || reconnectTries % 10 == 0)
                    Log("재연결 시도 " + reconnectTries + "회 실패 - 계속 시도합니다: " + ex.Message.Split('\n')[0]);
            }
            finally
            {
                reconnecting = false;
                UpdateUi();
            }
        }

        Task<T> Io<T>(Func<T> work)
        {
            return Task.Run(() => { lock (ioLock) { return work(); } });
        }
    }
}
