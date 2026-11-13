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
    // 복소 투자율 (토로이달 코어의 μ′, μ″ 주파수 특성)
    // ------------------------------------------------------------------

    class MuRow
    {
        public double F, Z, Theta, Rs, Ls, Mu1, Mu2, TanD, V = double.NaN, I = double.NaN;
        public string Raw;   // 계측기 응답 원문 (":MEAS? | :MONI?")
        public string Warn = "";   // 측정 신호가 설정과 다를 때 (Permeability.LevelWarning)
    }

    static class Permeability
    {
        public const double Mu0 = 4e-7 * Math.PI;

        public static readonly string[] DataHeader = { "f [Hz]", "Z [Ω]", "θ [°]", "Rs [Ω]", "Ls [H]", "μ′", "μ″", "tanδ", "Vmoni [V]", "Imoni [A]", "레벨 경고" };

        // 코어 치수 [mm] → 유효 자로 길이 le [mm], 유효 단면적 Ae [mm²]
        public static double Le(double od, double id) { return Math.PI * (od - id) / Math.Log(od / id); }
        public static double Ae(double od, double id, double h) { return h * (od - id) / 2; }

        // le [m], Ae [m²]. Rs = Z·cosθ, Ls = Z·sinθ/ω, μ′ = Ls·le/(μ0·N²·Ae), μ″ = (Rs − Rw)·le/(ω·μ0·N²·Ae)
        public static MuRow Calc(double f, double z, double thetaDeg, double rw, int n, double le, double ae)
        {
            double w = 2 * Math.PI * f, th = thetaDeg * Math.PI / 180, k = le / (Mu0 * n * n * ae);
            var r = new MuRow { F = f, Z = z, Theta = thetaDeg, Rs = z * Math.Cos(th), Ls = z * Math.Sin(th) / w };
            r.Mu1 = r.Ls * k;
            r.Mu2 = (r.Rs - rw) * k / w;
            r.TanD = r.Mu2 / r.Mu1;
            return r;
        }

        // decimals: 주파수는 계측기에 소수 둘째 자리까지 보냄 (공식 앱과 같음). 레벨 스윕은 더 잘게
        public static List<double> Frequencies(double start, double stop, int points, bool log, int decimals = 2)
        {
            var list = new List<double>();
            for (int i = 0; i < points; i++)
            {
                double t = points == 1 ? 0 : (double)i / (points - 1);
                list.Add(Math.Round(log ? start * Math.Pow(stop / start, t) : start + (stop - start) * t, decimals));
            }
            return list;
        }

        // 측정용 임시 조건을 적용하고 바꾸기 전 값(주파수·트리거 포함)을 돌려줌. 계측기가 거부하면 되돌린 뒤 예외
        public static Dictionary<string, string> ApplyTemp(IMeter m, MeasCond c, List<string> log)
        {
            var saved = c.Read(m);
            saved[":FREQ"] = m.Query(":FREQ?");
            saved[":TRIG"] = m.Query(":TRIG?");
            try
            {
                m.Query("*ESR?");   // 이전 오류 기록을 지움
                // PAR1에 넣을 값이 PAR2~4에 이미 있으면 계측기가 거부하므로 먼저 비움
                for (int i = 2; i <= 4; i++) if (c.Pars[i - 1] != null) m.Send(":PARameter" + i + " OFF");
                List<string> lines = c.Apply(m);
                m.Send(":TRIG EXT");
                log.AddRange(lines);
                string esr = m.Query("*ESR?").Trim();
                int bits;
                if (lines.Any(l => l.Contains("※")) || (int.TryParse(esr, out bits) && (bits & 56) != 0))   // 56 = 명령·실행·장치 오류 (공식 앱과 같은 판정)
                    throw new InvalidOperationException("계측기가 측정 조건을 받지 않았습니다 (*ESR? = " + esr + "). 로그의 ※ 표시와 허용 범위를 확인하세요.");
            }
            catch
            {
                MeasCond.Restore(m, saved);
                throw;
            }
            return saved;
        }

        // 한 점: 주파수 설정 → 트리거 → Z, θ → 모니터 전압·전류 (공식 앱 Form_LCR_Sweep과 같은 순서)
        public static MuRow MeasurePoint(IMeter m, double hz, double rw, int n, double le, double ae)
        {
            m.Send(":FREQ " + hz.ToString("F2", CultureInfo.InvariantCulture));
            string meas = m.Query("*TRG;:MEAS?"), moni = m.Query(":MONI?");
            string[] zt = Fmt.Tokens(meas), vi = Fmt.Tokens(moni);
            MuRow r = Calc(hz, Num(zt, 0), Num(zt, 1), rw, n, le, ae);
            r.V = Num(vi, 0);
            r.I = Num(vi, 1);
            r.Raw = meas + " | " + moni;
            return r;
        }

        static double Num(string[] tokens, int i)
        {
            return i < tokens.Length ? Value(tokens[i]) : double.NaN;
        }

        // 계측기 응답 숫자. ±9.99999E+33은 HIOKI의 "측정값 없음" 표시(공식 앱은 --- / DISP OUT으로 보여 줌) → NaN
        public static double Value(string token)
        {
            double v;
            return Fmt.TryParse(token.Trim(), out v) && Math.Abs(v) < 1e33 ? v : double.NaN;
        }

        // 권선 저항: PAR1을 잠시 Rdc로 바꿔 측정. 설정을 바꾼 직후에는 값이 없을 수 있어 최대 3번 다시 잼
        public static double MeasureRdc(IMeter m, List<string> log)
        {
            var saved = ApplyTemp(m, new MeasCond { Pars = new[] { "RDC", "OFF", "OFF", "OFF" } }, log);
            try
            {
                // IM3536 Rdc는 AC와 별도인 DC 레인지(:DCR:RANG)를 씀. HOLD면 수십 mΩ 권선은 UNDERFLOW(-9.99999E+33) → 잠시 자동 레인지
                saved[":DCR:RANG"] = m.Query(":DCR:RANG?").Trim();
                saved[":DCR:RANG:AUTO"] = m.Query(":DCR:RANG:AUTO?").Trim();
                m.Send(":DCR:RANG:AUTO ON");
                string resp = "";
                for (int i = 0; i < 5; i++)
                {
                    if (i > 0) Thread.Sleep(200);
                    resp = m.Query("*TRG;:MEAS?");
                    double r = Value(Fmt.Tokens(resp)[0]);
                    if (!double.IsNaN(r)) return r;
                    log.Add("Rdc 응답 " + resp + " (측정값 없음) - 다시 잽니다.");
                }
                throw new InvalidOperationException("계측기가 Rdc 값을 주지 않았습니다 (응답 " + resp + " = 측정값 없음/UNDERFLOW, DC 자동 레인지로 다시 잰 결과, DC 레인지 " + m.Query(":DCR:RANG?").Trim() + "번).\n"
                    + "코일이 측정 단자에 연결되어 있는지, 계측기에서 직접 Rdc를 골랐을 때 값이 나오는지 확인하세요.\n"
                    + "값이 나오지 않으면 멀티미터 등으로 잰 값을 Rw 칸에 직접 입력하세요.");
            }
            finally { MeasCond.Restore(m, saved); }
        }

        // 경고 문구는 "종류 (자세한 내용)" 형식. 측정 중에는 괄호 앞 종류가 처음 나올 때만 로그에 남김
        public static string WarningKind(string w) { int i = w.IndexOf(" ("); return i > 0 ? w.Substring(0, i) : w; }

        // 두 자리 유효숫자로 반올림한 주파수 표시 (예: 1147730 → 1.1MHz)
        static string RoughFreq(double hz)
        {
            double p = Math.Pow(10, Math.Floor(Math.Log10(hz)) - 1);
            return Fmt.FrequencyLabel(Math.Round(hz / p) * p);
        }

        public static string Amps(double a)
        {
            return a < 1e-3 ? (a * 1e6).ToString("G3", CultureInfo.InvariantCulture) + " µA" : (a * 1e3).ToString("G3", CultureInfo.InvariantCulture) + " mA";
        }

        // 자기공진에 가까워지면 겉보기 L이 1/(1-(f/fr)²)로 커져 μ′가 계속 오름. 공진 앞의 오르는 구간을 거슬러 올라가
        // 구간 최솟값보다 5% 넘게 오르기 전 마지막 점까지를 신뢰 구간으로. 오르는 점이 3개 미만이거나 5% 넘게 오르지 않았으면 공진 주파수의 절반으로 어림
        public static double TrustLimit(List<MuRow> valid, MuRow res, out bool bySlope)
        {
            int end = valid.IndexOf(res);
            int start = end - 1;
            while (start > 0 && valid[start - 1].Mu1 <= valid[start].Mu1 * 1.001) start--;   // 0.1% 이내 내려감은 잡음으로 봄
            var run = valid.Skip(Math.Max(0, start)).Take(end - start).ToList();
            double v0 = run.Count > 0 ? run.Min(r => r.Mu1) : 0;
            bySlope = run.Count >= 3 && run.Max(r => r.Mu1) > v0 * 1.05;   // 평평하면(5% 이내) 기울기로 판단할 수 없음
            if (!bySlope) return res.F / 2;
            return run.Last(r => r.Mu1 <= v0 * 1.05).F;
        }

        // cond: 측정에 쓴 조건 (CC이면 Imoni를 설정 전류와 비교, V/CV이면 첫 점과 비교)
        public static List<string> Warnings(List<MuRow> rows, MeasCond cond)
        {
            var w = new List<string>();
            var valid = rows.Where(r => !double.IsNaN(r.Z) && !double.IsNaN(r.Theta)).ToList();
            if (valid.Count == 0) return w;
            int sign = Math.Sign(valid[0].Theta);
            MuRow res = valid.FirstOrDefault(r => r.Mu1 < 0 || Math.Sign(r.Theta) != sign);
            if (res != null)
            {
                bool bySlope;
                double limit = TrustLimit(valid, res, out bySlope);
                w.Add("권선 자기공진 의심 (" + Fmt.FrequencyLabel(res.F) + "에서 μ′ 음수 또는 θ 부호 바뀜) - 신뢰 구간은 대략 "
                    + RoughFreq(limit) + " 이하" + (bySlope ? " (공진 앞에서 μ′가 5% 넘게 오르기 전)" : " (공진 주파수의 절반으로 어림)")
                    + ", 더 높은 주파수는 턴 수를 줄여 재측정 권장");
            }

            var cur = rows.Where(r => !double.IsNaN(r.I) && r.I > 0).ToList();
            if (cur.Count == 0) return w;
            if (cond != null && cond.Level == "CC" && cond.LevelValue > 0)
            {
                double set = cond.LevelValue;
                var high = cur.Where(r => r.I > set * 1.1).ToList();
                var low = cur.Where(r => r.I < set * 0.9).ToList();
                if (high.Count > 0)
                    w.Add("설정보다 전류가 많이 흐름 (설정 " + Amps(set) + ", 실제 최대 " + Amps(high.Max(r => r.I)) + ", "
                        + Fmt.FrequencyLabel(high.First().F) + "~" + Fmt.FrequencyLabel(high.Last().F)
                        + ") - 설정 전류가 계측기가 낼 수 있는 최소 신호보다 작습니다. 레벨을 올리세요");
                if (low.Count > 0)
                {
                    double vmax = cur.Where(r => !double.IsNaN(r.V)).Select(r => r.V).DefaultIfEmpty(0).Max();
                    var lowV = low.Where(r => !double.IsNaN(r.V)).ToList();
                    // 전류가 모자란 점들의 Vmoni가 측정 중 최대 전압 근처에 모여 있으면 출력 전압 한계
                    bool clipped = vmax > 0 && lowV.Count > 0 && lowV.Average(r => r.V) >= vmax * 0.9;
                    w.Add("설정 전류를 유지하지 못함 (설정 " + Amps(set) + ", 실제 최소 " + Amps(low.Min(r => r.I)) + ", "
                        + Fmt.FrequencyLabel(low.First().F) + "부터)"
                        + (clipped ? " - Vmoni가 약 " + vmax.ToString("0.0#", CultureInfo.InvariantCulture) + " V에서 막혀 있어 출력 전압 한계입니다. 턴 수를 줄이거나 레벨을 낮추세요"
                                   : " - 측정 레벨이 설정과 다릅니다"));
                }
            }
            else if (cond != null && cond.Level == "CV" && cur.Any(r => LevelWarning("CV", cond.LevelValue, r.V, r.I).Length > 0))
            {
                var bad = cur.Where(r => LevelWarning("CV", cond.LevelValue, r.V, r.I).Length > 0).ToList();
                w.Add("설정 전압을 유지하지 못함 (설정 " + Volt(cond.LevelValue) + ", Vmoni " + Volt(bad.Min(r => r.V)) + "~" + Volt(bad.Max(r => r.V)) + ", "
                    + Fmt.FrequencyLabel(bad.First().F) + "~" + Fmt.FrequencyLabel(bad.Last().F) + ") - 시료 임피던스가 계측기 출력 한계를 넘었습니다. CC 모드나 다른 레벨로 재측정 권장");
            }
            else if (cur.Count > 1 && cur.Any(r => Math.Abs(r.I / cur[0].I - 1) > 0.1))
                w.Add("측정 레벨이 일정하지 않음 (Imoni가 시작값 " + Amps(cur[0].I) + "보다 ±10% 넘게 변함, 범위 "
                    + Amps(cur.Min(r => r.I)) + "~" + Amps(cur.Max(r => r.I)) + "), CC 모드 권장");
            return w;
        }

        static string Volt(double v) { return double.IsNaN(v) ? "?" : v.ToString("G3", CultureInfo.InvariantCulture) + " V"; }

        // 한 점의 측정 신호를 설정과 비교: CC는 Imoni, CV는 Vmoni가 ±10% 넘게 벗어나면 경고 문장, 아니면 "".
        // V(개방전압)는 계측기 출력 저항 때문에 시료 전압이 원래 설정보다 낮으므로 비교하지 않음
        public static string LevelWarning(string mode, double set, double v, double i)
        {
            if (set <= 0) return "";
            string vi = "Vmoni " + Volt(v) + ", Imoni " + (double.IsNaN(i) ? "?" : Amps(i));
            if (mode == "CC" && !double.IsNaN(i))
            {
                if (i > set * 1.1) return "설정보다 전류가 많이 흐름 (설정 " + Amps(set) + ", " + vi + ")";
                if (i < set * 0.9) return "설정 전류를 유지하지 못함 (설정 " + Amps(set) + ", " + vi + ")";
            }
            if (mode == "CV" && !double.IsNaN(v) && Math.Abs(v / set - 1) > 0.1)
                return "설정 전압과 다름 (설정 " + Volt(set) + ", " + vi + ")";
            return "";
        }

        // MeasCond.Summary 문장(예: "CC 20mA / SPEED ...")에서 신호 종류와 설정값(V 또는 A). 읽지 못하면 Level이 null
        public static MeasCond LevelOf(string summary)
        {
            string[] p = (summary ?? "").Split('/')[0].Trim().Split(' ');
            double v;
            if (p.Length < 2 || !Fmt.TryParse(p[1].TrimEnd('A', 'V', 'm'), out v)) return new MeasCond();
            return new MeasCond { Level = p[0], LevelValue = p[0] == "CC" ? v / 1000 : v };
        }

        // 주파수마다 받은 :MONI? 응답을 비교해 한 칸에 넣을 경고 (주파수가 여러 개면 앞에 주파수 표시)
        public static string RowWarning(MeasCond c, List<string> monis, List<string> labels)
        {
            var w = new List<string>();
            for (int k = 0; k < monis.Count; k++)
            {
                string[] t = Fmt.Tokens(monis[k]);
                string s = LevelWarning(c.Level, c.LevelValue, t.Length > 0 ? Value(t[0]) : double.NaN, t.Length > 1 ? Value(t[1]) : double.NaN);
                if (s.Length > 0) w.Add(k < labels.Count ? labels[k] + ": " + s : s);
            }
            return string.Join(" / ", w);
        }

        static string G(double v) { return double.IsNaN(v) ? "" : v.ToString("G7", CultureInfo.InvariantCulture); }

        // 위쪽에 "항목,값" 측정 조건, 빈 줄, 아래에 데이터 표
        public static List<string> CsvLines(List<KeyValuePair<string, string>> info, List<MuRow> rows)
        {
            var lines = info.Select(kv => Fmt.CsvLine(new[] { kv.Key, kv.Value })).ToList();
            lines.Add("");
            lines.Add(Fmt.CsvLine(DataHeader));
            foreach (MuRow r in rows)
                lines.Add(Fmt.CsvLine(new[] { r.F, r.Z, r.Theta, r.Rs, r.Ls, r.Mu1, r.Mu2, r.TanD, r.V, r.I }.Select(G).Concat(new[] { r.Warn })));
            return lines;
        }

        // 기존 CSV(LCR_yyyy-MM-dd_HH-mm-ss)와 같은 날짜 표기. 예: MU_코어A_N10턴_2026-11-13_14-30-00
        public static string FileStem(string name, int n, DateTime time)
        {
            string s = name.Trim().Length == 0 ? "시료" : name.Trim();
            s = new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return "MU_" + s + "_N" + n + "턴_" + time.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        }

        // 가로 주파수(로그), 왼쪽 μ′ 실선, 오른쪽 μ″ 점선
        public static Charting.Chart NewChart()
        {
            // 배치 도중 높이가 0이 되면 차트가 예외를 내므로 최소 크기를 줌
            var chart = new Charting.Chart { Dock = DockStyle.Fill, BackColor = Theme.Card, MinimumSize = new Size(50, 50) };
            var area = new Charting.ChartArea("main") { BackColor = Theme.Card };
            // 로그축은 점이 하나도 없으면 그릴 때 오류가 나므로 첫 점을 넣을 때 켬 (AddPoint)
            area.AxisX.Minimum = 1e3;
            area.AxisX.Maximum = 8e6;
            area.AxisX.Title = "주파수 [Hz]";
            area.AxisX.LabelStyle.Format = "#,0";
            area.AxisY.Title = "μ′";
            area.AxisY2.Enabled = Charting.AxisEnabled.True;
            area.AxisY2.Title = "μ″";
            area.AxisY2.MajorGrid.Enabled = false;
            area.AxisY.TextOrientation = area.AxisY2.TextOrientation = Charting.TextOrientation.Horizontal;   // μ″ 기호가 세로 회전에서 깨져 보임
            foreach (Charting.Axis a in new[] { area.AxisX, area.AxisY, area.AxisY2 })
            {
                a.TitleFont = new Font(Theme.FontName, 9.5F);
                a.LabelStyle.Font = new Font(Theme.NumberFontName, 8.5F);
                a.MajorGrid.LineColor = Theme.Separator;
                a.LineColor = Theme.Secondary;
            }
            chart.ChartAreas.Add(area);
            chart.Legends.Add(new Charting.Legend { Docking = Charting.Docking.Top, Alignment = StringAlignment.Center, Font = new Font(Theme.FontName, 9F), BackColor = Theme.Card });
            chart.Titles.Add(new Charting.Title { Font = new Font(Theme.FontName, 10F, FontStyle.Bold), ForeColor = Theme.Text });
            chart.Series.Add(new Charting.Series("μ′ (왼쪽 축, 실선)") { ChartType = Charting.SeriesChartType.Line, Color = Theme.Accent, BorderWidth = 2 });
            chart.Series.Add(new Charting.Series("μ″ (오른쪽 축, 점선)")
            {
                ChartType = Charting.SeriesChartType.Line, Color = Color.FromArgb(230, 81, 0), BorderWidth = 2,
                BorderDashStyle = Charting.ChartDashStyle.Dash, YAxisType = Charting.AxisType.Secondary
            });
            return chart;
        }

        // 가로축은 측정 범위로 고정 (자동이면 로그축이 1 Hz부터 잡힘)
        public static void ClearChart(Charting.Chart chart, string title, double start, double stop)
        {
            chart.Titles[0].Text = title;
            chart.ChartAreas[0].AxisX.Minimum = start;
            chart.ChartAreas[0].AxisX.Maximum = stop;
            chart.ChartAreas[0].AxisX.IsLogarithmic = false;
            foreach (Charting.Series s in chart.Series) s.Points.Clear();
        }

        // 저장 그림은 화면 크기와 상관없이 1200×700
        public static void SavePng(Charting.Chart chart, string path)
        {
            DockStyle dock = chart.Dock;
            Size size = chart.Size;
            chart.Dock = DockStyle.None;
            chart.Size = new Size(1200, 700);
            try { chart.SaveImage(path, Charting.ChartImageFormat.Png); }
            finally
            {
                chart.Size = size;
                chart.Dock = dock;
            }
        }

        static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        public static void AddPoint(Charting.Chart chart, MuRow r)
        {
            if (r.F <= 0 || !Finite(r.Mu1) || !Finite(r.Mu2)) return;   // 그래프는 NaN을 그리지 못함 (CSV에는 빈칸으로 남음)
            chart.Series[0].Points.AddXY(r.F, r.Mu1);
            chart.Series[1].Points.AddXY(r.F, r.Mu2);
            chart.ChartAreas[0].AxisX.IsLogarithmic = true;
        }
    }
}
