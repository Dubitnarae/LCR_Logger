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
    // ------------------------------------------------------------------
    // 화면
    // ------------------------------------------------------------------

    partial class MainForm : Form
    {
        ComboBox cbIf, cbPort, cbBaud, cbUnit, cbSpeed, cbBias, cbLevel;
        ComboBox[] cbPar;                           // PAR2~PAR4 (PAR1은 Ls 고정)
        ComboBox cbPreset;                          // 측정 조건 프리셋 (이름을 직접 입력해 저장)
        Button btnApplyCond, btnPresetSave, btnPresetDelete;
        string condText = "";                       // CSV 측정조건 열에 남길 계측기의 실제 조건
        string currentIdn;                          // 연결한 계측기 *IDN? (복원 못 한 설정을 같은 계측기에만 되돌리기 위함)
        string updateAttemptDay;                    // 오늘 업데이트 확인을 시도했는지 (실패해도 1초마다 다시 시도하지 않도록)
        const string PresetPrefix = "Preset.";
        Label lblLevelUnit;
        TextBox txtCv, txtBias;                     // txtCv: 측정 신호 레벨 값 (V 또는 mA)
        // 측정 조건의 그 밖의 항목: 명령 → 입력 칸(빈칸) 또는 선택 상자("변경 안 함" + 계측기 설정 탭과 같은 선택지)
        static readonly string[] CondExtraKeys = { ":FREQ", ":RANG:AUTO", ":RANG", ":RANG:LOWZ", ":AVER", ":LIM", ":LIM:VOLT", ":LIM:CURR",
            ":TRIG:DEL", ":SSO", ":SSO:WAIT", ":DCR:DCD", ":DCR:ADJD", ":DCR:LFR" };
        readonly Dictionary<string, Control> condExtra = new Dictionary<string, Control>();
        Switch chkRestoreCond;
        MeasCond connectCond;                       // 연결할 때 적용한 측정 조건 (재연결 때 다시 보냄)
        Dictionary<string, string> savedCond;       // 바꾸기 전 계측기 값 (연결 해제할 때 복원)
        Button btnRefresh, btnConnect, btnMeasure, btnAuto, btnFolder, btnOpenFolder, btnNewFile, btnExport, btnClear, btnSend, btnUpdate;
        CheckBox chkDemo, chkTrg, chkMulti, chkSheet, chkAutoUpdate, chkReconnect;
        Label lblRepeat;
        bool updating;

        // USB 자동 재연결 상태
        readonly System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        static readonly string[] ParChoices = { "변경 안 함", "OFF", "Z", "Y", "PHASE", "CS", "CP", "D", "LP", "Q", "RS", "G", "RP", "X", "B", "RDC", "S", "E" };
        bool waitingReconnect, reconnecting, lostDemo;
        Func<IMeter> netOpener, lostOpener;   // LAN·GPIB 연결을 다시 여는 방법 (USB·RS-232C·데모는 null)
        int ifShown = -1;
        string lanAddr = "192.168.0.1:3500", gpibAddr = "1";
        string lostPort, lastPortList;
        int lostBaud, reconnectTries, reconnectGen;
        DateTime portSeenAt = DateTime.MinValue;

        // 마지막 측정값 (다시 입력용)
        List<string> lastResponses, lastPars;
        bool levelWarned;   // 직전 측정에 레벨 경고가 있었는지
        const int RepeatHotkeyId = 0x4C44;
        ComboBox cbHotkey, cbSheetValues, cbSheetSep, cbSheetEnd;
        NumericUpDown numInterval, numCount;
        TextBox txtMemo, txtFolder, txtCmd, txtLog, txtAddr;
        TextBox[] txtFreq;
        Label lblModel, lblFile, lblLast, lblPort, lblBaud, lblAddr;
        DataGridView grid;
        Charting.Chart trend;   // 기록 탭 오른쪽: 측정 번호별 인덕턴스 추이

        readonly object ioLock = new object();
        readonly Settings settings = new Settings();
        readonly CsvWriter csv = new CsvWriter();
        readonly List<string[]> rows = new List<string[]>();
        IMeter meter;
        List<string> pars = new List<string>();
        List<double> freqs = new List<double>();          // 다중 주파수 측정일 때만 채워짐
        List<string> freqLabels = new List<string>();
        int unitIndex = 2;
        bool busy, autoRunning, hotkeyRegistered;
        const int HotkeyId = 0x4C43;   // 'LC'
        const int WM_HOTKEY = 0x0312;
        static readonly Keys[] HotkeyChoices = { Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12 };

        readonly bool justUpdated;

        // 복소 투자율 탭
        TextBox txtMuName, txtOD, txtID, txtH, txtLe, txtAe, txtRw, txtMuStart, txtMuStop, txtMuLevel;
        NumericUpDown numTurns, numPoints, numAvg, numDelay;
        CheckBox chkDirect;
        ComboBox cbSpacing, cbMuLevel, cbMuSpeed;
        Button btnDcr, btnMuStart, btnMuStop, btnMuCsv, btnMuPng;
        ProgressBar muProgress;
        Label lblMuLevelUnit, lblMuWarn, lblMuFile;
        Charting.Chart muChart;
        Dictionary<string, Control> muSettings;     // 설정 파일 키 → 입력칸 (순서대로 불러옴)
        bool muRunning, muStopRequested;
        Dictionary<string, string> muSaved;         // 스윕 전 계측기 값 (끝나면 복원, 못 하면 연결 해제 때 복원)
        List<MuRow> muRows = new List<MuRow>();
        List<KeyValuePair<string, string>> muInfo;  // CSV 위쪽 측정 조건
        string muStem;                              // 마지막 자동 저장 파일 이름 (확장자 없음)

        // 스윕 측정 탭 (측정 전 계측기 값은 복소 투자율과 같은 muSaved에 보관: 두 스윕은 동시에 돌지 않음)
        TextBox txtSwName, txtSwStart, txtSwStop, txtSwList;
        ComboBox cbSwItem, cbSwSpacing;
        NumericUpDown numSwPoints, numSwRepeat, numSwRepeatWait, numSwDelay;
        Button btnSwMake, btnSwStart, btnSwStop, btnSwCsv, btnSwPng;
        ProgressBar swProgress;
        Label lblSwFile, lblSwUnit;
        Charting.Chart swChart;
        Dictionary<string, Control> swSettings;
        bool swRunning, swStopRequested;
        List<SweepRow> swRows = new List<SweepRow>();
        List<KeyValuePair<string, string>> swInfo;
        List<string> swPars = new List<string>();
        int swItem;
        string swStem;

        // 계측기 설정 탭
        DataGridView setGrid;
        Button btnSetRead, btnSetApply, btnShotColor, btnShotMono;
        PictureBox shotBox;
        Label lblShot;

        // 계산 도구 탭
        ComboBox cbAccPara, cbAccRange, cbAccSpeed, cbAccCable, cbAccBias, cbMrRange, cbMrDc;
        TextBox txtAccMain, txtAccSub, txtAccFreq, txtAccLevel, txtMrFreq, txtMrLevel;
        NumericUpDown numAccTemp;
        Label lblAccMainUnit, lblAccSub, lblAccResult, lblMrResult;

        public MainForm(bool justUpdated)
        {
            this.justUpdated = justUpdated;
            Text = "LCR Logger " + Updater.CurrentText + " - 인덕턴스 측정 기록";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }   // exe에 넣은 icon.ico (build.bat /win32icon)
            Font = new Font(Theme.FontName, 9F);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1100, Math.Max(620, Math.Min(960, Screen.PrimaryScreen.WorkingArea.Height - 60)));
            MinimumSize = new Size(860, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            LoadSettings();
            RefreshPorts();
            UpdateUi();
            Log("프로그램 시작 (버전 " + Updater.CurrentText + "). HIOKI 장비는 포트 목록에 ★ 표시됩니다.");
            if (justUpdated) Log("버전 " + Updater.CurrentText + "(으)로 업데이트했습니다.");
        }

        const int GridRow = 6;   // layout에서 측정 기록 표가 들어 있는 행 (카드 6개 다음)

        // 남는 공간이 최소 높이보다 작으면 채움 행을 고정 높이로 바꿔 스크롤바가 생기게 함. 공간이 넉넉하면 원래대로 채움
        static void FitFillRow(TableLayoutPanel layout, int row, int min)
        {
            if (layout.IsDisposed) return;
            int[] heights = layout.GetRowHeights();
            if (heights.Length <= row) return;
            int others = heights.Sum() - heights[row];
            int room = layout.ClientSize.Height - layout.Padding.Vertical - others;
            RowStyle style = layout.RowStyles[row];
            if (room >= min)
            {
                if (style.SizeType != SizeType.Percent) { style.SizeType = SizeType.Percent; style.Height = 100; }
            }
            else if (style.SizeType != SizeType.Absolute || (int)style.Height != min)
            {
                style.SizeType = SizeType.Absolute;
                style.Height = min;
            }
        }

        // 화면이 작아 카드들이 높이를 다 차지하면 채움 행 높이가 0이 됨 → 최소 높이를 주고 스크롤. 배치가 끝난 뒤 높이를 보고 판단
        static void KeepFillRow(TableLayoutPanel layout, int row, Func<int> min)
        {
            bool pending = false;
            layout.AutoScroll = true;
            layout.Layout += delegate
            {
                if (pending || !layout.IsHandleCreated) return;
                pending = true;
                layout.BeginInvoke((Action)(() => { pending = false; FitFillRow(layout, row, min()); }));
            };
        }

        void BuildUi()
        {
            // 바깥: 연결 카드 / 탭(인덕턴스 기록, 복소 투자율) / 로그. 연결과 로그는 두 탭이 같이 씀
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 10), BackColor = Theme.Bg };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            Controls.Add(outer);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(0, 8, 4, 0), BackColor = Theme.Bg };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 카드가 창 너비에 맞춰 줄바꿈되도록
            for (int i = 0; i < GridRow; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            KeepFillRow(layout, GridRow, () => Font.Height * 12);   // 표 행 5~6개가 보이는 정도 (DPI에 맞춰 커짐)

            var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Point(16, 5) };
            var tabLog = new TabPage("인덕턴스 기록") { BackColor = Theme.Bg };
            var tabMu = new TabPage("복소 투자율 (μ′, μ″)") { BackColor = Theme.Bg };
            tabLog.Controls.Add(layout);
            tabMu.Controls.Add(BuildMuTab());
            tabs.TabPages.Add(tabLog);
            tabs.TabPages.Add(tabMu);
            foreach (var t in new[] { new { Name = "스윕 측정", Body = BuildSweepTab() }, new { Name = "계측기 설정", Body = BuildSetTab() }, new { Name = "계산 도구", Body = BuildToolTab() } })
            {
                var page = new TabPage(t.Name) { BackColor = Theme.Bg };
                page.Controls.Add(t.Body);
                tabs.TabPages.Add(page);
            }
            // Enter = 측정은 기록 탭에서만 (투자율 탭 입력칸에서 Enter를 눌러도 기록 측정이 되지 않도록)
            tabs.SelectedIndexChanged += delegate { AcceptButton = tabs.SelectedIndex == 0 ? btnMeasure : null; };

            // 1. 연결
            var conn = Group("1. 연결");
            cbIf = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            cbIf.Items.AddRange(new object[] { "USB / RS-232C", "LAN", "GPIB" });
            cbIf.SelectedIndex = 0;
            txtAddr = new TextBox { Width = 170 };
            lblPort = Lbl("COM 포트");
            lblBaud = Lbl("속도(RS-232C만)");
            lblAddr = Lbl("IP:포트");
            cbPort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
            btnRefresh = Btn("새로고침", delegate { RefreshPorts(); });
            cbBaud = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            cbBaud.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200" });
            cbBaud.SelectedIndex = 0;
            chkDemo = new Switch { Text = "데모 모드(계측기 없이 시험)", Margin = new Padding(12, 4, 3, 3) };
            chkReconnect = new Switch { Text = "USB 자동 재연결", Margin = new Padding(12, 4, 3, 3) };
            btnConnect = Btn("연결", OnConnect, true);
            btnConnect.Width = 90;
            lblModel = new Label { AutoSize = true, Margin = new Padding(8, 9, 3, 3), ForeColor = Theme.Secondary, Text = "연결 안 됨" };
            Add(conn, Lbl("연결 방식"), cbIf, lblPort, cbPort, btnRefresh, lblBaud, cbBaud, lblAddr, txtAddr, chkDemo, chkReconnect, btnConnect, lblModel);
            outer.Controls.Add(conn.Parent);
            outer.Controls.Add(tabs);

            // 2. 측정
            var meas = Group("2. 측정");
            cbUnit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
            cbUnit.Items.AddRange(Fmt.InductanceUnits);
            cbUnit.SelectedIndex = unitIndex;
            cbUnit.SelectedIndexChanged += OnUnitChanged;
            txtMemo = new TextBox { Width = 150 };
            btnMeasure = Btn("측정 (Enter)", async delegate { await MeasureOnce(); }, true);
            btnMeasure.Width = 110;
            chkTrg = new Switch { Text = "누를 때마다 새로 측정(계측기 EXT 트리거)", Margin = new Padding(12, 4, 3, 3) };
            numInterval = new NumericUpDown { Width = 60, DecimalPlaces = 1, Minimum = 0.2M, Maximum = 3600, Value = 1, Increment = 0.5M };
            numCount = new NumericUpDown { Width = 60, Minimum = 0, Maximum = 100000, Value = 10 };
            btnAuto = Btn("연속 측정 시작", OnAuto);
            btnAuto.Width = 120;
            Add(meas, Lbl("L 단위"), cbUnit, Lbl("메모"), txtMemo, btnMeasure, chkTrg,
                Lbl("간격(초)"), numInterval, Lbl("횟수(0=무한)"), numCount, btnAuto);
            meas.SetFlowBreak(chkTrg, true);
            layout.Controls.Add(meas.Parent);

            // 다중 주파수: LCR 모드에서 주파수를 바꿔 가며 한 번에 측정 (연속 측정 모드 대신)
            var multi = Group("다중 주파수 측정 (연결 전에 설정)");
            chkMulti = new Switch { Text = "사용 - 측정 한 번에 아래 주파수를 차례로 측정해 한 행에 기록", Margin = new Padding(3, 4, 8, 3) };
            multi.Controls.Add(chkMulti);
            multi.SetFlowBreak(chkMulti, true);
            txtFreq = new TextBox[8];
            string[] defaults = { "1k", "100k", "200k", "1M", "", "", "", "" };
            for (int i = 0; i < txtFreq.Length; i++)
            {
                txtFreq[i] = new TextBox { Width = 70, Text = defaults[i] };
                Add(multi, Lbl("주파수" + (i + 1)), txtFreq[i]);
                if (i == 3) multi.SetFlowBreak(txtFreq[i], true);   // 4칸씩 두 줄
            }
            var hint = Lbl("Hz · 예: 120, 1k, 100k, 1M · 빈칸은 건너뜀");
            hint.ForeColor = Theme.Secondary;
            Add(multi, hint);
            layout.Controls.Add(multi.Parent);

            // 측정 조건: 연결할 때 보내고, 연결을 해제하면 원래 값으로 복원. 모든 주파수에 같은 조건 적용
            var cond = Group("측정 조건 (연결 전에 설정)");
            cbLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            cbLevel.Items.AddRange(new object[] { "변경 안 함", "V (개방전압)", "CV (정전압)", "CC (정전류)" });
            cbLevel.SelectedIndex = 0;
            txtCv = new TextBox { Width = 60, Text = "1.000" };
            lblLevelUnit = Lbl("V");
            cbLevel.SelectedIndexChanged += delegate
            {
                bool cc = cbLevel.SelectedIndex == 3;
                lblLevelUnit.Text = cc ? "mA" : "V";
                txtCv.Text = cc ? "10" : "1.000";   // 종류를 바꾸면 그 종류의 기본값으로
            };
            cbSpeed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            cbSpeed.Items.AddRange(new object[] { "변경 안 함", "FAST", "MED", "SLOW", "SLOW2" });
            cbSpeed.SelectedIndex = 0;
            cbBias = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            cbBias.Items.AddRange(new object[] { "변경 안 함", "OFF", "ON" });
            cbBias.SelectedIndex = 0;
            txtBias = new TextBox { Width = 60, Text = "0.00" };
            var condHint = Lbl("V·CV 0.005~5 V · CC mA · DC 바이어스 0~2.5 V · 허용 범위는 조건에 따라 달라지며, 계측기가 받은 값은 로그에 표시");
            condHint.ForeColor = Theme.Secondary;
            Add(cond, Lbl("측정 신호"), cbLevel, txtCv, lblLevelUnit, Lbl("SPEED"), cbSpeed, Lbl("DC 바이어스"), cbBias, txtBias, Lbl("V"), condHint);
            cond.SetFlowBreak(condHint, true);
            cbPar = new ComboBox[3];
            var parRow = new List<Control> { Lbl("측정 파라미터   PAR1"), new Label { Text = "Ls (고정)", AutoSize = true, Margin = new Padding(3, 9, 6, 3), ForeColor = Theme.Text } };
            for (int i = 0; i < cbPar.Length; i++)
            {
                cbPar[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
                cbPar[i].Items.AddRange(ParChoices);
                cbPar[i].FormattingEnabled = true;
                cbPar[i].Format += (s, e) => e.Value = Fmt.ParLabel((string)e.ListItem);
                cbPar[i].SelectedIndex = 0;
                parRow.Add(Lbl("PAR" + (i + 2)));
                parRow.Add(cbPar[i]);
            }
            Add(cond, parRow.ToArray());
            cond.SetFlowBreak(cbPar[cbPar.Length - 1], true);
            foreach (string key in CondExtraKeys)
            {
                SettingItem item = MeterSettings.Items.First(i => i.Cmd == key);
                Control c;
                if (item.Choices != null)
                {
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = key == ":RANG" ? 110 : 100 };
                    cb.Items.Add("변경 안 함");
                    cb.Items.AddRange(item.Choices);
                    cb.SelectedIndex = 0;
                    c = cb;
                }
                else c = new TextBox { Width = 70 };
                condExtra[key] = c;
                Add(cond, Lbl(key == ":FREQ" ? "주파수 [Hz] (다중 주파수 끌 때)" : item.Name), c);
            }
            var extraHint = Lbl("빈칸·\"변경 안 함\"은 계측기 값을 그대로 둠 · 범위는 [계측기 설정] 탭 설명 참고");
            extraHint.ForeColor = Theme.Secondary;
            Add(cond, extraHint);
            cond.SetFlowBreak(extraHint, true);
            chkRestoreCond = new Switch { Text = "연결 해제하면 계측기 측정 조건을 연결 전 값으로 되돌림 (끄면 바꾼 조건을 계측기에 그대로 둠)", Margin = new Padding(3, 4, 8, 3) };
            cond.Controls.Add(chkRestoreCond);
            cond.SetFlowBreak(chkRestoreCond, true);

            // 프리셋: 이름을 입력하고 [저장], 목록에서 고르면 불러옴. 연결 중이면 [적용]으로 계측기에 반영
            cbPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 170 };
            cbPreset.SelectionChangeCommitted += delegate { LoadPreset(cbPreset.SelectedItem as string); };
            btnPresetSave = Btn("프리셋 저장", OnPresetSave);
            btnPresetSave.Width = 100;
            btnPresetDelete = Btn("삭제", OnPresetDelete);
            btnApplyCond = Btn("연결 중 적용", OnApplyCond, true);
            btnApplyCond.Width = 110;
            var applyHint = Lbl("연결 중에 바꾼 조건은 [연결 중 적용]을 눌러야 계측기에 반영됩니다");
            applyHint.ForeColor = Theme.Secondary;
            Add(cond, Lbl("프리셋"), cbPreset, btnPresetSave, btnPresetDelete, btnApplyCond, applyHint);
            layout.Controls.Add(cond.Parent);

            // 3. 저장
            var save = Group("3. 저장 (측정할 때마다 CSV에 자동 기록)");
            txtFolder = new TextBox { Width = 330, ReadOnly = true };
            btnFolder = Btn("폴더 선택", OnPickFolder);
            btnOpenFolder = Btn("폴더 열기", OnOpenFolder);
            btnNewFile = Btn("새 파일로 시작", delegate { StartNewTable("새 파일로 시작합니다."); });
            btnExport = Btn("다른 이름으로 저장", OnExport);
            btnExport.Width = 130;
            btnClear = Btn("표 비우기", delegate { StartNewTable("표를 비웠습니다."); });
            lblFile = new Label { AutoSize = true, Margin = new Padding(8, 9, 3, 3), ForeColor = Theme.Secondary };
            Add(save, Lbl("저장 폴더"), txtFolder, btnFolder, btnOpenFolder, btnNewFile, btnExport, btnClear, lblFile);
            layout.Controls.Add(save.Parent);

            // 4. 스프레드시트 셀에 바로 입력
            var sheet = Group("4. 스프레드시트에 바로 입력 (엑셀, 구글 시트 등)");
            chkSheet = new Switch { Text = "사용 - 셀을 선택하고", Margin = new Padding(3, 4, 0, 3) };
            cbHotkey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 55 };
            cbHotkey.Items.AddRange(HotkeyChoices.Select(k => (object)k.ToString()).ToArray());
            cbHotkey.SelectedIndex = 3;
            cbSheetValues = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            cbSheetValues.Items.AddRange(new object[] { "Ls만", "모든 파라미터" });
            cbSheetValues.SelectedIndex = 0;
            cbSheetSep = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            cbSheetSep.Items.AddRange(new object[] { "Tab (오른쪽 칸)", "Enter (아래 칸)" });
            cbSheetSep.SelectedIndex = 0;
            cbSheetEnd = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            cbSheetEnd.Items.AddRange(new object[] { "Enter (다음 행)", "Tab (오른쪽 칸)", "이동 안 함" });
            cbSheetEnd.SelectedIndex = 0;
            chkSheet.CheckedChanged += delegate { ApplyHotkey(); };
            cbHotkey.SelectedIndexChanged += delegate { ApplyHotkey(); };
            lblRepeat = Lbl("");
            lblRepeat.ForeColor = Theme.Secondary;
            Add(sheet, chkSheet, cbHotkey, Lbl("키를 누르면 측정 후 입력   값:"), cbSheetValues,
                Lbl("값 사이"), cbSheetSep, Lbl("입력 후"), cbSheetEnd, lblRepeat);
            UpdateRepeatHint();
            layout.Controls.Add(sheet.Parent);

            var last = Group("최근 측정값");
            lblLast = new Label
            {
                AutoSize = true,
                Font = new Font(Theme.NumberFontName, 18F, FontStyle.Regular),
                ForeColor = Theme.Text,
                Margin = new Padding(3, 0, 3, 2),
                Text = "-"
            };
            last.Controls.Add(lblLast);
            layout.Controls.Add(last.Parent);

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Theme.Card,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                GridColor = Theme.Separator,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 32
            };
            grid.RowTemplate.Height = 28;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Card;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Secondary;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Card;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(Theme.FontName, 8.5F, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.DefaultCellStyle.BackColor = Theme.Card;
            grid.DefaultCellStyle.ForeColor = Theme.Text;
            grid.DefaultCellStyle.SelectionBackColor = Theme.Selection;
            grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
            grid.DefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
            trend = Permeability.NewChart();
            trend.Series.Clear();
            var ta = trend.ChartAreas[0];
            ta.AxisX.Minimum = ta.AxisX.Maximum = double.NaN;   // 투자율 그래프의 주파수 범위 대신 자동
            ta.AxisX.Title = "측정 번호";
            ta.AxisX.LabelStyle.Format = "0";
            ta.AxisY2.Enabled = Charting.AxisEnabled.False;
            ta.AxisY.IsStartedFromZero = false;   // 작은 흔들림이 보이도록
            ta.AxisY.LabelStyle.Format = "G6";
            ta.AxisX.TitleFont = ta.AxisY.TitleFont = new Font(Theme.FontName, 8.5F);
            trend.Titles.Clear();
            trend.Legends[0].Font = new Font(Theme.FontName, 8F);
            // 표와 그래프 사이 경계를 끌어 폭을 바꿈 (그래프를 오른쪽 끝까지 밀면 표만 넓게)
            var gridSplit = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Card, SplitterWidth = 6, Panel2MinSize = 0 };
            gridSplit.Panel1.Controls.Add(grid);
            gridSplit.Panel2.Controls.Add(trend);
            bool splitSet = false;
            gridSplit.SizeChanged += delegate
            {
                if (splitSet || gridSplit.Width < 100) return;
                splitSet = true;
                gridSplit.SplitterDistance = gridSplit.Width * 7 / 10;
            };
            var gridCard = new Card(null, false) { Padding = new Padding(10, 8, 10, 10) };
            gridCard.Controls.Add(gridSplit);
            layout.Controls.Add(gridCard);

            var cmdRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            txtCmd = new TextBox { Width = 250 };
            txtCmd.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnSendCommand(s, e); }
            };
            btnSend = Btn("명령 전송", OnSendCommand);
            chkAutoUpdate = new Switch { Text = "하루 한 번 업데이트 확인", Margin = new Padding(20, 4, 3, 3) };
            btnUpdate = Btn("업데이트 확인", async delegate { await CheckUpdate(false); });
            var btnAbout = Btn("정보", OnAbout);
            var lblDev = new Label { AutoSize = true, Margin = new Padding(12, 9, 3, 3), ForeColor = Theme.Secondary, Text = "개발자: " + Updater.Developer };
            cmdRow.Controls.AddRange(new Control[] { Lbl("직접 명령(예: :FREQ?)"), txtCmd, btnSend, chkAutoUpdate, btnUpdate, btnAbout, lblDev });
            layout.Controls.Add(cmdRow);

            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9F),
                BackColor = Theme.Card,
                ForeColor = Theme.Secondary,
                BorderStyle = BorderStyle.None
            };
            var logCard = new Card(null, false) { Padding = new Padding(12, 8, 6, 8), Margin = new Padding(0) };
            logCard.Controls.Add(txtLog);
            outer.Controls.Add(logCard);

            StyleInputs(outer);
            OnIfChanged();   // FieldBox로 감싼 뒤에 숨겨야 빈 테두리가 남지 않음
            cbIf.SelectedIndexChanged += delegate { OnIfChanged(); };
            AcceptButton = btnMeasure;
            FormClosing += delegate
            {
                autoRunning = false;
                muStopRequested = true;
                watchTimer.Stop();
                waitingReconnect = false;
                reconnectGen++;
                UnregisterHotkeys();
                CloseMeter(true);
                SaveSettings();
            };
            watchTimer.Tick += OnWatchTick;
            watchTimer.Start();
            Shown += async delegate
            {
                ApplyHotkey();
                if (chkAutoUpdate.Checked) await CheckUpdateDaily();
            };
            SetupColumns();
        }

        void OnAbout(object sender, EventArgs e)
        {
            MessageBox.Show(this,
                "LCR Logger " + Updater.CurrentText + "\n" +
                "HIOKI IM3536 인덕턴스 측정 기록 프로그램\n\n" +
                "개발자: " + Updater.Developer + "\n" +
                "GitHub: " + Updater.RepoUrl,
                "정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static string Today()
        {
            return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // 자동 확인은 하루 한 번: 오늘 확인에 성공했으면 여러 번 실행해도 다시 확인하지 않음
        async Task CheckUpdateDaily()
        {
            string today = Today();
            if (updating || updateAttemptDay == today) return;
            updateAttemptDay = today;
            if (settings.Get("LastUpdateCheck", "") == today) return;
            await CheckUpdate(true);
        }

        // quiet = 자동 확인 (실패나 최신 상태는 로그에만 남김)
        async Task CheckUpdate(bool quiet)
        {
            if (updating) return;
            updating = true;
            btnUpdate.Enabled = false;
            try
            {
                UpdateInfo info;
                try
                {
                    info = await Updater.CheckAsync();
                    settings.Set("LastUpdateCheck", Today());   // 확인에 성공한 날은 다시 확인하지 않음
                    settings.Save();
                }
                catch (Exception ex)
                {
                    Log("업데이트 확인 실패: " + ex.Message);
                    if (!quiet) MessageBox.Show(this, "업데이트를 확인하지 못했습니다.\n인터넷 연결을 확인하세요.\n\n" + ex.Message, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (info == null || !Updater.IsNewer(info.Version, Updater.Current))
                {
                    Log("최신 버전입니다 (" + Updater.CurrentText + ").");
                    if (!quiet) MessageBox.Show(this, "최신 버전입니다 (" + Updater.CurrentText + ").", "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Log("새 버전이 있습니다: " + info.Tag);
                if (info.Asset == null)
                {
                    if (!quiet) MessageBox.Show(this, "새 버전 " + info.Tag + "이 있지만 내려받을 파일이 없습니다.\n" + info.PageUrl, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (busy || autoRunning)
                {
                    // 측정이 끝나면 다시 확인하도록 오늘 확인 기록을 지움
                    settings.Set("LastUpdateCheck", "");
                    updateAttemptDay = null;
                    Log("측정 중이라 업데이트를 미룹니다. 측정이 끝나면 다시 확인합니다.");
                    return;
                }
                string notes = string.IsNullOrEmpty(info.Notes) ? "" : "\n\n변경 내용:\n" + (info.Notes.Length > 600 ? info.Notes.Substring(0, 600) + "..." : info.Notes);
                if (MessageBox.Show(this, "새 버전 " + info.Tag + "이 있습니다. (현재 " + Updater.CurrentText + ")\n지금 업데이트할까요?\n\n업데이트하면 계측기 연결을 끊고 프로그램을 다시 시작합니다." + notes,
                        "업데이트", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                autoRunning = false;
                CloseMeter(true);
                UpdateUi();
                Log("새 버전을 내려받는 중...");
                try { await Updater.DownloadAndReplaceAsync(info); }
                catch (UnauthorizedAccessException)
                {
                    Log("업데이트 실패: 프로그램 폴더에 쓸 권한이 없습니다.");
                    MessageBox.Show(this, "프로그램 폴더에 쓸 권한이 없어 업데이트하지 못했습니다.\n프로그램 폴더를 바탕화면이나 문서 폴더로 옮긴 뒤 다시 시도하세요.", "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                catch (Exception ex)
                {
                    Log("업데이트 실패: " + ex.Message);
                    MessageBox.Show(this, "업데이트하지 못했습니다. 지금 버전을 그대로 사용합니다.\n\n" + ex.Message, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Log("업데이트 완료. 다시 시작합니다.");
                Updater.Restart();
                Close();
            }
            finally
            {
                updating = false;
                if (!IsDisposed) btnUpdate.Enabled = true;
            }
        }

        void LoadSettings()
        {
            string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LCR_Data");
            txtFolder.Text = settings.Get("Folder", defaultFolder);
            unitIndex = settings.GetInt("Unit", 2, 0, Fmt.InductanceUnits.Length - 1);
            cbUnit.SelectedIndex = unitIndex;
            for (int i = 0; i < cbPar.Length; i++)
                cbPar[i].SelectedIndex = settings.GetInt("CondPar" + (i + 2), 0, 0, ParChoices.Length - 1);
            RefreshPresets(null);
            cbBaud.SelectedIndex = settings.GetInt("Baud", 0, 0, cbBaud.Items.Count - 1);
            lanAddr = settings.Get("LanAddr", lanAddr);
            gpibAddr = settings.Get("GpibAddr", gpibAddr);
            cbIf.SelectedIndex = settings.GetInt("Interface", 0, 0, cbIf.Items.Count - 1);
            chkTrg.Checked = settings.Get("ExtTrigger", "0") == "1";
            numInterval.Value = settings.GetInt("IntervalMs", 1000, 200, 3600000) / 1000M;
            numCount.Value = settings.GetInt("Count", 10, 0, 100000);
            chkMulti.Checked = settings.Get("MultiFreq", "1") == "1";
            string[] savedFreqs = settings.Get("Freqs", "").Split(';');
            if (savedFreqs.Length > 1)   // 1.21 이전은 4칸으로 저장됨 → 앞 칸만 채우고 나머지는 빈칸
                for (int i = 0; i < txtFreq.Length; i++) txtFreq[i].Text = i < savedFreqs.Length ? savedFreqs[i] : "";
            // 1.8 이전 설정(CondCv=1)은 CV로 이어받음
            cbLevel.SelectedIndex = settings.GetInt("CondLevel", settings.Get("CondCv", "0") == "1" ? 2 : 0, 0, cbLevel.Items.Count - 1);
            txtCv.Text = settings.Get("LevelValue", settings.Get("CvVolts", txtCv.Text));
            cbSpeed.SelectedIndex = settings.GetInt("CondSpeed", 0, 0, cbSpeed.Items.Count - 1);
            cbBias.SelectedIndex = settings.GetInt("CondBias", 0, 0, cbBias.Items.Count - 1);
            txtBias.Text = settings.Get("BiasVolts", txtBias.Text);
            foreach (string key in CondExtraKeys) SetExtra(key, settings.Get("Cond" + key, ""));
            chkRestoreCond.Checked = settings.Get("RestoreOnClose", "1") == "1";
            cbHotkey.SelectedIndex = settings.GetInt("SheetHotkey", 3, 0, HotkeyChoices.Length - 1);
            cbSheetValues.SelectedIndex = settings.GetInt("SheetValues", 0, 0, 1);
            cbSheetSep.SelectedIndex = settings.GetInt("SheetSep", 0, 0, 1);
            cbSheetEnd.SelectedIndex = settings.GetInt("SheetEnd", 0, 0, 2);
            chkSheet.Checked = settings.Get("SheetInput", "0") == "1";
            chkAutoUpdate.Checked = settings.Get("AutoUpdate", "1") == "1";
            chkReconnect.Checked = settings.Get("AutoReconnect", "1") == "1";
            LoadMuSettings();
            SetupColumns();
        }

        void SaveSettings()
        {
            settings.Set("Folder", txtFolder.Text);
            settings.Set("Unit", unitIndex.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < cbPar.Length; i++)
                settings.Set("CondPar" + (i + 2), cbPar[i].SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("Baud", cbBaud.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            OnIfChanged();   // 입력 중인 주소를 lanAddr/gpibAddr에 담음
            settings.Set("Interface", cbIf.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("LanAddr", lanAddr);
            settings.Set("GpibAddr", gpibAddr);
            settings.Set("ExtTrigger", chkTrg.Checked ? "1" : "0");
            settings.Set("IntervalMs", ((int)(numInterval.Value * 1000)).ToString(CultureInfo.InvariantCulture));
            settings.Set("Count", ((int)numCount.Value).ToString(CultureInfo.InvariantCulture));
            settings.Set("MultiFreq", chkMulti.Checked ? "1" : "0");
            settings.Set("CondLevel", cbLevel.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("LevelValue", txtCv.Text.Trim());
            settings.Set("CondSpeed", cbSpeed.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("CondBias", cbBias.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("BiasVolts", txtBias.Text.Trim());
            foreach (string key in CondExtraKeys) settings.Set("Cond" + key, GetExtra(key));
            settings.Set("RestoreOnClose", chkRestoreCond.Checked ? "1" : "0");
            settings.Set("SheetHotkey", cbHotkey.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("SheetValues", cbSheetValues.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("SheetSep", cbSheetSep.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("SheetEnd", cbSheetEnd.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("SheetInput", chkSheet.Checked ? "1" : "0");
            settings.Set("AutoUpdate", chkAutoUpdate.Checked ? "1" : "0");
            settings.Set("AutoReconnect", chkReconnect.Checked ? "1" : "0");
            settings.Set("Freqs", string.Join(";", txtFreq.Select(t => t.Text.Trim())));
            SaveMuSettings();
            var port = cbPort.SelectedItem as PortItem;
            if (port != null) settings.Set("Port", port.Port);
            settings.Save();
        }

        static FlowLayoutPanel Group(string title)
        {
            var card = new Card(title, true);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, BackColor = Theme.Card };
            card.Controls.Add(flow);
            return flow;
        }

        static void Add(FlowLayoutPanel panel, params Control[] controls)
        {
            panel.Controls.AddRange(controls);
        }

        static Label Lbl(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(10, 9, 0, 3), ForeColor = Theme.Text };
        }

        static Button Btn(string text, EventHandler onClick, bool primary = false)
        {
            var b = new PillButton(primary) { Text = text, AutoSize = true };
            b.Click += onClick;
            return b;
        }

        // 선택 상자, 입력 칸, 숫자 칸을 모두 같은 둥근 테두리 칸(FieldBox)에 넣음
        static void StyleInputs(Control parent)
        {
            foreach (Control c in parent.Controls.Cast<Control>().ToList())
            {
                var combo = c as ComboBox;
                var text = c as TextBox;
                var num = c as NumericUpDown;
                bool wrap = false;
                if (combo != null) { combo.FlatStyle = FlatStyle.Flat; wrap = true; }
                else if (text != null && text.BorderStyle == BorderStyle.Fixed3D) { text.BorderStyle = BorderStyle.None; wrap = true; }
                else if (num != null) { num.BorderStyle = BorderStyle.None; wrap = true; }

                if (!wrap) { StyleInputs(c); continue; }
                int index = parent.Controls.GetChildIndex(c);
                var flow = parent as FlowLayoutPanel;
                bool brk = flow != null && flow.GetFlowBreak(c);   // 감싸면 SetFlowBreak가 사라지므로 옮겨 줌
                parent.Controls.Remove(c);
                var field = new FieldBox(c);
                parent.Controls.Add(field);
                parent.Controls.SetChildIndex(field, index);
                if (brk) flow.SetFlowBreak(field, true);
            }
        }

        // Windows 11: 제목 표시줄을 배경색과 같게 (지원하지 않으면 무시)
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int caption = ColorTranslator.ToWin32(Theme.Bg), text = ColorTranslator.ToWin32(Theme.Text);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);   // DWMWA_CAPTION_COLOR
                DwmSetWindowAttribute(Handle, 36, ref text, 4);      // DWMWA_TEXT_COLOR
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // ---------------- 공통 ----------------

        void UpdateUi()
        {
            bool connected = meter != null;
            btnConnect.Text = connected ? "연결 해제" : waitingReconnect ? "재연결 취소" : "연결";
            btnConnect.Enabled = !busy || connected;
            // 재연결을 기다리는 동안에는 끊기기 전 설정 그대로 다시 연결하므로 설정을 잠금
            bool editable = !connected && !busy && !waitingReconnect;
            cbIf.Enabled = txtAddr.Enabled = cbPort.Enabled = btnRefresh.Enabled = cbBaud.Enabled = chkDemo.Enabled = chkMulti.Enabled = editable;
            // 측정 조건은 연결 중에도 바꿀 수 있음 ([연결 중 적용]으로 반영)
            bool condEditable = !busy && !waitingReconnect && !autoRunning;
            foreach (ComboBox c in cbPar) c.Enabled = condEditable;
            cbPreset.Enabled = btnPresetSave.Enabled = btnPresetDelete.Enabled = condEditable;
            btnApplyCond.Enabled = connected && condEditable;
            cbLevel.Enabled = txtCv.Enabled = cbSpeed.Enabled = cbBias.Enabled = txtBias.Enabled = condEditable;
            foreach (Control c in condExtra.Values) c.Enabled = condEditable;
            foreach (TextBox t in txtFreq) t.Enabled = editable;
            chkTrg.Enabled = freqs.Count == 0 || !connected;
            btnMeasure.Enabled = connected && !busy && !autoRunning;
            btnAuto.Enabled = connected && !muRunning && !swRunning;
            btnAuto.Text = autoRunning ? "연속 측정 중지" : "연속 측정 시작";
            // 복소 투자율: 측정 중에는 연결 해제와 입력 변경을 막음
            if (muRunning || swRunning) btnConnect.Enabled = false;
            btnMuStart.Enabled = btnDcr.Enabled = btnSwStart.Enabled = btnSetRead.Enabled = btnSetApply.Enabled = btnShotColor.Enabled = btnShotMono.Enabled
                = connected && !busy && !autoRunning;
            btnSwStop.Enabled = swRunning;
            foreach (Control c in swSettings.Values) c.Enabled = !swRunning;
            btnSwMake.Enabled = !swRunning;
            btnSwCsv.Enabled = btnSwPng.Enabled = !swRunning && swRows.Count > 0;
            btnMuStop.Enabled = muRunning;
            foreach (Control c in muSettings.Values) c.Enabled = !muRunning;
            btnMuCsv.Enabled = btnMuPng.Enabled = !muRunning && muRows.Count > 0;
            numInterval.Enabled = numCount.Enabled = !autoRunning;
            btnSend.Enabled = connected && !busy && !autoRunning;
            cbUnit.Enabled = btnNewFile.Enabled = btnClear.Enabled = btnFolder.Enabled = !autoRunning;
        }

        void Log(string message)
        {
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture) + message + "\r\n");
        }
    }
}
