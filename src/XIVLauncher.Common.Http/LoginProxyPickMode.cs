namespace XIVLauncher.Common.Http;

/// <summary>
///     登陆代理入口选取策略
/// </summary>
public enum LoginProxyPickMode
{
    /// <summary>
    ///     优先复用上次成功的入口, 失败才切换
    /// </summary>
    Sticky = 0,

    /// <summary>
    ///     每次配置生效时在启用条目中随机选取
    /// </summary>
    Random = 1,

    /// <summary>
    ///     固定使用手动指定的条目
    /// </summary>
    Manual = 2
}
