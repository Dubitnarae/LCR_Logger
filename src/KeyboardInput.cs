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
}
