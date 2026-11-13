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
}
