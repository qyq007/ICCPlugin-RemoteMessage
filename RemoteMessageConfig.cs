using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteMessagePlugin
{
    /// <summary>
    /// 插件配置。配置文件为插件配置目录下的 config.json，缺失时使用默认值。
    /// </summary>
    internal sealed class RemoteMessageConfig
    {
        /// <summary>监听端口。</summary>
        public int Port { get; set; } = 23000;

        /// <summary>监听地址。* / + / any / 0.0.0.0 表示所有网卡，localhost / 127.0.0.1 表示仅本机。</summary>
        public string BindAddress { get; set; } = "*";

        /// <summary>访问令牌。留空表示不鉴权；强烈建议对外网开放时设置。</summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>单条消息最大字符数。</summary>
        public int MaxContentLength { get; set; } = 2000;

        /// <summary>每个客户端每分钟允许的请求数，0 表示不限制。</summary>
        public int RateLimitPerMinute { get; set; } = 0;

        /// <summary>收到消息时是否播放提示音。</summary>
        public bool PlaySound { get; set; } = true;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static RemoteMessageConfig Load(string folder, Action<string> log, Action<string, Exception> logError)
        {
            string path = Path.Combine(folder, "config.json");
            if (!File.Exists(path))
            {
                log($"未找到配置文件 {path}，使用默认配置。可参考 config.example.json。");
                return new RemoteMessageConfig();
            }

            try
            {
                var config = JsonSerializer.Deserialize<RemoteMessageConfig>(File.ReadAllText(path), JsonOptions)
                             ?? new RemoteMessageConfig();
                config.Normalize();
                log($"已加载配置: {path}（端口 {config.Port}，鉴权 {(string.IsNullOrEmpty(config.Token) ? "关闭" : "开启")}）");
                return config;
            }
            catch (Exception ex)
            {
                logError($"读取 {path} 失败，使用默认配置", ex);
                return new RemoteMessageConfig();
            }
        }

        private void Normalize()
        {
            if (Port < 1 || Port > 65535) Port = 23000;
            if (string.IsNullOrWhiteSpace(BindAddress)) BindAddress = "*";
            if (MaxContentLength < 1 || MaxContentLength > 10000) MaxContentLength = 2000;
            if (RateLimitPerMinute < 0) RateLimitPerMinute = 0;
            Token ??= string.Empty;
        }
    }
}
