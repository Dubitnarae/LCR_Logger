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
    // 인덕턴스 기록 탭: 코어 치수로 측정마다 μ′ 계산, 자주 쓰는 코어 크기는 프리셋으로 저장
    partial class MainForm
    {
        Switch chkCore;
        ComboBox cbCore;
        NumericUpDown numCoreN;
        TextBox txtCoreOD, txtCoreID, txtCoreH;
        Label lblCoreLeAe;
        Button btnCoreSave, btnCoreDelete;
        string tableCore = "";   // 지금 표·CSV를 시작할 때의 코어 설정 (바뀌면 다음 측정부터 새 파일)
        const string CorePrefix = "Core.";

        FlowLayoutPanel BuildCoreCard()
        {
            var g = Group("코어 투자율 (측정할 때마다 인덕턴스로 μ′ 계산)");
            chkCore = new Switch { Text = "사용", Margin = new Padding(3, 3, 8, 2) };
            cbCore = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 150 };
            cbCore.SelectionChangeCommitted += delegate { LoadCore(cbCore.SelectedItem as string); };
            btnCoreSave = Btn("코어 저장", OnCoreSave);
            btnCoreDelete = Btn("삭제", OnCoreDelete);
            numCoreN = new NumericUpDown { Width = 60, Minimum = 1, Maximum = 10000, Value = 10 };
            txtCoreOD = new TextBox { Width = 60 };
            txtCoreID = new TextBox { Width = 60 };
            txtCoreH = new TextBox { Width = 60 };
            lblCoreLeAe = Lbl("");
            lblCoreLeAe.ForeColor = Theme.Secondary;
            numCoreN.ValueChanged += delegate { UpdateCoreLabel(); };
            foreach (TextBox t in new[] { txtCoreOD, txtCoreID, txtCoreH }) t.TextChanged += delegate { UpdateCoreLabel(); };
            chkCore.CheckedChanged += delegate { UpdateCoreLabel(); };
            Add(g, chkCore, Lbl("코어 프리셋"), cbCore, btnCoreSave, btnCoreDelete, Lbl("턴 수 N"), numCoreN,
                Lbl("외경 OD"), txtCoreOD, Lbl("내경 ID"), txtCoreID, Lbl("높이 h"), txtCoreH, Lbl("mm"), lblCoreLeAe);
            g.SetFlowBreak(btnCoreDelete, true);
            return g;
        }

        // k = le/(μ0·N²·Ae) [SI]. 치수가 잘못되면 NaN
        double CoreK()
        {
            double od, id, h;
            if (!CoreSize(out od, out id, out h)) return double.NaN;
            int n = (int)numCoreN.Value;
            return Permeability.Le(od, id) * 1e-3 / (Permeability.Mu0 * n * n * Permeability.Ae(od, id, h) * 1e-6);
        }

        bool CoreSize(out double od, out double id, out double h)
        {
            return Fmt.TryParse(txtCoreOD.Text.Trim(), out od) & Fmt.TryParse(txtCoreID.Text.Trim(), out id) & Fmt.TryParse(txtCoreH.Text.Trim(), out h)
                && od > id && id > 0 && h > 0;
        }

        // CSV 위쪽에 남기는 코어 설정 (끄면 빈 문자열)
        string CoreText()
        {
            if (!chkCore.Checked) return "";
            double od, id, h;
            if (!CoreSize(out od, out id, out h)) return "치수 오류";
            return string.Format(CultureInfo.InvariantCulture, "{0}N=" + numCoreN.Value + ", OD={1} ID={2} h={3} mm (le={4:0.####} mm, Ae={5:0.####} mm²)",
                cbCore.Text.Trim().Length > 0 ? cbCore.Text.Trim() + ", " : "", od, id, h, Permeability.Le(od, id), Permeability.Ae(od, id, h));
        }

        void UpdateCoreLabel()
        {
            double od, id, h;
            bool ok = CoreSize(out od, out id, out h);
            lblCoreLeAe.Text = ok ? string.Format(CultureInfo.InvariantCulture, "le = {0:0.###} mm · Ae = {1:0.###} mm²", Permeability.Le(od, id), Permeability.Ae(od, id, h))
                : "외경 > 내경 > 0, 높이 > 0 으로 입력";
            lblCoreLeAe.ForeColor = ok || !chkCore.Checked ? Theme.Secondary : Theme.Red;
        }

        void LoadCore(string name)
        {
            string text = name == null ? null : settings.Get(CorePrefix + name, null);
            if (text == null) return;
            string[] p = text.Split(';');   // "OD;ID;h" [mm]
            if (p.Length < 3) return;
            txtCoreOD.Text = p[0];
            txtCoreID.Text = p[1];
            txtCoreH.Text = p[2];
            Log("코어 불러옴: " + name);
        }

        void RefreshCores(string select)
        {
            cbCore.Items.Clear();
            foreach (string key in settings.KeysWithPrefix(CorePrefix).OrderBy(k => k, StringComparer.CurrentCulture))
                cbCore.Items.Add(key.Substring(CorePrefix.Length));
            cbCore.Text = select ?? "";
        }

        void OnCoreSave(object sender, EventArgs e)
        {
            string name = cbCore.Text.Trim();
            double od, id, h;
            if (name.Length == 0 || name.IndexOfAny(new[] { '=', '\r', '\n' }) >= 0)
            {
                MessageBox.Show(this, "코어 프리셋 칸에 이름(예: T50, 25x15x10)을 입력한 뒤 [코어 저장]을 누르세요.\n(이름에 '='는 쓸 수 없습니다)", "코어", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!CoreSize(out od, out id, out h))
            {
                MessageBox.Show(this, "외경·내경·높이를 확인하세요. (외경 > 내경 > 0, 높이 > 0, mm)", "코어", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (settings.Get(CorePrefix + name, null) != null
                && MessageBox.Show(this, "코어 '" + name + "'이 이미 있습니다. 지금 치수로 덮어쓸까요?", "코어", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            settings.Set(CorePrefix + name, string.Join(";", new[] { od, id, h }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
            settings.Save();
            RefreshCores(name);
            Log("코어 저장: " + name);
        }

        void OnCoreDelete(object sender, EventArgs e)
        {
            string name = cbCore.Text.Trim();
            if (name.Length == 0 || settings.Get(CorePrefix + name, null) == null)
            {
                MessageBox.Show(this, "삭제할 코어를 목록에서 고르세요.", "코어", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "코어 '" + name + "'을 삭제할까요?", "코어", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            settings.Remove(CorePrefix + name);
            settings.Save();
            RefreshCores(null);
            Log("코어 삭제: " + name);
        }

        void LoadCoreSettings()
        {
            RefreshCores(settings.Get("CoreName", ""));
            chkCore.Checked = settings.Get("CoreOn", "0") == "1";
            numCoreN.Value = settings.GetInt("CoreN", 10, 1, 10000);
            txtCoreOD.Text = settings.Get("CoreOD", "");
            txtCoreID.Text = settings.Get("CoreID", "");
            txtCoreH.Text = settings.Get("CoreH", "");
            UpdateCoreLabel();
        }

        void SaveCoreSettings()
        {
            settings.Set("CoreName", cbCore.Text.Trim());
            settings.Set("CoreOn", chkCore.Checked ? "1" : "0");
            settings.Set("CoreN", ((int)numCoreN.Value).ToString(CultureInfo.InvariantCulture));
            settings.Set("CoreOD", txtCoreOD.Text.Trim());
            settings.Set("CoreID", txtCoreID.Text.Trim());
            settings.Set("CoreH", txtCoreH.Text.Trim());
        }
    }
}
