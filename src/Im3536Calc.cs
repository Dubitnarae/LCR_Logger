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
    // IM3536 정확도·측정 범위 계산 (공식 앱 Calc Accuracy / Calc Measure Range의 IM3536 부분)
    // ------------------------------------------------------------------

    class AccResult
    {
        public double Z, Ph, AccZ, AccPh, MainMinPct, MainMaxPct, SubMin, SubMax;
        public string Formula;
    }

    static class Im3536Calc
    {
        public static readonly string[] Paras = { "Z - θ", "Cs - D", "Cp - D", "Ls - Q", "Lp - Q", "Rdc" };
        public static readonly string[] MainUnits = { "Ω", "F", "F", "H", "H", "Ω" };
        public static readonly string[] SubNames = { "θ [°]", "D", "D", "Q", "Q", "-" };
        public static readonly string[] Ranges = { "100mΩ", "1Ω", "10Ω", "100Ω", "1kΩ", "10kΩ", "100kΩ", "1MΩ", "10MΩ", "100MΩ" };
        public static readonly string[] Cables = { "0 m", "1 m", "2 m", "4 m" };
        public static readonly string[] Speeds = { "FAST", "MED", "SLOW", "SLOW2" };
        static readonly double[] RangeVal = { 0.1, 1, 10, 100, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8 };

        // 정확도 A, B [%, °]: [행(0 = 100MΩ … 9 = 100mΩ, 공식 앱 순서), 주파수 구간, {A, B}]. -1은 보증 범위 밖
        static readonly double[,,] TblZ =
        { { { 1, 1 }, { 6, 5 }, { 3, 2 }, { 3, 2 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { 0.5, 0.3 }, { 0.8, 1 }, { 0.5, 0.3 }, { 0.5, 0.3 }, { 2, 1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { 0.2, 0.1 }, { 0.4, 0.08 }, { 0.3, 0.05 }, { 0.3, 0.05 }, { 0.5, 0.1 }, { 3, 0.5 }, { -1, -1 }, { -1, -1 } },
            { { 0.1, 0.01 }, { 0.3, 0.03 }, { 0.2, 0.03 }, { 0.2, 0.03 }, { 0.25, 0.04 }, { 1, 0.3 }, { 2, 0.5 }, { 2, 0.5 } },
            { { 0.1, 0.01 }, { 0.3, 0.03 }, { 0.2, 0.02 }, { 0.05, 0.02 }, { 0.3, 0.02 }, { 0.5, 0.05 }, { 2, 0.5 }, { 2, 0.5 } },
            { { 0.1, 0.01 }, { 0.3, 0.02 }, { 0.2, 0.02 }, { 0.2, 0.02 }, { 0.2, 0.02 }, { 0.4, 0.02 }, { 1.5, 0.2 }, { 1.5, 0.2 } },
            { { 0.1, 0.02 }, { 0.3, 0.02 }, { 0.2, 0.02 }, { 0.2, 0.02 }, { 0.2, 0.02 }, { 0.5, 0.03 }, { 1.5, 0.2 }, { 1.5, 0.2 } },
            { { 0.2, 0.15 }, { 0.5, 0.1 }, { 0.4, 0.05 }, { 0.4, 0.05 }, { 0.4, 0.05 }, { 0.8, 0.1 }, { 2, 1.5 }, { 2, 1.5 } },
            { { 0.3, 0.3 }, { 1.5, 1 }, { 1, 0.3 }, { 1, 0.3 }, { 1, 0.3 }, { 1.5, 1 }, { 3, 3 }, { 3, 3 } },
            { { 1, 1 }, { 8, 8 }, { 5, 4 }, { 3, 2 }, { 2, 2 }, { 4, 3 }, { -1, -1 }, { -1, -1 } } };
        static readonly double[,,] TblPh =
        { { { 0, 0 }, { 5, 3 }, { 2, 2 }, { 2, 2 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { 0, 0 }, { 0.8, 0.5 }, { 0.4, 0.2 }, { 0.4, 0.2 }, { 2, 1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { 0, 0 }, { 0.3, 0.08 }, { 0.2, 0.02 }, { 0.2, 0.02 }, { 0.6, 0.1 }, { 3, 0.5 }, { -1, -1 }, { -1, -1 } },
            { { 0, 0 }, { 0.2, 0.02 }, { 0.1, 0.02 }, { 0.1, 0.02 }, { 0.2, 0.02 }, { 1, 0.3 }, { 2, 0.3 }, { 2, 0.3 } },
            { { 0, 0 }, { 0.2, 0.02 }, { 0.1, 0.02 }, { 0.03, 0.02 }, { 0.2, 0.02 }, { 0.5, 0.05 }, { 1.5, 0.3 }, { 1.5, 0.3 } },
            { { 0, 0 }, { 0.2, 0.01 }, { 0.1, 0.02 }, { 0.1, 0.02 }, { 0.15, 0.02 }, { 0.4, 0.02 }, { 1.5, 0.2 }, { 1.5, 0.2 } },
            { { 0, 0 }, { 0.2, 0.01 }, { 0.15, 0.01 }, { 0.1, 0.01 }, { 0.15, 0.02 }, { 0.5, 0.03 }, { 1.5, 0.2 }, { 1.5, 0.2 } },
            { { 0, 0 }, { 0.3, 0.1 }, { 0.3, 0.03 }, { 0.3, 0.03 }, { 0.3, 0.03 }, { 0.5, 0.05 }, { 2, 1 }, { 2, 1 } },
            { { 0, 0 }, { 0.8, 0.5 }, { 0.5, 0.2 }, { 0.5, 0.2 }, { 0.5, 0.2 }, { 0.7, 0.5 }, { 3, 2 }, { 3, 2 } },
            { { 0, 0 }, { 5, 4 }, { 3, 2 }, { 2, 1.5 }, { 2, 1.5 }, { 3, 4 }, { -1, -1 }, { -1, -1 } } };

        // 측정 범위: 레벨 허용 [V] [레인지(0 = 100mΩ), 주파수 구간, {최소, 최대}], 측정 가능 Z [Ω] [레인지, {최소, 최대}]. Wide = 넓은 범위 표
        static readonly double[,,] TblLev =
        { { { 1, 1 }, { 0.101, 5 }, { 0.101, 5 }, { 0.101, 5 }, { 0.101, 5 }, { 0.501, 5 }, { -1, -1 }, { -1, -1 } },
            { { 1, 1 }, { 0.05, 5 }, { 0.05, 5 }, { 0.05, 5 }, { 0.05, 5 }, { 0.101, 5 }, { 0.501, 1 }, { 0.501, 1 } },
            { { 1, 1 }, { 0.05, 5 }, { 0.05, 5 }, { 0.05, 5 }, { 0.05, 5 }, { 0.05, 5 }, { 0.101, 1 }, { 0.101, 1 } },
            { { 1, 1 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.05, 1 }, { 0.101, 1 } },
            { { 1, 1 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.05, 1 }, { 0.101, 1 } },
            { { 1, 1 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.05, 5 }, { 0.05, 1 }, { 0.101, 1 } },
            { { 1, 1 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.05, 5 }, { 0.101, 1 }, { -1, -1 } },
            { { 1, 1 }, { 0.05, 5 }, { 0.05, 5 }, { 0.05, 5 }, { 0.101, 5 }, { 0.501, 5 }, { -1, -1 }, { -1, -1 } },
            { { 1, 1 }, { 0.101, 5 }, { 0.101, 5 }, { 0.101, 5 }, { 0.501, 5 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { 1, 1 }, { 0.101, 5 }, { 0.101, 5 }, { 0.101, 5 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } } };
        static readonly double[,,] TblLevWide =
        { { { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { 1, 1 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.05, 5 }, { 0.05, 1 }, { 0.101, 1 } },
            { { 1, 1 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.01, 5 }, { 0.05, 5 }, { 0.101, 1 }, { -1, -1 } },
            { { 1, 1 }, { 0.05, 5 }, { 0.05, 5 }, { 0.05, 5 }, { 0.101, 5 }, { 0.501, 5 }, { -1, -1 }, { -1, -1 } },
            { { 1, 1 }, { 0.101, 5 }, { 0.101, 5 }, { 0.101, 5 }, { 0.501, 5 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { 1, 1 }, { 0.101, 5 }, { 0.101, 5 }, { 0.101, 5 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } },
            { { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } } };
        static readonly double[,] TblRangeZ = { { 0.001, 0.1 }, { 0.08, 1 }, { 0.8, 10 }, { 8, 100 }, { 80, 1000 }, { 800, 10000 }, { 8000, 100000 }, { 80000, 1000000 }, { 800000, 10000000 }, { 8000000, 200000000 } };
        static readonly double[,] TblRangeZWide = { { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 }, { 80, 10000 }, { 800, 100000 }, { 8000, 1000000 }, { 80000, 10000000 }, { 800000, 100000000 }, { -1, -1 } };

        // 케이블 길이·레인지별 보증 주파수 상한 [cable, range] (공식 앱 CheckSettingRange). DC 바이어스 ON이면 일부 더 낮음
        static readonly double[,] MaxF =
        {
            { 1e6, 8e6, 8e6, 8e6, 8e6, 8e6, 5e6, 1e6, 1e5, 1e4 },
            { 1e6, 1e6, 8e6, 8e6, 8e6, 8e6, 5e6, 1e6, 1e5, 1e4 },
            { 1e6, 1e6, 2e6, 2e6, 2e6, 2e6, 2e6, 1e6, 1e5, 1e4 },
            { 1e5, 1e6, 1e6, 1e6, 1e6, 1e6, 1e6, 1e5, 1e4, 1e4 }
        };

        public static double MaxFreq(int cable, int range, bool bias)
        {
            if (bias && cable < 3 && range == 0) return 1e5;
            if (bias && cable == 0 && range == 1) return 1e6;
            return MaxF[cable, range];
        }

        public static int FreqIndex(double f)
        {
            return f == 0 ? 0 : f < 100 ? 1 : f < 1e3 ? 2 : f <= 1e4 ? 3 : f <= 1e5 ? 4 : f <= 1e6 ? 5 : f <= 5e6 ? 6 : 7;
        }

        // 지금 |Z|보다 큰 첫 레인지 (공식 앱 자동 레인지 버튼)
        public static int AutoRange(double z)
        {
            int i = 0;
            while (i < RangeVal.Length && Math.Abs(z) >= RangeVal[i]) i++;
            return Math.Min(i, RangeVal.Length - 1);
        }

        // 측정값(주, 부) → |Z|, θ [°]. para: 0 Z-θ, 1 Cs-D, 2 Cp-D, 3 Ls-Q, 4 Lp-Q, 5 Rdc (공식 앱 ConvToZPH)
        public static void ToZPh(int para, double main, double sub, double f, out double z, out double ph)
        {
            double w = 2 * Math.PI * f;
            switch (para)
            {
                case 1: ph = -Math.Abs(Math.Atan(1 / sub)) * 180 / Math.PI; z = Math.Abs(-1 / (w * main * Math.Sin(Math.Atan(1 / sub)))); break;
                case 2: ph = -Math.Abs(Math.Atan(1 / sub)) * 180 / Math.PI; z = Math.Abs(Math.Sin(-Math.Atan(1 / sub)) / (w * main)); break;
                case 3: ph = Math.Abs(Math.Atan(sub) * 180 / Math.PI); z = Math.Abs(w * main / Math.Sin(Math.Atan(sub))); break;
                case 4: ph = Math.Abs(Math.Atan(sub) * 180 / Math.PI); z = Math.Abs(-w * main * Math.Sin(-Math.Atan(sub))); break;
                case 5: z = Math.Abs(main); ph = 0; break;
                default: z = Math.Abs(main); ph = sub; break;
            }
        }

        // |Z|, θ → 측정값(주, 부) (공식 앱 ConvToPara)
        public static void FromZPh(int para, double z, double ph, double f, out double main, out double sub)
        {
            double w = 2 * Math.PI * f, p = ph * Math.PI / 180, pc = Math.Max(-90, Math.Min(90, ph)) * Math.PI / 180;
            switch (para)
            {
                case 1: main = -1 / (w * Math.Abs(z) * Math.Sin(pc)); sub = Math.Cos(p) / Math.Abs(Math.Sin(p)); break;
                case 2: main = Math.Sin(-pc) / Math.Abs(z) / w; sub = Math.Cos(p) / Math.Abs(Math.Sin(p)); break;
                case 3: main = Math.Abs(z) * Math.Sin(pc) / w; sub = Math.Abs(Math.Sin(p)) / Math.Cos(p); break;
                case 4: main = -1 / (w * (Math.Sin(-pc) / Math.Abs(z))); sub = Math.Abs(Math.Sin(p)) / Math.Cos(p); break;
                case 5: main = z; sub = 0; break;
                default: main = z; sub = ph; break;
            }
        }

        static double Basic(bool isZ, double z, double f, int range)
        {
            int row = 9 - range, fi = FreqIndex(f);
            double[,,] t = isZ ? TblZ : TblPh;
            double a = t[row, fi, 0], b = t[row, fi, 1];
            if (a < 0 || b < 0) throw new InvalidOperationException("이 주파수·레인지 조합은 정확도 보증 범위 밖입니다. 레인지나 주파수를 바꾸세요.");
            double r = RangeVal[range];
            double acc = range >= 4 ? a + b * Math.Abs(10 * z / r - 1) : a + b * Math.Abs(r / z - 1);
            if (f > 1e6 && f <= 5e6) acc *= (f / 1e6 + 3) / 4;
            else if (f > 5e6) acc *= f / 1e6 / 2;
            return acc;
        }

        // 공식 앱 IM3536 계수. 레벨 1 V 초과는 1 + 2/V (공식 앱 그대로)
        static double LevelCoef(int para, double lev) { return para == 5 ? 1 : lev < 1 ? 1 + 0.2 / lev : lev == 1 ? 1 : 1 + 2 / lev; }
        static double SpeedCoef(int para, int speed) { return para == 5 ? new[] { 4.0, 3, 2, 1 }[speed] : new[] { 8.0, 4, 2, 1 }[speed]; }
        static double CableCoef(int cable) { return new[] { 1, 1.5, 2, 3 }[cable]; }
        static double BiasCoef(int para, bool bias) { return bias && para != 5 ? 2 : 1; }
        static double TempCoef(int temp) { return temp < 18 || temp > 28 ? 1 + 0.1 * Math.Abs(temp - 23) : 1; }

        public static AccResult Accuracy(int para, double main, double sub, double f, double lev, int range, int speed, int cable, bool bias, int temp)
        {
            bool rdc = para == 5;
            if (rdc) { f = 0; lev = 1; }
            else
            {
                if (f < 4 || f > 8e6) throw new InvalidOperationException("주파수는 4 Hz ~ 8 MHz 범위로 입력하세요.");
                if (lev < 0.01 || lev > 5) throw new InvalidOperationException("레벨은 0.01 ~ 5 V 범위로 입력하세요.");
                if (para >= 1 && para <= 4 && sub < 0) throw new InvalidOperationException((para <= 2 ? "D" : "Q") + " 값은 0 이상이어야 합니다.");
                if (f > MaxFreq(cable, range, bias))
                    throw new InvalidOperationException("케이블 " + Cables[cable] + "·레인지 " + Ranges[range] + (bias ? "·DC 바이어스 ON" : "") + "에서 정확도를 보증하는 주파수는 "
                        + Fmt.FrequencyLabel(MaxFreq(cable, range, bias)) + " 이하입니다.");
            }
            if (main == 0 || double.IsNaN(main)) throw new InvalidOperationException("측정값(주 파라미터)을 0이 아닌 숫자로 입력하세요.");
            var r = new AccResult();
            ToZPh(para, main, sub, f, out r.Z, out r.Ph);
            double lc = LevelCoef(para, lev), sc = SpeedCoef(para, speed), cc = CableCoef(cable), bc = BiasCoef(para, bias), tc = TempCoef(temp), k = lc * sc * cc * bc * tc;
            double bz = Basic(true, r.Z, f, range), bp = rdc ? 0 : Basic(false, r.Z, f, range);
            r.AccZ = Math.Abs(bz * k);
            r.AccPh = Math.Abs(bp * k);
            double zmin = r.Z * (1 - r.AccZ / 100), zmax = r.Z * (1 + r.AccZ / 100), pmin = r.Ph - r.AccPh, pmax = r.Ph + r.AccPh;
            var m4 = new double[4];
            var s4 = new double[4];
            FromZPh(para, zmin, pmin, f, out m4[0], out s4[0]);
            FromZPh(para, zmin, pmax, f, out m4[1], out s4[1]);
            FromZPh(para, zmax, pmin, f, out m4[2], out s4[2]);
            FromZPh(para, zmax, pmax, f, out m4[3], out s4[3]);
            Array.Sort(m4);
            double am = Math.Abs(main);
            r.MainMinPct = -Math.Abs(am - m4[0]) / am * 100;
            r.MainMaxPct = Math.Abs(am - m4[3]) / am * 100;
            if (para == 1 || para == 2)
            {
                // D는 θ가 0°나 ±180°를 넘으면 무한대 (공식 앱 CalcMaxMin_D)
                r.SubMin = s4[0];
                r.SubMax = s4[3];
                if (pmin < 0 && pmax > 0) r.SubMax = double.PositiveInfinity;
                if ((pmin < 180 && pmax > 180) || (pmin < -180 && pmax > -180)) r.SubMin = double.NegativeInfinity;
            }
            else if (para == 3 || para == 4)
            {
                // Q (공식 앱 CalcMaxMin_Q): |θ|가 작은 쪽이 최소
                Func<double, double> q = ph => Math.Abs(Math.Sin(ph * Math.PI / 180)) / Math.Cos(ph * Math.PI / 180);
                bool maxBig = Math.Abs(pmax) >= Math.Abs(pmin);
                r.SubMin = q(maxBig ? pmin : pmax);
                r.SubMax = q(maxBig ? pmax : pmin);
                if ((pmin < 0) != (pmax < 0)) r.SubMin = 0;
                if ((pmin < 180 && pmax > 180) || (pmin < -180 && pmax > -180)) r.SubMax = 0;
                // 공식 앱은 θ가 ±90°를 넘으면 Q 최대를 음수로 보여 줌. 실제로는 Q가 무한대까지 갈 수 있다는 뜻이므로 +∞로 표시
                if ((pmin < 90 && pmax > 90) || (pmin < -90 && pmax > -90)) r.SubMax = double.PositiveInfinity;
            }
            else
            {
                Array.Sort(s4);
                r.SubMin = s4[0];
                r.SubMax = s4[3];
            }
            Func<double, string> g = v => v.ToString("0.#####", CultureInfo.InvariantCulture);
            string coefs = " × 레벨 " + g(lc) + " × SPEED " + g(sc) + " × 케이블 " + g(cc) + " × DC바이어스 " + g(bc) + " × 온도 " + g(tc);
            r.Formula = "Z: " + r.AccZ.ToString("F5", CultureInfo.InvariantCulture) + " % = 기본 " + bz.ToString("F5", CultureInfo.InvariantCulture) + coefs
                + (rdc ? "" : "\nθ: " + r.AccPh.ToString("F5", CultureInfo.InvariantCulture) + " ° = 기본 " + bp.ToString("F5", CultureInfo.InvariantCulture) + coefs);
            return r;
        }

        // |Z|가 이 레인지의 측정 범위(보통 또는 넓은 범위 표) 안인지
        public static bool Covers(int range, double z)
        {
            return (z >= TblRangeZ[range, 0] && z <= TblRangeZ[range, 1]) || (TblRangeZWide[range, 0] >= 0 && z >= TblRangeZWide[range, 0] && z <= TblRangeZWide[range, 1]);
        }

        // 레인지에서 잴 수 있는 |Z| 범위 [Ω]. dc = Rdc 측정(레벨 1 V 고정)
        public static void MeasRange(int range, bool dc, double f, double lev, out double zmin, out double zmax)
        {
            if (dc) { f = 0; lev = 1; }
            else
            {
                if (f < 4 || f > 8e6) throw new InvalidOperationException("주파수는 4 Hz ~ 8 MHz 범위로 입력하세요.");
                if (lev < 0.01 || lev > 5) throw new InvalidOperationException("레벨은 0.01 ~ 5 V 범위로 입력하세요.");
            }
            int fi = FreqIndex(f);
            double wmin = TblLevWide[range, fi, 0], wmax = TblLevWide[range, fi, 1];
            bool wide = wmin >= 0 && lev >= wmin && lev <= wmax;
            if (!wide)
            {
                double min = TblLev[range, fi, 0], max = TblLev[range, fi, 1];
                if (min < 0) throw new InvalidOperationException("레인지 " + Ranges[range] + "에서는 이 주파수로 측정할 수 없습니다.");
                if (lev < min || lev > max)
                    throw new InvalidOperationException("레인지 " + Ranges[range] + "·이 주파수에서 레벨은 " + min.ToString("0.###", CultureInfo.InvariantCulture) + " ~ " + max.ToString("0.###", CultureInfo.InvariantCulture) + " V입니다.");
            }
            zmin = wide ? TblRangeZWide[range, 0] : TblRangeZ[range, 0];
            zmax = wide ? TblRangeZWide[range, 1] : TblRangeZ[range, 1];
        }
    }
}
