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
        // ---------------- 스윕 측정 ----------------

        Control BuildSweepTab()
        {
            var lay = TabLayout(3);
            KeepFillRow(lay, 3, () => Font.Height * 24);

            var pts = Group("스윕 점 (공식 앱 Sweep Measurement)");
            txtSwName = new TextBox { Width = 140 };
            cbSwItem = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            cbSwItem.Items.AddRange(Sweep.Items);
            cbSwItem.SelectedIndex = 0;
            txtSwStart = new TextBox { Width = 70, Text = "1k" };
            txtSwStop = new TextBox { Width = 70, Text = "1M" };
            numSwPoints = new NumericUpDown { Width = 60, Minimum = 1, Maximum = 5000, Value = 50 };
            cbSwSpacing = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
            cbSwSpacing.Items.AddRange(new object[] { "로그", "선형" });
            cbSwSpacing.SelectedIndex = 0;
            btnSwMake = Btn("점 목록 만들기", OnSwMake);
            lblSwUnit = Hint(Sweep.RangeText(0));
            cbSwItem.SelectedIndexChanged += delegate
            {
                int it = cbSwItem.SelectedIndex;
                lblSwUnit.Text = Sweep.RangeText(it);
                txtSwStart.Text = it == 0 ? "1k" : it == 3 ? "0.1" : "0.01";   // 항목을 바꾸면 그 항목의 기본 범위로
                txtSwStop.Text = it == 0 ? "1M" : it == 3 ? "10" : "1";
                txtSwList.Text = "";
            };
            Add(pts, Lbl("시료명"), txtSwName, Lbl("스윕 항목"), cbSwItem, Lbl("시작"), txtSwStart, Lbl("끝"), txtSwStop, Lbl("점 수"), numSwPoints, Lbl("간격"), cbSwSpacing, btnSwMake, lblSwUnit);
            pts.SetFlowBreak(lblSwUnit, true);
            txtSwList = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 560, Height = 54 };
            var listHint = Hint("점 목록: 쉼표·줄바꿈으로 구분, 직접 고쳐도 됨");
            Add(pts, Lbl("점 목록"), txtSwList, listHint);
            pts.SetFlowBreak(listHint, true);
            numSwRepeat = new NumericUpDown { Width = 70, Minimum = 1, Maximum = 32000, Value = 1 };
            numSwRepeatWait = new NumericUpDown { Width = 70, Minimum = 0, Maximum = 32000, Value = 0 };
            numSwDelay = new NumericUpDown { Width = 70, Minimum = 0, Maximum = 32000, Increment = 10, Value = 0 };
            var ms = Lbl("ms");
            Add(pts, Lbl("반복 횟수"), numSwRepeat, Lbl("반복 사이 대기"), numSwRepeatWait, Lbl("초   점 사이 대기"), numSwDelay, ms);
            pts.SetFlowBreak(ms, true);
            btnSwStart = Btn("측정 시작", OnSwStart, true);
            btnSwStart.Width = 110;
            btnSwStop = Btn("중지", delegate { swStopRequested = true; Log("스윕 측정 중지 요청 - 지금 점까지 측정하고 멈춥니다."); });
            swProgress = new ProgressBar { Width = 220, Height = 14, Margin = new Padding(12, 11, 3, 3) };
            Add(pts, btnSwStart, btnSwStop, swProgress, Hint("측정값은 계측기의 PAR1~PAR4 (계측기 설정 탭에서 바꿈). 신호 종류·주파수는 끝나면 원래대로 되돌림"));
            lay.Controls.Add(pts.Parent);

            var result = Group("결과 (끝나면 저장 폴더에 CSV와 그래프 PNG를 자동 저장 · 그래프: 왼쪽 축 PAR1, 오른쪽 축 PAR2)");
            btnSwCsv = Btn("CSV 다른 이름으로 저장", delegate
            {
                if (swRows.Count == 0 || swInfo == null) return;
                SaveAs("CSV (*.csv)|*.csv", SwDialogName(".csv"), path => File.WriteAllLines(path, Sweep.CsvLines(swInfo, swItem, swPars, swRows), new UTF8Encoding(true)));
            });
            btnSwPng = Btn("그래프 PNG 저장", delegate
            {
                if (swRows.Count > 0) SaveAs("PNG 그림 (*.png)|*.png", SwDialogName(".png"), path => Permeability.SavePng(swChart, path));
            });
            lblSwFile = new Label { AutoSize = true, Margin = new Padding(8, 9, 3, 3), ForeColor = Theme.Secondary };
            Add(result, btnSwCsv, btnSwPng, lblSwFile);
            lay.Controls.Add(result.Parent);
            lay.Controls.Add(new Panel { Height = 0, Margin = Padding.Empty });   // 행 번호를 복소 투자율 탭과 맞춤 (그래프 = 3행)

            swChart = Sweep.NewChart();
            var chartCard = new Card(null, false) { Padding = new Padding(10, 8, 10, 10) };
            chartCard.Controls.Add(swChart);
            lay.Controls.Add(chartCard);

            swSettings = new Dictionary<string, Control>
            {
                { "SwName", txtSwName }, { "SwItem", cbSwItem }, { "SwStart", txtSwStart }, { "SwStop", txtSwStop }, { "SwPoints", numSwPoints },
                { "SwSpacing", cbSwSpacing }, { "SwList", txtSwList }, { "SwRepeat", numSwRepeat }, { "SwRepeatWait", numSwRepeatWait }, { "SwDelay", numSwDelay }
            };
            return lay;
        }

        string SwDialogName(string ext)
        {
            return (swStem != null ? Path.GetFileName(swStem) : "SWEEP_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)) + ext;
        }

        // 시작·끝·점 수·간격으로 점 목록을 채움 (공식 앱 Auto Setup)
        void OnSwMake(object sender, EventArgs e)
        {
            int it = cbSwItem.SelectedIndex;
            try
            {
                List<double> ends = Sweep.ParsePoints(it, txtSwStart.Text + "," + txtSwStop.Text);
                bool log = cbSwSpacing.SelectedIndex == 0;
                List<double> list = Permeability.Frequencies(ends[0], ends[1], (int)numSwPoints.Value, log, it == 0 ? 2 : 6);
                txtSwList.Text = string.Join(", ", list.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)));
            }
            catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "스윕 측정", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        async void OnSwStart(object sender, EventArgs e)
        {
            if (meter == null || busy || autoRunning) return;
            if (txtSwList.Text.Trim().Length == 0) OnSwMake(sender, e);
            int item = cbSwItem.SelectedIndex;
            List<double> pts;
            try { pts = Sweep.ParsePoints(item, txtSwList.Text); }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "스윕 측정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            IMeter m = meter;
            int reps = (int)numSwRepeat.Value, waitMs = (int)numSwRepeatWait.Value * 1000, delay = (int)numSwDelay.Value, total = pts.Count * reps;
            string name = txtSwName.Text.Trim();
            bool logX = cbSwSpacing.SelectedIndex == 0 && pts.Min() > 0;
            busy = swRunning = true;
            swStopRequested = false;
            swRows = new List<SweepRow>();
            swInfo = null;
            swStem = null;
            swItem = item;
            lblSwFile.Text = "측정 중...";
            lblSwFile.ForeColor = Theme.Secondary;
            swProgress.Maximum = total;
            swProgress.Value = 0;
            UpdateUi();
            DateTime started = DateTime.Now;
            string status = "완료", meterCond = "";
            Exception error = null;
            Log("스윕 측정 시작: " + Sweep.Items[item] + " " + pts.Count + "점 × " + reps + "회");
            try
            {
                var lines = new List<string>();
                // 앞 측정에서 되돌리지 못한 값이 남아 있으면 그것이 진짜 원래 값이므로 유지
                try { var applied = await Io(() => Permeability.ApplyTemp(m, Sweep.Cond(item, pts[0]), lines)); if (muSaved == null) muSaved = applied; }
                finally { foreach (string l in lines) Log(l); }
                PersistPending();   // 측정 중 프로그램이 죽어도 다음 연결 때 되돌릴 수 있도록
                swPars = await Io(() => Enumerable.Range(1, 4).Select(i => m.Query(":PARameter" + i + "?").Trim().ToUpperInvariant()).Where(p => p.Length > 0 && p != "OFF").ToList());
                if (swPars.Count == 0) throw new InvalidOperationException("계측기의 PAR1~PAR4가 모두 OFF입니다. [계측기 설정] 탭에서 측정할 파라미터를 고르세요.");
                meterCond = await Io(() => MeasCond.Summary(m));
                MeasCond freqLevel = Permeability.LevelOf(meterCond);   // 주파수 스윕은 계측기에 설정된 레벨과 비교
                var swWarned = new List<string>();
                Sweep.ClearChart(swChart, (name.Length > 0 ? name + "  ·  " : "") + Sweep.Items[item] + " 스윕", item, swPars, logX, pts.Min(), pts.Max());
                Log("측정 파라미터: " + string.Join(", ", swPars.Select(Fmt.DisplayName)));
                SetTimeout(m, 60000);   // 저주파·SLOW2·평균이 많으면 한 점에 수 초 이상 걸림
                for (int rep = 1; rep <= reps && !swStopRequested; rep++)
                {
                    for (int w = 0; rep > 1 && w < waitMs && !swStopRequested; w += 100) await Task.Delay(100);
                    for (int i = 0; i < pts.Count && !swStopRequested; i++)
                    {
                        double v = pts[i];
                        int n = swPars.Count;
                        SweepRow row = await Io(() => Sweep.MeasurePoint(m, item, v, n));
                        if (IsDisposed) return;
                        row.Rep = rep;
                        row.No = i + 1;
                        if (swRows.Count == 0) Log("첫 점 응답 (:MEAS? | :MONI?): " + row.Raw);
                        MeasCond lc = item == 0 ? freqLevel : Sweep.Cond(item, v);
                        row.Warn = Permeability.LevelWarning(lc.Level, lc.LevelValue, row.V, row.I);
                        if (row.Warn.Length > 0 && !swWarned.Contains(Permeability.WarningKind(row.Warn)))
                        {
                            swWarned.Add(Permeability.WarningKind(row.Warn));   // 같은 종류는 처음 한 번만 로그·경고음
                            Log("※ " + Sweep.Items[item] + " " + v.ToString("0.######", CultureInfo.InvariantCulture) + ": " + row.Warn);
                            SystemSounds.Exclamation.Play();
                        }
                        swRows.Add(row);
                        Sweep.AddPoint(swChart, row, swPars);
                        swProgress.Value = swRows.Count;
                        if (delay > 0) await Task.Delay(delay);
                    }
                }
                if (swStopRequested) status = "중지됨 (" + swRows.Count + "/" + total + "점)";
            }
            catch (Exception ex)
            {
                error = ex;
                status = "오류로 중단 (" + swRows.Count + "/" + total + "점): " + ex.Message.Split('\n')[0];
            }
            if (IsDisposed) return;
            SetTimeout(m, 5000);

            // 받은 데이터부터 저장 (계측기 복원이 실패해도 데이터는 남도록)
            if (swRows.Count > 0)
            {
                Func<string, string, KeyValuePair<string, string>> kv = (k, v) => new KeyValuePair<string, string>(k, v);
                swInfo = new List<KeyValuePair<string, string>>
                {
                    kv("측정 일시", started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    kv("시료명", name),
                    kv("스윕 항목", Sweep.Items[item]),
                    kv("점", pts.Count + "점 (" + string.Join(", ", pts.Select(p => p.ToString("0.######", CultureInfo.InvariantCulture))) + ")"),
                    kv("반복", reps + "회, 반복 사이 " + numSwRepeatWait.Value + "초, 점 사이 " + delay + " ms"),
                    kv("파라미터", string.Join(", ", swPars.Select(Fmt.DisplayName))),
                    kv("계측기", currentIdn ?? ""),
                    kv("계측기 조건", meterCond),
                    kv("상태", status),
                    kv("경고", swRows.Count(r => r.Warn.Length > 0) == 0 ? "없음" : "측정 신호가 설정과 다른 점 " + swRows.Count(r => r.Warn.Length > 0) + "개 (레벨 경고 열 참고)")
                };
                try
                {
                    Directory.CreateDirectory(txtFolder.Text);
                    string file = Sweep.FileStem(name, started), stem = Path.Combine(txtFolder.Text, file);
                    for (int k = 2; File.Exists(stem + ".csv") || File.Exists(stem + ".png"); k++) stem = Path.Combine(txtFolder.Text, file + "_" + k);
                    File.WriteAllLines(stem + ".csv", Sweep.CsvLines(swInfo, item, swPars, swRows), new UTF8Encoding(true));
                    Permeability.SavePng(swChart, stem + ".png");
                    swStem = stem;
                    lblSwFile.Text = Path.GetFileName(stem) + ".csv / .png  (" + status + ")";
                    Log("스윕 측정 저장: " + stem + ".csv, .png");
                }
                catch (Exception ex)
                {
                    lblSwFile.Text = "자동 저장 실패 - [CSV 다른 이름으로 저장]으로 저장하세요";
                    lblSwFile.ForeColor = Theme.Red;
                    Log("※ 스윕 측정 자동 저장 실패: " + ex.Message);
                }
            }
            else lblSwFile.Text = status;

            await RestoreTemp(m);
            busy = swRunning = false;
            Log("스윕 측정 " + status);
            if (error != null)
            {
                if (IsDisconnect(error, m)) OnLost("USB 연결이 끊어졌습니다: " + error.Message);
                else
                {
                    SystemSounds.Hand.Play();
                    MessageBox.Show(this, "측정을 멈췄습니다. 그때까지 받은 " + swRows.Count + "점은 저장했습니다.\n\n" + error.Message, "스윕 측정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else SystemSounds.Asterisk.Play();
            UpdateUi();
        }

        // ---------------- 계측기 설정 · 화면 캡처 ----------------

        Control BuildSetTab()
        {
            var lay = TabLayout(1);
            var top = Group("계측기 설정 (공식 앱 Set 화면) · [새 값] 칸에 넣은 항목만 보냄 · 바로 계측기에 적용되고 되돌리지 않음");
            btnSetRead = Btn("계측기에서 읽기", OnSetRead);
            btnSetApply = Btn("바꾼 값 적용", OnSetApply, true);
            Add(top, btnSetRead, btnSetApply, Hint("※ [인덕턴스 기록] 탭의 측정 조건으로 바꾼 항목은 연결을 해제할 때 원래대로 돌아갑니다"));
            lay.Controls.Add(top.Parent);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            setGrid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Theme.Card, BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, GridColor = Theme.Separator,
                EnableHeadersVisualStyles = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 30,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            setGrid.RowTemplate.Height = 26;
            setGrid.ColumnHeadersDefaultCellStyle.BackColor = setGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Card;
            setGrid.ColumnHeadersDefaultCellStyle.ForeColor = setGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Secondary;
            setGrid.DefaultCellStyle.SelectionBackColor = Theme.Selection;
            setGrid.DefaultCellStyle.SelectionForeColor = Theme.Text;
            string[] heads = { "항목", "명령", "현재 값", "새 값", "입력 범위 · 설명" };
            int[] weights = { 22, 18, 14, 14, 42 };
            for (int i = 0; i < heads.Length; i++)
                setGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = heads[i], FillWeight = weights[i], ReadOnly = i != 3, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (SettingItem s in MeterSettings.Items)
            {
                int r = setGrid.Rows.Add(s.Name, s.Cmd, "", "", s.Hint);
                if (s.Choices == null) continue;
                var cell = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                cell.Items.Add("");
                cell.Items.AddRange(s.Choices.Select(Fmt.ParLabel).ToArray());   // 값 중 PAR 기호만 바뀜
                setGrid.Rows[r].Cells[3] = cell;
            }
            setGrid.Columns[3].DefaultCellStyle.BackColor = Color.FromArgb(250, 250, 252);
            var gridCard = new Card(null, false) { Dock = DockStyle.Fill, Padding = new Padding(8) };
            gridCard.Controls.Add(setGrid);
            body.Controls.Add(gridCard, 0, 0);

            var shot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
            shot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var shotTop = Group("계측기 화면 캡처 (공식 앱 Hard Copy)");
            btnShotColor = Btn("컬러로 캡처", delegate { OnShot(false); });
            btnShotMono = Btn("흑백으로 캡처", delegate { OnShot(true); });
            lblShot = Hint("캡처하면 저장 폴더에 PNG로 저장");
            Add(shotTop, btnShotColor, btnShotMono, lblShot);
            shot.Controls.Add(shotTop.Parent);
            shotBox = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Card };
            var shotCard = new Card(null, false) { Dock = DockStyle.Fill, Padding = new Padding(8) };
            shotCard.Controls.Add(shotBox);
            shot.Controls.Add(shotCard);
            body.Controls.Add(shot, 1, 0);
            lay.Controls.Add(body);
            return lay;
        }

        async void OnSetRead(object sender, EventArgs e)
        {
            if (meter == null || busy || autoRunning) return;
            IMeter m = meter;
            busy = true;
            UpdateUi();
            try
            {
                List<string> values = await Io(() => MeterSettings.Items.Select(s => m.Query(s.Cmd + "?").Trim()).ToList());
                for (int i = 0; i < values.Count; i++)
                {
                    setGrid.Rows[i].Cells[2].Value = MeterSettings.Items[i].Cmd.StartsWith(":PARameter") ? Fmt.ParLabel(values[i]) : values[i];
                    setGrid.Rows[i].Cells[3].Value = "";
                    setGrid.Rows[i].Cells[2].Style.ForeColor = Theme.Text;
                }
                Log("계측기 설정을 읽었습니다 (" + values.Count + "개 항목).");
            }
            catch (Exception ex)
            {
                if (IsDisconnect(ex, m)) OnLost("USB 연결이 끊어졌습니다: " + ex.Message);
                else MessageBox.Show(this, "설정을 읽지 못했습니다. 계측기 화면이 LCR 모드(일반 측정 화면)인지 확인하세요.\n\n" + ex.Message, "계측기 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                busy = false;
                UpdateUi();
            }
        }

        async void OnSetApply(object sender, EventArgs e)
        {
            if (meter == null || busy || autoRunning) return;
            setGrid.EndEdit();
            var changes = new List<KeyValuePair<int, string>>();
            for (int i = 0; i < MeterSettings.Items.Length; i++)
            {
                string v = Fmt.ParToken(Convert.ToString(setGrid.Rows[i].Cells[3].Value).Trim());
                if (v.Length > 0) changes.Add(new KeyValuePair<int, string>(i, v));
            }
            if (changes.Count == 0)
            {
                MessageBox.Show(this, "[새 값] 칸에 바꿀 값을 넣으세요. 먼저 [계측기에서 읽기]로 지금 값을 확인하면 편합니다.", "계측기 설정", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            IMeter m = meter;
            busy = true;
            UpdateUi();
            var failed = new List<string>();
            try
            {
                foreach (var c in changes)
                {
                    SettingItem s = MeterSettings.Items[c.Key];
                    string v = c.Value;
                    string esr = await Io(() => MeterSettings.Apply(m, s, v));
                    Log("설정: " + s.Cmd + " " + v + (esr == null ? "" : "  ※ 계측기가 받지 않음 (*ESR? = " + esr + ")"));
                    if (esr != null) failed.Add(s.Name + " = " + v);
                }
                List<string> values = await Io(() => MeterSettings.Items.Select(s => m.Query(s.Cmd + "?").Trim()).ToList());
                for (int i = 0; i < values.Count; i++)
                {
                    bool bad = changes.Any(c => c.Key == i) && failed.Contains(MeterSettings.Items[i].Name + " = " + changes.First(c => c.Key == i).Value);
                    setGrid.Rows[i].Cells[2].Value = MeterSettings.Items[i].Cmd.StartsWith(":PARameter") ? Fmt.ParLabel(values[i]) : values[i];
                    setGrid.Rows[i].Cells[2].Style.ForeColor = bad ? Theme.Red : changes.Any(c => c.Key == i) ? Theme.Green : Theme.Text;
                    if (!bad) setGrid.Rows[i].Cells[3].Value = "";
                }
                if (failed.Count > 0)
                    MessageBox.Show(this, "계측기가 받지 않은 값이 있습니다 (빨간색):\n\n" + string.Join("\n", failed) + "\n\n허용 범위와 다른 설정(예: 같은 파라미터 중복, 레인지 고정 여부)을 확인하세요.",
                        "계측기 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                if (IsDisconnect(ex, m)) OnLost("USB 연결이 끊어졌습니다: " + ex.Message);
                else MessageBox.Show(this, "설정 중 오류: " + ex.Message, "계측기 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                busy = false;
                UpdateUi();
            }
        }

        async void OnShot(bool mono)
        {
            if (meter == null || busy || autoRunning) return;
            IMeter m = meter;
            busy = true;
            UpdateUi();
            lblShot.Text = "계측기 화면을 받는 중...";
            try
            {
                SetTimeout(m, 30000);
                byte[] data = await Io(() => m.QueryBlock(":HCOPy:DATA? " + (mono ? "monochrome" : "color")));
                Directory.CreateDirectory(txtFolder.Text);
                string path = Path.Combine(txtFolder.Text, "SCREEN_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".png");
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms))
                {
                    img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Image old = shotBox.Image;
                    shotBox.Image = new Bitmap(img);
                    if (old != null) old.Dispose();
                }
                lblShot.Text = Path.GetFileName(path);
                Log("계측기 화면 저장: " + path);
            }
            catch (Exception ex)
            {
                lblShot.Text = "캡처 실패";
                if (IsDisconnect(ex, m)) OnLost("USB 연결이 끊어졌습니다: " + ex.Message);
                else MessageBox.Show(this, "계측기 화면을 받지 못했습니다.\n\n" + ex.Message, "화면 캡처", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                SetTimeout(m, 5000);
                busy = false;
                UpdateUi();
            }
        }

        // ---------------- 계산 도구 (계측기 없이) ----------------

        static ComboBox Combo(int width, IEnumerable<string> items, int selected)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
            c.Items.AddRange(items.Cast<object>().ToArray());
            c.SelectedIndex = selected;
            return c;
        }

        Control BuildToolTab()
        {
            var lay = TabLayout(2);
            var acc = Group("정확도 계산 (IM3536, 공식 앱 Calc Accuracy) · 측정값과 조건을 넣으면 오차 범위를 계산");
            cbAccPara = Combo(90, Im3536Calc.Paras, 3);
            txtAccMain = new TextBox { Width = 90, Text = "100u" };
            lblAccMainUnit = Lbl("H");
            lblAccSub = Lbl("Q");
            txtAccSub = new TextBox { Width = 70, Text = "50" };
            txtAccFreq = new TextBox { Width = 70, Text = "1k" };
            txtAccLevel = new TextBox { Width = 60, Text = "1" };
            var v = Lbl("V");
            Add(acc, Lbl("파라미터"), cbAccPara, Lbl("측정값"), txtAccMain, lblAccMainUnit, lblAccSub, txtAccSub, Lbl("주파수"), txtAccFreq, Lbl("레벨"), txtAccLevel, v);
            acc.SetFlowBreak(v, true);
            cbAccRange = Combo(80, Im3536Calc.Ranges, 4);
            var btnAuto = Btn("자동 레인지", delegate
            {
                try
                {
                    double z, ph;
                    AccInputs(out z, out ph);
                    cbAccRange.SelectedIndex = Im3536Calc.AutoRange(z);
                }
                catch (InvalidOperationException ex) { lblAccResult.Text = ex.Message; }
            });
            cbAccSpeed = Combo(80, Im3536Calc.Speeds, 1);
            cbAccCable = Combo(60, Im3536Calc.Cables, 0);
            cbAccBias = Combo(60, new[] { "OFF", "ON" }, 0);
            numAccTemp = new NumericUpDown { Width = 55, Minimum = 0, Maximum = 40, Value = 23 };
            var degC = Lbl("°C");
            Add(acc, Lbl("레인지"), cbAccRange, btnAuto, Lbl("SPEED"), cbAccSpeed, Lbl("케이블 길이"), cbAccCable, Lbl("DC 바이어스"), cbAccBias, Lbl("온도"), numAccTemp, degC);
            acc.SetFlowBreak(degC, true);
            lblAccResult = new Label { AutoSize = true, Margin = new Padding(10, 8, 3, 6), ForeColor = Theme.Text, Font = new Font(Theme.FontName, 10F) };
            Add(acc, Btn("계산", OnAccCalc, true), lblAccResult);
            cbAccPara.SelectedIndexChanged += delegate
            {
                int p = cbAccPara.SelectedIndex;
                lblAccMainUnit.Text = Im3536Calc.MainUnits[p];
                lblAccSub.Text = Im3536Calc.SubNames[p];
                txtAccSub.Enabled = txtAccFreq.Enabled = txtAccLevel.Enabled = p != 5;   // Rdc는 DC·1 V 고정
            };
            lay.Controls.Add(acc.Parent);

            var mr = Group("측정 범위 계산 (IM3536, 공식 앱 Calc Measure Range) · 레인지마다 잴 수 있는 Z·L·C 범위");
            cbMrRange = Combo(80, Im3536Calc.Ranges, 4);
            cbMrDc = Combo(70, new[] { "AC", "DC (Rdc)" }, 0);
            txtMrFreq = new TextBox { Width = 70, Text = "1k" };
            txtMrLevel = new TextBox { Width = 60, Text = "1" };
            cbMrDc.SelectedIndexChanged += delegate { txtMrFreq.Enabled = txtMrLevel.Enabled = cbMrDc.SelectedIndex == 0; };
            lblMrResult = new Label { AutoSize = true, Margin = new Padding(10, 8, 3, 6), ForeColor = Theme.Text, Font = new Font(Theme.FontName, 10F) };
            var v2 = Lbl("V");
            Add(mr, Lbl("레인지"), cbMrRange, Lbl("측정"), cbMrDc, Lbl("주파수"), txtMrFreq, Lbl("레벨"), txtMrLevel, v2);
            mr.SetFlowBreak(v2, true);
            Add(mr, Btn("계산", OnMrCalc, true), lblMrResult);
            lay.Controls.Add(mr.Parent);
            lay.Controls.Add(new Panel { BackColor = Theme.Bg });
            return lay;
        }

        // 1k, 100u, 2.2n 같은 SI 접두어 입력
        static bool TryParseSi(string text, out double v)
        {
            string t = text.Trim().Replace("µ", "u").Replace("μ", "u");
            double mul = 1;
            const string pre = "pnumkMG";
            double[] muls = { 1e-12, 1e-9, 1e-6, 1e-3, 1e3, 1e6, 1e9 };
            if (t.Length > 1 && pre.IndexOf(t[t.Length - 1]) >= 0) { mul = muls[pre.IndexOf(t[t.Length - 1])]; t = t.Substring(0, t.Length - 1); }
            bool ok = Fmt.TryParse(t, out v);
            v *= mul;
            return ok;
        }

        static double SiInput(TextBox t, string name)
        {
            double v;
            if (!TryParseSi(t.Text, out v)) throw new InvalidOperationException(name + " 값 '" + t.Text + "'를 읽을 수 없습니다 (예: 100u, 1k, 2.2n).");
            return v;
        }

        void AccInputs(out double z, out double ph)
        {
            int p = cbAccPara.SelectedIndex;
            double main = SiInput(txtAccMain, "측정값"), sub = p == 5 ? 0 : SiInput(txtAccSub, Im3536Calc.SubNames[p]), f = p == 5 ? 0 : SiInput(txtAccFreq, "주파수");
            Im3536Calc.ToZPh(p, main, sub, f, out z, out ph);
        }

        static string Eng(double v, string unit)
        {
            if (double.IsPositiveInfinity(v)) return "+∞";
            if (double.IsNegativeInfinity(v)) return "-∞";
            return Fmt.Pretty(unit == "H" ? "LS" : unit == "F" ? "CS" : "Z", v.ToString("R", CultureInfo.InvariantCulture)).Split('=')[1].Trim();
        }

        void OnAccCalc(object sender, EventArgs e)
        {
            try
            {
                int p = cbAccPara.SelectedIndex;
                double main = SiInput(txtAccMain, "측정값"), sub = p == 5 ? 0 : SiInput(txtAccSub, Im3536Calc.SubNames[p]);
                double f = p == 5 ? 0 : SiInput(txtAccFreq, "주파수"), lev = p == 5 ? 1 : SiInput(txtAccLevel, "레벨");
                AccResult r = Im3536Calc.Accuracy(p, main, sub, f, lev, cbAccRange.SelectedIndex, cbAccSpeed.SelectedIndex, cbAccCable.SelectedIndex, cbAccBias.SelectedIndex == 1, (int)numAccTemp.Value);
                Func<double, string> g = x => double.IsInfinity(x) ? (x > 0 ? "+∞" : "-∞") : x.ToString("G6", CultureInfo.InvariantCulture);
                string unit = Im3536Calc.MainUnits[p];
                bool covers = Im3536Calc.Covers(cbAccRange.SelectedIndex, r.Z);
                lblAccResult.ForeColor = covers ? Theme.Text : Theme.Red;
                lblAccResult.Text = (covers ? "" : "※ |Z|가 " + Im3536Calc.Ranges[cbAccRange.SelectedIndex] + " 레인지의 측정 범위 밖입니다. [자동 레인지]를 누르세요 (계산식은 공식 앱 그대로라 값은 나오지만 믿을 수 없음)\n")
                    + Im3536Calc.Paras[p].Split(' ')[0] + " 정확도: " + r.MainMinPct.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) + " % ~ "
                    + r.MainMaxPct.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) + " %   ("
                    + Eng(Math.Abs(main) * (1 + r.MainMinPct / 100), unit) + " ~ " + Eng(Math.Abs(main) * (1 + r.MainMaxPct / 100), unit) + ")"
                    + (p == 5 ? "" : "\n" + Im3536Calc.SubNames[p] + " 범위: " + g(r.SubMin) + " ~ " + g(r.SubMax))
                    + "\n|Z| = " + Eng(r.Z, "Ω") + ", θ = " + r.Ph.ToString("0.###", CultureInfo.InvariantCulture) + "°  ·  권장 레인지 " + Im3536Calc.Ranges[Im3536Calc.AutoRange(r.Z)]
                    + "\n" + r.Formula;
            }
            catch (InvalidOperationException ex)
            {
                lblAccResult.ForeColor = Theme.Red;
                lblAccResult.Text = ex.Message;
            }
        }

        void OnMrCalc(object sender, EventArgs e)
        {
            try
            {
                bool dc = cbMrDc.SelectedIndex == 1;
                double f = dc ? 0 : SiInput(txtMrFreq, "주파수"), lev = dc ? 1 : SiInput(txtMrLevel, "레벨"), zmin, zmax;
                Im3536Calc.MeasRange(cbMrRange.SelectedIndex, dc, f, lev, out zmin, out zmax);
                lblMrResult.ForeColor = Theme.Text;
                lblMrResult.Text = (dc ? "Rdc" : "|Z|") + ": " + Eng(zmin, "Ω") + " ~ " + Eng(zmax, "Ω")
                    + (dc ? "" : "\nL (Ls·Lp): " + Eng(zmin / (2 * Math.PI * f), "H") + " ~ " + Eng(zmax / (2 * Math.PI * f), "H")
                        + "\nC (Cs·Cp): " + Eng(1 / (2 * Math.PI * f * zmax), "F") + " ~ " + Eng(1 / (2 * Math.PI * f * zmin), "F"));
            }
            catch (InvalidOperationException ex)
            {
                lblMrResult.ForeColor = Theme.Red;
                lblMrResult.Text = ex.Message;
            }
        }
    }
}
