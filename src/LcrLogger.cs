// LCR Logger - HIOKI LCR 미터(IM3536 / IM3523 / IM3533 / IM3590 / IM7580 등)에서
// 측정값을 USB(가상 COM 포트)·RS-232C·LAN·GPIB로 받아 CSV 파일로 저장하는 프로그램.
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
using System.Net.Sockets;
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
[assembly: System.Reflection.AssemblyVersion("1.27.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.27.0.0")]

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
}
