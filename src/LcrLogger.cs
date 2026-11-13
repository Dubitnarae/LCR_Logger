// LCR Logger - HIOKI LCR 미터(IM3536 / IM3523 / IM3533 / IM3590 / IM7580 등)에서
// 측정값을 USB(가상 COM 포트)로 받아 CSV 파일로 저장하는 프로그램.
//
// 통신 사양은 HIOKI "LCR Meter Sample Application" 1.4.0 분석 결과를 따름:
//   - USB 연결은 가상 COM 포트 (HIOKI USB VID = 108F)
//   - 터미네이터 CR+LF, RTS 활성
//   - *IDN? / :HEAD OFF / :PAR1?~:PAR4? / :MEAS? / *TRG

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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Charting = System.Windows.Forms.DataVisualization.Charting;

[assembly: System.Reflection.AssemblyTitle("LCR Logger")]
[assembly: System.Reflection.AssemblyProduct("LCR Logger")]
[assembly: System.Reflection.AssemblyCompany("KIM YEONGDO")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 KIM YEONGDO")]
[assembly: System.Reflection.AssemblyVersion("1.16.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.16.0.0")]

namespace LcrLogger
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 압축 파일 안에서 바로 실행하면 임시 폴더에서 실행되므로 압축 해제를 안내
            if (Application.ExecutablePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("압축 파일 안에서 실행한 것 같습니다.\n\n압축을 먼저 풀고(마우스 오른쪽 → 압축 풀기) 풀린 폴더의 LCR_Logger.exe를 실행하세요.",
                    "LCR Logger", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            Updater.CleanupOld();
            Application.Run(new MainForm(args.Contains("--updated")));
            return 0;
        }
    }

    // ------------------------------------------------------------------
    // 자동 업데이트 (GitHub Releases의 최신 릴리스에 올린 LCR_Logger.exe로 교체)
    // ------------------------------------------------------------------

    class ReleaseAsset
    {
        public string Name, Url, Sha256;
        public long Size;
        public bool IsZip { get { return Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase); } }
    }

    class UpdateInfo
    {
        public Version Version;
        public string Tag, Notes, PageUrl;
        public ReleaseAsset Asset;   // 배포 zip(우선) 또는 LCR_Logger.exe
    }

    static class Updater
    {
        public const string Developer = "KIM YEONGDO";
        public const string Repo = "Dubitnarae/LCR_Logger";
        public const string RepoUrl = "https://github.com/" + Repo;
        public const string AssetName = "LCR_Logger.exe";

        public static Version Current
        {
            get { return typeof(Updater).Assembly.GetName().Version; }
        }

        public static string CurrentText
        {
            get { return Current.Major + "." + Current.Minor + (Current.Build > 0 ? "." + Current.Build : ""); }
        }

        // "v1.4", "1.4.1" → Version (읽을 수 없으면 null)
        public static Version ParseTag(string tag)
        {
            if (tag == null) return null;
            var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        public static bool IsNewer(Version latest, Version current)
        {
            return latest != null && Normalize(latest) > Normalize(current);
        }

        // GitHub API의 releases/latest 응답(JSON)을 해석
        public static UpdateInfo Parse(string json)
        {
            var root = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) return null;
            var info = new UpdateInfo
            {
                Tag = root.ContainsKey("tag_name") ? root["tag_name"] as string : null,
                Notes = root.ContainsKey("body") ? root["body"] as string : null,
                PageUrl = root.ContainsKey("html_url") ? root["html_url"] as string : RepoUrl + "/releases"
            };
            info.Version = ParseTag(info.Tag);
            var assets = root.ContainsKey("assets") ? root["assets"] as object[] : null;
            ReleaseAsset zip = null, exe = null;
            if (assets != null)
            {
                foreach (var a in assets.OfType<Dictionary<string, object>>())
                {
                    var asset = new ReleaseAsset
                    {
                        Name = a.ContainsKey("name") ? a["name"] as string ?? "" : "",
                        Url = a.ContainsKey("browser_download_url") ? a["browser_download_url"] as string : null,
                        Size = a.ContainsKey("size") ? Convert.ToInt64(a["size"], CultureInfo.InvariantCulture) : 0
                    };
                    string digest = a.ContainsKey("digest") ? a["digest"] as string : null;
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        asset.Sha256 = digest.Substring(7).ToLowerInvariant();
                    if (asset.Url == null) continue;
                    if (zip == null && asset.IsZip && asset.Name.StartsWith("LCR_Logger", StringComparison.OrdinalIgnoreCase)) zip = asset;
                    if (string.Equals(asset.Name, AssetName, StringComparison.OrdinalIgnoreCase)) exe = asset;
                }
            }
            info.Asset = zip ?? exe;
            return info;
        }

        static System.Net.WebClient Client()
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;   // TLS 1.2
            var wc = new System.Net.WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "LCR_Logger/" + CurrentText;
            return wc;
        }

        public static Task<UpdateInfo> CheckAsync()
        {
            return Task.Run(() =>
            {
                using (var wc = Client())
                {
                    wc.Headers[System.Net.HttpRequestHeader.Accept] = "application/vnd.github+json";
                    return Parse(wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest"));
                }
            });
        }

        static string ExePath { get { return Application.ExecutablePath; } }

        // 새 버전을 받아 검사한 뒤 실행 중인 exe와 바꿔 놓는다. (실행 중인 exe는 이름 변경만 가능)
        public static Task DownloadAndReplaceAsync(UpdateInfo info)
        {
            return Task.Run(() =>
            {
                ReleaseAsset asset = info.Asset;
                string exe = ExePath, dir = Path.GetDirectoryName(exe), tmp = exe + ".new", old = exe + ".old";
                if (File.Exists(tmp)) File.Delete(tmp);

                byte[] data;
                using (var wc = Client()) data = wc.DownloadData(asset.Url);
                if (asset.Size > 0 && data.Length != asset.Size)
                    throw new InvalidDataException("받은 파일 크기가 다릅니다.");
                if (asset.Sha256 != null && Sha256Hex(data) != asset.Sha256)
                    throw new InvalidDataException("받은 파일의 해시가 맞지 않습니다.");

                var docs = new Dictionary<string, byte[]>();
                if (asset.IsZip)
                {
                    byte[] exeBytes = null;
                    using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(data), System.IO.Compression.ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            bool isExe = string.Equals(entry.Name, AssetName, StringComparison.OrdinalIgnoreCase);
                            bool isDoc = entry.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
                            if (!isExe && !isDoc) continue;
                            using (var s = entry.Open())
                            using (var ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                if (isExe) exeBytes = ms.ToArray(); else docs[entry.Name] = ms.ToArray();
                            }
                        }
                    }
                    if (exeBytes == null) throw new InvalidDataException("zip 안에 " + AssetName + "이 없습니다.");
                    data = exeBytes;
                }
                if (data.Length < 2 || data[0] != 'M' || data[1] != 'Z')
                    throw new InvalidDataException("받은 파일이 실행 파일이 아닙니다.");

                File.WriteAllBytes(tmp, data);
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Move(tmp, exe); }
                catch { File.Move(old, exe); throw; }

                // 사용설명서 등 함께 배포한 문서도 새 것으로 (실패해도 업데이트는 유지)
                foreach (var doc in docs)
                {
                    try { File.WriteAllBytes(Path.Combine(dir, doc.Key), doc.Value); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            });
        }

        static string Sha256Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        public static void Restart()
        {
            Process.Start(new ProcessStartInfo(ExePath, "--updated") { UseShellExecute = false });
        }

        // 지난 업데이트에서 남은 이전 exe 정리 (이전 프로세스가 끝날 때까지 몇 번 재시도)
        public static void CleanupOld()
        {
            string old = ExePath + ".old";
            if (!File.Exists(old)) return;
            Task.Run(() =>
            {
                for (int i = 0; i < 20 && File.Exists(old); i++)
                {
                    try { File.Delete(old); }
                    catch (IOException) { Thread.Sleep(500); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(500); }
                }
            });
        }
    }

    // ------------------------------------------------------------------
    // 계측기 통신
    // ------------------------------------------------------------------

    interface IMeter : IDisposable
    {
        void Send(string command);
        string Query(string command);
    }

    class SerialMeter : IMeter
    {
        readonly SerialPort port;
        readonly Stream stream;

        public string PortName { get { return port.PortName; } }
        public bool IsOpen { get { return port.IsOpen; } }

        // 응답 대기 시간. 평소 5초, 복소 투자율 스윕(저주파·SLOW2·평균 많음)에서는 길게
        public int TimeoutMs { get { return port.ReadTimeout; } set { port.ReadTimeout = value; } }

        public SerialMeter(string portName, int baudRate)
        {
            port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            port.RtsEnable = true;
            port.NewLine = "\r\n";
            port.Encoding = Encoding.ASCII;
            port.ReadTimeout = 5000;
            port.WriteTimeout = 3000;
            port.Open();
            // USB를 뽑은 뒤 포트 스트림의 종료자가 예외를 던져 프로그램이 꺼지는 .NET 문제를 피함
            stream = port.BaseStream;
            GC.SuppressFinalize(stream);
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }

        public void Send(string command)
        {
            port.Write(command + "\r\n");
        }

        public string Query(string command)
        {
            port.DiscardInBuffer();
            Send(command);
            try
            {
                return port.ReadLine().Trim();
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("'" + command + "' 응답 없음 (" + port.ReadTimeout / 1000 + "초). 계측기의 인터페이스 설정(USB)과 케이블을 확인하세요.");
            }
        }

        public void Dispose()
        {
            // 장치가 이미 분리된 경우 닫기에서 예외가 나도 무시
            try { if (port.IsOpen) port.Close(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { port.Dispose(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // 연결할 때 계측기에 보내는 측정 조건 (꺼짐/null = 변경 안 함). 연결을 해제하면 바꾼 항목만 원래 값으로 되돌림
    class MeasCond
    {
        public string Level;       // 측정 신호 종류: V(개방전압) / CV(정전압) / CC(정전류), null이면 변경 안 함
        public double LevelValue;  // V, CV는 볼트, CC는 암페어
        public string Speed;       // FAST / MEDium / SLOW / SLOW2
        public int Bias;           // 0 변경 안 함, 1 OFF, 2 ON
        public double BiasVolts;
        public string[] Pars = new string[4];   // PAR1~PAR4에 보낼 파라미터 (null이면 변경 안 함)
        public int Average;        // 평균 횟수 1~256, 0이면 변경 안 함 (복소 투자율 측정에서 사용)

        public bool Any { get { return Level != null || Speed != null || Bias != 0 || Average > 0 || Pars.Any(p => p != null); } }

        static string ParKey(int i) { return ":PARameter" + (i + 1); }

        // 신호 종류별 레벨 값 명령
        static string LevelKey(string mode)
        {
            return mode == "V" ? ":LEVel:VOLTage" : mode == "CV" ? ":LEVel:CVOLTage" : ":LEVel:CCURRent";
        }

        static string LevelName(string mode)
        {
            return mode == "V" ? "개방전압 V" : mode == "CV" ? "정전압 CV" : "정전류 CC";
        }

        static string NormPar(string p)
        {
            p = p.Trim().ToUpperInvariant();
            return p == "PH" ? "PHASE" : p == "DC" ? "RDC" : p;
        }

        static string Volts(double v, string format)
        {
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        // 바꾸기 전 값을 읽어 둠 (바꿀 항목만)
        public Dictionary<string, string> Read(IMeter m)
        {
            var s = new Dictionary<string, string>();
            for (int i = 0; i < Pars.Length; i++)
                if (Pars[i] != null) s[ParKey(i)] = m.Query(ParKey(i) + "?");
            if (Level != null)
            {
                s[":LEVel"] = m.Query(":LEVel?");
                s[LevelKey(Level)] = m.Query(LevelKey(Level) + "?");
            }
            if (Speed != null) s[":SPEEd"] = m.Query(":SPEEd?");
            if (Average > 0)
            {
                // 평균 명령은 공식 앱처럼 줄임말 :AVER 로 보냄 (실기에서 :AVERage? 는 응답 없음, 정식 이름은 :AVERaging)
                // 평균 1회는 OFF로 답함 (HIOKI 샘플 앱과 같은 해석). 되돌릴 때는 공식 앱처럼 숫자로 보냄
                string a = m.Query(":AVER?").Trim();
                s[":AVER"] = a.ToUpperInvariant() == "OFF" ? "1" : a;
            }
            if (Bias != 0) s[":DCBias"] = m.Query(":DCBias?");
            if (Bias == 2) s[":DCBias:LEVel"] = m.Query(":DCBias:LEVel?");
            return s;
        }

        // 설정을 보내고 계측기가 실제로 받은 값을 읽어 로그용 문장으로 돌려줌
        public List<string> Apply(IMeter m)
        {
            var log = new List<string>();
            if (Pars.Any(p => p != null))
            {
                for (int i = 0; i < Pars.Length; i++)
                    if (Pars[i] != null) m.Send(ParKey(i) + " " + Pars[i]);
                var got = new List<string>();
                bool ok = true;
                for (int i = 0; i < Pars.Length; i++)
                {
                    if (Pars[i] == null) continue;
                    string g = m.Query(ParKey(i) + "?");
                    got.Add("PAR" + (i + 1) + "=" + g);
                    if (NormPar(g) != NormPar(Pars[i])) ok = false;
                }
                log.Add("측정 조건: " + string.Join(", ", got) + (ok ? "" : "  ※ 계측기가 값을 받지 않았습니다. 같은 파라미터가 겹치지 않는지 확인하세요."));
            }
            if (Level != null)
            {
                bool cc = Level == "CC";
                m.Send(LevelKey(Level) + " " + (cc ? Volts(LevelValue, "0.00E+00") : Volts(LevelValue, "0.000")));
                m.Send(":LEVel " + Level);
                string mode = m.Query(":LEVel?").ToUpperInvariant(), v = m.Query(LevelKey(Level) + "?");
                double got;
                string shown = cc && Fmt.TryParse(v, out got) ? Volts(got * 1000, "G6") + " mA" : v + " V";
                log.Add("측정 조건: " + LevelName(Level) + " " + shown
                    + (Matches(mode == Level, v, LevelValue, cc ? LevelValue * 0.01 : 0.0005) ? "" : "  ※ 계측기가 값을 받지 않았습니다. 허용 범위를 확인하세요."));
            }
            if (Speed != null)
            {
                m.Send(":SPEEd " + Speed);
                string got = m.Query(":SPEEd?");
                string wanted = Speed.ToUpperInvariant();
                if (wanted == "MEDIUM" || wanted == "MED") wanted = "MEDIUM";
                log.Add("측정 조건: SPEED " + got + (got.ToUpperInvariant() == wanted ? "" : "  ※ 계측기가 값을 받지 않았습니다."));
            }
            if (Average > 0)
            {
                string n = Average.ToString(CultureInfo.InvariantCulture);
                m.Send(":AVER " + n);
                string got = m.Query(":AVER?").Trim();
                bool ok = got == n || (Average == 1 && got.ToUpperInvariant() == "OFF");
                log.Add("측정 조건: 평균 " + got + (ok ? "" : "  ※ 계측기가 값을 받지 않았습니다."));
            }
            if (Bias == 2)
            {
                m.Send(":DCBias:LEVel " + Volts(BiasVolts, "0.00"));
                m.Send(":DCBias ON");
                string on = m.Query(":DCBias?").ToUpperInvariant(), v = m.Query(":DCBias:LEVel?");
                log.Add("측정 조건: DC 바이어스 " + on + " " + v + " V" + (Matches(on == "ON", v, BiasVolts, 0.0005) ? "" : "  ※ 계측기가 값을 받지 않았습니다. 허용 범위를 확인하세요."));
            }
            else if (Bias == 1)
            {
                m.Send(":DCBias OFF");
                log.Add("측정 조건: DC 바이어스 " + m.Query(":DCBias?"));
            }
            return log;
        }

        static bool Matches(bool modeOk, string response, double wanted, double tolerance)
        {
            double got;
            return modeOk && Fmt.TryParse(response, out got) && Math.Abs(got - wanted) <= tolerance;
        }

        // 복원 순서 (Apply의 반대): 주파수·트리거(스윕) → DC 바이어스 → 평균 → SPEED → 레벨 값 → 신호 종류 → PAR1~4
        static readonly string[] RestoreOrder =
        {
            ":FREQ", ":TRIG", ":DCBias:LEVel", ":DCBias", ":AVER", ":SPEEd", ":LEVel:VOLTage", ":LEVel:CVOLTage", ":LEVel:CCURRent", ":LEVel",
            ":PARameter1", ":PARameter2", ":PARameter3", ":PARameter4"
        };

        // Read로 읽어 둔 값을 되돌림. 저장된 항목만 보내므로 프로그램을 다시 켠 뒤에도 그대로 쓸 수 있음
        public static void Restore(IMeter m, Dictionary<string, string> s)
        {
            string v;
            foreach (string key in RestoreOrder)
                if (s.TryGetValue(key, out v) && v.Length > 0) m.Send(key + " " + v);
        }

        // 설정 파일에 보관하는 형식: "키 값|키 값|..." (키와 응답 값에는 공백·| 가 없음)
        public static string Serialize(Dictionary<string, string> s)
        {
            return string.Join("|", RestoreOrder.Where(s.ContainsKey).Select(k => k + " " + s[k]));
        }

        public static Dictionary<string, string> Deserialize(string text)
        {
            var s = new Dictionary<string, string>();
            foreach (string part in text.Split('|'))
            {
                int sp = part.IndexOf(' ');
                if (sp > 0 && RestoreOrder.Contains(part.Substring(0, sp))) s[part.Substring(0, sp)] = part.Substring(sp + 1).Trim();
            }
            return s;
        }

        // CSV에 남길 실제 측정 조건 (프로그램이 바꾸지 않은 항목도 계측기에서 읽음). 예: "CV 1.000V / SPEED MEDIUM / DC바이어스 OFF"
        public static string Summary(IMeter m)
        {
            string mode = m.Query(":LEVel?").Trim().ToUpperInvariant();
            string level = mode;
            if (mode == "V" || mode == "CV" || mode == "CC")
            {
                string v = m.Query(LevelKey(mode) + "?").Trim();
                double a;
                level += " " + (mode == "CC" && Fmt.TryParse(v, out a) ? Volts(a * 1000, "G6") + "mA" : v + "V");
            }
            string bias = m.Query(":DCBias?").Trim().ToUpperInvariant();
            if (bias == "ON") bias += " " + m.Query(":DCBias:LEVel?").Trim() + "V";
            return level + " / SPEED " + m.Query(":SPEEd?").Trim().ToUpperInvariant() + " / DC바이어스 " + bias;
        }
    }

    // 계측기 없이 화면/저장 기능을 확인하기 위한 가상 계측기
    class DemoMeter : IMeter
    {
        readonly Random rnd = new Random();
        double freq = 1000;
        string trig = "INT";
        string level = "V", vvolt = "1.000", cvolt = "1.000", ccurr = "10.00E-03", speed = "MEDIUM", dcbias = "OFF", dclevel = "0.00", aver = "OFF";

        // 가상 토로이달 코어 (복소 투자율 시험용): μi 10000, 차단 주파수 200 kHz 완화형 μ* = μi / (1 + j·f/fc),
        // OD 25 / ID 15 / h 10 mm, 10턴, 권선 저항 0.05 Ω. Z·θ·Rdc·모니터 값이 이 코어를 따름
        public const double CoreMuI = 10000, CoreFc = 200e3, CoreOD = 25, CoreID = 15, CoreH = 10, CoreRw = 0.05;
        public const int CoreN = 10;
        internal static int RdcNoValueCount;   // 자체 점검용: Rdc를 이 횟수만큼 "측정값 없음"(-9.99999E+33)으로 답함

        public static void CoreImpedance(double f, out double z, out double thetaDeg)
        {
            double le = Permeability.Le(CoreOD, CoreID) / 1000, ae = Permeability.Ae(CoreOD, CoreID, CoreH) / 1e6;
            double l0 = Permeability.Mu0 * CoreN * CoreN * ae / le, w = 2 * Math.PI * f, x = f / CoreFc, d = 1 + x * x;
            double rs = w * l0 * CoreMuI * x / d + CoreRw, xs = w * l0 * CoreMuI / d;
            z = Math.Sqrt(rs * rs + xs * xs);
            thetaDeg = Math.Atan2(xs, rs) * 180 / Math.PI;
        }
        readonly string[] pars = { "LS", "Q", "OFF", "OFF" };

        // ":PAR2" / ":PARAMETER2" 형태에서 번호(1~4)를 읽음. 아니면 0
        static int ParIndex(string c)
        {
            Match m = Regex.Match(c, @"^:PAR(?:AMETER)?([1-4])(?:\s|\?|$)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        // 직접 명령 ":DEMO:UNPLUG" → 5초 동안 USB가 빠진 것처럼 동작 (자동 재연결 시험용)
        internal static DateTime UnpluggedUntil = DateTime.MinValue;

        static void CheckPlugged()
        {
            if (DateTime.Now < UnpluggedUntil) throw new IOException("장치가 분리되었습니다 (데모).");
        }

        public void Send(string command)
        {
            CheckPlugged();
            string c = command.Trim().ToUpperInvariant();
            if (c == ":DEMO:UNPLUG") UnpluggedUntil = DateTime.Now.AddSeconds(5);
            double v;
            if (c.StartsWith(":FREQ ") && Fmt.TryParse(c.Substring(6), out v)) freq = v;
            if (c.StartsWith(":TRIG ")) trig = c.Substring(6);
            int pi = ParIndex(c);
            if (pi > 0 && c.IndexOf(' ') > 0) pars[pi - 1] = c.Substring(c.IndexOf(' ') + 1).Trim();
            if (c.StartsWith(":LEVEL:CVOLTAGE ")) cvolt = c.Substring(16);
            else if (c.StartsWith(":LEVEL:VOLTAGE ")) vvolt = c.Substring(15);
            else if (c.StartsWith(":LEVEL:CCURRENT ")) ccurr = c.Substring(16);
            else if (c.StartsWith(":LEVEL ")) level = c.Substring(7);
            if (c.StartsWith(":SPEED ")) speed = c.Substring(7) == "MED" ? "MEDIUM" : c.Substring(7);
            // 실제 IM3536처럼 :AVER / :AVERAGING 만 받음 (:AVERAGE 는 모르는 명령)
            if (Regex.IsMatch(c, @"^:AVER(AGING)? ")) aver = c.Substring(c.IndexOf(' ') + 1).Trim() == "1" ? "OFF" : c.Substring(c.IndexOf(' ') + 1).Trim();
            if (c.StartsWith(":DCBIAS:LEVEL ")) dclevel = c.Substring(14);
            else if (c.StartsWith(":DCBIAS ")) dcbias = c.Substring(8);
        }

        public string Query(string command)
        {
            Thread.Sleep(60);
            CheckPlugged();
            string c = command.Trim().ToUpperInvariant();
            if (c == "*IDN?") return "HIOKI,IM3536(DEMO),000000000,V1.00";
            if (c == ":MODE?") return "LCR";
            if (c == ":FREQ?") return freq.ToString("0.0000E+00", CultureInfo.InvariantCulture);
            if (c == ":TRIG?") return trig;
            if (c == ":LEVEL?") return level;
            if (c == ":LEVEL:CVOLTAGE?") return cvolt;
            if (c == ":LEVEL:VOLTAGE?") return vvolt;
            if (c == ":LEVEL:CCURRENT?") return ccurr;
            if (c == ":SPEED?") return speed;
            if (c == ":DCBIAS?") return dcbias;
            if (c == ":DCBIAS:LEVEL?") return dclevel;
            if (c == ":AVER?" || c == ":AVERAGING?") return aver;
            if (c == "*ESR?") return "0";
            if (c == ":MONI?")
            {
                // 모니터 전압·전류: CC는 전류 일정, CV는 전압 일정, V는 출력 저항 100 Ω 뒤의 개방전압
                double z, th, i, v;
                CoreImpedance(freq, out z, out th);
                if (level == "CC") { Fmt.TryParse(ccurr, out i); v = i * z; }
                else if (level == "CV") { Fmt.TryParse(cvolt, out v); i = v / z; }
                else { Fmt.TryParse(vvolt, out v); i = v / (z + 100); v = i * z; }
                return v.ToString("0.0000E+00", CultureInfo.InvariantCulture) + "," + i.ToString("0.0000E+00", CultureInfo.InvariantCulture);
            }
            int pi = ParIndex(c);
            if (pi > 0 && c.EndsWith("?")) return pars[pi - 1];
            if (c == ":MEAS?" || c == "*TRG;:MEAS?")
            {
                // 주파수가 올라가면 인덕턴스가 조금 줄어드는 코일을 흉내 냄. 켜진 파라미터마다 값 하나
                double l = 100e-6 * (1 - 0.01 * Math.Log10(freq / 1000)) * (1 + (rnd.NextDouble() - 0.5) * 0.002);
                double q = 45 * Math.Sqrt(freq / 1000) + rnd.NextDouble();
                double cz, cth;
                CoreImpedance(freq, out cz, out cth);
                cz *= 1 + (rnd.NextDouble() - 0.5) * 0.001;
                var values = new List<string>();
                foreach (string p in pars)
                {
                    if (p == "OFF") continue;
                    double v = p == "LS" || p == "LP" ? l : p == "Q" ? q : p == "D" ? 1 / q : p == "PHASE" ? cth : p == "Z" ? cz : p == "RDC" ? (RdcNoValueCount-- > 0 ? -9.99999E+33 : CoreRw) : 2 * Math.PI * freq * l;
                    values.Add(v.ToString("0.00000E+00", CultureInfo.InvariantCulture));
                }
                return string.Join(",", values);
            }
            return "";
        }

        public void Dispose() { }
    }

    class PortItem
    {
        public string Port;
        public string Caption;
        public bool Hioki;

        public override string ToString()
        {
            return (Hioki ? "★ " : "") + Caption;
        }

        // 샘플 앱과 같은 방식: WMI로 포트 클래스 장치를 찾고 HIOKI VID(108F)를 표시
        public static List<PortItem> Enumerate()
        {
            var list = new List<PortItem>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Caption, DeviceID FROM Win32_PnPEntity WHERE ClassGuid='{4D36E978-E325-11CE-BFC1-08002BE10318}'"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        string caption = Convert.ToString(o["Caption"]);
                        Match m = Regex.Match(caption ?? "", @"\((COM\d+)\)");
                        if (!m.Success) continue;
                        string id = Convert.ToString(o["DeviceID"]) ?? "";
                        list.Add(new PortItem
                        {
                            Port = m.Groups[1].Value,
                            Caption = caption,
                            Hioki = id.ToUpperInvariant().Contains("VID_108F")
                        });
                    }
                }
            }
            catch (ManagementException) { }

            foreach (string name in SerialPort.GetPortNames())
            {
                if (!list.Any(p => p.Port == name))
                    list.Add(new PortItem { Port = name, Caption = name });
            }
            return list.OrderByDescending(p => p.Hioki).ThenBy(p => p.Port).ToList();
        }
    }

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
            fields.Add("원본응답");
            return fields;
        }

        public static string[] Tokens(string response)
        {
            return response.Split(',').Select(t => t.Trim()).ToArray();
        }

        public static string[] RowFields(int no, DateTime time, string memo, string condition, List<string> pars, List<string> responses, int inductanceUnit)
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
            fields.Add(string.Join(" | ", responses));
            return fields.ToArray();
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

    // ------------------------------------------------------------------
    // 복소 투자율 (토로이달 코어의 μ′, μ″ 주파수 특성)
    // ------------------------------------------------------------------

    class MuRow
    {
        public double F, Z, Theta, Rs, Ls, Mu1, Mu2, TanD, V = double.NaN, I = double.NaN;
        public string Raw;   // 계측기 응답 원문 (":MEAS? | :MONI?")
    }

    static class Permeability
    {
        public const double Mu0 = 4e-7 * Math.PI;

        public static readonly string[] DataHeader = { "f [Hz]", "Z [Ω]", "θ [°]", "Rs [Ω]", "Ls [H]", "μ′", "μ″", "tanδ", "Vmoni [V]", "Imoni [A]" };

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

        public static List<double> Frequencies(double start, double stop, int points, bool log)
        {
            var list = new List<double>();
            for (int i = 0; i < points; i++)
            {
                double t = points == 1 ? 0 : (double)i / (points - 1);
                list.Add(Math.Round(log ? start * Math.Pow(stop / start, t) : start + (stop - start) * t, 2));   // 계측기에 소수 둘째 자리까지 보냄 (공식 앱과 같음)
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
                string resp = "";
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) Thread.Sleep(200);
                    resp = m.Query("*TRG;:MEAS?");
                    double r = Value(Fmt.Tokens(resp)[0]);
                    if (!double.IsNaN(r)) return r;
                    log.Add("Rdc 응답 " + resp + " (측정값 없음) - 다시 잽니다.");
                }
                throw new InvalidOperationException("계측기가 Rdc 값을 주지 않았습니다 (응답 " + resp + " = 측정값 없음).\n"
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

        // cond: 측정에 쓴 조건 (CC이면 Imoni를 설정 전류와 비교, V/CV이면 첫 점과 비교)
        public static List<string> Warnings(List<MuRow> rows, MeasCond cond)
        {
            var w = new List<string>();
            var valid = rows.Where(r => !double.IsNaN(r.Z) && !double.IsNaN(r.Theta)).ToList();
            if (valid.Count == 0) return w;
            int sign = Math.Sign(valid[0].Theta);
            MuRow res = valid.FirstOrDefault(r => r.Mu1 < 0 || Math.Sign(r.Theta) != sign);
            // shortcut: 신뢰 구간을 공진 주파수의 절반으로 어림 (실측 NX4 10턴: 공진 2.3 MHz, μ′ 왜곡은 1 MHz 부근부터). 더 정확히 하려면 μ′ 기울기로 판정
            if (res != null)
                w.Add("권선 자기공진 의심 (" + Fmt.FrequencyLabel(res.F) + "에서 μ′ 음수 또는 θ 부호 바뀜) - 신뢰 구간은 대략 "
                    + RoughFreq(res.F / 2) + " 이하, 더 높은 주파수는 턴 수를 줄여 재측정 권장");

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
            else if (cur.Count > 1 && cur.Any(r => Math.Abs(r.I / cur[0].I - 1) > 0.1))
                w.Add("측정 레벨이 일정하지 않음 (Imoni가 시작값 " + Amps(cur[0].I) + "보다 ±10% 넘게 변함, 범위 "
                    + Amps(cur.Min(r => r.I)) + "~" + Amps(cur.Max(r => r.I)) + "), CC 모드 권장");
            return w;
        }

        static string G(double v) { return double.IsNaN(v) ? "" : v.ToString("G7", CultureInfo.InvariantCulture); }

        // 위쪽에 "항목,값" 측정 조건, 빈 줄, 아래에 데이터 표
        public static List<string> CsvLines(List<KeyValuePair<string, string>> info, List<MuRow> rows)
        {
            var lines = info.Select(kv => Fmt.CsvLine(new[] { kv.Key, kv.Value })).ToList();
            lines.Add("");
            lines.Add(Fmt.CsvLine(DataHeader));
            foreach (MuRow r in rows)
                lines.Add(Fmt.CsvLine(new[] { r.F, r.Z, r.Theta, r.Rs, r.Ls, r.Mu1, r.Mu2, r.TanD, r.V, r.I }.Select(G)));
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

    // 지금 활성화된 창(엑셀, 구글 시트 등)에 키보드로 치는 것처럼 입력
    static class KeyboardInput
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        // Shift/Ctrl/Alt를 누른 채 입력하면 Tab·Enter가 Shift+Tab 등으로 바뀌므로 손을 뗄 때까지 기다림
        public static async Task WaitModifiersReleased()
        {
            const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
            for (int i = 0; i < 100; i++)
            {
                if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0 && (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) == 0)
                    return;
                await Task.Delay(20);
            }
        }

        static INPUT Key(ushort vk, ushort scan, uint flags)
        {
            var input = new INPUT { type = INPUT_KEYBOARD };
            input.u.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags };
            return input;
        }

        static void Send(List<INPUT> inputs)
        {
            INPUT[] array = inputs.ToArray();
            if (SendInput((uint)array.Length, array, Marshal.SizeOf(typeof(INPUT))) != array.Length)
                throw new InvalidOperationException("키 입력이 차단되었습니다 (관리자 권한으로 실행 중인 창에는 입력할 수 없습니다).");
        }

        // 유니코드 문자로 보내므로 한/영 입력 상태와 상관없이 숫자가 그대로 입력됨
        public static void TypeText(string text)
        {
            var inputs = new List<INPUT>();
            foreach (char c in text)
            {
                inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
                inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            if (inputs.Count > 0) Send(inputs);
        }

        // Win32 INPUT 구조체 크기: 64비트 40바이트, 32비트 28바이트여야 SendInput이 동작
        public static bool StructSizeOk()
        {
            return Marshal.SizeOf(typeof(INPUT)) == (IntPtr.Size == 8 ? 40 : 28);
        }

        public static void PressKey(Keys key)
        {
            Send(new List<INPUT> { Key((ushort)key, 0, 0), Key((ushort)key, 0, KEYEVENTF_KEYUP) });
        }
    }

    // 사용자 설정: %APPDATA%\LCR_Logger\settings.ini (키=값)
    class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>();
        readonly string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LCR_Logger", "settings.ini");

        public Settings()
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch (IOException) { }
        }

        public string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : fallback;
        }

        public int GetInt(string key, int fallback, int min, int max)
        {
            int v;
            if (!int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        public void Set(string key, string value)
        {
            values[key] = value;
        }

        public void Remove(string key)
        {
            values.Remove(key);
        }

        public List<string> KeysWithPrefix(string prefix)
        {
            return values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, values.Select(kv => kv.Key + "=" + kv.Value), Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------
    // 디자인 (밝은 회색 배경 + 둥근 흰 카드 + 파란 강조색)
    // ------------------------------------------------------------------

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(245, 245, 247);
        public static readonly Color Card = Color.White;
        public static readonly Color Text = Color.FromArgb(29, 29, 31);
        public static readonly Color Secondary = Color.FromArgb(110, 110, 115);
        public static readonly Color Separator = Color.FromArgb(222, 222, 227);
        public static readonly Color Accent = Color.FromArgb(0, 113, 227);
        public static readonly Color AccentHover = Color.FromArgb(0, 119, 237);
        public static readonly Color AccentPressed = Color.FromArgb(0, 96, 196);
        public static readonly Color Fill = Color.FromArgb(232, 232, 237);
        public static readonly Color FillHover = Color.FromArgb(222, 222, 228);
        public static readonly Color FillPressed = Color.FromArgb(210, 210, 216);
        public static readonly Color Green = Color.FromArgb(36, 138, 61);
        public static readonly Color SwitchOn = Color.FromArgb(52, 199, 89);
        public static readonly Color Red = Color.FromArgb(215, 0, 21);
        public static readonly Color Selection = Color.FromArgb(232, 240, 254);
        public const string FontName = "맑은 고딕";
        public const string NumberFontName = "Segoe UI";

        public static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color ParentBack(Control c)
        {
            return c.Parent != null ? c.Parent.BackColor : Bg;
        }
    }

    // 제목이 위에 붙은 둥근 흰 카드
    class Card : Panel
    {
        readonly string title;
        const int Radius = 12;

        public Card(string title, bool autoSize)
        {
            this.title = title;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            Dock = DockStyle.Fill;
            AutoSize = autoSize;
            if (autoSize) AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0, 0, 0, 8);
            Padding = new Padding(10, title == null ? 8 : 28, 10, 6);
        }

        // 주어진 너비에서 안쪽 내용이 줄바꿈된 뒤의 높이를 돌려줌 (자동 크기 카드)
        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 && proposedSize.Width < 10000 ? proposedSize.Width : Width;
            int inner = 0;
            foreach (Control c in Controls)
                inner = Math.Max(inner, c.GetPreferredSize(new Size(Math.Max(1, width - Padding.Horizontal), 0)).Height);
            return new Size(width, inner + Padding.Vertical);
        }

        int laidOutWidth = -1;

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // 너비가 바뀌면 줄바꿈이 달라지므로 부모가 높이를 다시 계산하게 함
            if (AutoSize && Parent != null && Width != laidOutWidth)
            {
                laidOutWidth = Width;
                int want = GetPreferredSize(new Size(Width, 0)).Height;
                if (want != Height) Parent.PerformLayout();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(r, Radius))
            using (var fill = new SolidBrush(Theme.Card))
            using (var border = new Pen(Theme.Separator))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            if (title != null)
            {
                using (var f = new Font(Theme.FontName, 9.5F, FontStyle.Bold))
                    TextRenderer.DrawText(g, title, f, new Point(14, 8), Theme.Text, Theme.Card);
            }
        }
    }

    // 알약 모양 버튼 (primary = 파란 배경)
    class PillButton : Button
    {
        readonly bool primary;
        bool hover, pressed;

        public PillButton(bool primary)
        {
            this.primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Margin = new Padding(4, 3, 4, 3);
            AccessibleRole = AccessibleRole.PushButton;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(t.Width + 28, Math.Max(30, t.Height + 12));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.ParentBack(this));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color back, fore;
            if (!Enabled) { back = primary ? Color.FromArgb(176, 208, 243) : Color.FromArgb(240, 240, 243); fore = primary ? Color.White : Color.FromArgb(175, 175, 180); }
            else if (primary) { back = pressed ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent; fore = Color.White; }
            else { back = pressed ? Theme.FillPressed : hover ? Theme.FillHover : Theme.Fill; fore = Theme.Text; }
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(r, Math.Min(Height / 2f - 1, 15)))
            using (var b = new SolidBrush(back))
                g.FillPath(b, path);
            if (Focused && ShowFocusCues)
            {
                using (var path = Theme.Rounded(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Math.Min(Height / 2f - 2, 14)))
                using (var p = new Pen(Color.FromArgb(120, Theme.Accent), 2))
                    g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // 입력 컨트롤을 감싸는 둥근 테두리 칸 (평소 연회색, 입력 중 파란색, 비활성 회색 바탕)
    class FieldBox : Panel
    {
        readonly Control inner;
        bool focused;
        const int Height0 = 30, PadLeft = 7, PadRight = 3;
        static readonly Color Border = Color.FromArgb(199, 199, 204);
        static readonly Color DisabledFill = Color.FromArgb(242, 242, 245);

        public FieldBox(Control inner)
        {
            this.inner = inner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Margin = new Padding(4, 3, 4, 3);
            Size = new Size(inner.Width + PadLeft + PadRight, Height0);
            inner.Margin = Padding.Empty;
            Controls.Add(inner);
            inner.Enter += delegate { focused = true; Invalidate(); };
            inner.Leave += delegate { focused = false; Invalidate(); };
            inner.EnabledChanged += delegate { ApplyColors(); };
            inner.TextChanged += delegate { if (!inner.Visible) Invalidate(); };
            var combo = inner as ComboBox;
            if (combo != null) combo.SelectedIndexChanged += delegate { if (!inner.Visible) Invalidate(); };
            ApplyColors();
            // 칸 아무 곳이나 눌러도 입력 컨트롤로
            MouseDown += delegate { if (inner.Enabled) inner.Focus(); };
        }

        void ApplyColors()
        {
            Color fill = inner.Enabled ? Theme.Card : DisabledFill;
            BackColor = fill;
            inner.BackColor = fill;
            // 비활성 선택 상자는 자체 회색 틀이 생기므로 숨기고 값만 직접 그림
            if (inner is ComboBox) inner.Visible = inner.Enabled;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            inner.SetBounds(PadLeft, Math.Max(0, (Height - inner.Height) / 2), Math.Max(10, Width - PadLeft - PadRight), inner.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.ParentBack(this));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(r, 7))
            using (var fill = new SolidBrush(BackColor))
            using (var pen = new Pen(focused ? Theme.Accent : Border, focused ? 1.6f : 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (!inner.Visible)
            {
                var textRect = new Rectangle(PadLeft + 2, 0, Width - PadLeft - 22, Height);
                TextRenderer.DrawText(g, inner.Text, Font, textRect, Theme.Secondary, BackColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                float cx = Width - 13, cy = Height / 2f;
                using (var p = new Pen(Color.FromArgb(175, 175, 180), 1.5f))
                    g.DrawLines(p, new[] { new PointF(cx - 3.5f, cy - 1.5f), new PointF(cx, cy + 2), new PointF(cx + 3.5f, cy - 1.5f) });
            }
        }
    }

    // 체크박스를 토글 스위치 모양으로 그림 (CheckBox 동작은 그대로)
    class Switch : CheckBox
    {
        const int TrackW = 34, TrackH = 20;

        public Switch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            AutoSize = true;
        }

        // 직접 그리는 컨트롤이라 화면 낭독기/자동화에 체크 상자임을 알려 줌
        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new SwitchAccessible(this);
        }

        class SwitchAccessible : ControlAccessibleObject
        {
            readonly Switch owner;
            public SwitchAccessible(Switch owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
            public override string DefaultAction { get { return owner.Checked ? "선택 취소" : "선택"; } }
            public override AccessibleStates State
            {
                get
                {
                    var s = AccessibleStates.Focusable;
                    if (owner.Checked) s |= AccessibleStates.Checked;
                    if (!owner.Enabled) s |= AccessibleStates.Unavailable;
                    if (owner.Focused) s |= AccessibleStates.Focused;
                    return s;
                }
            }
            public override void DoDefaultAction() { if (owner.Enabled) owner.Checked = !owner.Checked; }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(TrackW + 8 + t.Width + 2, Math.Max(TrackH + 4, t.Height + 4));
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color back = Theme.ParentBack(this);
            g.Clear(back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float y = (Height - TrackH) / 2f;
            Color track = Checked ? Theme.SwitchOn : Color.FromArgb(220, 220, 225);
            if (!Enabled) track = Color.FromArgb(120, track);
            using (var path = Theme.Rounded(new RectangleF(1, y, TrackW, TrackH), TrackH / 2f))
            using (var b = new SolidBrush(track))
                g.FillPath(b, path);
            float knob = TrackH - 4;
            float x = Checked ? 1 + TrackW - knob - 2 : 3;
            using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                g.FillEllipse(shadow, x, y + 2.6f, knob, knob);
            using (var k = new SolidBrush(Color.White))
                g.FillEllipse(k, x, y + 2, knob, knob);
            var textRect = new Rectangle(TrackW + 9, 0, Width - TrackW - 9, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? Theme.Text : Theme.Secondary, back,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
    }

    // ------------------------------------------------------------------
    // 화면
    // ------------------------------------------------------------------

    class MainForm : Form
    {
        ComboBox cbPort, cbBaud, cbUnit, cbSpeed, cbBias, cbLevel;
        ComboBox[] cbPar;                           // PAR2~PAR4 (PAR1은 Ls 고정)
        ComboBox cbPreset;                          // 측정 조건 프리셋 (이름을 직접 입력해 저장)
        Button btnApplyCond, btnPresetSave, btnPresetDelete;
        string condText = "";                       // CSV 측정조건 열에 남길 계측기의 실제 조건
        string currentIdn;                          // 연결한 계측기 *IDN? (복원 못 한 설정을 같은 계측기에만 되돌리기 위함)
        string updateAttemptDay;                    // 오늘 업데이트 확인을 시도했는지 (실패해도 1초마다 다시 시도하지 않도록)
        const string PresetPrefix = "Preset.";
        Label lblLevelUnit;
        TextBox txtCv, txtBias;                     // txtCv: 측정 신호 레벨 값 (V 또는 mA)
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
        string lostPort, lastPortList;
        int lostBaud, reconnectTries, reconnectGen;
        DateTime portSeenAt = DateTime.MinValue;

        // 마지막 측정값 (다시 입력용)
        List<string> lastResponses, lastPars;
        const int RepeatHotkeyId = 0x4C44;
        ComboBox cbHotkey, cbSheetValues, cbSheetSep, cbSheetEnd;
        NumericUpDown numInterval, numCount;
        TextBox txtMemo, txtFolder, txtCmd, txtLog;
        TextBox[] txtFreq;
        Label lblModel, lblFile, lblLast;
        DataGridView grid;

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

        public MainForm(bool justUpdated)
        {
            this.justUpdated = justUpdated;
            Text = "LCR Logger " + Updater.CurrentText + " - 인덕턴스 측정 기록";
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
            // Enter = 측정은 기록 탭에서만 (투자율 탭 입력칸에서 Enter를 눌러도 기록 측정이 되지 않도록)
            tabs.SelectedIndexChanged += delegate { AcceptButton = tabs.SelectedIndex == 0 ? btnMeasure : null; };

            // 1. 연결
            var conn = Group("1. 연결");
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
            Add(conn, Lbl("COM 포트"), cbPort, btnRefresh, Lbl("속도(USB는 무관)"), cbBaud, chkDemo, chkReconnect, btnConnect, lblModel);
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
            txtFreq = new TextBox[4];
            string[] defaults = { "1k", "100k", "200k", "1M" };
            for (int i = 0; i < txtFreq.Length; i++)
            {
                txtFreq[i] = new TextBox { Width = 70, Text = defaults[i] };
                Add(multi, Lbl("주파수" + (i + 1)), txtFreq[i]);
            }
            var hint = Lbl("Hz · 예: 120, 1k, 100k, 1M · 빈칸은 건너뜀");
            hint.ForeColor = Theme.Secondary;
            Add(multi, hint);
            layout.Controls.Add(multi.Parent);

            // 측정 조건: 연결할 때 보내고, 연결을 해제하면 원래 값으로 복원. 모든 주파수에 같은 조건 적용
            var cond = Group("측정 조건 (연결 전에 설정 · 연결 해제하면 계측기 값을 원래대로 복원)");
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
                cbPar[i].SelectedIndex = 0;
                parRow.Add(Lbl("PAR" + (i + 2)));
                parRow.Add(cbPar[i]);
            }
            Add(cond, parRow.ToArray());
            cond.SetFlowBreak(cbPar[cbPar.Length - 1], true);

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
            var gridCard = new Card(null, false) { Padding = new Padding(10, 8, 10, 10) };
            gridCard.Controls.Add(grid);
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
            chkTrg.Checked = settings.Get("ExtTrigger", "0") == "1";
            numInterval.Value = settings.GetInt("IntervalMs", 1000, 200, 3600000) / 1000M;
            numCount.Value = settings.GetInt("Count", 10, 0, 100000);
            chkMulti.Checked = settings.Get("MultiFreq", "1") == "1";
            string[] savedFreqs = settings.Get("Freqs", "").Split(';');
            if (savedFreqs.Length == txtFreq.Length)
                for (int i = 0; i < txtFreq.Length; i++) txtFreq[i].Text = savedFreqs[i];
            // 1.8 이전 설정(CondCv=1)은 CV로 이어받음
            cbLevel.SelectedIndex = settings.GetInt("CondLevel", settings.Get("CondCv", "0") == "1" ? 2 : 0, 0, cbLevel.Items.Count - 1);
            txtCv.Text = settings.Get("LevelValue", settings.Get("CvVolts", txtCv.Text));
            cbSpeed.SelectedIndex = settings.GetInt("CondSpeed", 0, 0, cbSpeed.Items.Count - 1);
            cbBias.SelectedIndex = settings.GetInt("CondBias", 0, 0, cbBias.Items.Count - 1);
            txtBias.Text = settings.Get("BiasVolts", txtBias.Text);
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
            settings.Set("ExtTrigger", chkTrg.Checked ? "1" : "0");
            settings.Set("IntervalMs", ((int)(numInterval.Value * 1000)).ToString(CultureInfo.InvariantCulture));
            settings.Set("Count", ((int)numCount.Value).ToString(CultureInfo.InvariantCulture));
            settings.Set("MultiFreq", chkMulti.Checked ? "1" : "0");
            settings.Set("CondLevel", cbLevel.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("LevelValue", txtCv.Text.Trim());
            settings.Set("CondSpeed", cbSpeed.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("CondBias", cbBias.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            settings.Set("BiasVolts", txtBias.Text.Trim());
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
                parent.Controls.Remove(c);
                var field = new FieldBox(c);
                parent.Controls.Add(field);
                parent.Controls.SetChildIndex(field, index);
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

        // ---------------- 연결 ----------------

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
                if (chkDemo.Checked)
                {
                    m = new DemoMeter();
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
        MeasCond ReadCond()
        {
            var c = new MeasCond();
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
                    // 복소 투자율 측정 직전 값으로 먼저 되돌림 (실패하면 파일에 남은 값으로 다음 연결 때 묻습니다)
                    try { MeasCond.Restore(meter, muSaved); muSaved = null; PersistPending(); Log("복소 투자율 측정 전 계측기 설정으로 되돌렸습니다."); }
                    catch (Exception ex) { Log("복소 투자율 측정 전 설정 복원 실패: " + ex.Message); }
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
            lostPort = serial != null ? serial.PortName : null;
            int baud;
            lostBaud = int.TryParse(cbBaud.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out baud) ? baud : 9600;
            reconnectTries = 0;
            reconnectGen++;
            portSeenAt = DateTime.MinValue;
            lastPortList = null;
            lblModel.Text = "USB 연결 끊김 - 다시 연결되기를 기다리는 중...";
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
                m = lostDemo ? new DemoMeter() : await Task.Run(() => (IMeter)new SerialMeter(port, baud));
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
                if (list.Count == 0)
                {
                    // EXT 트리거일 때는 한 줄로 보내 측정이 끝난 뒤의 값을 받음
                    string response = await Io(() => m.Query(trg ? "*TRG;:MEAS?" : ":MEAS?"));
                    responses = new List<string> { response };
                }
                else
                {
                    responses = await Io(() => MeasureFrequencies(m, list));
                }
                AddRow(responses);
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
        internal static List<string> MeasureFrequencies(IMeter m, List<double> list)
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
                }
            }
            finally
            {
                m.Send(":FREQ " + originalFreq);
                m.Send(":TRIG " + originalTrigger);
            }
            return responses;
        }

        void AddRow(List<string> responses)
        {
            foreach (string response in responses)
            {
                int count = Fmt.Tokens(response).Length;
                if (count != pars.Count)
                    Log("※ 응답 값 개수(" + count + ")가 파라미터 개수(" + pars.Count + ")와 다릅니다. 원본응답 열을 확인하세요.");
            }

            lastResponses = responses;
            lastPars = pars;
            string[] row = Fmt.RowFields(rows.Count + 1, DateTime.Now, txtMemo.Text, condText, pars, responses, unitIndex);
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
            lblLast.Text = "#" + rows.Count + "   " + (parts.Count > 0 ? string.Join("    ", parts) : string.Join(" | ", responses));
            lblLast.ForeColor = Theme.Text;
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
            foreach (var kv in muSettings)
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
            foreach (var kv in muSettings)
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

            if (muSaved != null && meter == m)
            {
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
            var serial = m as SerialMeter;
            if (serial == null) return;
            try { serial.TimeoutMs = ms; }
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
                try { r = await Io(() => Permeability.MeasureRdc(m, lines)); }
                finally { foreach (string l in lines) Log(l); }
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

        // ---------------- 공통 ----------------

        void UpdateUi()
        {
            bool connected = meter != null;
            btnConnect.Text = connected ? "연결 해제" : waitingReconnect ? "재연결 취소" : "연결";
            btnConnect.Enabled = !busy || connected;
            // 재연결을 기다리는 동안에는 끊기기 전 설정 그대로 다시 연결하므로 설정을 잠금
            bool editable = !connected && !busy && !waitingReconnect;
            cbPort.Enabled = btnRefresh.Enabled = cbBaud.Enabled = chkDemo.Enabled = chkMulti.Enabled = editable;
            // 측정 조건은 연결 중에도 바꿀 수 있음 ([연결 중 적용]으로 반영)
            bool condEditable = !busy && !waitingReconnect && !autoRunning;
            foreach (ComboBox c in cbPar) c.Enabled = condEditable;
            cbPreset.Enabled = btnPresetSave.Enabled = btnPresetDelete.Enabled = condEditable;
            btnApplyCond.Enabled = connected && condEditable;
            cbLevel.Enabled = txtCv.Enabled = cbSpeed.Enabled = cbBias.Enabled = txtBias.Enabled = condEditable;
            foreach (TextBox t in txtFreq) t.Enabled = editable;
            chkTrg.Enabled = freqs.Count == 0 || !connected;
            btnMeasure.Enabled = connected && !busy && !autoRunning;
            btnAuto.Enabled = connected && !muRunning;
            btnAuto.Text = autoRunning ? "연속 측정 중지" : "연속 측정 시작";
            // 복소 투자율: 측정 중에는 연결 해제와 입력 변경을 막음
            if (muRunning) btnConnect.Enabled = false;
            btnMuStart.Enabled = btnDcr.Enabled = connected && !busy && !autoRunning;
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
                multiWriter.Append(Fmt.RowFields(1, DateTime.Now, "", MeasCond.Summary(m), pars, responses, 2));
                string[] multiLines = File.ReadAllLines(multiWriter.FilePath, Encoding.UTF8);
                Check(multiLines[0].Contains("메모,측정조건,Ls@1kHz [µH],Q@1kHz,Ls@100kHz [µH]"), "다중 주파수 열 제목 (측정조건 열 포함)");
                Check(multiLines[1].Contains(",V 1.000V / SPEED MEDIUM / DC바이어스 OFF,"), "CSV 측정조건 값");
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
                var unplug = new DemoMeter();
                unplug.Send(":DEMO:UNPLUG");
                bool threw = false;
                try { unplug.Query(":MEAS?"); } catch (IOException) { threw = true; }
                DemoMeter.UnpluggedUntil = DateTime.MinValue;
                Check(threw && unplug.Query("*IDN?").StartsWith("HIOKI"), "데모 USB 분리/재연결 흉내");
                MuSelfTest(m, condKeys, output);
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

            // CSV: 머리 정보 + 빈 줄 + 열 제목 + 데이터
            var info = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("시료명", "코어, A") };
            List<string> lines = Permeability.CsvLines(info, rowsCc);
            Check(lines[0] == "시료명,\"코어, A\"" && lines[1] == "" && lines[2] == "f [Hz],Z [Ω],θ [°],Rs [Ω],Ls [H],μ′,μ″,tanδ,Vmoni [V],Imoni [A]"
                && lines.Count == 3 + rowsCc.Count && lines[3].Split(',').Length == 10, "복소 투자율 CSV 형식");
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

        // 출력이 파일/파이프로 리다이렉트되면 콘솔 인코딩을 바꿀 수 없으므로 UTF-8 바이트로 직접 씀
        static void Print(string text)
        {
            using (var stdout = Console.OpenStandardOutput())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stdout.Write(bytes, 0, bytes.Length);
            }
        }

        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
        }
    }
}
