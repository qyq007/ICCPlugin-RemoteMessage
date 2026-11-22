using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace RemoteMessagePlugin
{
    /// <summary>
    /// 内置 WebUI 页面。HTML 作为嵌入资源打包进 DLL，避免额外文件依赖。
    /// </summary>
    internal static class WebUi
    {
        private static string? _html;

        /// <summary>把配置值注入页面模板。</summary>
        public static string Render(int maxContentLength)
        {
            return Html.Replace("{{MAX_LENGTH}}", maxContentLength.ToString());
        }

        public static string Html
        {
            get
            {
                if (_html != null) return _html;

                using var stream = typeof(WebUi).Assembly.GetManifestResourceStream("RemoteMessagePlugin.WebUI.html")
                                   ?? throw new InvalidOperationException("找不到内嵌的 WebUI 资源。");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                _html = reader.ReadToEnd();
                return _html;
            }
        }
    }
}
