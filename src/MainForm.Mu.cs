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
        // ---------------- 복소 투자율 ----------------

        Control BuildMuTab()
        {
            var lay = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(0, 8, 4, 0), BackColor = Theme.Bg };
            lay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) lay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lay.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            KeepFillRow(lay, 3, () => Font.Height * 24);   // 그래프 최소 높이

            var sample = Group("시료 (토로이달 코어)");
            txtMuName = new TextBox { Width = 140 };
            numTurns = new NumericUpDown { Width = 60, Minimum = 1, Maximum = 10000, Value = DemoMeter.CoreN };
            txtOD = new TextBox { Width = 55, Text = "25" };
            txtID = new TextBox { Width = 55, Text = "15" };
            txtH = new TextBox { Width = 55, Text = "10" };
            chkDirect = new Switch { Text = "le·Ae 직접 입력", Margin = new Padding(16, 4, 3, 3) };
            txtLe = new TextBox { Width = 70 };
            txtAe = new TextBox { Width = 70 };
            txtRw = new TextBox { Width = 70, Text = "0" };
            btnDcr = Btn("DCR 측정", OnDcr);
            var rwHint = Lbl("계측기의 Rdc로 재서 Rw를 채움 (μ″ 계산에서 빼는 권선 저항)");
            rwHint.ForeColor = Theme.Secondary;
            var mm = Lbl("mm");
            var mm2 = Lbl("mm²");
            Add(sample, Lbl("시료명"), txtMuName, Lbl("턴 수 N"), numTurns, Lbl("외경 OD"), txtOD, Lbl("내경 ID"), txtID, Lbl("높이 h"), txtH, mm,
                chkDirect, Lbl("le"), txtLe, Lbl("mm   Ae"), txtAe, mm2);
            sample.SetFlowBreak(mm, true);
            sample.SetFlowBreak(mm2, true);
            Add(sample, Lbl("권선 저항 Rw"), txtRw, Lbl("Ω"), btnDcr, rwHint);
            foreach (TextBox t in new[] { txtOD, txtID, txtH }) t.TextChanged += delegate { UpdateGeometry(); };
            chkDirect.CheckedChanged += delegate { UpdateGeometry(); };
            lay.Controls.Add(sample.Parent);

            var sweep = Group("측정 설정 (측정하는 동안만 계측기에 적용하고, 끝나면 원래대로 되돌림)");
            txtMuStart = new TextBox { Width = 70, Text = "1k" };
            txtMuStop = new TextBox { Width = 70, Text = "8M" };
            numPoints = new NumericUpDown { Width = 60, Minimum = 2, Maximum = 801, Value = 100 };
            cbSpacing = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
            cbSpacing.Items.AddRange(new object[] { "로그", "선형" });
            cbSpacing.SelectedIndex = 0;
            var freqHint = Lbl("Hz · 4 Hz ~ 8 MHz · 예: 1k, 100k, 8M");
            freqHint.ForeColor = Theme.Secondary;
            Add(sweep, Lbl("시작"), txtMuStart, Lbl("끝"), txtMuStop, Lbl("점 수"), numPoints, Lbl("간격"), cbSpacing, freqHint);
            sweep.SetFlowBreak(freqHint, true);
            cbMuLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            cbMuLevel.Items.AddRange(new object[] { "V (개방전압)", "CV (정전압)", "CC (정전류)" });
            cbMuLevel.SelectedIndex = 2;
            txtMuLevel = new TextBox { Width = 60, Text = "1" };
            lblMuLevelUnit = Lbl("mA");
            cbMuLevel.SelectedIndexChanged += delegate
            {
                bool cc = cbMuLevel.SelectedIndex == 2;
                lblMuLevelUnit.Text = cc ? "mA" : "V";
                txtMuLevel.Text = cc ? "1" : "0.1";   // 종류를 바꾸면 그 종류의 낮은 기본값으로
            };
            cbMuSpeed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            cbMuSpeed.Items.AddRange(new object[] { "FAST", "MED", "SLOW", "SLOW2" });
            cbMuSpeed.SelectedIndex = 1;
            numAvg = new NumericUpDown { Width = 60, Minimum = 1, Maximum = 256, Value = 1 };
            numDelay = new NumericUpDown { Width = 70, Minimum = 0, Maximum = 10000, Increment = 10, Value = 0 };
            var ms = Lbl("ms");   // 입력칸은 나중에 테두리 칸으로 감싸져 줄바꿈 표시가 사라지므로 뒤의 글자에 줄바꿈을 둠
            Add(sweep, Lbl("측정 신호"), cbMuLevel, txtMuLevel, lblMuLevelUnit, Lbl("SPEED"), cbMuSpeed, Lbl("평균"), numAvg, Lbl("점 사이 대기"), numDelay, ms);
            sweep.SetFlowBreak(ms, true);
            btnMuStart = Btn("측정 시작", OnMuStart, true);
            btnMuStart.Width = 110;
            btnMuStop = Btn("중지", delegate { muStopRequested = true; Log("복소 투자율 측정 중지 요청 - 지금 점까지 측정하고 멈춥니다."); });
            muProgress = new ProgressBar { Width = 220, Height = 14, Margin = new Padding(12, 11, 3, 3) };
            Add(sweep, btnMuStart, btnMuStop, muProgress);
            lay.Controls.Add(sweep.Parent);

            var result = Group("결과 (측정이 끝나면 저장 폴더에 CSV와 그래프 PNG를 자동 저장)");
            btnMuCsv = Btn("CSV 다른 이름으로 저장", OnMuSaveCsv);
            btnMuPng = Btn("그래프 PNG 저장", OnMuSavePng);
            lblMuFile = new Label { AutoSize = true, Margin = new Padding(8, 9, 3, 3), ForeColor = Theme.Secondary };
            lblMuWarn = new Label { AutoSize = true, Margin = new Padding(10, 6, 3, 3), ForeColor = Theme.Red, Font = new Font(Theme.FontName, 9F, FontStyle.Bold) };
            Add(result, btnMuCsv, btnMuPng, lblMuFile, lblMuWarn);
            result.SetFlowBreak(lblMuFile, true);
            lay.Controls.Add(result.Parent);

            muChart = Permeability.NewChart();
            var chartCard = new Card(null, false) { Padding = new Padding(10, 8, 10, 10) };
            chartCard.Controls.Add(muChart);
            lay.Controls.Add(chartCard);

            // 불러오는 순서가 중요: 신호 종류(기본값으로 덮어씀) → 레벨 값, 직접 입력 스위치 → le·Ae
            muSettings = new Dictionary<string, Control>
            {
                { "MuName", txtMuName }, { "MuTurns", numTurns }, { "MuOD", txtOD }, { "MuID", txtID }, { "MuH", txtH },
                { "MuDirect", chkDirect }, { "MuLe", txtLe }, { "MuAe", txtAe }, { "MuRw", txtRw },
                { "MuStart", txtMuStart }, { "MuStop", txtMuStop }, { "MuPoints", numPoints }, { "MuSpacing", cbSpacing },
                { "MuLevel", cbMuLevel }, { "MuLevelValue", txtMuLevel }, { "MuSpeed", cbMuSpeed }, { "MuAvg", numAvg }, { "MuDelay", numDelay }
            };
            UpdateGeometry();
            return lay;
        }

        void LoadMuSettings()
        {
            foreach (var kv in muSettings.Concat(swSettings))
            {
                string v = settings.Get(kv.Key, null);
                if (v == null) continue;
                var t = kv.Value as TextBox;
                var n = kv.Value as NumericUpDown;
                var c = kv.Value as ComboBox;
                var b = kv.Value as CheckBox;
                decimal d;
                int i;
                if (t != null) t.Text = v;
                else if (n != null && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out d)) n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, d));
                else if (c != null && int.TryParse(v, out i) && i >= 0 && i < c.Items.Count) c.SelectedIndex = i;
                else if (b != null) b.Checked = v == "1";
            }
            UpdateGeometry();
        }

        void SaveMuSettings()
        {
            foreach (var kv in muSettings.Concat(swSettings))
            {
                var n = kv.Value as NumericUpDown;
                var c = kv.Value as ComboBox;
                var b = kv.Value as CheckBox;
                settings.Set(kv.Key, n != null ? n.Value.ToString(CultureInfo.InvariantCulture)
                    : c != null ? c.SelectedIndex.ToString(CultureInfo.InvariantCulture)
                    : b != null ? (b.Checked ? "1" : "0")
                    : kv.Value.Text.Trim());
            }
        }

        // OD·ID·h로 le·Ae를 계산해 표시. 직접 입력이면 사용자가 쓴 값을 그대로 둠
        void UpdateGeometry()
        {
            txtLe.ReadOnly = txtAe.ReadOnly = !chkDirect.Checked;
            if (chkDirect.Checked) return;
            double od, id, h;
            bool ok = Fmt.TryParse(txtOD.Text.Trim(), out od) & Fmt.TryParse(txtID.Text.Trim(), out id) & Fmt.TryParse(txtH.Text.Trim(), out h)
                && od > id && id > 0 && h > 0;
            txtLe.Text = ok ? Permeability.Le(od, id).ToString("0.####", CultureInfo.InvariantCulture) : "-";
            txtAe.Text = ok ? Permeability.Ae(od, id, h).ToString("0.####", CultureInfo.InvariantCulture) : "-";
        }

        class MuSetup
        {
            public string Name, LevelText, SpeedText;
            public int N, Points, DelayMs;
            public double LeMm, AeMm2, Rw, Start, Stop;
            public bool Log;
            public MeasCond Cond;
        }

        static double ReadNum(TextBox t, string name, double min, double max)
        {
            double v;
            if (!Fmt.TryParse(t.Text.Trim(), out v) || v < min || v > max)
                throw new InvalidOperationException(name + " 값 '" + t.Text + "'를 읽을 수 없습니다. "
                    + min.ToString("G6", CultureInfo.InvariantCulture) + " ~ " + max.ToString("G6", CultureInfo.InvariantCulture) + " 범위로 입력하세요.");
            return v;
        }

        // 화면 입력을 읽어 확인. 잘못된 값이면 InvalidOperationException
        MuSetup ReadMuSetup()
        {
            var s = new MuSetup { Name = txtMuName.Text.Trim(), N = (int)numTurns.Value, Points = (int)numPoints.Value, DelayMs = (int)numDelay.Value, Log = cbSpacing.SelectedIndex == 0 };
            if (chkDirect.Checked)
            {
                s.LeMm = ReadNum(txtLe, "le [mm]", 0.01, 100000);
                s.AeMm2 = ReadNum(txtAe, "Ae [mm²]", 0.0001, 1000000);
            }
            else
            {
                double od = ReadNum(txtOD, "외경 OD", 0.01, 10000), id = ReadNum(txtID, "내경 ID", 0.001, 10000), h = ReadNum(txtH, "높이 h", 0.001, 10000);
                if (id >= od) throw new InvalidOperationException("내경 ID는 외경 OD보다 작아야 합니다.");
                s.LeMm = Permeability.Le(od, id);
                s.AeMm2 = Permeability.Ae(od, id, h);
            }
            s.Rw = ReadNum(txtRw, "권선 저항 Rw", 0, 1e6);
            if (!Fmt.TryParseFrequency(txtMuStart.Text, out s.Start) || !Fmt.TryParseFrequency(txtMuStop.Text, out s.Stop) || s.Start >= s.Stop)
                throw new InvalidOperationException("시작·끝 주파수는 4 Hz ~ 8 MHz 범위이고 시작이 끝보다 작아야 합니다 (예: 1k, 8M).");
            string[] modes = { "V", "CV", "CC" };
            string mode = modes[cbMuLevel.SelectedIndex];
            bool cc = mode == "CC";
            double lv = cc ? ReadNum(txtMuLevel, "CC 전류(mA)", 0.01, 50) : ReadNum(txtMuLevel, mode + " 전압(V)", 0.005, 5);
            string[] speeds = { "FAST", "MEDium", "SLOW", "SLOW2" };
            s.Cond = new MeasCond
            {
                Level = mode,
                LevelValue = cc ? lv / 1000 : lv,   // CC는 화면에서 mA, 계측기에는 A로 보냄
                Speed = speeds[cbMuSpeed.SelectedIndex],
                Average = (int)numAvg.Value,
                Pars = new[] { "Z", "PHASE", "OFF", "OFF" }
            };
            s.LevelText = mode + " " + lv.ToString("G6", CultureInfo.InvariantCulture) + (cc ? " mA" : " V");
            s.SpeedText = (string)cbMuSpeed.SelectedItem;
            return s;
        }

        List<KeyValuePair<string, string>> MuInfo(MuSetup s, DateTime started, string status, string meterCond)
        {
            Func<string, string, KeyValuePair<string, string>> kv = (k, v) => new KeyValuePair<string, string>(k, v);
            Func<double, string> g = v => v.ToString("G6", CultureInfo.InvariantCulture);
            List<string> warns = Permeability.Warnings(muRows, s.Cond);
            return new List<KeyValuePair<string, string>>
            {
                kv("측정 일시", started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                kv("시료명", s.Name),
                kv("턴 수 N", s.N.ToString(CultureInfo.InvariantCulture)),
                kv("OD [mm]", chkDirect.Checked ? "" : txtOD.Text.Trim()),
                kv("ID [mm]", chkDirect.Checked ? "" : txtID.Text.Trim()),
                kv("h [mm]", chkDirect.Checked ? "" : txtH.Text.Trim()),
                kv("le [mm]", g(s.LeMm)),
                kv("Ae [mm²]", g(s.AeMm2)),
                kv("le·Ae", chkDirect.Checked ? "직접 입력" : "OD·ID·h로 계산"),
                kv("Rw [Ω]", g(s.Rw)),
                kv("측정 신호", s.LevelText),
                kv("SPEED", s.SpeedText),
                kv("평균", s.Cond.Average.ToString(CultureInfo.InvariantCulture)),
                kv("점 사이 대기 [ms]", s.DelayMs.ToString(CultureInfo.InvariantCulture)),
                kv("주파수", Fmt.FrequencyLabel(s.Start) + " ~ " + Fmt.FrequencyLabel(s.Stop) + ", " + s.Points + "점, " + (s.Log ? "로그" : "선형") + " 간격"),
                kv("계측기", currentIdn ?? ""),
                kv("계측기 조건", meterCond),
                kv("상태", status),
                kv("경고", warns.Count == 0 ? "없음" : string.Join(" / ", warns))
            };
        }

        async void OnMuStart(object sender, EventArgs e)
        {
            if (meter == null || busy || autoRunning) return;
            MuSetup s;
            try { s = ReadMuSetup(); }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "복소 투자율", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this,
                    "측정 전에 계측기에서 OPEN / SHORT 보정을 했습니까?\n\n"
                    + "보정하지 않으면 리드선·지그의 임피던스가 μ′, μ″에 그대로 들어가 고주파에서 크게 틀어집니다.\n\n"
                    + "[예]를 누르면 측정을 시작합니다.",
                    "OPEN/SHORT 보정 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            IMeter m = meter;
            List<double> list = Permeability.Frequencies(s.Start, s.Stop, s.Points, s.Log);
            double le = s.LeMm / 1000, ae = s.AeMm2 / 1e6;
            busy = muRunning = true;
            muStopRequested = false;
            muRows = new List<MuRow>();
            muInfo = null;
            muStem = null;
            Permeability.ClearChart(muChart, (s.Name.Length > 0 ? s.Name : "시료") + "  ·  N = " + s.N + "턴  ·  " + s.LevelText, s.Start, s.Stop);
            lblMuWarn.Text = "";
            lblMuFile.Text = "측정 중...";
            lblMuFile.ForeColor = Theme.Secondary;
            muProgress.Maximum = list.Count;
            muProgress.Value = 0;
            UpdateUi();
            DateTime started = DateTime.Now;
            string status = "완료", meterCond = "";
            Exception error = null;
            var shown = new List<string>();
            Log("복소 투자율 측정 시작: " + Fmt.FrequencyLabel(s.Start) + " ~ " + Fmt.FrequencyLabel(s.Stop) + ", " + list.Count + "점, "
                + s.LevelText + ", N=" + s.N + ", le=" + s.LeMm.ToString("G5", CultureInfo.InvariantCulture) + " mm, Ae=" + s.AeMm2.ToString("G5", CultureInfo.InvariantCulture)
                + " mm², Rw=" + s.Rw.ToString("G5", CultureInfo.InvariantCulture) + " Ω");
            try
            {
                var lines = new List<string>();
                // 앞 측정에서 되돌리지 못한 값이 남아 있으면 그것이 진짜 원래 값이므로 유지
                try { var applied = await Io(() => Permeability.ApplyTemp(m, s.Cond, lines)); if (muSaved == null) muSaved = applied; }
                finally { foreach (string l in lines) Log(l); }
                PersistPending();   // 측정 중 프로그램이 죽어도 다음 연결 때 되돌릴 수 있도록
                meterCond = await Io(() => MeasCond.Summary(m));
                SetTimeout(m, 60000);   // 저주파·SLOW2·평균이 많으면 한 점에 수 초 이상 걸림 (공식 앱은 무제한 대기)
                for (int i = 0; i < list.Count && !muStopRequested; i++)
                {
                    double hz = list[i];
                    MuRow row = await Io(() => Permeability.MeasurePoint(m, hz, s.Rw, s.N, le, ae));
                    if (IsDisposed) return;
                    if (i == 0) Log("첫 점 응답 (:MEAS? | :MONI?): " + row.Raw);
                    row.Warn = Permeability.LevelWarning(s.Cond.Level, s.Cond.LevelValue, row.V, row.I);
                    muRows.Add(row);
                    Permeability.AddPoint(muChart, row);
                    muProgress.Value = i + 1;
                    List<string> warns = Permeability.Warnings(muRows, s.Cond);
                    foreach (string w in warns)
                    {
                        if (shown.Contains(Permeability.WarningKind(w))) continue;   // 같은 종류는 처음 한 번만 로그·경고음 (내용은 점마다 갱신됨)
                        shown.Add(Permeability.WarningKind(w));
                        Log("※ " + w);
                        SystemSounds.Exclamation.Play();
                    }
                    lblMuWarn.Text = string.Join("\n", warns.Select(w => "※ " + w));
                    if (s.DelayMs > 0) await Task.Delay(s.DelayMs);
                }
                if (muStopRequested) status = "중지됨 (" + muRows.Count + "/" + list.Count + "점)";
            }
            catch (Exception ex)
            {
                error = ex;
                status = "오류로 중단 (" + muRows.Count + "/" + list.Count + "점): " + ex.Message.Split('\n')[0];
            }
            if (IsDisposed) return;
            SetTimeout(m, 5000);
            foreach (string w in Permeability.Warnings(muRows, s.Cond)) Log("※ 최종 경고: " + w);

            // 받은 데이터부터 저장 (계측기 복원이 실패해도 데이터는 남도록)
            if (muRows.Count > 0)
            {
                muInfo = MuInfo(s, started, status, meterCond);
                try
                {
                    Directory.CreateDirectory(txtFolder.Text);
                    string name = Permeability.FileStem(s.Name, s.N, started), stem = Path.Combine(txtFolder.Text, name);
                    for (int k = 2; File.Exists(stem + ".csv") || File.Exists(stem + ".png"); k++) stem = Path.Combine(txtFolder.Text, name + "_" + k);
                    File.WriteAllLines(stem + ".csv", Permeability.CsvLines(muInfo, muRows), new UTF8Encoding(true));
                    Permeability.SavePng(muChart, stem + ".png");
                    muStem = stem;
                    lblMuFile.Text = Path.GetFileName(stem) + ".csv / .png  (" + status + ")";
                    Log("복소 투자율 저장: " + stem + ".csv, .png");
                }
                catch (Exception ex)
                {
                    lblMuFile.Text = "자동 저장 실패 - [CSV 다른 이름으로 저장]으로 저장하세요";
                    lblMuFile.ForeColor = Theme.Red;
                    Log("※ 복소 투자율 자동 저장 실패: " + ex.Message);
                }
            }
            else lblMuFile.Text = status;

            await RestoreTemp(m);
            busy = muRunning = false;
            Log("복소 투자율 측정 " + status);
            if (error != null)
            {
                if (IsDisconnect(error, m)) OnLost("USB 연결이 끊어졌습니다: " + error.Message);
                else
                {
                    SystemSounds.Hand.Play();
                    MessageBox.Show(this, "측정을 멈췄습니다. 그때까지 받은 " + muRows.Count + "점은 저장했습니다.\n\n" + error.Message, "복소 투자율", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else SystemSounds.Asterisk.Play();
            UpdateUi();
        }

        static void SetTimeout(IMeter m, int ms)
        {
            var timed = m as ITimed;
            if (timed == null) return;
            try { timed.TimeoutMs = ms; }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }

        // 권선 저항: PAR1을 잠시 Rdc로 바꿔 한 번 측정하고 원래대로 되돌림
        async void OnDcr(object sender, EventArgs e)
        {
            if (meter == null || busy || autoRunning) return;
            IMeter m = meter;
            busy = true;
            UpdateUi();
            try
            {
                var lines = new List<string>();
                double r;
                SetTimeout(m, 60000);   // DC 자동 레인지는 레인지를 몇 번 바꾸며 재므로 한 번에 5초를 넘길 수 있음
                try { r = await Io(() => Permeability.MeasureRdc(m, lines)); }
                finally { SetTimeout(m, 5000); foreach (string l in lines) Log(l); }
                txtRw.Text = r.ToString("G6", CultureInfo.InvariantCulture);
                Log("DCR 측정: Rw = " + txtRw.Text + " Ω (계측기 설정은 원래대로 되돌림)");
            }
            catch (Exception ex)
            {
                if (IsDisconnect(ex, m)) OnLost("USB 연결이 끊어졌습니다: " + ex.Message);
                else
                {
                    Log("DCR 측정 실패: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "DCR 측정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                busy = false;
                UpdateUi();
            }
        }

        string MuDialogName(string ext)
        {
            return (muStem != null ? Path.GetFileName(muStem) : "MU_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)) + ext;
        }

        void OnMuSaveCsv(object sender, EventArgs e)
        {
            if (muRows.Count == 0 || muInfo == null) return;
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = MuDialogName(".csv"), InitialDirectory = txtFolder.Text })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllLines(dlg.FileName, Permeability.CsvLines(muInfo, muRows), new UTF8Encoding(true));
                    Log("저장 완료: " + dlg.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, "저장하지 못했습니다. 파일이 열려 있는지 확인하세요.\n\n" + ex.Message, "저장", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        void OnMuSavePng(object sender, EventArgs e)
        {
            if (muRows.Count == 0) return;
            using (var dlg = new SaveFileDialog { Filter = "PNG 그림 (*.png)|*.png", FileName = MuDialogName(".png"), InitialDirectory = txtFolder.Text })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Permeability.SavePng(muChart, dlg.FileName);
                    Log("저장 완료: " + dlg.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, "저장하지 못했습니다.\n\n" + ex.Message, "저장", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        // 스윕(복소 투자율·스윕 측정) 전 값으로 계측기를 되돌림. 실패하면 연결 해제 때 다시 시도
        async Task RestoreTemp(IMeter m)
        {
            if (muSaved == null || meter != m) return;
            var saved = muSaved;
            try
            {
                await Io(() => { MeasCond.Restore(m, saved); return true; });
                muSaved = null;
                PersistPending();
                Log("계측기 설정을 측정 전으로 되돌렸습니다.");
            }
            catch (Exception ex) { Log("※ 계측기 설정을 되돌리지 못했습니다 (연결을 해제할 때 다시 시도합니다): " + ex.Message); }
        }

        void SaveAs(string filter, string fileName, Action<string> write)
        {
            using (var dlg = new SaveFileDialog { Filter = filter, FileName = fileName, InitialDirectory = txtFolder.Text })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    write(dlg.FileName);
                    Log("저장 완료: " + dlg.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, "저장하지 못했습니다. 파일이 열려 있는지 확인하세요.\n\n" + ex.Message, "저장", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        static TableLayoutPanel TabLayout(int autoRows)
        {
            var lay = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(0, 8, 4, 0), BackColor = Theme.Bg };
            lay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < autoRows; i++) lay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lay.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return lay;
        }

        static Label Hint(string text)
        {
            var l = Lbl(text);
            l.ForeColor = Theme.Secondary;
            return l;
        }
    }
}
