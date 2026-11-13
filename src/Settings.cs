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
}
