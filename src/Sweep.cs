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
    // 스윕 측정 (공식 앱 Sweep Measurement: 주파수 / V / CV / CC를 바꿔 가며 PAR1~4 측정)
    // ------------------------------------------------------------------

    class SweepRow
    {
        public int Rep, No;
        public double X, V = double.NaN, I = double.NaN;
        public double[] Vals;
        public DateTime Time;
        public string Raw, Warn = "";
    }

    static class Sweep
    {
        public static readonly string[] Items = { "주파수 [Hz]", "개방전압 V [V]", "정전압 CV [V]", "정전류 CC [mA]" };
        static readonly string[] Modes = { null, "V", "CV", "CC" };

        public static string RangeText(int item)
        {
            return item == 0 ? "4 Hz ~ 8 MHz (예: 1k, 100k, 1M)" : item == 3 ? "0.01 ~ 50 mA" : "0.005 ~ 5 V";
        }

        // 점 목록 글자 → 값 (주파수 Hz / 전압 V / 전류 mA). 쉼표·줄바꿈·공백으로 구분
        public static List<double> ParsePoints(int item, string text)
        {
            var list = new List<double>();
            foreach (string t in text.Split(new[] { ',', ';', '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v;
                bool ok = item == 0 ? Fmt.TryParseFrequency(t, out v)
                    : Fmt.TryParse(t, out v) && (item == 3 ? v >= 0.01 && v <= 50 : v >= 0.005 && v <= 5);
                if (!ok) throw new InvalidOperationException("점 '" + t + "'을(를) 읽을 수 없습니다. " + RangeText(item) + " 범위로 입력하세요.");
                list.Add(v);
            }
            if (list.Count == 0) throw new InvalidOperationException("점 목록이 비어 있습니다. [점 목록 만들기]를 누르거나 직접 입력하세요.");
            if (list.Count > 5000) throw new InvalidOperationException("점은 5000개까지입니다 (공식 앱과 같은 한도).");
            return list;
        }

        // 스윕 동안 바꾸는 측정 신호 종류 (주파수 스윕이면 바꾸지 않음). 값은 첫 점으로 시작
        public static MeasCond Cond(int item, double first)
        {
            return item == 0 ? new MeasCond() : new MeasCond { Level = Modes[item], LevelValue = item == 3 ? first / 1000 : first };
        }

        // 한 점: 값 설정 → 계측기가 받았는지(*ESR?) → 트리거 → PAR 값 → 모니터 전압·전류 (공식 앱 Form_LCR_Sweep 순서)
        public static SweepRow MeasurePoint(IMeter m, int item, double v, int nPars)
        {
            string cmd = item == 0 ? ":FREQ " + v.ToString("F2", CultureInfo.InvariantCulture)
                : MeasCond.LevelKey(Modes[item]) + " " + (item == 3 ? v / 1000 : v).ToString("0.000000", CultureInfo.InvariantCulture);
            m.Send(cmd);
            string esr = m.Query("*ESR?").Trim();
            int bits;
            if (int.TryParse(esr, out bits) && (bits & 56) != 0)
                throw new InvalidOperationException("계측기가 '" + cmd + "'을(를) 받지 않았습니다 (*ESR? = " + esr + "). 지금 레인지·주파수에서 허용되는 값인지 확인하세요.");
            string meas = m.Query("*TRG;:MEAS?"), moni = m.Query(":MONI?");
            string[] t = Fmt.Tokens(meas), vi = Fmt.Tokens(moni);
            return new SweepRow
            {
                X = v,
                Vals = Enumerable.Range(0, nPars).Select(i => i < t.Length ? Permeability.Value(t[i]) : double.NaN).ToArray(),
                V = vi.Length > 0 ? Permeability.Value(vi[0]) : double.NaN,
                I = vi.Length > 1 ? Permeability.Value(vi[1]) : double.NaN,
                Time = DateTime.Now,
                Raw = meas + " | " + moni
            };
        }

        static string G(double v) { return double.IsNaN(v) ? "" : v.ToString("G7", CultureInfo.InvariantCulture); }

        public static List<string> CsvLines(List<KeyValuePair<string, string>> info, int item, List<string> pars, List<SweepRow> rows)
        {
            var lines = info.Select(kv => Fmt.CsvLine(new[] { kv.Key, kv.Value })).ToList();
            lines.Add("");
            lines.Add(Fmt.CsvLine(new[] { "반복", "No", Items[item] }.Concat(pars.Select(p => Fmt.Header(p, 0))).Concat(new[] { "Vmoni [V]", "Imoni [A]", "측정시각", "레벨 경고" })));
            foreach (SweepRow r in rows)
                lines.Add(Fmt.CsvLine(new[] { r.Rep.ToString(CultureInfo.InvariantCulture), r.No.ToString(CultureInfo.InvariantCulture), G(r.X) }
                    .Concat(r.Vals.Select(G)).Concat(new[] { G(r.V), G(r.I), r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.Warn })));
            return lines;
        }

        public static string FileStem(string name, DateTime time)
        {
            string s = name.Trim().Length == 0 ? "시료" : name.Trim();
            s = new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return "SWEEP_" + s + "_" + time.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        }

        // 그래프 모양은 복소 투자율과 같게. 왼쪽 축 PAR1, 오른쪽 축 PAR2, 반복마다 선 하나
        public static Charting.Chart NewChart()
        {
            var chart = Permeability.NewChart();
            chart.Series.Clear();
            chart.ChartAreas[0].AxisY.LabelStyle.Format = chart.ChartAreas[0].AxisY2.LabelStyle.Format = "G4";
            chart.Tag = false;
            return chart;
        }

        public static void ClearChart(Charting.Chart chart, string title, int item, List<string> pars, bool logX, double min, double max)
        {
            chart.Series.Clear();
            chart.Titles[0].Text = title;
            var a = chart.ChartAreas[0];
            a.AxisX.Title = Items[item];
            a.AxisX.LabelStyle.Format = item == 0 ? "#,0" : "G4";
            a.AxisX.IsLogarithmic = false;
            a.AxisX.Minimum = Math.Min(min, max);
            a.AxisX.Maximum = Math.Max(min, max) > Math.Min(min, max) ? Math.Max(min, max) : Math.Min(min, max) * 1.01 + 1e-9;
            a.AxisY.Title = pars.Count > 0 ? Fmt.Header(pars[0], 0) : "";
            a.AxisY2.Enabled = pars.Count > 1 ? Charting.AxisEnabled.True : Charting.AxisEnabled.False;
            a.AxisY2.Title = pars.Count > 1 ? Fmt.Header(pars[1], 0) : "";
            chart.Tag = logX;
        }

        public static void AddPoint(Charting.Chart chart, SweepRow r, List<string> pars)
        {
            for (int k = 0; k < Math.Min(2, pars.Count); k++)
            {
                double y = r.Vals[k];
                if (r.X <= 0 || double.IsNaN(y) || double.IsInfinity(y)) continue;   // 그래프는 NaN을 그리지 못함 (CSV에는 빈칸)
                string name = Fmt.DisplayName(pars[k]) + " · " + r.Rep + "회" + (k == 1 ? " (오른쪽, 점선)" : "");
                Charting.Series s = chart.Series.FindByName(name);
                if (s == null)
                {
                    s = new Charting.Series(name) { ChartType = Charting.SeriesChartType.Line, BorderWidth = 2 };
                    if (k == 1) { s.YAxisType = Charting.AxisType.Secondary; s.BorderDashStyle = Charting.ChartDashStyle.Dash; }
                    chart.Series.Add(s);
                }
                s.Points.AddXY(r.X, y);
            }
            if ((bool)chart.Tag) chart.ChartAreas[0].AxisX.IsLogarithmic = true;
        }
    }
}
