using System.Net;

namespace XIVLauncher.Common.Http;

/// <summary>
///     登陆代理条目配置
/// </summary>
public sealed class LoginProxyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     完整代理地址, 形如 http://用户名:密码@主机:端口
    /// </summary>
    public string Url { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     解析代理地址为连接所需的主机端口与认证信息
    /// </summary>
    public bool TryGetProxyParts(out Uri proxyUri, out ICredentials? credentials)
    {
        proxyUri    = null!;
        credentials = null;

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp                    ||
            string.IsNullOrWhiteSpace(uri.Host))
            return false;

        proxyUri = new UriBuilder(Uri.UriSchemeHttp, uri.Host, uri.IsDefaultPort ? 80 : uri.Port).Uri;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            credentials = new NetworkCredential
            (
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty
            );
        }

        return true;
    }

    /// <summary>
    ///     列表展示用地址, 认证密码段打码
    /// </summary>
    public string GetMaskedUrl()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return Url;

        var user = uri.UserInfo.Split(':', 2)[0];
        return Url.Replace($"{uri.UserInfo}@", $"{user}:***@");
    }
}
