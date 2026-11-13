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
    // 측정 파라미터 / 단위 / CSV 형식
    // ------------------------------------------------------------------

    static class Fmt
    {
        public static readonly string[] InductanceUnits = { "H", "mH", "µH", "nH" };
        static readonly double[] InductanceFactors = { 1, 1e-3, 1e-6, 1e-9 };

        public static bool IsInductance(string par)
        {
            return par == "LS" || par == "LP";
        }

        // 목록 표시용: 계측기 화면처럼 기호를 같이 씀 (PHASE (θ), S (σ), E (ε)). Ls·Rdc처럼 대소문자만 다르면 그대로
        public static string ParLabel(string par)
        {
            string d = DisplayName(par);
            return string.Equals(d, par, StringComparison.OrdinalIgnoreCase) ? par : par + " (" + d + ")";
        }

        // ParLabel → 계측기 명령 값 ("PHASE (θ)" → "PHASE")
        public static string ParToken(string label)
        {
            int i = label.IndexOf(" (");
            return i > 0 ? label.Substring(0, i) : label;
        }

        public static string DisplayName(string par)
        {
            switch (par)
            {
                case "LS": return "Ls";
                case "LP": return "Lp";
                case "CS": return "Cs";
                case "CP": return "Cp";
                case "RS": return "Rs";
                case "RP": return "Rp";
                case "RDC": case "DC": return "Rdc";
                case "PHASE": case "PH": return "θ";
                case "S": return "σ";
                case "E": return "ε";
                default: return par;
            }
        }

        public static string Unit(string par, int inductanceUnit)
        {
            if (IsInductance(par)) return InductanceUnits[inductanceUnit];
            switch (par)
            {
                case "CS": case "CP": return "F";
                case "Z": case "RS": case "RP": case "X": case "RDC": case "DC": return "Ω";
                case "Y": case "G": case "B": return "S";
                case "PHASE": case "PH": return "°";
                default: return "";
            }
        }

        public static string Header(string par, int inductanceUnit, string frequencyLabel = null)
        {
            string name = DisplayName(par) + (frequencyLabel == null ? "" : "@" + frequencyLabel);
            string unit = Unit(par, inductanceUnit);
            return unit.Length == 0 ? name : name + " [" + unit + "]";
        }

        // "1k", "100kHz", "1M", "120" 같은 입력을 Hz로 변환 (IM3536 범위 4 Hz ~ 8 MHz)
        public static bool TryParseFrequency(string text, out double hz)
        {
            hz = 0;
            string t = text.Trim().Replace(" ", "");
            if (t.EndsWith("hz", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 2);
            double multiplier = 1;
            if (t.EndsWith("k", StringComparison.OrdinalIgnoreCase)) multiplier = 1e3;
            else if (t.EndsWith("m", StringComparison.OrdinalIgnoreCase)) multiplier = 1e6;
            if (multiplier != 1) t = t.Substring(0, t.Length - 1);
            double v;
            if (!TryParse(t, out v)) return false;
            hz = v * multiplier;
            return hz >= 4 && hz <= 8e6;
        }

        public static string FrequencyLabel(double hz)
        {
            if (hz >= 1e6) return (hz / 1e6).ToString("G6", CultureInfo.InvariantCulture) + "MHz";
            if (hz >= 1e3) return (hz / 1e3).ToString("G6", CultureInfo.InvariantCulture) + "kHz";
            return hz.ToString("G6", CultureInfo.InvariantCulture) + "Hz";
        }

        public static bool TryParse(string token, out double value)
        {
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // 표와 CSV에 들어갈 값: 인덕턴스는 선택한 단위로 환산, 나머지는 계측기 응답 그대로
        public static string CellValue(string par, string token, int inductanceUnit)
        {
            double v;
            if (IsInductance(par) && TryParse(token, out v))
                return (v / InductanceFactors[inductanceUnit]).ToString("G8", CultureInfo.InvariantCulture);
            return token;
        }

        // 화면 표시용 (예: 100.012 µH)
        public static string Pretty(string par, string token)
        {
            double v;
            if (!TryParse(token, out v)) return DisplayName(par) + " = " + token;
            string unit = IsInductance(par) ? "H" : Unit(par, 0);
            string text;
            if (unit == "H" || unit == "F" || unit == "Ω" || unit == "S")
                text = Engineering(v) + unit;
            else
                text = v.ToString("G6", CultureInfo.InvariantCulture) + (unit.Length > 0 ? " " + unit : "");
            return DisplayName(par) + " = " + text;
        }

        static string Engineering(double v)
        {
            double a = Math.Abs(v);
            string[] prefixes = { "p", "n", "µ", "m", "", "k", "M", "G" };
            int exp = a == 0 ? 0 : (int)Math.Floor(Math.Log10(a) / 3);
            exp = Math.Max(-4, Math.Min(3, exp));
            double scaled = v / Math.Pow(1000, exp);
            return scaled.ToString("G6", CultureInfo.InvariantCulture) + " " + prefixes[exp + 4];
        }

        // 스프레드시트에 타이핑할 숫자: 지수 표기(E+01) 없이 일반 소수로
        public static string PlainNumber(string text)
        {
            double v;
            if (!TryParse(text, out v)) return text;
            double rounded = double.Parse(v.ToString("G10", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            try
            {
                return ((decimal)rounded).ToString(CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                return rounded.ToString("G10", CultureInfo.InvariantCulture);
            }
        }

        // 셀에 입력할 값 목록 (주파수 순서). lsOnly면 주파수마다 Ls 하나 (Ls가 없으면 Lp, 둘 다 없으면 전체).
        public static List<string> TypedValues(List<string> pars, List<string> responses, int inductanceUnit, bool lsOnly)
        {
            int pick = pars.IndexOf("LS");
            if (pick < 0) pick = pars.IndexOf("LP");
            var values = new List<string>();
            foreach (string response in responses)
            {
                string[] tokens = Tokens(response);
                for (int i = 0; i < pars.Count; i++)
                {
                    if (lsOnly && pick >= 0 && i != pick) continue;
                    values.Add(i < tokens.Length ? PlainNumber(CellValue(pars[i], tokens[i], inductanceUnit)) : "");
                }
            }
            return values;
        }

        public static string CsvLine(IEnumerable<string> fields)
        {
            return string.Join(",", fields.Select(f =>
                f.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + f.Replace("\"", "\"\"") + "\"" : f));
        }

        // frequencyLabels가 비어 있으면 단일 측정, 있으면 주파수마다 파라미터 열을 반복
        public static List<string> HeaderFields(List<string> pars, int inductanceUnit, List<string> frequencyLabels)
        {
            var fields = new List<string> { "No", "측정시각", "메모", "측정조건" };
            if (frequencyLabels.Count == 0)
                fields.AddRange(pars.Select(p => Header(p, inductanceUnit)));
            else
                foreach (string f in frequencyLabels)
                    fields.AddRange(pars.Select(p => Header(p, inductanceUnit, f)));
            fields.Add("모니터 V,I");
            fields.Add("레벨 경고");
            fields.Add("원본응답");
            return fields;
        }

        public static string[] Tokens(string response)
        {
            return response.Split(',').Select(t => t.Trim()).ToArray();
        }

        // moni: 주파수별 :MONI? 응답("V,I")을 " | "로 이은 것, warn: 측정 신호가 설정과 다를 때 경고
        public static string[] RowFields(int no, DateTime time, string memo, string condition, List<string> pars, List<string> responses, int inductanceUnit, string moni = "", string warn = "")
        {
            var fields = new List<string>
            {
                no.ToString(CultureInfo.InvariantCulture),
                time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                memo,
                condition ?? ""
            };
            foreach (string response in responses)
            {
                string[] tokens = Tokens(response);
                for (int i = 0; i < pars.Count; i++)
                    fields.Add(i < tokens.Length ? CellValue(pars[i], tokens[i], inductanceUnit) : "");
            }
            fields.Add(moni);
            fields.Add(warn);
            fields.Add(string.Join(" | ", responses));
            return fields.ToArray();
        }

        // 추이 그래프 점: 주파수마다 인덕턴스(없으면 첫 파라미터) 값 하나. row는 RowFields 결과 (앞 4칸 뒤에 주파수별 파라미터)
        public static List<KeyValuePair<string, double>> TrendPoints(List<string> pars, List<string> frequencyLabels, string[] row)
        {
            var points = new List<KeyValuePair<string, double>>();
            if (pars.Count == 0) return points;
            int shown = Math.Max(0, pars.FindIndex(IsInductance));
            for (int f = 0; f < Math.Max(1, frequencyLabels.Count); f++)
            {
                int col = 4 + f * pars.Count + shown;
                double v;
                if (col < row.Length && TryParse(row[col], out v) && Math.Abs(v) < 1e30)   // ±9.99999E+33 = 측정값 없음
                    points.Add(new KeyValuePair<string, double>(frequencyLabels.Count > 0 ? frequencyLabels[f] : DisplayName(pars[shown]), v));
            }
            return points;
        }
    }

    // 측정 행을 CSV 파일에 즉시 추가. 파일이 엑셀 등에서 열려 있어 실패하면 보관했다가 다음에 다시 기록.
    class CsvWriter
    {
        readonly List<string> pending = new List<string>();
        public string FilePath { get; private set; }
        public int PendingCount { get { return pending.Count; } }

        public void Start(string folder, List<string> header)
        {
            Directory.CreateDirectory(folder);
            string stem = Path.Combine(folder, "LCR_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
            FilePath = stem + ".csv";
            for (int n = 2; File.Exists(FilePath); n++)
                FilePath = stem + "_" + n + ".csv";
            pending.Clear();
            File.WriteAllText(FilePath, Fmt.CsvLine(header) + "\r\n", new UTF8Encoding(true));
        }

        public void Reset()
        {
            FilePath = null;
            pending.Clear();
        }

        public void Append(string[] row)
        {
            pending.Add(Fmt.CsvLine(row));
            File.AppendAllText(FilePath, string.Join("\r\n", pending) + "\r\n", new UTF8Encoding(false));
            pending.Clear();
        }
    }
}
