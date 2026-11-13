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
        // ---------------- 측정 ----------------

        // 성공하면 계측기 응답(주파수별), 실패하면 null
        async Task<List<string>> MeasureOnce()
        {
            if (meter == null || busy) return null;
            IMeter m = meter;
            bool trg = chkTrg.Checked;
            List<double> list = freqs;
            busy = true;
            UpdateUi();
            try
            {
                List<string> responses;
                var monis = new List<string>();
                if (list.Count == 0)
                {
                    // EXT 트리거일 때는 한 줄로 보내 측정이 끝난 뒤의 값을 받음
                    string response = await Io(() => { string r = m.Query(trg ? "*TRG;:MEAS?" : ":MEAS?"); monis.Add(m.Query(":MONI?")); return r; });
                    responses = new List<string> { response };
                }
                else
                {
                    responses = await Io(() => MeasureFrequencies(m, list, monis));
                }
                AddRow(responses, monis);
                return responses;
            }
            catch (Exception ex)
            {
                if (IsDisconnect(ex, m)) OnLost("USB 연결이 끊어졌습니다: " + ex.Message);
                else
                {
                    Log("측정 실패: " + ex.Message);
                    ShowFailure(ex.Message);
                }
                return null;
            }
            finally
            {
                busy = false;
                UpdateUi();
            }
        }

        // 주파수를 하나씩 바꾸고 EXT 트리거로 새로 측정. 끝나면 원래 주파수와 트리거로 되돌림.
        // monis가 있으면 주파수마다 :MONI? (모니터 전압,전류)도 받아 넣음
        internal static List<string> MeasureFrequencies(IMeter m, List<double> list, List<string> monis = null)
        {
            var responses = new List<string>();
            string originalTrigger = m.Query(":TRIG?");
            string originalFreq = m.Query(":FREQ?");
            try
            {
                m.Send(":TRIG EXT");
                foreach (double hz in list)
                {
                    m.Send(":FREQ " + hz.ToString("R", CultureInfo.InvariantCulture));
                    responses.Add(m.Query("*TRG;:MEAS?"));
                    if (monis != null) monis.Add(m.Query(":MONI?"));
                }
            }
            finally
            {
                m.Send(":FREQ " + originalFreq);
                m.Send(":TRIG " + originalTrigger);
            }
            return responses;
        }

        void AddRow(List<string> responses, List<string> monis)
        {
            foreach (string response in responses)
            {
                int count = Fmt.Tokens(response).Length;
                if (count != pars.Count)
                    Log("※ 응답 값 개수(" + count + ")가 파라미터 개수(" + pars.Count + ")와 다릅니다. 원본응답 열을 확인하세요.");
            }

            lastResponses = responses;
            lastPars = pars;
            string warn = Permeability.RowWarning(Permeability.LevelOf(condText), monis, responses.Count > 1 ? freqLabels : new List<string>());
            if (warn.Length > 0 && !levelWarned)
            {
                Log("※ 레벨 경고 (#" + (rows.Count + 1) + "): " + warn);   // 경고가 새로 생길 때만 로그·경고음 (자동 측정 중 반복 방지)
                SystemSounds.Exclamation.Play();
            }
            levelWarned = warn.Length > 0;
            string[] row = Fmt.RowFields(rows.Count + 1, DateTime.Now, txtMemo.Text, condText, pars, responses, unitIndex, string.Join(" | ", monis), warn);
            rows.Add(row);
            WriteCsv(row);   // 데이터부터 저장: 아래 화면 표시에서 오류가 나도 기록은 남음
            grid.Rows.Add(row);
            try
            {
                grid.FirstDisplayedScrollingRowIndex = grid.Rows.Count - 1;
                grid.ClearSelection();
                grid.Rows[grid.Rows.Count - 1].Selected = true;
            }
            catch (InvalidOperationException) { }   // 창이 작아 표 높이가 0이면 스크롤할 수 없음 ("행을 표시할 공간이 없습니다")
            foreach (var pt in Fmt.TrendPoints(pars, freqLabels, row))
            {
                Charting.Series s = trend.Series.FindByName(pt.Key);
                if (s == null) trend.Series.Add(s = new Charting.Series(pt.Key) { ChartType = Charting.SeriesChartType.Line, BorderWidth = 2, MarkerStyle = Charting.MarkerStyle.Circle, MarkerSize = 5 });
                s.Points.AddXY(rows.Count, pt.Value);
            }

            var parts = new List<string>();
            if (responses.Count == 1)
            {
                string[] tokens = Fmt.Tokens(responses[0]);
                for (int i = 0; i < pars.Count && i < tokens.Length; i++)
                    parts.Add(Fmt.Pretty(pars[i], tokens[i]));
            }
            else
            {
                // 주파수가 여러 개면 주파수별로 인덕턴스(없으면 첫 파라미터)만 크게 표시
                int shown = Math.Max(0, pars.FindIndex(Fmt.IsInductance));
                for (int f = 0; f < responses.Count; f++)
                {
                    string[] tokens = Fmt.Tokens(responses[f]);
                    if (shown < tokens.Length && shown < pars.Count)
                        parts.Add(freqLabels[f] + ": " + Fmt.Pretty(pars[shown], tokens[shown]).Split('=')[1].Trim());
                }
            }
            lblLast.Text = "#" + rows.Count + "   " + (parts.Count > 0 ? string.Join("    ", parts) : string.Join(" | ", responses))
                + (warn.Length > 0 ? "    ※ 레벨 경고 (표의 레벨 경고 열 참고)" : "");
            lblLast.ForeColor = warn.Length > 0 ? Theme.Red : Theme.Text;
        }

        // 측정 실패를 로그에만 남기면 눈에 띄지 않으므로 최근 측정값 칸에 빨간 글씨로 표시하고 경고음
        void ShowFailure(string message)
        {
            string first = message.Split('\n')[0].Trim();
            lblLast.Text = "측정 실패: " + (first.Length > 60 ? first.Substring(0, 60) + "..." : first);
            lblLast.ForeColor = Theme.Red;
            SystemSounds.Hand.Play();
        }

        void WriteCsv(string[] row)
        {
            try
            {
                if (csv.FilePath == null)
                {
                    csv.Start(txtFolder.Text, Fmt.HeaderFields(pars, unitIndex, freqLabels));
                    Log("저장 파일: " + csv.FilePath);
                }
                csv.Append(row);
                lblFile.Text = Path.GetFileName(csv.FilePath) + " (" + rows.Count + "행)";
                lblFile.ForeColor = Theme.Secondary;
            }
            catch (Exception ex)
            {
                lblFile.Text = "저장 대기 " + csv.PendingCount + "행 - 파일이 열려 있는지 확인";
                lblFile.ForeColor = Theme.Red;
                Log("CSV 기록 실패(다음 측정 때 다시 시도): " + ex.Message);
            }
        }

        async void OnAuto(object sender, EventArgs e)
        {
            if (autoRunning)
            {
                autoRunning = false;
                return;
            }
            autoRunning = true;
            UpdateUi();
            int count = (int)numCount.Value;
            int intervalMs = (int)(numInterval.Value * 1000);
            Log("연속 측정 시작: 간격 " + numInterval.Value + "초, 횟수 " + (count == 0 ? "무한" : count.ToString()));
            int done = 0;
            while (autoRunning && meter != null && (count == 0 || done < count))
            {
                var sw = Stopwatch.StartNew();
                if (await MeasureOnce() == null) break;
                done++;
                while (autoRunning && (count == 0 || done < count) && sw.ElapsedMilliseconds < intervalMs)
                    await Task.Delay(50);
            }
            autoRunning = false;
            Log("연속 측정 종료: " + done + "회");
            UpdateUi();
        }

        // ---------------- 스프레드시트 입력 ----------------

        void UnregisterHotkeys()
        {
            if (!hotkeyRegistered || !IsHandleCreated) return;
            KeyboardInput.UnregisterHotKey(Handle, HotkeyId);
            KeyboardInput.UnregisterHotKey(Handle, RepeatHotkeyId);
            hotkeyRegistered = false;
        }

        void UpdateRepeatHint()
        {
            lblRepeat.Text = "Shift+" + HotkeyChoices[cbHotkey.SelectedIndex] + ": 마지막 값 다시 입력";
        }

        void ApplyHotkey()
        {
            UpdateRepeatHint();
            if (!IsHandleCreated) return;
            UnregisterHotkeys();
            if (!chkSheet.Checked) return;
            Keys key = HotkeyChoices[cbHotkey.SelectedIndex];
            const uint MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
            hotkeyRegistered = KeyboardInput.RegisterHotKey(Handle, HotkeyId, MOD_NOREPEAT, (uint)key);
            if (hotkeyRegistered)
            {
                Log("스프레드시트 입력 켜짐: 셀을 선택하고 " + key + " 키를 누르세요.");
                if (KeyboardInput.RegisterHotKey(Handle, RepeatHotkeyId, MOD_SHIFT | MOD_NOREPEAT, (uint)key))
                    Log("마지막 값 다시 입력: Shift+" + key);
                else
                    Log("※ Shift+" + key + " 키를 다른 프로그램이 쓰고 있어 '마지막 값 다시 입력'은 쓸 수 없습니다.");
            }
            else
                Log("※ " + key + " 키를 다른 프로그램이 쓰고 있습니다. 다른 키를 고르세요.");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId)
                OnSheetHotkey();
            else if (m.Msg == WM_HOTKEY && (int)m.WParam == RepeatHotkeyId)
                OnRepeatHotkey();
            base.WndProc(ref m);
        }

        // 다시 측정하지 않고 직전 측정값을 선택한 셀에 다시 입력 (셀 위치를 잘못 잡았을 때)
        async void OnRepeatHotkey()
        {
            if (busy)
            {
                SystemSounds.Hand.Play();
                Log("다시 입력: 측정 중이라 무시했습니다.");
                return;
            }
            if (lastResponses == null)
            {
                SystemSounds.Hand.Play();
                Log("다시 입력: 아직 측정한 값이 없습니다.");
                return;
            }
            IntPtr target = KeyboardInput.GetForegroundWindow();
            if (target == Handle)
            {
                Log("다시 입력: 스프레드시트에서 셀을 선택한 뒤 눌러 주세요.");
                return;
            }
            await TypeIntoSheet(target, lastPars, lastResponses, "마지막 값 다시 입력 (#" + rows.Count + ")");
        }

        // 값을 Tab/Enter로 구분해 입력. 입력 전에 Shift 등에서 손을 뗄 때까지 기다리고, 창이 바뀌었으면 입력하지 않음.
        async Task TypeIntoSheet(IntPtr target, List<string> valuePars, List<string> responses, string what)
        {
            await KeyboardInput.WaitModifiersReleased();
            if (KeyboardInput.GetForegroundWindow() != target)
            {
                SystemSounds.Hand.Play();
                Log("단축키: 다른 창으로 바뀌어 입력하지 않았습니다 (CSV에는 저장됨).");
                return;
            }
            List<string> values = Fmt.TypedValues(valuePars, responses, unitIndex, cbSheetValues.SelectedIndex == 0);
            Keys separator = cbSheetSep.SelectedIndex == 0 ? Keys.Tab : Keys.Enter;
            try
            {
                for (int i = 0; i < values.Count; i++)
                {
                    KeyboardInput.TypeText(values[i]);
                    await Task.Delay(30);
                    if (i < values.Count - 1)
                    {
                        KeyboardInput.PressKey(separator);
                        await Task.Delay(30);
                    }
                }
                if (cbSheetEnd.SelectedIndex == 0) KeyboardInput.PressKey(Keys.Enter);
                else if (cbSheetEnd.SelectedIndex == 1) KeyboardInput.PressKey(Keys.Tab);
                SystemSounds.Asterisk.Play();
                Log(what + ": " + string.Join(", ", values));
            }
            catch (Exception ex)
            {
                SystemSounds.Hand.Play();
                Log("스프레드시트 입력 실패: " + ex.Message);
            }
        }

        async void OnSheetHotkey()
        {
            if (meter == null || busy || autoRunning)
            {
                SystemSounds.Hand.Play();
                Log(meter == null ? "단축키: 계측기가 연결되어 있지 않습니다." : "단축키: 측정 중이라 무시했습니다.");
                return;
            }

            // 단축키를 누른 순간의 창(스프레드시트)에 입력. 이 프로그램 창이면 측정만 함.
            IntPtr target = KeyboardInput.GetForegroundWindow();
            List<string> responses = await MeasureOnce();
            if (responses == null) return;   // 실패 경고음은 MeasureOnce(또는 연결 끊김 처리)에서 이미 냄
            if (target == Handle)
            {
                Log("단축키: 이 프로그램 창이 선택되어 있어 측정만 했습니다.");
                return;
            }
            await TypeIntoSheet(target, pars, responses, "스프레드시트에 입력");
        }

        // ---------------- 저장 ----------------

        void SetupColumns()
        {
            grid.Columns.Clear();
            List<string> header = Fmt.HeaderFields(pars, unitIndex, freqLabels);
            foreach (string h in header)
            {
                int c = grid.Columns.Add(h, h);
                grid.Columns[c].SortMode = DataGridViewColumnSortMode.NotSortable;
            }
            grid.Columns[0].FillWeight = 30;
            grid.Columns[0].MinimumWidth = 44;
            grid.Columns[1].FillWeight = 110;
            grid.Columns[3].FillWeight = 150;   // 측정조건
            trend.Series.Clear();
            int shown = Math.Max(0, pars.FindIndex(Fmt.IsInductance));
            trend.ChartAreas[0].AxisY.Title = pars.Count > 0 ? Fmt.Header(pars[shown], unitIndex) : "";
        }

        void StartNewTable(string message)
        {
            rows.Clear();
            grid.Rows.Clear();
            csv.Reset();
            SetupColumns();
            lblLast.Text = "-";
            lblFile.Text = "다음 측정 시 새 파일 생성";
            lblFile.ForeColor = Theme.Secondary;
            if (message != null) Log(message);
        }

        void OnUnitChanged(object sender, EventArgs e)
        {
            if (cbUnit.SelectedIndex == unitIndex) return;
            if (rows.Count > 0)
            {
                var answer = MessageBox.Show(this,
                    "단위를 바꾸면 지금 표를 비우고 새 CSV 파일로 기록합니다.\n(이미 저장된 파일은 그대로 남습니다)",
                    "단위 변경", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer != DialogResult.OK)
                {
                    cbUnit.SelectedIndex = unitIndex;
                    return;
                }
            }
            unitIndex = cbUnit.SelectedIndex;
            StartNewTable("인덕턴스 단위: " + Fmt.InductanceUnits[unitIndex]);
        }

        void OnPickFolder(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog { SelectedPath = txtFolder.Text })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                txtFolder.Text = dlg.SelectedPath;
                if (rows.Count > 0) StartNewTable("저장 폴더 변경: 다음 측정부터 새 파일에 기록합니다.");
            }
        }

        void OnOpenFolder(object sender, EventArgs e)
        {
            Directory.CreateDirectory(txtFolder.Text);
            Process.Start("explorer.exe", "\"" + txtFolder.Text + "\"");
        }

        void OnExport(object sender, EventArgs e)
        {
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "저장할 측정 데이터가 없습니다.", "저장");
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                FileName = "LCR_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".csv",
                InitialDirectory = txtFolder.Text
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var lines = new List<string> { Fmt.CsvLine(Fmt.HeaderFields(pars, unitIndex, freqLabels)) };
                lines.AddRange(rows.Select(r => Fmt.CsvLine(r)));
                File.WriteAllText(dlg.FileName, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
                Log("저장 완료: " + dlg.FileName);
            }
        }

        // ---------------- 직접 명령 ----------------

        async void OnSendCommand(object sender, EventArgs e)
        {
            string cmd = txtCmd.Text.Trim();
            if (cmd.Length == 0 || meter == null || busy) return;
            IMeter m = meter;
            busy = true;
            UpdateUi();
            try
            {
                Log("> " + cmd);
                if (cmd.Contains("?"))
                {
                    string response = await Io(() => m.Query(cmd));
                    Log("< " + response);
                }
                else
                {
                    await Io(() => { m.Send(cmd); return true; });
                }
            }
            catch (Exception ex)
            {
                if (IsDisconnect(ex, m)) OnLost("USB 연결이 끊어졌습니다: " + ex.Message);
                else Log("명령 실패: " + ex.Message);
            }
            finally
            {
                busy = false;
                UpdateUi();
            }
        }
    }
}
