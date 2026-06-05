using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

namespace SunlightPlugin
{
    public class MainEntry : IExtensionApplication
    {
        private static PaletteSet _paletteSet = null;

        private static void LogLoadedAssemblyInfo()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string path = asm.Location;
                string version = asm.GetName().Version == null ? "unknown" : asm.GetName().Version.ToString();
                string writeTime = File.Exists(path)
                    ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss")
                    : "missing";

                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage($"\n[SUN] dll={path}");
                    doc.Editor.WriteMessage($"\n[SUN] ver={version}, writeTime={writeTime}");
                }
            }
            catch
            {
            }
        }

        public void Initialize() { }
        public void Terminate() { }

        [CommandMethod("SUN")]
        public void ShowSunlightPalette()
        {
            var licenseStatus = LicenseManager.GetCurrentStatus();
            if (!licenseStatus.IsValid)
            {
                WriteLicenseStatus(licenseStatus, showDialog: true);
                return;
            }

            LogLoadedAssemblyInfo();

            if (_paletteSet == null)
            {
                _paletteSet = new PaletteSet("GPU 日照强排引擎", new Guid("11223344-5566-7788-99AA-BBCCDDEEFF00"));
                _paletteSet.Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu | PaletteSetStyles.ShowAutoHideButton;

                // 【终极死机克星】：严禁 WPF 面板强行锁定 CAD 键盘焦点！
                _paletteSet.KeepFocus = false;

                SunControlUI ui = new SunControlUI();
                _paletteSet.AddVisual("设置面板", ui);
                _paletteSet.MinimumSize = new System.Drawing.Size(280, 600);
            }
            _paletteSet.Visible = true;
        }

        [CommandMethod("SUNLICINFO")]
        public void ShowLicenseInfo()
        {
            WriteLicenseStatus(LicenseManager.GetCurrentStatus(), showDialog: false);
        }

        [CommandMethod("SUNLICSTATUS")]
        public void ShowLicenseStatus()
        {
            WriteLicenseStatus(LicenseManager.GetCurrentStatus(), showDialog: false);
        }

        [CommandMethod("SUNLICACT")]
        public void ActivateLicense()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var options = new Autodesk.AutoCAD.EditorInput.PromptStringOptions("\n请输入授权码: ")
            {
                AllowSpaces = true
            };
            var result = doc.Editor.GetString(options);
            if (result.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

            var status = LicenseManager.Activate(result.StringResult);
            WriteLicenseStatus(status, showDialog: !status.IsValid);
        }

        [CommandMethod("SUNLICCLEAR")]
        public void ClearLicense()
        {
            LicenseManager.ClearLicense();
            WriteLicenseStatus(LicenseManager.GetCurrentStatus(), showDialog: false);
        }

        private static void WriteLicenseStatus(LicenseStatus status, bool showDialog)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            string summary = LicenseManager.BuildStatusSummary(status);

            if (doc != null)
            {
                doc.Editor.WriteMessage("\n" + summary.Replace("\r\n", "\n"));
            }

            if (showDialog)
            {
                Application.ShowAlertDialog(summary + "\n\n操作命令:\n1. SUNLICINFO 查看机器码\n2. SUNLICACT 输入授权码\n3. SUNLICSTATUS 查看授权状态");
            }
        }
    }
}