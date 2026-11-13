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
    // 계측기 설정 (공식 앱 Set LCR 화면의 IM3536 항목. 명령 철자는 공식 앱 그대로)
    // ------------------------------------------------------------------

    class SettingItem
    {
        public string Name, Cmd, Hint;
        public string[] Choices;   // null이면 직접 입력
        public SettingItem(string name, string cmd, string hint, params string[] choices)
        {
            Name = name; Cmd = cmd; Hint = hint; Choices = choices.Length == 0 ? null : choices;
        }
    }

    static class MeterSettings
    {
        static readonly string[] OnOff = { "ON", "OFF" };
        static readonly string[] ParList = { "OFF", "Z", "Y", "PHASE", "CS", "CP", "D", "LS", "LP", "Q", "RS", "G", "RP", "X", "B", "RDC", "S", "E" };

        public static readonly SettingItem[] Items =
        {
            new SettingItem("측정 주파수 [Hz]", ":FREQ", "4 ~ 8000000"),
            new SettingItem("측정 신호 종류", ":LEVel", "V 개방전압 / CV 정전압 / CC 정전류", "V", "CV", "CC"),
            new SettingItem("개방전압 V [V]", ":LEVel:VOLTage", "0.005 ~ 5"),
            new SettingItem("정전압 CV [V]", ":LEVel:CVOLTage", "0.005 ~ 5"),
            new SettingItem("정전류 CC [A]", ":LEVel:CCURRent", "0.00001 ~ 0.05 (10 µA ~ 50 mA)"),
            new SettingItem("리밋 (전압·전류 제한)", ":LIM", "측정 신호의 전압·전류가 리밋 값을 넘지 않게 함", OnOff),
            new SettingItem("리밋 전압 [V]", ":LIM:VOLT", "0.005 ~ 5"),
            new SettingItem("리밋 전류 [A]", ":LIM:CURR", "0.00001 ~ 0.05"),
            new SettingItem("자동 레인지", ":RANG:AUTO", "OFF면 아래 레인지로 고정 (HOLD)", OnOff),
            new SettingItem("레인지", ":RANG", "번호로 보냄 (괄호는 범위 이름)", "1 (100mΩ)", "2 (1Ω)", "3 (10Ω)", "4 (100Ω)", "5 (1kΩ)", "6 (10kΩ)", "7 (100kΩ)", "8 (1MΩ)", "9 (10MΩ)", "10 (100MΩ)"),
            new SettingItem("LOW Z 모드", ":RANG:LOWZ", "저임피던스 고정밀 모드", OnOff),
            new SettingItem("SPEED", ":SPEEd", "느릴수록 정확", "FAST", "MEDIUM", "SLOW", "SLOW2"),
            new SettingItem("평균 횟수", ":AVER", "1 ~ 256 (1 = OFF)"),
            new SettingItem("트리거", ":TRIG", "INT 내부(연속) / EXT 외부(명령·신호 때만)", "INT", "EXT"),
            new SettingItem("트리거 지연 [s]", ":TRIG:DEL", "0 ~ 9.999 (트리거 후 측정까지 대기)"),
            new SettingItem("트리거 동기 출력", ":SSO", "측정하는 동안에만 측정 신호를 냄", OnOff),
            new SettingItem("동기 출력 대기 [s]", ":SSO:WAIT", "신호를 낸 뒤 측정까지 대기"),
            new SettingItem("DC 바이어스", ":DCBias", "", OnOff),
            new SettingItem("DC 바이어스 전압 [V]", ":DCBias:LEVel", "0 ~ 2.5"),
            new SettingItem("PAR1", ":PARameter1", "같은 파라미터는 두 칸에 못 넣음", ParList),
            new SettingItem("PAR2", ":PARameter2", "", ParList),
            new SettingItem("PAR3", ":PARameter3", "", ParList),
            new SettingItem("PAR4", ":PARameter4", "", ParList),
            new SettingItem("Rdc 측정 지연 [s]", ":DCR:DCD", "DC 측정 전 대기"),
            new SettingItem("Rdc 보정 지연 [s]", ":DCR:ADJD", "DC 오프셋 보정 전 대기"),
            new SettingItem("전원 주파수 [Hz]", ":DCR:LFR", "Rdc 측정의 전원 잡음 제거 (50 / 60)", "50", "60")
        };

        // 보내고 계측기가 거부했는지 *ESR?(명령·실행·장치 오류 = 56)로 확인. 거부하면 그 값, 받으면 null
        public static string Apply(IMeter m, SettingItem s, string value)
        {
            m.Query("*ESR?");
            m.Send(s.Cmd + " " + value);
            string esr = m.Query("*ESR?").Trim();
            int bits;
            return int.TryParse(esr, out bits) && (bits & 56) != 0 ? esr : null;
        }
    }
}
