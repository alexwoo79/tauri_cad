using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

namespace SunlightPlugin
{
    public class MainEntry : IExtensionApplication
    {
        private static PaletteSet _paletteSet = null;

        public void Initialize() { }
        public void Terminate() { }

        [CommandMethod("SUN")]
        public void ShowSunlightPalette()
        {
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
    }
}