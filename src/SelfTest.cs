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
    // 자체 점검 (LCR_Logger.exe --selftest) : 데모 계측기로 파싱과 CSV 기록을 확인
    // ------------------------------------------------------------------

    static class SelfTest
    {
        public static int Run()
        {
            var output = new StringBuilder();
            try
            {
                IMeter m = new DemoMeter();
                var pars = new List<string>();
                for (int i = 1; i <= 4; i++)
                {
                    string p = m.Query(":PAR" + i + "?").ToUpperInvariant();
                    if (p != "OFF") pars.Add(p);
                }

                string folder = Path.Combine(Path.GetTempPath(), "lcr_logger_selftest");
                var writer = new CsvWriter();
                writer.Start(folder, Fmt.HeaderFields(pars, 2, new List<string>()));
                for (int n = 1; n <= 3; n++)
                {
                    string response = m.Query(":MEAS?");
                    writer.Append(Fmt.RowFields(n, DateTime.Now, n == 2 ? "코일, A" : "", "CV 1.000V / SPEED MEDIUM / DC바이어스 OFF", pars, new List<string> { response }, 2));
                    output.AppendLine("응답: " + response + "  ->  " + Fmt.Pretty(pars[0], response.Split(',')[0]));
                }
                output.AppendLine("CSV: " + writer.FilePath);
                output.Append(File.ReadAllText(writer.FilePath, Encoding.UTF8));

                // 다중 주파수: 주파수 4개를 측정해 한 행으로, 끝나면 원래 주파수/트리거로 복귀
                var hzList = new List<double>();
                foreach (string f in new[] { "1k", "100kHz", "200 k", "1M" })
                {
                    double hz;
                    Check(Fmt.TryParseFrequency(f, out hz), "주파수 입력 " + f);
                    hzList.Add(hz);
                }
                var labels = hzList.Select(Fmt.FrequencyLabel).ToList();
                Check(string.Join(",", labels) == "1kHz,100kHz,200kHz,1MHz", "주파수 표시");
                double bad;
                Check(!Fmt.TryParseFrequency("9M", out bad) && !Fmt.TryParseFrequency("abc", out bad), "범위 밖 주파수 거부");

                string freqBefore = m.Query(":FREQ?"), trigBefore = m.Query(":TRIG?");
                List<string> responses = MainForm.MeasureFrequencies(m, hzList);
                Check(responses.Count == 4, "주파수 4개 응답");
                Check(m.Query(":FREQ?") == freqBefore && m.Query(":TRIG?") == trigBefore, "원래 주파수/트리거 복귀");
                // 측정 조건: CV + SPEED + DC 바이어스를 보내고, 복원하면 원래 값으로 돌아오는지
                var cond = new MeasCond { Level = "CC", LevelValue = 0.02, Speed = "SLOW2", Bias = 2, BiasVolts = 1.5, Pars = new[] { "LS", "Z", "PHASE", "OFF" } };
                string[] condKeys = { ":LEVel?", ":LEVel:CVOLTage?", ":LEVel:CCURRent?", ":LEVel:VOLTage?", ":SPEEd?", ":DCBias?", ":DCBias:LEVel?", ":PARameter1?", ":PARameter2?", ":PARameter3?", ":PARameter4?" };
                string before = string.Join("|", condKeys.Select(k => m.Query(k)));
                var original = cond.Read(m);
                List<string> condLog = cond.Apply(m);
                Check(m.Query(":LEVel?") == "CC" && m.Query(":LEVel:CCURRent?") == "2.00E-02" && m.Query(":SPEEd?") == "SLOW2" && m.Query(":DCBias?") == "ON", "측정 조건 적용");
                Check(condLog.Count == 4 && condLog.All(l => !l.Contains("※")), "측정 조건 적용 확인 로그");
                Check(m.Query(":PARameter3?") == "PHASE" && m.Query(":MEAS?").Split(',').Length == 3, "PAR2~4 적용 (측정 응답 3개)");
                MeasCond.Restore(m, original);
                Check(string.Join("|", condKeys.Select(k => m.Query(k))) == before, "측정 조건 복원");
                Check(!new MeasCond().Any && cond.Any, "측정 조건 변경 없음 판별");
                foreach (string lv in new[] { "V", "CV" })
                {
                    var one = new MeasCond { Level = lv, LevelValue = 0.25 };
                    var saved = one.Read(m);
                    List<string> oneLog = one.Apply(m);
                    Check(oneLog.Count == 1 && !oneLog[0].Contains("※") && m.Query(":LEVel?") == lv, "측정 신호 " + lv + " 적용");
                    MeasCond.Restore(m, saved);
                    Check(string.Join("|", condKeys.Select(k => m.Query(k))) == before, "측정 신호 " + lv + " 복원");
                }
                output.AppendLine("측정 조건 로그: " + string.Join(" / ", condLog));

                // 그 밖의 설정: 레인지 HOLD 5, 리밋 전압, 트리거 지연 → 복원하면 AUTO·원래 값으로
                string[] exKeys = { ":RANG?", ":RANG:AUTO?", ":LIM:VOLT?", ":TRIG:DEL?", ":DCR:LFR?" };
                string exBefore = string.Join("|", exKeys.Select(k => m.Query(k)));
                var ex = new MeasCond();
                foreach (var kv in new[] { ":RANG:AUTO OFF", ":RANG 5", ":LIM:VOLT 1", ":TRIG:DEL 0.1", ":DCR:LFR 50" })
                    ex.Extra.Add(new KeyValuePair<string, string>(kv.Split(' ')[0], kv.Split(' ')[1]));
                var exSaved = ex.Read(m);
                List<string> exLog = ex.Apply(m);
                Check(ex.Any && m.Query(":RANG?") == "5" && m.Query(":RANG:AUTO?") == "OFF" && exLog.Count == 5 && exLog.All(l => !l.Contains("※")), "그 밖의 측정 조건 적용 " + string.Join(" / ", exLog));
                MeasCond.Restore(m, MeasCond.Deserialize(MeasCond.Serialize(exSaved)));
                Check(string.Join("|", exKeys.Select(k => m.Query(k))) == exBefore, "그 밖의 측정 조건 복원");

                // 비정상 종료 대비: 원래 값을 문자열로 저장했다가 다시 읽어 되돌릴 수 있는지
                var crash = new MeasCond { Level = "CC", LevelValue = 0.01, Speed = "FAST", Bias = 2, BiasVolts = 2, Pars = new[] { "LS", "CP", "D", "Q" } };
                string stored = MeasCond.Serialize(crash.Read(m));
                crash.Apply(m);
                Check(MeasCond.Deserialize(stored).Count == 9, "보관한 원래 값 개수");
                MeasCond.Restore(m, MeasCond.Deserialize(stored));
                Check(string.Join("|", condKeys.Select(k => m.Query(k))) == before, "보관한 원래 값으로 복원");
                Check(MeasCond.Deserialize("이상한 값|:BEEP 1|:SPEEd").Count == 0, "보관 값 중 모르는 명령은 무시");

                // CSV 측정조건 열: 바꾸지 않은 항목도 계측기 실제 값으로
                string summary = MeasCond.Summary(m);
                Check(summary == "V 1.000V / SPEED MEDIUM / DC바이어스 OFF", "측정 조건 요약 " + summary);
                new MeasCond { Level = "CC", LevelValue = 0.02, Bias = 2, BiasVolts = 1.5 }.Apply(m);
                Check(MeasCond.Summary(m) == "CC 20mA / SPEED MEDIUM / DC바이어스 ON 1.50V", "측정 조건 요약 (CC, 바이어스)");
                MeasCond.Restore(m, MeasCond.Deserialize(stored));
                output.AppendLine("측정 조건 요약: " + summary);
                var multiWriter = new CsvWriter();
                multiWriter.Start(folder, Fmt.HeaderFields(pars, 2, labels));
                Check(multiWriter.FilePath != writer.FilePath, "같은 초에 만든 파일도 덮어쓰지 않음");

                // 스프레드시트 입력값: 지수 표기 없이, 인덕턴스만 / 전체
                Check(Fmt.PlainNumber("4.55083E+01") == "45.5083", "지수 표기 -> 일반 소수");
                Check(Fmt.PlainNumber("1.2E-12") == "0.0000000000012", "아주 작은 값");
                Check(Fmt.PlainNumber("9.9E+37") == "9.9E+37", "범위 밖 값은 그대로");
                List<string> lOnly = Fmt.TypedValues(pars, responses, 2, true);
                List<string> all = Fmt.TypedValues(pars, responses, 2, false);
                Check(lOnly.Count == 4 && all.Count == 8, "입력값 개수 (인덕턴스만 4 / 전체 8)");
                Check(lOnly.All(v => !v.Contains("E")), "입력값에 지수 표기 없음");
                Check(KeyboardInput.StructSizeOk(), "SendInput 구조체 크기");
                var lsLp = new List<string> { "LP", "Q", "LS" };
                List<string> picked = Fmt.TypedValues(lsLp, new List<string> { "2.0E-04,4.5E+01,1.0E-04", "1.9E-04,9.0E+01,0.9E-04" }, 2, true);
                Check(string.Join(",", picked) == "100,90", "Lp가 같이 있어도 Ls만 입력");
                output.AppendLine("셀 입력값(인덕턴스만): " + string.Join(" [Tab] ", lOnly));
                output.AppendLine("셀 입력값(전체): " + string.Join(" [Tab] ", all));
                multiWriter.Append(Fmt.RowFields(1, DateTime.Now, "", MeasCond.Summary(m), pars, responses, 2, "1.0E+00,1.0E-03", "레벨 시험"));
                string[] multiLines = File.ReadAllLines(multiWriter.FilePath, Encoding.UTF8);
                Check(multiLines[0].Contains("메모,측정조건,Ls@1kHz [µH],Q@1kHz,Ls@100kHz [µH]"), "다중 주파수 열 제목 (측정조건 열 포함)");
                Check(multiLines[1].Contains(",V 1.000V / SPEED MEDIUM / DC바이어스 OFF,"), "CSV 측정조건 값");
                var trend = Fmt.TrendPoints(lsLp, labels.Take(2).ToList(), new[] { "1", "t", "", "", "200", "45", "100", "190", "90", "90", "", "", "" });
                Check(trend.Count == 2 && trend[0].Key == labels[0] && trend[0].Value == 200 && trend[1].Value == 190, "추이 그래프 점 (주파수별 첫 인덕턴스)");
                Check(Fmt.TrendPoints(pars, new List<string>(), new[] { "1", "t", "", "", "9.99999E+33", "1" }).Count == 0, "추이 그래프에 측정값 없음 빼기");
                Check(multiLines[0].Contains(",\"모니터 V,I\",레벨 경고,원본응답") && multiLines[1].Contains(",\"1.0E+00,1.0E-03\",레벨 시험,"), "CSV 모니터·레벨 경고 열");
                var moniList = new List<string>();
                MainForm.MeasureFrequencies(m, hzList, moniList);
                Check(moniList.Count == 4 && moniList.All(x => Fmt.Tokens(x).Length == 2), "다중 주파수 :MONI? 응답");
                Check(multiLines[1].Split(',').Length >= 3 + 8, "다중 주파수 값 열 개수");
                output.AppendLine("다중 주파수 CSV:");
                output.AppendLine(multiLines[0]);
                output.AppendLine(multiLines[1]);

                Check(Fmt.CellValue("LS", "1.00000E-04", 2) == "100", "H -> µH 환산");
                Check(Fmt.CellValue("LS", "1.00000E-04", 1) == "0.1", "H -> mH 환산");
                Check(Fmt.CellValue("Q", "4.5E+01", 2) == "4.5E+01", "비인덕턴스 값은 그대로");
                Check(Fmt.CsvLine(new[] { "a,b", "c\"d" }) == "\"a,b\",\"c\"\"d\"", "CSV 따옴표 처리");
                Check(Fmt.Pretty("LS", "1.00012E-04") == "Ls = 100.012 µH", "표시 형식");
                Check(Fmt.Header("PH", 2) == "θ [°]" && Fmt.Header("DC", 2) == "Rdc [Ω]", "IM3536 파라미터 이름");
                Check(Fmt.ParLabel("PHASE") == "PHASE (θ)" && Fmt.ParLabel("S") == "S (σ)" && Fmt.ParLabel("LS") == "LS" && Fmt.ParLabel("RDC") == "RDC" && Fmt.ParToken("PHASE (θ)") == "PHASE" && Fmt.ParToken("CV") == "CV", "파라미터 기호 병행 표기");
                Check(m.Query("*TRG;:MEAS?").Split(',').Length == 2, "*TRG;:MEAS? 응답");
                Check(Updater.IsNewer(Updater.ParseTag("v1.5"), new Version(1, 4, 0, 0)), "새 버전 판별");
                Check(!Updater.IsNewer(Updater.ParseTag("v1.4"), new Version(1, 4, 0, 0)), "같은 버전은 업데이트 안 함");
                Check(!Updater.IsNewer(Updater.ParseTag("v1.3.9"), new Version(1, 4, 0, 0)), "낮은 버전은 업데이트 안 함");
                Check(Updater.IsNewer(Updater.ParseTag("1.4.1"), new Version(1, 4, 0, 0)), "패치 버전 판별");
                var rel = Updater.Parse("{\"tag_name\":\"v1.5\",\"html_url\":\"https://github.com/x/y/releases/tag/v1.5\",\"body\":\"수정\",\"assets\":[" +
                    "{\"name\":\"LCR_Logger_v1.5.zip\",\"browser_download_url\":\"https://e/zip\",\"size\":10}," +
                    "{\"name\":\"LCR_Logger.exe\",\"browser_download_url\":\"https://e/exe\",\"size\":54272,\"digest\":\"sha256:ABCDEF\"}]}");
                Check(rel.Asset.Url == "https://e/zip" && rel.Asset.IsZip && rel.Version == new Version(1, 5, 0), "릴리스 정보 해석 (zip 우선)");
                var exeOnly = Updater.Parse("{\"tag_name\":\"v1.5\",\"assets\":[" +
                    "{\"name\":\"LCR_Logger.exe\",\"browser_download_url\":\"https://e/exe\",\"size\":54272,\"digest\":\"sha256:ABCDEF\"}]}");
                Check(exeOnly.Asset.Url == "https://e/exe" && exeOnly.Asset.Size == 54272 && exeOnly.Asset.Sha256 == "abcdef", "릴리스 정보 해석 (exe만)");
                Check(Updater.Parse("{\"tag_name\":\"v1.5\",\"assets\":[]}").Asset == null, "첨부 파일 없는 릴리스");
                Check(MainForm.IsDisconnect(new IOException("x"), new DemoMeter()), "통신 오류는 연결 끊김으로 판단");
                Check(!MainForm.IsDisconnect(new TimeoutException("x"), new DemoMeter()), "응답 없음은 연결 끊김 아님(데모)");
                LanSelfTest();
                var unplug = new DemoMeter();
                unplug.Send(":DEMO:UNPLUG");
                bool threw = false;
                try { unplug.Query(":MEAS?"); } catch (IOException) { threw = true; }
                DemoMeter.UnpluggedUntil = DateTime.MinValue;
                Check(threw && unplug.Query("*IDN?").StartsWith("HIOKI"), "데모 USB 분리/재연결 흉내");
                MuSelfTest(m, condKeys, output);
                ToolsSelfTest(m, output);
                output.AppendLine("SELFTEST OK");
                Print(output.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                Print(output + "SELFTEST FAIL: " + ex.Message + "\r\n");
                return 1;
            }
        }

        static bool Near(double a, double b, double rel) { return Math.Abs(a - b) <= rel * Math.Abs(b); }

        // 복소 투자율: 치수·계산식(알려진 L, R), 가상 코어 스윕, 조건 복원, 경고, CSV, 그래프 PNG
        static void MuSelfTest(IMeter m, string[] condKeys, StringBuilder output)
        {
            double le = Permeability.Le(25, 15), ae = Permeability.Ae(25, 15, 10);
            Check(Math.Abs(le - 61.5) < 0.01 && ae == 50, "le·Ae 계산 (le=" + le + ", Ae=" + ae + ")");
            // L0 = μ0·N²·Ae/le (N=10) ≈ 1.0217e-7 H. L = 10000·L0 이면 μ′ = 10000, Rs − Rw = ω·L0·2000 이면 μ″ = 2000
            double l0 = Permeability.Mu0 * 100 * (ae / 1e6) / (le / 1000);
            Check(Near(l0, 1.0217e-7, 1e-3), "μ0·N²·Ae/le 값 " + l0);
            double f0 = 1e5, w0 = 2 * Math.PI * f0, ls = 10000 * l0, rs = 0.05 + w0 * l0 * 2000, xs = w0 * ls;
            MuRow known = Permeability.Calc(f0, Math.Sqrt(rs * rs + xs * xs), Math.Atan2(xs, rs) * 180 / Math.PI, 0.05, 10, le / 1000, ae / 1e6);
            Check(Near(known.Mu1, 10000, 1e-9) && Near(known.Mu2, 2000, 1e-9) && Near(known.TanD, 0.2, 1e-9) && Near(known.Ls, ls, 1e-9) && Near(known.Rs, rs, 1e-9),
                "알려진 L, R → μ′=" + known.Mu1 + ", μ″=" + known.Mu2);

            Check(double.IsNaN(Permeability.Value("-9.99999E+33")) && double.IsNaN(Permeability.Value("9.99999E+33")) && Permeability.Value(" 5.1200E-02") == 0.0512, "측정값 없음(±9.99999E+33) 처리");
            Check(Near(Permeability.MeasureRdc(m, new List<string>()), DemoMeter.CoreRw, 1e-9) && m.Query(":PAR1?") != "RDC", "DCR 측정 후 PAR 복원");
            DemoMeter.RdcNoValueCount = 2;   // 설정 직후 두 번은 값 없음 → 세 번째에 받음
            var rdcLog = new List<string>();
            Check(Near(Permeability.MeasureRdc(m, rdcLog), DemoMeter.CoreRw, 1e-9) && rdcLog.Count(l => l.Contains("다시 잽니다")) == 2, "Rdc 값 없음이면 다시 잼");
            DemoMeter.RdcNoValueCount = 5;
            bool noRdc = false;
            try { Permeability.MeasureRdc(m, new List<string>()); } catch (InvalidOperationException) { noRdc = true; }
            DemoMeter.RdcNoValueCount = 0;
            Check(noRdc && m.Query(":PAR1?") != "RDC", "Rdc 값이 끝내 없으면 오류, PAR 복원");
            m.Send(":DCR:RANG 6"); m.Send(":DCR:RANG:AUTO OFF");   // DC 레인지 10kΩ HOLD → 그대로 재면 UNDERFLOW
            Check(Near(Permeability.MeasureRdc(m, new List<string>()), DemoMeter.CoreRw, 1e-9) && m.Query(":DCR:RANG?") == "6" && m.Query(":DCR:RANG:AUTO?") == "OFF", "Rdc는 DC 자동 레인지로 재고 HOLD 레인지 복원");
            m.Send(":DCR:RANG:AUTO ON");

            List<double> fl = Permeability.Frequencies(1e3, 8e6, 100, true);
            Check(fl.Count == 100 && fl[0] == 1e3 && fl[99] == 8e6 && Near(fl[1] / fl[0], Math.Pow(8000, 1 / 99.0), 1e-4), "로그 간격 주파수");
            List<double> lin = Permeability.Frequencies(1e3, 2e3, 3, false);
            Check(lin.Count == 3 && lin[1] == 1500, "선형 간격 주파수");

            // 가상 코어 스윕 (CC 1 mA, 평균 4): 저주파 μ′ ≈ 10000, μ″ 최대가 차단 주파수(200 kHz) 근처, 경고 없음, 끝나면 조건 복원
            string[] keys = condKeys.Concat(new[] { ":FREQ?", ":TRIG?", ":AVER?" }).ToArray();
            string before = string.Join("|", keys.Select(k => m.Query(k)));
            var cond = new MeasCond { Level = "CC", LevelValue = 0.001, Speed = "MEDium", Average = 4, Pars = new[] { "Z", "PHASE", "OFF", "OFF" } };
            var log = new List<string>();
            var saved = Permeability.ApplyTemp(m, cond, log);
            Check(m.Query(":TRIG?") == "EXT" && m.Query(":AVER?") == "4" && m.Query(":PAR1?") == "Z" && log.All(l => !l.Contains("※")), "스윕 조건 적용");
            var rowsCc = fl.Select(hz => Permeability.MeasurePoint(m, hz, DemoMeter.CoreRw, DemoMeter.CoreN, le / 1000, ae / 1e6)).ToList();
            MeasCond.Restore(m, saved);
            Check(string.Join("|", keys.Select(k => m.Query(k))) == before, "스윕 후 계측기 조건 복원");
            Check(Near(rowsCc[0].Mu1, 10000, 0.01) && rowsCc[0].Mu2 < 200, "가상 코어 저주파 μ′ " + rowsCc[0].Mu1);
            MuRow peak = rowsCc.OrderByDescending(r => r.Mu2).First();
            Check(peak.F > 150e3 && peak.F < 270e3 && Near(peak.Mu2, 5000, 0.02), "μ″ 최대 " + peak.Mu2 + " @ " + peak.F + " Hz");
            Check(Near(rowsCc[0].I, 0.001, 1e-6) && rowsCc.All(r => !double.IsNaN(r.V)), "Vmoni·Imoni 읽기");
            Check(Permeability.Warnings(rowsCc, cond).Count == 0, "CC 측정은 경고 없음");

            // V 모드(출력 저항 100 Ω): 임피던스가 커지면 전류가 줄어 레벨 경고
            var vCond = new MeasCond { Level = "V", LevelValue = 0.1, Pars = new[] { "Z", "PHASE", "OFF", "OFF" } };
            var vSaved = Permeability.ApplyTemp(m, vCond, new List<string>());
            var rowsV = Permeability.Frequencies(1e3, 8e6, 5, true).Select(hz => Permeability.MeasurePoint(m, hz, 0, 10, le / 1000, ae / 1e6)).ToList();
            MeasCond.Restore(m, vSaved);
            Check(Permeability.Warnings(rowsV, vCond).Any(w => w.Contains("CC 모드")), "V 모드 레벨 변화 경고");
            // 자기공진: θ 부호가 바뀌거나 μ′가 음수
            var res = rowsCc.Take(3).Select(r => Permeability.Calc(r.F, r.Z, r.Theta, 0.05, 10, le / 1000, ae / 1e6)).ToList();
            res.Add(Permeability.Calc(5e6, 100, -30, 0.05, 10, le / 1000, ae / 1e6));
            List<string> rw = Permeability.Warnings(res, cond);
            Check(rw.Count == 1 && rw[0].Contains("자기공진") && rw[0].Contains("5MHz에서") && rw[0].Contains("대략 2.5MHz 이하"), "자기공진 경고와 신뢰 구간 " + rw[0]);
            // 공진(2.3 MHz) 앞에서 μ′가 1/(1-(f/fr)²)로 오르면 5% 넘기 전(500 kHz, 104.96)까지가 신뢰 구간
            var rise = new[] { 1e3, 1e4, 1e5, 3e5, 5e5, 7e5, 1e6, 1.5e6, 2e6 }.Select(f => new MuRow { F = f, Z = 1, Theta = 80, Mu1 = 100 / (1 - f * f / (2.3e6 * 2.3e6)) }).ToList();
            rise.Add(new MuRow { F = 2.5e6, Z = 1, Theta = -80, Mu1 = -50 });
            rw = Permeability.Warnings(rise, cond);
            Check(rw.Count == 1 && rw[0].Contains("대략 500kHz 이하") && rw[0].Contains("5% 넘게"), "μ′ 기울기로 신뢰 구간 " + rw[0]);

            // CC 설정값과 비교: 실측 NX4처럼 처음엔 설정보다 많이 흐르고(최소 신호 한계), 고주파에선 모자람(출력 전압 한계 ~1 V)
            var cc10 = new MeasCond { Level = "CC", LevelValue = 10e-6 };
            var lowSet = new List<MuRow>();
            foreach (double f in new[] { 1e3, 5e3, 3e4, 1e5 }) lowSet.Add(new MuRow { F = f, Z = 1, Theta = 80, Mu1 = 1, I = f < 2e4 ? 86e-6 : 10e-6, V = 0.003 });
            List<string> lw = Permeability.Warnings(lowSet, cc10);
            Check(lw.Count == 1 && lw[0].Contains("설정 10 µA") && lw[0].Contains("실제 최대 86 µA") && lw[0].Contains("1kHz~5kHz") && lw[0].Contains("레벨을 올리세요"), "CC 설정보다 많이 흐름 " + string.Join(" / ", lw));
            var cc100 = new MeasCond { Level = "CC", LevelValue = 100e-6 };
            var clip = new List<MuRow>();
            foreach (double f in new[] { 1e3, 1e5, 1e6, 1.6e6, 2e6 }) clip.Add(new MuRow { F = f, Z = 1, Theta = 80, Mu1 = 1, I = f < 1.5e6 ? 97e-6 : 79e-6, V = f < 1.5e6 ? 0.004 * f / 1e3 : 1.0 });
            clip[2].V = 0.99;   // 1 MHz: 아직 전류는 유지
            List<string> cw = Permeability.Warnings(clip, cc100);
            Check(cw.Count == 1 && cw[0].Contains("설정 100 µA") && cw[0].Contains("실제 최소 79 µA") && cw[0].Contains("1.6MHz부터") && cw[0].Contains("출력 전압 한계"), "CC 출력 전압 한계 " + string.Join(" / ", cw));
            Check(Permeability.WarningKind(cw[0]) == "설정 전류를 유지하지 못함", "경고 종류 구분");

            // 한 점 레벨 비교: CC는 Imoni, CV는 Vmoni ±10%, V(개방전압)는 비교 안 함
            Check(Permeability.LevelWarning("CC", 1e-3, 0.5, 0.98e-3) == "" && Permeability.LevelWarning("CC", 1e-3, 5, 0.6e-3).StartsWith("설정 전류를 유지하지 못함 (설정 1 mA, Vmoni 5 V, Imoni 600 µA)")
                && Permeability.LevelWarning("CC", 10e-6, 0.003, 86e-6).StartsWith("설정보다 전류가 많이 흐름"), "한 점 CC 비교");
            Check(Permeability.LevelWarning("CV", 1, 0.95, 0.01) == "" && Permeability.LevelWarning("CV", 1, 0.7, 0.05).StartsWith("설정 전압과 다름 (설정 1 V, Vmoni 0.7 V")
                && Permeability.LevelWarning("V", 1, 0.2, 0.01) == "", "한 점 CV 비교, V는 비교 안 함");
            MeasCond lo = Permeability.LevelOf("CC 20mA / SPEED MEDIUM / DC바이어스 OFF"), lv = Permeability.LevelOf("CV 1.000V / SPEED FAST / DC바이어스 OFF");
            Check(lo.Level == "CC" && Near(lo.LevelValue, 0.02, 1e-9) && lv.Level == "CV" && lv.LevelValue == 1 && Permeability.LevelOf("확인 불가").Level == null, "측정 조건 요약에서 레벨 읽기");
            Check(Permeability.RowWarning(lv, new List<string> { "1.0E+00,1.0E-02", "6.0E-01,5.0E-02" }, new List<string> { "1kHz", "1MHz" }).StartsWith("1MHz: 설정 전압과 다름"), "다중 주파수 레벨 경고");
            var cvRows = new List<MuRow> { new MuRow { F = 1e3, Z = 1, Theta = 80, Mu1 = 1, V = 1, I = 1e-3 }, new MuRow { F = 1e6, Z = 1, Theta = 80, Mu1 = 1, V = 0.6, I = 5e-3 } };
            List<string> cvw = Permeability.Warnings(cvRows, new MeasCond { Level = "CV", LevelValue = 1 });
            Check(cvw.Count == 1 && cvw[0].StartsWith("설정 전압을 유지하지 못함 (설정 1 V, Vmoni 0.6 V~0.6 V, 1MHz~1MHz)"), "CV 설정 전압 비교 " + string.Join(" / ", cvw));

            // CSV: 머리 정보 + 빈 줄 + 열 제목 + 데이터
            var info = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("시료명", "코어, A") };
            List<string> lines = Permeability.CsvLines(info, rowsCc);
            Check(lines[0] == "시료명,\"코어, A\"" && lines[1] == "" && lines[2] == "f [Hz],Z [Ω],θ [°],Rs [Ω],Ls [H],μ′,μ″,tanδ,Vmoni [V],Imoni [A],레벨 경고"
                && lines.Count == 3 + rowsCc.Count && lines[3].Split(',').Length == 11, "복소 투자율 CSV 형식");
            Check(Permeability.CsvLines(info, new List<MuRow> { new MuRow { F = 1000 } })[3].EndsWith(",,"), "읽지 못한 값은 빈칸");
            Check(Permeability.FileStem(" 코어 A/1 ", 10, new DateTime(2026, 11, 13, 9, 5, 0)) == "MU_코어 A_1_N10턴_2026-11-13_09-05-00"
                && Permeability.FileStem("", 3, new DateTime(2026, 1, 2, 3, 4, 5)) == "MU_시료_N3턴_2026-01-02_03-04-05", "파일 이름 규칙");

            // 그래프 PNG (점 1개, 전체)
            string png = Path.Combine(Path.GetTempPath(), "lcr_logger_selftest", "mu_selftest.png");
            using (Charting.Chart chart = Permeability.NewChart())
            {
                chart.Size = new Size(900, 500);
                chart.SaveImage(png, Charting.ChartImageFormat.Png);   // 프로그램 시작 때처럼 빈 그래프
                Permeability.ClearChart(chart, "selftest", 1e3, 8e6);
                Permeability.AddPoint(chart, rowsCc[0]);
                chart.SaveImage(png, Charting.ChartImageFormat.Png);
                Permeability.AddPoint(chart, new MuRow { F = 1, Mu1 = double.NaN });
                rowsCc.Skip(1).ToList().ForEach(r => Permeability.AddPoint(chart, r));
                Permeability.SavePng(chart, png);
                Check(chart.Series[0].Points.Count == rowsCc.Count && new FileInfo(png).Length > 5000
                    && chart.Size == new Size(900, 500), "그래프 PNG 저장");
                using (var img = Image.FromFile(png)) Check(img.Width == 1200 && img.Height == 700, "PNG 크기 1200×700");
            }
            output.AppendLine("복소 투자율: μ′(1kHz)=" + rowsCc[0].Mu1.ToString("G6", CultureInfo.InvariantCulture) + ", μ″ 최대=" + peak.Mu2.ToString("G5", CultureInfo.InvariantCulture)
                + " @ " + Fmt.FrequencyLabel(peak.F) + ", 그래프: " + png);
            output.AppendLine("복소 투자율 CSV 예: " + lines[3]);
        }

        // 스윕 측정, 계측기 설정, 화면 캡처, IM3536 정확도·측정 범위 계산
        static void ToolsSelfTest(IMeter m, StringBuilder output)
        {
            // 점 목록
            Check(Sweep.ParsePoints(0, "1k, 100k\n1M").SequenceEqual(new[] { 1e3, 1e5, 1e6 }), "스윕 점 목록 (주파수)");
            Check(Sweep.ParsePoints(3, "0.5;10 50").SequenceEqual(new[] { 0.5, 10, 50 }), "스윕 점 목록 (CC mA)");
            foreach (var bad in new[] { new { I = 0, T = "9M" }, new { I = 1, T = "6" }, new { I = 3, T = "0.001" }, new { I = 0, T = " " } })
            {
                bool threw = false;
                try { Sweep.ParsePoints(bad.I, bad.T); } catch (InvalidOperationException) { threw = true; }
                Check(threw, "스윕 점 범위 밖 거부 " + bad.T);
            }

            // 주파수 스윕: PAR 값 개수, 끝나면 주파수·트리거 복원
            string[] keys = { ":FREQ?", ":TRIG?", ":LEVel?", ":LEVel:CCURRent?" };
            string before = string.Join("|", keys.Select(k => m.Query(k)));
            var pars = Enumerable.Range(1, 4).Select(i => m.Query(":PARameter" + i + "?").ToUpperInvariant()).Where(p => p != "OFF").ToList();
            var saved = Permeability.ApplyTemp(m, Sweep.Cond(0, 1e3), new List<string>());
            var rows = Sweep.ParsePoints(0, "1k,10k,100k").Select(hz => Sweep.MeasurePoint(m, 0, hz, pars.Count)).ToList();
            MeasCond.Restore(m, saved);
            Check(rows.Count == 3 && rows.All(r => r.Vals.Length == pars.Count && !double.IsNaN(r.Vals[0]) && !double.IsNaN(r.I)), "주파수 스윕 값");
            Check(string.Join("|", keys.Select(k => m.Query(k))) == before, "주파수 스윕 후 복원");

            // CC 스윕: 점마다 전류를 바꾸고 Imoni가 따라옴, 끝나면 신호 종류·전류 복원
            saved = Permeability.ApplyTemp(m, Sweep.Cond(3, 1), new List<string>());
            Check(m.Query(":LEVel?") == "CC", "CC 스윕 신호 종류");
            var cc = new[] { 1.0, 2, 5 }.Select(ma => Sweep.MeasurePoint(m, 3, ma, pars.Count)).ToList();
            MeasCond.Restore(m, saved);
            Check(cc.Select(r => Math.Round(r.I * 1000, 6)).SequenceEqual(new[] { 1.0, 2, 5 }), "CC 스윕 Imoni " + string.Join(",", cc.Select(r => r.I)));
            Check(string.Join("|", keys.Select(k => m.Query(k))) == before, "CC 스윕 후 복원");

            for (int i = 0; i < cc.Count; i++) { cc[i].Rep = 1; cc[i].No = i + 1; }
            List<string> lines = Sweep.CsvLines(new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("시료명", "A") }, 3, pars, cc);
            Check(lines[2] == "반복,No,정전류 CC [mA],Ls [H],Q,Vmoni [V],Imoni [A],측정시각,레벨 경고" && lines[3].StartsWith("1,1,1,") && lines.Count == 6, "스윕 CSV " + lines[2]);
            Check(Sweep.FileStem("a/b", new DateTime(2026, 11, 13, 1, 2, 3)) == "SWEEP_a_b_2026-11-13_01-02-03", "스윕 파일 이름");
            using (Charting.Chart chart = Sweep.NewChart())
            {
                chart.Size = new Size(600, 400);
                Sweep.ClearChart(chart, "t", 3, pars, true, 1, 5);
                cc.ForEach(r => Sweep.AddPoint(chart, r, pars));
                cc[0].Rep = 2;
                Sweep.AddPoint(chart, cc[0], pars);
                string png = Path.Combine(Path.GetTempPath(), "lcr_logger_selftest", "sweep_selftest.png");
                Permeability.SavePng(chart, png);
                Check(chart.Series.Count == 4 && chart.Series[0].Points.Count == 3 && new FileInfo(png).Length > 3000, "스윕 그래프 (반복마다 선)");
            }

            // 계측기 설정: 모든 항목 읽기, 보내면 그대로 읽힘
            List<string> values = MeterSettings.Items.Select(s => m.Query(s.Cmd + "?")).ToList();
            Check(values.All(v => v.Length > 0), "설정 항목 모두 읽기 (빈 응답: " + string.Join(",", MeterSettings.Items.Where((s, i) => values[i].Length == 0).Select(s => s.Cmd)) + ")");
            SettingItem range = MeterSettings.Items.First(s => s.Cmd == ":RANG");
            Check(MeterSettings.Apply(m, range, "7") == null && m.Query(":RANG?") == "7", "설정 보내기");
            MeterSettings.Apply(m, range, values[Array.IndexOf(MeterSettings.Items, range)]);

            // 화면 캡처 (데모 BMP)
            using (var img = Image.FromStream(new MemoryStream(m.QueryBlock(":HCOPy:DATA? color")))) Check(img.Width == 480 && img.Height == 272, "화면 캡처 BMP");

            // 정확도: Ls 100 µH, Q 50, 1 kHz, 1 V, 1Ω 레인지, MED → |Z| 0.62844 Ω, Z ±4.70948 %, θ ±2.47298 °
            int auto = Im3536Calc.AutoRange(0.6284);
            AccResult a = Im3536Calc.Accuracy(3, 100e-6, 50, 1e3, 1, auto, 1, 0, false, 23);
            Check(auto == 1 && Near(a.Z, 0.628444, 1e-5) && Near(a.Ph, 88.85424, 1e-6) && Near(a.AccZ, 4.709477, 1e-6) && Near(a.AccPh, 2.472985, 1e-6), "정확도 Ls-Q " + a.AccZ + " / " + a.AccPh);
            Check(a.MainMinPct < 0 && a.MainMaxPct > 0 && a.SubMin < 50 && a.SubMax > 50, "정확도 범위 방향");
            AccResult rdc = Im3536Calc.Accuracy(5, 1, 0, 0, 0, 2, 1, 0, false, 23);
            Check(Near(rdc.AccZ, 4.65, 1e-9) && Near(rdc.MainMaxPct, 4.65, 1e-9) && Near(rdc.MainMinPct, -4.65, 1e-9), "정확도 Rdc " + rdc.AccZ);
            Check(Near(Im3536Calc.Accuracy(0, 0.6284, 88, 1e3, 1, 1, 1, 0, false, 33).AccZ, Im3536Calc.Accuracy(0, 0.6284, 88, 1e3, 1, 1, 1, 0, false, 23).AccZ * 2, 1e-9), "온도 계수 (33 °C = 2배)");
            bool limited = false;
            try { Im3536Calc.Accuracy(0, 1e7, 0, 1e5, 1, 9, 1, 3, false, 23); } catch (InvalidOperationException) { limited = true; }
            Check(limited, "케이블·레인지 주파수 한계");
            for (int p = 0; p < 5; p++)
            {
                double z, ph, mm, ss, main = p == 0 ? 50 : p <= 2 ? 1e-9 : 1e-4, sub = p == 0 ? 30 : p <= 2 ? 0.01 : 20;
                Im3536Calc.ToZPh(p, main, sub, 1e4, out z, out ph);
                Im3536Calc.FromZPh(p, z, ph, 1e4, out mm, out ss);
                Check(Near(mm, main, 1e-9) && Near(ss, sub, 1e-9), "Z·θ 변환 왕복 " + Im3536Calc.Paras[p]);
            }

            // 측정 범위: 1kΩ 레인지 1 kHz 1 V는 넓은 범위 표(80 Ω ~ 10 kΩ), 100mΩ 레인지 5 MHz는 측정 불가
            double zmin, zmax;
            Im3536Calc.MeasRange(4, false, 1e3, 1, out zmin, out zmax);
            Check(zmin == 80 && zmax == 10000, "측정 범위 1kΩ");
            Im3536Calc.MeasRange(0, true, 0, 0, out zmin, out zmax);
            Check(zmin == 0.001 && zmax == 0.1, "측정 범위 Rdc 100mΩ");
            bool noRange = false;
            try { Im3536Calc.MeasRange(0, false, 5e6, 1, out zmin, out zmax); } catch (InvalidOperationException) { noRange = true; }
            Check(noRange, "측정 범위 불가 조합");
            Check(Im3536Calc.Covers(1, 0.6284) && !Im3536Calc.Covers(4, 0.6284) && Im3536Calc.Covers(4, 9000), "레인지 측정 범위 안/밖 판정");
            output.AppendLine("정확도 예 (Ls 100µH, Q 50, 1kHz): " + a.Formula.Replace("\n", " / "));
        }

        // 출력이 파일/파이프로 리다이렉트되면 콘솔 인코딩을 바꿀 수 없으므로 UTF-8 바이트로 직접 씀
        static void Print(string text)
        {
            using (var stdout = Console.OpenStandardOutput())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stdout.Write(bytes, 0, bytes.Length);
            }
        }

        // 내 컴퓨터 안에 가짜 계측기(TCP)를 띄워 LAN 연결의 줄 읽기·블록 읽기·주소 해석을 확인
        static void LanSelfTest()
        {
            bool bad = false;
            try { MainForm.NetOpener(1, "192.168.0.1:abc"); } catch (InvalidOperationException) { bad = true; }
            Check(bad, "LAN 잘못된 포트 거부");
            bad = false;
            try { MainForm.NetOpener(2, "31"); } catch (InvalidOperationException) { bad = true; }
            Check(bad, "GPIB 주소 범위 확인");

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(() =>
            {
                using (var c = listener.AcceptTcpClient())
                using (var s = c.GetStream())
                {
                    var r = new StreamReader(s, Encoding.ASCII);
                    var w = new StreamWriter(s, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                    for (string line; (line = r.ReadLine()) != null; )
                    {
                        if (line == "*IDN?") w.WriteLine("HIOKI,IM3536,0,V1.00");
                        else if (line == ":HCOPy:DATA?") w.WriteLine("#15hello");
                    }
                }
            });
            try
            {
                using (var m = MainForm.NetOpener(1, "127.0.0.1:" + port)())
                {
                    Check(m.Query("*IDN?") == "HIOKI,IM3536,0,V1.00", "LAN 응답 한 줄 읽기");
                    Check(Encoding.ASCII.GetString(m.QueryBlock(":HCOPy:DATA?")) == "hello", "LAN 바이너리 블록 읽기");
                    ((ITimed)m).TimeoutMs = 300;
                    bool timedOut = false;
                    try { m.Query(":NOREPLY?"); } catch (TimeoutException) { timedOut = true; }
                    Check(timedOut, "LAN 응답 없음은 시간 초과로 알림");
                }
                server.Wait(3000);
            }
            finally { listener.Stop(); }
        }

        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
        }
    }
}
