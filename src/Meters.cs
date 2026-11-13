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
    // 계측기 통신
    // ------------------------------------------------------------------

    interface IMeter : IDisposable
    {
        void Send(string command);
        string Query(string command);
        byte[] QueryBlock(string command);   // 바이너리 블록 응답 (#<자릿수><길이><데이터>), 예: 화면 캡처
    }

    // 응답 대기 시간을 바꿀 수 있는 연결 (USB·RS-232C·LAN·GPIB)
    interface ITimed
    {
        int TimeoutMs { get; set; }
    }

    static class Block
    {
        // '#', 길이 자릿수 n, n자리 길이, 데이터 순서로 읽음 (끝 문자는 읽지 않음)
        public static byte[] Read(Stream s, string command)
        {
            if (s.ReadByte() != '#') throw new InvalidDataException("'" + command + "' 응답이 바이너리 블록(#)이 아닙니다.");
            int digits = s.ReadByte() - '0';
            var len = new StringBuilder();
            for (int i = 0; i < digits && i < 9; i++) len.Append((char)s.ReadByte());
            int size;
            if (digits < 1 || digits > 9 || !int.TryParse(len.ToString(), out size) || size <= 0)
                throw new InvalidDataException("'" + command + "' 블록 길이를 읽을 수 없습니다 (" + len + ").");
            var data = new byte[size];
            for (int got = 0; got < size; )
            {
                int n = s.Read(data, got, size - got);
                if (n <= 0) throw new IOException("'" + command + "' 블록 데이터가 중간에 끊겼습니다.");
                got += n;
            }
            return data;
        }
    }

    // LAN: 계측기의 통신 포트(기본 3500)에 TCP로 연결. 공식 앱 LAN.cs와 같은 방식 (CR+LF 보내고 LF까지 받음)
    class LanMeter : IMeter, ITimed
    {
        readonly TcpClient client;
        readonly NetworkStream stream;
        public readonly string Address;

        public int TimeoutMs { get { return stream.ReadTimeout; } set { stream.ReadTimeout = value; } }

        public LanMeter(string host, int port)
        {
            Address = host + ":" + port;
            client = new TcpClient { NoDelay = true };
            try
            {
                // 기본 Connect는 주소가 틀리면 20초 넘게 멈추므로 5초만 기다림
                if (!client.ConnectAsync(host, port).Wait(5000))
                    throw new TimeoutException(Address + " 에 연결하지 못했습니다 (5초). 계측기의 IP 주소·포트와 LAN 케이블을 확인하세요.");
            }
            catch (AggregateException ex)
            {
                client.Close();
                throw new IOException(Address + " 연결 실패: " + ex.InnerException.Message, ex.InnerException);
            }
            catch { client.Close(); throw; }
            stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 3000;
        }

        public void Send(string command)
        {
            byte[] b = Encoding.ASCII.GetBytes(command + "\r\n");
            stream.Write(b, 0, b.Length);
        }

        void Discard()
        {
            var buf = new byte[4096];
            while (stream.DataAvailable) stream.Read(buf, 0, buf.Length);
        }

        Exception Timeout(IOException ex, string command)
        {
            var se = ex.InnerException as SocketException;
            if (se != null && se.SocketErrorCode == SocketError.TimedOut)
                return new TimeoutException("'" + command + "' 응답 없음 (" + stream.ReadTimeout / 1000 + "초). 계측기의 인터페이스 설정(LAN)을 확인하세요.");
            return null;
        }

        public string Query(string command)
        {
            Discard();
            Send(command);
            var line = new List<byte>();
            try
            {
                for (int b; (b = stream.ReadByte()) != '\n'; )
                {
                    if (b < 0) throw new IOException("계측기가 LAN 연결을 끊었습니다.");
                    line.Add((byte)b);
                }
            }
            catch (IOException ex) { throw Timeout(ex, command) ?? ex; }
            return Encoding.ASCII.GetString(line.ToArray()).Trim();
        }

        public byte[] QueryBlock(string command)
        {
            Discard();
            Send(command);
            try
            {
                byte[] data = Block.Read(stream, command);
                Thread.Sleep(100);
                Discard();   // 끝 문자
                return data;
            }
            catch (IOException ex) { throw Timeout(ex, command) ?? ex; }
        }

        public void Dispose()
        {
            try { client.Close(); }
            catch (SocketException) { }
        }
    }

    // GPIB: NI-VISA 또는 Keysight IO Libraries의 visa32.dll을 직접 부름 (공식 앱은 NI-488.2 .NET 라이브러리 사용)
    class GpibMeter : IMeter, ITimed
    {
        [DllImport("visa32.dll")] static extern int viOpenDefaultRM(out int rm);
        [DllImport("visa32.dll")] static extern int viOpen(int rm, string rsrc, int mode, int timeout, out int vi);
        [DllImport("visa32.dll")] static extern int viClose(int vi);
        [DllImport("visa32.dll")] static extern int viSetAttribute(int vi, int attr, IntPtr value);
        [DllImport("visa32.dll")] static extern int viWrite(int vi, byte[] buf, int count, out int ret);
        [DllImport("visa32.dll")] static extern int viRead(int vi, byte[] buf, int count, out int ret);

        const int AttrTimeout = 0x3FFF001A;                 // VI_ATTR_TMO_VALUE
        const int SuccessMaxCount = 0x3FFF0006;             // VI_SUCCESS_MAX_CNT: 버퍼가 차서 더 읽을 것이 있음
        const int ErrorTimeout = unchecked((int)0xBFFF0015); // VI_ERROR_TMO

        readonly int rm, vi;
        int timeoutMs = 5000;
        public readonly string Resource;

        public int TimeoutMs
        {
            get { return timeoutMs; }
            set { Check(viSetAttribute(vi, AttrTimeout, new IntPtr(value)), "대기 시간 설정"); timeoutMs = value; }
        }

        public GpibMeter(int address)
        {
            Resource = "GPIB0::" + address + "::INSTR";
            try { Check(viOpenDefaultRM(out rm), "VISA 시작"); }
            catch (DllNotFoundException)
            {
                throw new InvalidOperationException("GPIB를 쓰려면 NI-VISA(또는 Keysight IO Libraries)와 GPIB 카드 드라이버를 설치하세요.");
            }
            int status = viOpen(rm, Resource, 0, 3000, out vi);
            if (status < 0)
            {
                viClose(rm);
                throw new IOException(Resource + " 를 열지 못했습니다 (VISA 오류 0x" + status.ToString("X8") + "). GPIB 주소와 케이블, 계측기의 인터페이스 설정(GPIB)을 확인하세요.");
            }
            TimeoutMs = 5000;
        }

        static void Check(int status, string what)
        {
            if (status < 0) throw new IOException(what + " 실패 (VISA 오류 0x" + status.ToString("X8") + ").");
        }

        public void Send(string command)
        {
            byte[] b = Encoding.ASCII.GetBytes(command + "\r\n");
            int written;
            Check(viWrite(vi, b, b.Length, out written), "'" + command + "' 보내기");
        }

        // 계측기가 EOI로 끝을 알릴 때까지 읽음
        byte[] ReadAll(string command)
        {
            var all = new MemoryStream();
            var buf = new byte[65536];
            int status, got;
            do
            {
                status = viRead(vi, buf, buf.Length, out got);
                if (status == ErrorTimeout) throw new TimeoutException("'" + command + "' 응답 없음 (" + timeoutMs / 1000 + "초). 계측기의 인터페이스 설정(GPIB)을 확인하세요.");
                Check(status, "'" + command + "' 읽기");
                all.Write(buf, 0, got);
            } while (status == SuccessMaxCount);
            return all.ToArray();
        }

        public string Query(string command)
        {
            Send(command);
            return Encoding.ASCII.GetString(ReadAll(command)).Trim();
        }

        public byte[] QueryBlock(string command)
        {
            Send(command);
            return Block.Read(new MemoryStream(ReadAll(command)), command);
        }

        public void Dispose()
        {
            viClose(vi);
            viClose(rm);
        }
    }

    class SerialMeter : IMeter, ITimed
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
                throw new TimeoutException("'" + command + "' 응답 없음 (" + port.ReadTimeout / 1000 + "초). 계측기의 인터페이스 설정(USB 또는 RS-232C 속도)과 케이블을 확인하세요.");
            }
        }

        // 공식 앱 Form_HardCopy와 같은 읽기: '#', 길이 자릿수 n, n자리 길이, 데이터, 끝 문자(CR 또는 CR+LF)
        public byte[] QueryBlock(string command)
        {
            port.DiscardInBuffer();
            Send(command);
            try
            {
                if (port.ReadByte() != '#') throw new InvalidDataException("'" + command + "' 응답이 바이너리 블록(#)이 아닙니다.");
                int digits = port.ReadByte() - '0';
                var len = new StringBuilder();
                for (int i = 0; i < digits; i++) len.Append((char)port.ReadByte());
                int size;
                if (digits < 1 || digits > 9 || !int.TryParse(len.ToString(), out size) || size <= 0)
                    throw new InvalidDataException("'" + command + "' 블록 길이를 읽을 수 없습니다 (" + len + ").");
                var data = new byte[size];
                for (int got = 0; got < size; ) got += port.Read(data, got, size - got);
                Thread.Sleep(100);
                port.DiscardInBuffer();   // 끝 문자
                return data;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("'" + command + "' 응답 없음 (" + port.ReadTimeout / 1000 + "초).");
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
        public List<KeyValuePair<string, string>> Extra = new List<KeyValuePair<string, string>>();   // 그 밖의 설정 (명령, 값), 예: (":RANG", "5")

        public bool Any { get { return Level != null || Speed != null || Bias != 0 || Average > 0 || Extra.Count > 0 || Pars.Any(p => p != null); } }

        static string ParKey(int i) { return ":PARameter" + (i + 1); }

        // 신호 종류별 레벨 값 명령
        public static string LevelKey(string mode)
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
            foreach (var kv in Extra)
            {
                s[kv.Key] = m.Query(kv.Key + "?").Trim();
                // 레인지 번호와 AUTO는 같이 되돌려야 원래 상태가 됨
                if (kv.Key == ":RANG" || kv.Key == ":RANG:AUTO")
                    foreach (string k in new[] { ":RANG", ":RANG:AUTO" }) s[k] = m.Query(k + "?").Trim();
            }
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
                    got.Add("PAR" + (i + 1) + "=" + Fmt.ParLabel(g));
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
            foreach (var kv in Extra)
            {
                m.Send(kv.Key + " " + kv.Value);
                string got = m.Query(kv.Key + "?").Trim();
                SettingItem item = MeterSettings.Items.FirstOrDefault(i => i.Cmd == kv.Key);
                double a, b;
                bool ok = string.Equals(got, kv.Value, StringComparison.OrdinalIgnoreCase)
                    || (Fmt.TryParse(got, out a) && Fmt.TryParse(kv.Value, out b) && Math.Abs(a - b) <= Math.Abs(b) * 1e-3 + 1e-12);
                log.Add("측정 조건: " + (item != null ? item.Name : kv.Key) + " " + got + (ok ? "" : "  ※ 계측기가 값을 받지 않았습니다 (보낸 값 " + kv.Value + "). 허용 범위를 확인하세요."));
            }
            return log;
        }

        static bool Matches(bool modeOk, string response, double wanted, double tolerance)
        {
            double got;
            return modeOk && Fmt.TryParse(response, out got) && Math.Abs(got - wanted) <= tolerance;
        }

        // 복원 순서 (Apply의 반대): 주파수·트리거(스윕) → 그 밖의 설정(값 먼저, ON/OFF·AUTO 나중) → DC 바이어스 → 평균 → SPEED → 레벨 값 → 신호 종류 → PAR1~4
        static readonly string[] RestoreOrder =
        {
            ":FREQ", ":TRIG", ":TRIG:DEL", ":SSO:WAIT", ":SSO", ":LIM:VOLT", ":LIM:CURR", ":LIM", ":RANG:LOWZ", ":RANG", ":RANG:AUTO",
            ":DCR:DCD", ":DCR:ADJD", ":DCR:LFR", ":DCBias:LEVel", ":DCBias", ":AVER", ":SPEEd", ":LEVel:VOLTage", ":LEVel:CVOLTage", ":LEVel:CCURRent", ":LEVel",
            ":PARameter1", ":PARameter2", ":PARameter3", ":PARameter4",
            ":DCR:RANG", ":DCR:RANG:AUTO"   // :DCR:RANG n 은 HOLD로 바꾸므로 AUTO를 마지막에
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
            int sp = c.IndexOf(' ');
            if (sp > 0 && extra.ContainsKey(c.Substring(0, sp))) extra[c.Substring(0, sp)] = c.Substring(sp + 1).Trim();
        }

        // [계측기 설정] 탭 항목 중 위에서 따로 흉내 내지 않는 것 (보낸 값을 그대로 돌려줌)
        readonly Dictionary<string, string> extra = new Dictionary<string, string>
        {
            { ":LIM", "OFF" }, { ":LIM:VOLT", "5.000" }, { ":LIM:CURR", "50.00E-03" }, { ":RANG:AUTO", "ON" }, { ":RANG", "4" }, { ":DCR:RANG:AUTO", "ON" }, { ":DCR:RANG", "2" },
            { ":RANG:LOWZ", "OFF" }, { ":TRIG:DEL", "0.0000" }, { ":SSO", "OFF" }, { ":SSO:WAIT", "0.0010" },
            { ":DCR:DCD", "0.0000" }, { ":DCR:ADJD", "0.0030" }, { ":DCR:LFR", "60" }
        };

        // 화면 캡처 흉내: 480×272 BMP
        public byte[] QueryBlock(string command)
        {
            CheckPlugged();
            using (var bmp = new Bitmap(480, 272))
            using (var g = Graphics.FromImage(bmp))
            using (var ms = new MemoryStream())
            {
                bool mono = command.ToUpperInvariant().Contains("MONO");
                g.Clear(mono ? Color.White : Color.FromArgb(20, 40, 90));
                g.DrawString("IM3536 DEMO " + Fmt.FrequencyLabel(freq), new Font("Arial", 20), mono ? Brushes.Black : Brushes.White, 20, 110);
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                return ms.ToArray();
            }
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
                    double v = p == "LS" || p == "LP" ? l : p == "Q" ? q : p == "D" ? 1 / q : p == "PHASE" ? cth : p == "Z" ? cz : p == "RDC" ? (RdcNoValueCount-- > 0 || (extra[":DCR:RANG:AUTO"] == "OFF" && extra[":DCR:RANG"] != "1") ? -9.99999E+33 : CoreRw) : 2 * Math.PI * freq * l;
                    values.Add(v.ToString("0.00000E+00", CultureInfo.InvariantCulture));
                }
                return string.Join(",", values);
            }
            string ex;
            if (c.EndsWith("?") && extra.TryGetValue(c.TrimEnd('?'), out ex)) return ex;
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
}
